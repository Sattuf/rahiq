using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Rahiq.Api;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common;
using Rahiq.Infrastructure.Common.Security;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.Modules.Cart;
using Rahiq.Modules.Catalog;
using Rahiq.Modules.Content;
using Rahiq.Modules.Conversations;
using Rahiq.Modules.Customers;
using Rahiq.Modules.Customers.Infrastructure;
using Rahiq.Modules.Documents;
using Rahiq.Modules.Inventory;
using Rahiq.Modules.Notifications;
using Rahiq.Modules.Ordering;
using Rahiq.Modules.Payments;
using Rahiq.Modules.Pricing;
using Rahiq.Modules.Shipping;
using Rahiq.SharedKernel.Compliance;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

builder.Host.UseSerilog((context, logger) => logger.ReadFrom.Configuration(context.Configuration));

// ── Modules (ADR-001, ADR-013) ──────────────────────────────────────────
builder.Services.AddRahiqInfrastructure(configuration);
builder.Services.AddSingleton(Taxonomy.CreateClaimsGuard());
builder.Services
    .AddCatalogModule(configuration)
    .AddInventoryModule(configuration)
    .AddPricingModule(configuration)
    .AddCartModule(configuration)
    .AddOrderingModule(configuration)
    .AddPaymentsModule(configuration)
    .AddShippingModule(configuration)
    .AddCustomersModule(configuration)
    .AddContentModule(configuration)
    .AddDocumentsModule(configuration)
    .AddNotificationsModule(configuration)
    .AddConversationsModule(configuration)
    .AddModuleAssembly(typeof(Program).Assembly);
builder.Services.AddScoped<DemoSeeder>();
builder.Services.Configure<WebOptions>(configuration.GetSection(WebOptions.Section));
builder.Services.AddHttpClient("web-revalidate", c => c.Timeout = TimeSpan.FromSeconds(5));

var mvc = builder.Services.AddControllers(o => o.Filters.Add<UnhandledConflictFilter>());
mvc.ConfigureApplicationPartManager(parts =>
{
    parts.FeatureProviders.Add(new InternalControllerFeatureProvider());
    foreach (var assembly in ModuleAssemblies.All)
    {
        parts.ApplicationParts.Add(new AssemblyPart(assembly));
    }
});
mvc.AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
});
builder.Services.AddProblemDetails();

// ── Security (security.md) ──────────────────────────────────────────────
var auth = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
if (auth.SigningKey.Length < 32)
{
    throw new InvalidOperationException("Auth:SigningKey must be at least 32 characters (set it in the secret store).");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = TokenService.ValidationParameters(auth);
    })

    // The live inbox (SignalR) connects from the browser straight to the API, where the BFF cookie cannot go.
    // It uses its own short-lived token with its own audience, valid for the hub only and useless for the rest of the API.
    .AddJwtBearer(HubTokens.Scheme, o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = HubTokens.ValidationParameters(auth);
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = c =>
            {
                if (c.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    c.Token = c.Request.Query["access_token"]; // WebSockets cannot carry an Authorization header.
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("customer", p => p.RequireClaim(RahiqClaims.Kind, "customer"));
    foreach (var permission in Permissions.All)
    {
        // Staff only, with the permission, and signed in with the second factor (security.md §1).
        o.AddPolicy(permission, p => p.RequireClaim(RahiqClaims.Kind, "staff").RequireClaim("amr", "mfa").RequireClaim(RahiqClaims.Permission, permission));
    }

    o.AddPolicy(HubTokens.Policy, p => p.AddAuthenticationSchemes(HubTokens.Scheme)
        .RequireClaim(RahiqClaims.Kind, "staff").RequireClaim("amr", "mfa").RequireClaim(RahiqClaims.Permission, Permissions.ConversationsView));
});

builder.Services.AddDataProtection().SetApplicationName("rahiq")
    .PersistKeysToFileSystem(new DirectoryInfo(configuration["DataProtection:KeysPath"] ?? "App_Data/keys"));

var rateLimitsOn = configuration.GetValue("RateLimits:Enabled", true); // Off only in the automated test host.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    void Policy(string name, int permits, TimeSpan window) => o.AddPolicy(name, c => rateLimitsOn
        ? RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window })
        : RateLimitPartition.GetNoLimiter(Ip(c)));

    Policy("otp", 5, TimeSpan.FromMinutes(15));
    Policy("login", 20, TimeSpan.FromMinutes(15));
    Policy("coupon", 10, TimeSpan.FromHours(1)); // security.md §5: 10 coupon attempts an hour.
    Policy("checkout", 30, TimeSpan.FromMinutes(10));
    Policy("tracking", 20, TimeSpan.FromMinutes(10));
    Policy("public-lookup", 120, TimeSpan.FromMinutes(1));
});

var web = configuration.GetSection(WebOptions.Section).Get<WebOptions>() ?? new WebOptions();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(web.AllowedOrigins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("X-Cart-Token")));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

// ── Caching (architecture.md §6): catalog reads only; stock and price are re-read at cart and pay ──
builder.Services.AddOutputCache(o => o.AddPolicy("catalog", p => p.Expire(TimeSpan.FromSeconds(60)).SetVaryByQuery("*").SetVaryByHeader("Accept-Language").Tag("catalog")));

// ── Health (devops.md): liveness and readiness for rolling deploys ─────
var health = builder.Services.AddHealthChecks();
health.AddNpgSql(configuration.GetConnectionString("Postgres")!, name: "postgres", tags: ["ready"]);
if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("Redis")))
{
    health.AddRedis(configuration.GetConnectionString("Redis")!, name: "redis", tags: ["ready"]);
}

var app = builder.Build();

if (await Cli.TryRunAsync(app, args))
{
    return;
}

if (configuration.GetValue("Database:MigrateOnStartup", false))
{
    await Cli.MigrateAsync(app.Services);
}

app.UseForwardedHeaders();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) => ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error : Serilog.Events.LogEventLevel.Information);
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();

app.MapControllers();
app.MapConversations();
app.MapHubTokens();
app.MapMedia();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapGet("/", () => Results.Ok(new { service = "rahiq-api", status = "ok" })).ExcludeFromDescription();

await app.RunAsync();

public partial class Program;

namespace Rahiq.Api
{
    public sealed record WebOptions
    {
        public const string Section = "Web";

        public string[] AllowedOrigins { get; init; } = ["http://localhost:3000"];

        /// <summary>Next.js on-demand revalidation endpoint (ISR tags, architecture.md §6).</summary>
        public string? RevalidateUrl { get; init; }

        public string? RevalidateSecret { get; init; }
    }
}
