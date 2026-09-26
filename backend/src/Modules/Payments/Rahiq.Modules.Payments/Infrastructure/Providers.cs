using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Payments.Application;
using Rahiq.Modules.Payments.Contracts;
using Rahiq.Modules.Payments.Domain;

namespace Rahiq.Modules.Payments.Infrastructure;

public sealed record PaymentOptions
{
    public const string Section = "Payments";

    /// <summary>"sandbox" (development, tests, staging without a contract) or "iyzico".</summary>
    public string Provider { get; init; } = "sandbox";

    public string SandboxSecret { get; init; } = "sandbox-payment-secret-change-me";

    public string IyzicoBaseUrl { get; init; } = "https://sandbox-api.iyzipay.com";

    public string? IyzicoApiKey { get; init; }

    public string? IyzicoSecretKey { get; init; }

    public int[] EnabledInstallments { get; init; } = [1, 2, 3, 6, 9];
}

internal sealed record ProviderSession(string Token, string RedirectUrl, string? ProviderRef);

/// <summary>A verified statement from the provider about one payment. Only these change a payment (ADR-006).</summary>
internal sealed record ProviderNotification(
    string EventId,
    string SessionToken,
    bool Succeeded,
    long Amount,
    string Currency,
    int Installments,
    string? ProviderRef,
    string? FailureReason,
    string Raw);

internal sealed record ProviderInbound(string Body, IReadOnlyDictionary<string, string> Form, IReadOnlyDictionary<string, string> Headers);

internal sealed record ProviderTransaction(string ProviderRef, string OrderNumber, long Amount, string Currency);

internal interface IPaymentProvider
{
    string Name { get; }

    /// <summary>True when the provider's callback is the buyer's browser (answer with a redirect, not JSON).</summary>
    bool CallbackIsBrowser { get; }

    Task<ProviderSession> CreateSessionAsync(Payment payment, PaymentRequest request, string callbackUrl, CancellationToken cancellationToken);

    /// <returns>Null when the notification cannot be verified (bad signature, unknown token).</returns>
    Task<ProviderNotification?> VerifyAsync(ProviderInbound inbound, CancellationToken cancellationToken);

    /// <summary>Asks the provider directly (for payments whose notification never came).</summary>
    Task<ProviderNotification?> RetrieveAsync(Payment payment, CancellationToken cancellationToken);

    Task<string> RefundAsync(Payment payment, long amount, string ip, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderTransaction>> TransactionsAsync(DateOnly day, CancellationToken cancellationToken);
}

/// <summary>
/// The sandbox plays a card provider end to end: a hosted page, 3-D-Secure-like confirmation, and a notification
/// signed with HMAC-SHA256 that goes through the real webhook endpoint.
/// </summary>
internal sealed class SandboxPaymentProvider(IOptions<PaymentOptions> options, IOptions<StoreOptions> store, IHttpClientFactory http, RahiqDbContext db) : IPaymentProvider
{
    public const string SignatureHeader = "X-Sandbox-Signature";

    public string Name => "sandbox";

    public bool CallbackIsBrowser => false;

    public Task<ProviderSession> CreateSessionAsync(Payment payment, PaymentRequest request, string callbackUrl, CancellationToken cancellationToken)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return Task.FromResult(new ProviderSession(token, $"{store.Value.PublicApiUrl.TrimEnd('/')}/dev/payments/sandbox/{token}", $"sbx_{payment.Id:N}"));
    }

    public Task<ProviderNotification?> VerifyAsync(ProviderInbound inbound, CancellationToken cancellationToken)
    {
        var signature = inbound.Headers.GetValueOrDefault(SignatureHeader.ToLowerInvariant());
        if (signature is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(inbound.Body, options.Value.SandboxSecret)), Encoding.ASCII.GetBytes(signature.ToLowerInvariant())))
        {
            return Task.FromResult<ProviderNotification?>(null);
        }

        var body = JsonSerializer.Deserialize<SandboxNotification>(inbound.Body, JsonDefaults.Options)!;
        return Task.FromResult<ProviderNotification?>(new ProviderNotification(
            body.EventId, body.Token, body.Outcome == "succeeded", body.Amount, body.Currency, body.Installments, body.ProviderRef, body.Outcome == "succeeded" ? null : body.Reason ?? "declined", inbound.Body));
    }

    public Task<ProviderNotification?> RetrieveAsync(Payment payment, CancellationToken cancellationToken) =>
        Task.FromResult<ProviderNotification?>(null); // The sandbox only speaks through its webhook.

    public Task<string> RefundAsync(Payment payment, long amount, string ip, CancellationToken cancellationToken) =>
        Task.FromResult($"sbx_refund_{Guid.NewGuid():N}");

    public async Task<IReadOnlyList<ProviderTransaction>> TransactionsAsync(DateOnly day, CancellationToken cancellationToken)
    {
        // The sandbox's "settlement report" is the successful notifications it sent that day.
        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = from.AddDays(1);
        var events = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.Set<WebhookEventRow>().Where(e => e.Provider == Name && e.ReceivedAt >= from && e.ReceivedAt < to && e.Outcome == "succeeded"), cancellationToken);
        var result = new List<ProviderTransaction>();
        foreach (var e in events)
        {
            var n = JsonSerializer.Deserialize<SandboxNotification>(e.Payload, JsonDefaults.Options)!;
            var payment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(db.Set<Payment>(), p => p.SessionToken == n.Token, cancellationToken);
            result.Add(new ProviderTransaction(n.ProviderRef ?? n.Token, payment?.OrderNumber ?? "?", n.Amount, n.Currency));
        }

        return result;
    }

    /// <summary>Used by the sandbox page (and tests) to send a notification exactly as a provider would.</summary>
    public async Task SendAsync(SandboxNotification notification, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(notification, JsonDefaults.Options);
        using var client = http.CreateClient("sandbox-webhook");
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add(SignatureHeader, Sign(body, options.Value.SandboxSecret));
        using var response = await client.PostAsync(new Uri($"{store.Value.PublicApiUrl.TrimEnd('/')}/webhooks/payments/sandbox"), content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static string Sign(string body, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}

public sealed record SandboxNotification(string EventId, string Token, string Outcome, long Amount, string Currency, int Installments, string? ProviderRef, string? Reason);

/// <summary>
/// iyzico Checkout Form (hosted, 3-D Secure, instalments). The browser returns to our callback with a token; we then
/// ask iyzico server-to-server (signed with our secret key) what really happened. That authenticated answer is the
/// source of truth, so a forged callback can change nothing.
/// NOTE: written against iyzico's public API documentation; it must pass the full sandbox run in docs/gates before go-live.
/// </summary>
internal sealed class IyzicoPaymentProvider(IOptions<PaymentOptions> options, IHttpClientFactory http, RahiqDbContext db) : IPaymentProvider
{
    public string Name => "iyzico";

    public bool CallbackIsBrowser => true;

    public async Task<ProviderSession> CreateSessionAsync(Payment payment, PaymentRequest request, string callbackUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var b = request.Buyer;
        var (name, surname) = SplitName(b.Name);
        var address = new JsonObject { ["contactName"] = b.Name, ["city"] = b.City, ["country"] = "Turkey", ["address"] = b.Address };
        var body = new JsonObject
        {
            ["locale"] = request.Locale == "tr" ? "tr" : "en",
            ["conversationId"] = request.OrderNumber,
            ["price"] = Amount(request.Basket.Sum(i => i.Price)),
            ["paidPrice"] = Amount(request.Amount),
            ["currency"] = request.Currency,
            ["basketId"] = request.OrderNumber,
            ["paymentGroup"] = "PRODUCT",
            ["callbackUrl"] = callbackUrl,
            ["enabledInstallments"] = new JsonArray([.. options.Value.EnabledInstallments.Select(i => (JsonNode)i)]),
            ["buyer"] = new JsonObject
            {
                ["id"] = b.Email,
                ["name"] = name,
                ["surname"] = surname,
                ["gsmNumber"] = b.Phone,
                ["email"] = b.Email,
                ["identityNumber"] = b.IdentityNumber ?? "11111111111", // iyzico accepts this placeholder when TCKN is not collected.
                ["registrationAddress"] = b.Address,
                ["ip"] = b.Ip,
                ["city"] = b.City,
                ["country"] = "Turkey",
            },
            ["shippingAddress"] = address,
            ["billingAddress"] = address.DeepClone(),
            ["basketItems"] = new JsonArray([.. request.Basket.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id,
                ["name"] = i.Name,
                ["category1"] = i.Category,
                ["itemType"] = "PHYSICAL",
                ["price"] = Amount(i.Price),
            })]),
        };

        var response = await Post("/payment/iyzipos/checkoutform/initialize/auth/ecom", body, cancellationToken);
        if (response["status"]?.GetValue<string>() != "success")
        {
            throw new InvalidOperationException($"iyzico initialize failed: {response["errorMessage"]}");
        }

        return new ProviderSession(response["token"]!.GetValue<string>(), response["paymentPageUrl"]!.GetValue<string>(), null);
    }

    public async Task<ProviderNotification?> VerifyAsync(ProviderInbound inbound, CancellationToken cancellationToken)
    {
        var token = inbound.Form.GetValueOrDefault("token");
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var payment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(db.Set<Payment>(), p => p.Provider == Name && p.SessionToken == token, cancellationToken);
        return payment is null ? null : await RetrieveAsync(payment, cancellationToken);
    }

    public async Task<ProviderNotification?> RetrieveAsync(Payment payment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var response = await Post("/payment/iyzipos/checkoutform/auth/ecom/detail", new JsonObject
        {
            ["locale"] = "tr",
            ["conversationId"] = payment.OrderNumber,
            ["token"] = payment.SessionToken,
        }, cancellationToken);

        if (response["status"]?.GetValue<string>() != "success")
        {
            return null;
        }

        var paymentStatus = response["paymentStatus"]?.GetValue<string>();
        if (paymentStatus is null or "INIT_THREEDS" or "CALLBACK_THREEDS")
        {
            return null; // Not finished yet.
        }

        var succeeded = paymentStatus == "SUCCESS" && response["fraudStatus"]?.GetValue<int>() != -1;
        var paymentId = response["paymentId"]?.ToString();
        return new ProviderNotification(
            $"{paymentId ?? payment.SessionToken}:{paymentStatus}",
            payment.SessionToken!,
            succeeded,
            ToMinor(response["paidPrice"]),
            response["currency"]?.GetValue<string>() ?? payment.Currency,
            response["installment"]?.GetValue<int>() ?? 1,
            paymentId,
            succeeded ? null : response["errorMessage"]?.GetValue<string>() ?? paymentStatus,
            response.ToJsonString());
    }

    public async Task<string> RefundAsync(Payment payment, long amount, string ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var response = await Post("/v2/payment/refund", new JsonObject
        {
            ["locale"] = "tr",
            ["conversationId"] = payment.OrderNumber,
            ["paymentId"] = payment.ProviderRef,
            ["price"] = Amount(amount),
            ["currency"] = payment.Currency,
            ["ip"] = ip,
        }, cancellationToken);

        return response["status"]?.GetValue<string>() == "success"
            ? response["paymentTransactionId"]?.ToString() ?? response["paymentId"]?.ToString() ?? "iyzico"
            : throw new InvalidOperationException($"iyzico refund failed: {response["errorMessage"]}");
    }

    public Task<IReadOnlyList<ProviderTransaction>> TransactionsAsync(DateOnly day, CancellationToken cancellationToken) =>
        // iyzico's settlement report API ("reporting/settlement") is enabled per merchant; wired when the contract is signed.
        Task.FromResult<IReadOnlyList<ProviderTransaction>>([]);

    /// <summary>IYZWSv2: HMAC-SHA256 over randomKey + path + body with the secret key.</summary>
    private async Task<JsonNode> Post(string path, JsonObject body, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var json = body.ToJsonString();
        var randomKey = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{RandomNumberGenerator.GetInt32(100_000, 999_999)}";
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(o.IyzicoSecretKey!), Encoding.UTF8.GetBytes(randomKey + path + json))).ToLowerInvariant();
        var authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes($"apiKey:{o.IyzicoApiKey}&randomKey:{randomKey}&signature:{signature}"));

        using var client = http.CreateClient("iyzico");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(o.IyzicoBaseUrl.TrimEnd('/') + path));
        request.Headers.TryAddWithoutValidation("Authorization", $"IYZWSv2 {authorization}");
        request.Headers.TryAddWithoutValidation("x-iyzi-rnd", randomKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken))!;
    }

    private static string Amount(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static long ToMinor(JsonNode? value) =>
        value is null ? 0 : (long)Math.Round(decimal.Parse(value.ToString(), CultureInfo.InvariantCulture) * 100m, MidpointRounding.AwayFromZero);

    private static (string Name, string Surname) SplitName(string full)
    {
        var parts = full.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? (full.Trim(), full.Trim()) : (string.Join(' ', parts[..^1]), parts[^1]);
    }
}
