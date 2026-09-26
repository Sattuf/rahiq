using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Outbox;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Pricing.Contracts;

namespace Rahiq.Api;

internal static class ModuleAssemblies
{
    public static readonly Assembly[] All =
    [
        typeof(Modules.Catalog.CatalogModule).Assembly,
        typeof(Modules.Inventory.InventoryModule).Assembly,
        typeof(Modules.Pricing.PricingModule).Assembly,
        typeof(Modules.Cart.CartModule).Assembly,
        typeof(Modules.Ordering.OrderingModule).Assembly,
        typeof(Modules.Payments.PaymentsModule).Assembly,
        typeof(Modules.Shipping.ShippingModule).Assembly,
        typeof(Modules.Customers.CustomersModule).Assembly,
        typeof(Modules.Content.ContentModule).Assembly,
        typeof(Modules.Documents.DocumentsModule).Assembly,
        typeof(Modules.Notifications.NotificationsModule).Assembly,
        typeof(Modules.Conversations.ConversationsModule).Assembly,
    ];
}

/// <summary>
/// Database guards surface as clear 409s instead of 500s: a unique key (duplicate SKU, slug, double order),
/// a CHECK constraint (stock can never go negative) or a stale optimistic-concurrency version.
/// </summary>
internal sealed class UnhandledConflictFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var (code, message) = context.Exception switch
        {
            DbUpdateConcurrencyException => ("concurrency.stale", "Someone else changed this meanwhile. Reload and try again."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => ("conflict.duplicate", "This already exists."),
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } => ("conflict.duplicate", "This already exists."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.CheckViolation } } => ("conflict.rule", "This would break a stock or amount rule."),
            PostgresException { SqlState: PostgresErrorCodes.CheckViolation } => ("conflict.rule", "This would break a stock or amount rule."),
            _ => (null, null),
        };

        if (code is null)
        {
            return;
        }

        var problem = new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = code, Detail = message };
        problem.Extensions["code"] = code;
        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict };
        context.ExceptionHandled = true;
    }
}

/// <summary>API responses are data, never pages: the strictest headers apply (security.md §8).</summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var h = context.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h.XFrameOptions = "DENY";
        if (!context.Request.Path.StartsWithSegments("/dev"))
        {
            h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        }

        if (context.Request.IsHttps)
        {
            h.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
        }

        return next(context);
    }
}

internal static class MediaEndpoint
{
    /// <summary>
    /// Local development serves blobs from disk under /media. Only public prefixes (product images) are served;
    /// lab reports, labels, return photos and invoices are private and go through their own authorised endpoints.
    /// </summary>
    public static void MapMedia(this WebApplication app) =>
        app.MapGet("/media/{**key}", async (string key, IBlobStorage storage, CancellationToken ct) =>
        {
            if (!key.StartsWith("products/", StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal))
            {
                return Results.NotFound();
            }

            var stream = await storage.GetAsync(key, ct);
            return stream is null ? Results.NotFound() : Results.Stream(stream, "image/webp", enableRangeProcessing: true);
        }).ExcludeFromDescription();
}

/// <summary>
/// Keeps the storefront fresh (architecture.md §6): on publish or price change, the API's output cache is evicted and
/// Next.js is asked to revalidate the tagged ISR pages. The cart and checkout never trust these caches anyway.
/// </summary>
internal sealed partial class WebRevalidation(IOutputCacheStore cache, IHttpClientFactory http, IOptions<WebOptions> options, ILogger<WebRevalidation> logger)
    : IEventHandler<ProductPublished>, IEventHandler<ProductChanged>, IEventHandler<PriceChanged>
{
    public Task Handle(ProductPublished domainEvent, CancellationToken cancellationToken) => Revalidate(["catalog", $"product:{domainEvent.Slug}"], cancellationToken);

    public Task Handle(ProductChanged domainEvent, CancellationToken cancellationToken) => Revalidate(["catalog", $"product:{domainEvent.Slug}"], cancellationToken);

    public Task Handle(PriceChanged domainEvent, CancellationToken cancellationToken) => Revalidate(["catalog"], cancellationToken);

    private async Task Revalidate(string[] tags, CancellationToken cancellationToken)
    {
        await cache.EvictByTagAsync("catalog", cancellationToken);
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.RevalidateUrl))
        {
            return;
        }

        try
        {
            using var client = http.CreateClient("web-revalidate");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(o.RevalidateUrl)) { Content = JsonContent.Create(new { tags }) };
            request.Headers.Add("X-Revalidate-Secret", o.RevalidateSecret);
            using var response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogFailed(logger, ex); // Pages still expire on their own (ISR revalidate window).
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storefront revalidation failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}

/// <summary>
/// Operational commands, run with the same image: <c>migrate</c> (before each deploy, devops.md §3), <c>seed</c>
/// (demo data for development and staging), <c>create-owner</c>, <c>reindex</c>, <c>drain-outbox</c>.
/// </summary>
internal static class Cli
{
    private static readonly string[] TelegramUpdates = ["message"];

    public static async Task<bool> TryRunAsync(WebApplication app, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            return false;
        }

        var services = app.Services;
        switch (args[0])
        {
            case "migrate":
                await MigrateAsync(services);
                return true;
            case "reset" when app.Environment.IsDevelopment():
                NpgsqlConnection.ClearAllPools();
                await SqlMigrator.DropDatabaseAsync(app.Configuration.GetConnectionString("Postgres")!);
                goto case "seed";
            case "seed":
                await MigrateAsync(services);
                await using (var scope = services.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(CancellationToken.None);
                    await services.GetRequiredService<OutboxProcessor>().DrainAsync();
                }

                return true;
            case "create-owner" when args.Length >= 4:
                await using (var scope = services.CreateAsyncScope())
                {
                    var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Modules.Customers.Application.BootstrapOwnerCommand(args[1], args[2], args[3]));
                    Console.WriteLine(result.IsSuccess ? $"Owner {args[1]} ready. Sign in at /admin and set up two-factor authentication." : result.Error.Message);
                }

                return true;
            case "reindex":
                await using (var scope = services.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<Modules.Catalog.Infrastructure.ProductSearch>().RebuildAsync(CancellationToken.None);
                }

                return true;
            case "drain-outbox":
                await services.GetRequiredService<OutboxProcessor>().DrainAsync();
                return true;
            case "telegram-webhook":
                await RegisterTelegramWebhookAsync(services);
                return true;
            default:
                Console.Error.WriteLine("Commands: migrate | seed | reset (development) | create-owner <email> <name> <password> | reindex | drain-outbox | telegram-webhook");
                Environment.ExitCode = 2;
                return true;
        }
    }

    /// <summary>
    /// Points the Telegram bot at /webhooks/telegram with our secret (Telegram echoes it in a header on every call).
    /// Run once per environment, after Channels:Telegram and Store:PublicApiUrl are set.
    /// </summary>
    private static async Task RegisterTelegramWebhookAsync(IServiceProvider services)
    {
        var telegram = services.GetRequiredService<IOptions<Modules.Conversations.Infrastructure.Channels.ChannelOptions>>().Value.Telegram;
        var api = services.GetRequiredService<IOptions<StoreOptions>>().Value.PublicApiUrl.TrimEnd('/');
        if (string.IsNullOrEmpty(telegram.BotToken) || telegram.WebhookSecret is not { Length: >= 16 })
        {
            Console.Error.WriteLine("Set Channels:Telegram:BotToken and Channels:Telegram:WebhookSecret (16+ characters: A-Z, a-z, 0-9, _ and -) first.");
            Environment.ExitCode = 2;
            return;
        }

        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("telegram");
        using var response = await client.PostAsJsonAsync(new Uri($"https://api.telegram.org/bot{telegram.BotToken}/setWebhook"), new
        {
            url = $"{api}/webhooks/telegram",
            secret_token = telegram.WebhookSecret,
            allowed_updates = TelegramUpdates,
        });
        Console.WriteLine($"Telegram: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    public static async Task MigrateAsync(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (services.GetRequiredService<IHostEnvironment>().IsDevelopment() || configuration.GetValue("Database:CreateIfMissing", false))
        {
            await SqlMigrator.EnsureDatabaseAsync(configuration.GetConnectionString("Postgres")!);
        }

        var applied = await services.GetRequiredService<SqlMigrator>().MigrateAsync(typeof(Program).Assembly);
        Console.WriteLine(applied.Count == 0 ? "Database is up to date." : $"Applied: {string.Join(", ", applied)}");
    }
}
