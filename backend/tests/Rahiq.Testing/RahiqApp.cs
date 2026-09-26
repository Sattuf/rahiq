using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Outbox;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Application;
using Rahiq.Modules.Inventory.Application;
using Rahiq.Modules.Pricing.Application;
using Rahiq.SharedKernel;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rahiq.Testing;

/// <summary>Time the tests control: reservations expire when the test says so.</summary>
public sealed class TestClock : IClock
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public void Set(DateTimeOffset at) => _now = at;
}

/// <summary>
/// One real PostgreSQL per test run: RAHIQ_TEST_POSTGRES when set (CI service, a local server), otherwise a
/// Testcontainers container. Each fixture gets its own freshly migrated database (testing.md §1: never a fake database).
/// </summary>
public static class TestDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _server;
    private static PostgreSqlContainer? _container;

    public static async Task<string> CreateAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_server is null)
            {
                var fromEnv = Environment.GetEnvironmentVariable("RAHIQ_TEST_POSTGRES");
                if (!string.IsNullOrWhiteSpace(fromEnv))
                {
                    _server = fromEnv;
                }
                else
                {
                    _container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").WithCommand("-c", "max_connections=300").Build();
                    await _container.StartAsync();
                    _server = _container.GetConnectionString();
                }
            }
        }
        finally
        {
            Gate.Release();
        }

        var builder = new NpgsqlConnectionStringBuilder(_server) { Database = $"rahiq_test_{Guid.NewGuid():N}", MaxPoolSize = 120 };
        await SqlMigrator.EnsureDatabaseAsync(builder.ConnectionString);
        return builder.ConnectionString;
    }
}

public sealed class RahiqApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SandboxSecret = "test-sandbox-secret";
    public const string TelegramSecret = "test-telegram-secret-0123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rahiq-tests", Guid.NewGuid().ToString("N"));

    public string ConnectionString { get; private set; } = string.Empty;

    public TestClock Clock { get; } = new();

    public OtpInbox Otp { get; } = new();

    public async Task InitializeAsync()
    {
        ConnectionString = await TestDatabase.CreateAsync();
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SqlMigrator>().MigrateAsync(typeof(Program).Assembly);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = ConnectionString,
            ["ConnectionStrings:Redis"] = string.Empty,
            ["Auth:SigningKey"] = "test-signing-key-0123456789-0123456789-abcdef",
            ["Auth:CheckBreachedPasswords"] = "false",
            ["Workers:Enabled"] = "false",
            ["RateLimits:Enabled"] = "false",
            ["Email:Transport"] = "log",
            ["Storage:Provider"] = "filesystem",
            ["Storage:FileSystemRoot"] = Path.Combine(_root, "blobs"),
            ["DataProtection:KeysPath"] = Path.Combine(_root, "keys"),
            ["Payments:Provider"] = "sandbox",
            ["Payments:SandboxSecret"] = SandboxSecret,
            ["Web:RevalidateUrl"] = string.Empty,
            ["Store:PublicApiUrl"] = "http://localhost",
            ["Serilog:MinimumLevel:Default"] = "Warning",
            ["Channels:Telegram:BotToken"] = "0:test",
            ["Channels:Telegram:WebhookSecret"] = TelegramSecret,
            ["Agent:Enabled"] = "false",
            ["Agent:DebounceSeconds"] = "0",
        };
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton(Otp);
            services.AddScoped<Modules.Customers.Contracts.IOtpDelivery>(_ => Otp);
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    public async Task<T> Send<T>(IRequest<T> request)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    /// <summary>A signed-in owner (password + TOTP), as the admin panel's BFF would be.</summary>
    public async Task<HttpClient> StaffClientAsync(HttpClient? client = null)
    {
        var email = $"owner-{Guid.NewGuid():N}@rahiq.local";
        const string password = "a-long-owner-passphrase-2026";
        Ok(await Send(new Modules.Customers.Application.BootstrapOwnerCommand(email, "Owner", password)));
        client ??= CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/staff/login", new { email, password })).Content.ReadFromJsonAsync<JsonObject>();
        var ticket = login!["enrollmentTicket"]!.GetValue<string>();
        var enrollment = await (await client.PostAsJsonAsync("/api/auth/staff/totp/start", new { ticket })).Content.ReadFromJsonAsync<JsonObject>();
        var code = Totp(enrollment!["secret"]!.GetValue<string>(), Clock.UtcNow);
        var confirmed = await (await client.PostAsJsonAsync("/api/auth/staff/totp/confirm", new { ticket, code })).Content.ReadFromJsonAsync<JsonObject>();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", confirmed!["tokens"]!["accessToken"]!.GetValue<string>());
        return client;
    }

    /// <summary>RFC 6238 code for a base32 secret.</summary>
    public static string Totp(string base32Secret, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(base32Secret);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var key = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in base32Secret.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                key.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        var message = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, at.ToUnixTimeSeconds() / 30);
#pragma warning disable CA5350 // RFC 6238.
        var hash = System.Security.Cryptography.HMACSHA1.HashData(key.ToArray(), message);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Delivers all committed events, as the background worker would.</summary>
    public async Task DrainOutboxAsync() => await Services.GetRequiredService<OutboxProcessor>().DrainAsync();

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<T> Scalar<T>(string sql, object? args = null)
    {
        await using var c = await OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? default!;
    }

    public async Task Exec(string sql, object? args = null)
    {
        await using var c = await OpenAsync();
        await c.ExecuteAsync(sql, args);
    }

    /// <summary>A published product with one variant, a price and stock, ready to sell. Returns the variant id.</summary>
    public async Task<Guid> CreateSellableAsync(string type, long price, params (string Code, int Qty, DateOnly? BestBefore)[] batches)
    {
        var slug = $"{type.Replace("_", "-", StringComparison.Ordinal)}-{Guid.NewGuid():N}";
        var id = Ok(await Send(new CreateProductCommand(type, slug)));
        var version = Ok(await Send(new AdminGetProductQuery(id))).Version;
        var isHoney = type is "honey" or "comb_honey";
        var attributes = type switch
        {
            "perfume" => """{"concentration":"edp","inci":["ALCOHOL DENAT.","PARFUM"]}""",
            _ => """{"floralSource":"chestnut"}""",
        };
        Ok(await Send(new UpdateProductCommand(id, version, slug,
            [new("tr", "Test ürün", null, null, null, null, null), new("ar", "منتج تجريبي", null, null, null, null, null), new("en", "Test product", null, null, null, null, null)],
            JsonDocument.Parse(attributes).RootElement, [], [], false, 0)));
        var sku = $"T-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var variant = Ok(await Send(new UpsertVariantCommand(id, null, isHoney
            ? new VariantInput(sku, null, 500, null, 700, "liquid", false, true, 0)
            : new VariantInput(sku, 50, null, null, 400, "flammable", false, true, 0))));
        OkResult(await Send(new SetPriceCommand(variant, price)));
        OkResult(await Send(new PublishProductCommand(id, false, null)));
        foreach (var (code, qty, bestBefore) in batches)
        {
            Ok(await Send(new ReceiveBatchCommand(variant, code, qty, null, bestBefore, null, null)));
        }

        return variant;
    }

    public Task<Guid> CreateHoneyAsync(long price, int qty) => CreateSellableAsync("honey", price, ("B1", qty, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(2)));

    /// <summary>A guest with a cart holding the given items, and a checkout with a complete address.</summary>
    public async Task<Guest> GuestWithCheckoutAsync(params (Guid VariantId, int Qty)[] items) => await GuestWithCheckoutAsync(null, items);

    public async Task<Guest> GuestWithCheckoutAsync(string? coupon, params (Guid VariantId, int Qty)[] items)
    {
        var client = CreateClient();
        string? token = null;
        foreach (var (variant, qty) in items)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cart/lines") { Content = JsonContent.Create(new { variantId = variant, qty }) };
            if (token is not null)
            {
                request.Headers.Add("X-Cart-Token", token);
            }

            using var response = await client.SendAsync(request);
            await EnsureOk(response);
            token ??= response.Headers.GetValues("X-Cart-Token").First();
        }

        client.DefaultRequestHeaders.Add("X-Cart-Token", token);
        if (coupon is not null)
        {
            await EnsureOk(await client.PutAsJsonAsync("/api/cart/coupon", new { code = coupon }));
        }

        var checkoutId = await (await client.PostAsJsonAsync("/api/checkout", new { })).Content.ReadFromJsonAsync<Guid>();
        var email = $"guest-{Guid.NewGuid():N}@example.com";
        var address = new { fullName = "Test Alıcı", phone = "+905551112233", provinceCode = 6, district = "Çankaya", line1 = "Atatürk Blv. 1" };
        await EnsureOk(await client.PutAsJsonAsync($"/api/checkout/{checkoutId}", new { email, phone = "+905551112233", shippingAddress = address, isGift = false, hidePrices = false, marketingConsent = false }));
        return new Guest(client, checkoutId, email);
    }

    public sealed record Guest(HttpClient Client, Guid CheckoutId, string Email);

    public static async Task<string> ContractsHashAsync(Guest guest, string method)
    {
        var view = await guest.Client.GetFromJsonAsync<JsonObject>($"/api/checkout/{guest.CheckoutId}?paymentMethod={method}");
        return view!["contracts"]!["hash"]!.GetValue<string>();
    }

    public static async Task<HttpResponseMessage> PayAsync(Guest guest, string method, string? hash = null, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(guest);
        hash ??= await ContractsHashAsync(guest, method);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/checkout/{guest.CheckoutId}/pay")
        {
            Content = JsonContent.Create(new { paymentMethod = method, acceptedContractsHash = hash }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await guest.Client.SendAsync(request);
    }

    /// <summary>A notification exactly as the sandbox provider sends it, correctly (or wrongly) signed.</summary>
    public async Task<HttpResponseMessage> SandboxWebhookAsync(string sessionToken, string outcome, long amount, string? eventId = null, string? secret = null)
    {
        var body = JsonSerializer.Serialize(new { eventId = eventId ?? $"evt_{Guid.NewGuid():N}", token = sessionToken, outcome, amount, currency = "TRY", installments = 1, providerRef = (string?)null, reason = (string?)null }, JsonDefaults.Options);
        var signature = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret ?? SandboxSecret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Sandbox-Signature", signature);
        return await CreateClient().PostAsync(new Uri("/webhooks/payments/sandbox", UriKind.Relative), content);
    }

    public Task<string> SessionTokenAsync(string orderNumber) =>
        Scalar<string>("SELECT session_token FROM payments.payments WHERE order_number = @orderNumber", new { orderNumber });

    public Task<string> OrderStatusAsync(string orderNumber) =>
        Scalar<string>("SELECT status FROM ordering.orders WHERE number = @orderNumber", new { orderNumber });

    public static async Task<string> OrderNumberAsync(HttpResponseMessage payResponse)
    {
        ArgumentNullException.ThrowIfNull(payResponse);
        await EnsureOk(payResponse);
        return (await payResponse.Content.ReadFromJsonAsync<JsonObject>())!["number"]!.GetValue<string>();
    }

    public static async Task EnsureOk(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    public static T Ok<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"{result.Error.Code}: {result.Error.Message} {JsonSerializer.Serialize(result.Error.Details)}");

    public static void OkResult(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"{result.Error.Code}: {result.Error.Message} {JsonSerializer.Serialize(result.Error.Details)}");
        }
    }
}

/// <summary>Catches sign-in codes in tests instead of e-mailing them.</summary>
public sealed class OtpInbox : Modules.Customers.Contracts.IOtpDelivery
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _codes = new(StringComparer.OrdinalIgnoreCase);

    public Task SendAsync(string email, string code, string locale, CancellationToken cancellationToken)
    {
        _codes[email] = code;
        return Task.CompletedTask;
    }

    public string CodeFor(string email) => _codes[email];
}
