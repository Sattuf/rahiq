using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Shipping.Contracts;
using Rahiq.Modules.Shipping.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Shipping.Infrastructure;

internal sealed class ProvinceRow
{
    public int Code { get; init; }

    public required string Name { get; init; }

    public required string ZoneCode { get; init; }
}

internal sealed class RateRow
{
    public required string ZoneCode { get; init; }

    public required string Method { get; init; }

    public int MaxWeightG { get; init; }

    public long Amount { get; set; }

    public string Currency { get; init; } = "TRY";
}

internal sealed class ShippingSettings
{
    public int Id { get; init; } = 1;

    public long? FreeShippingThreshold { get; set; }

    public string Currency { get; init; } = "TRY";
}

internal sealed class ShippingModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProvinceRow>(b =>
        {
            b.ToTable("provinces", "shipping");
            b.HasKey(p => p.Code);
            b.Property(p => p.Code).ValueGeneratedNever();
        });
        modelBuilder.Entity<RateRow>(b =>
        {
            b.ToTable("rates", "shipping");
            b.HasKey(r => new { r.ZoneCode, r.Method, r.MaxWeightG });
            b.Property(r => r.Currency).HasColumnType("char(3)");
        });
        modelBuilder.Entity<ShippingSettings>(b =>
        {
            b.ToTable("settings", "shipping");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.Currency).HasColumnType("char(3)");
        });
        modelBuilder.Entity<Shipment>(b =>
        {
            b.ToTable("shipments", "shipping");
            b.HasKey(s => s.Id);
            b.Ignore(s => s.DomainEvents);
            b.Property(s => s.Events).HasColumnType("jsonb");
        });
    }
}

internal sealed class ShippingRates(RahiqDbContext db) : IShippingRates
{
    public async Task<IReadOnlyList<ShippingRateOption>> OptionsAsync(int provinceCode, int weightG, Money subtotalAfterDiscount, bool hasFlammable, CancellationToken cancellationToken)
    {
        var province = await db.Set<ProvinceRow>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == provinceCode, cancellationToken);
        if (province is null)
        {
            return [];
        }

        var bands = await db.Set<RateRow>().AsNoTracking().Where(r => r.ZoneCode == province.ZoneCode).ToListAsync(cancellationToken);

        // Alcohol-based perfume travels by road only (compliance.md §3): no express/air methods for such parcels.
        var allowed = bands.Where(b => !hasFlammable || b.Method == "standard").Select(b => new RateBand(b.ZoneCode, b.Method, b.MaxWeightG, b.Amount)).ToList();
        return RateTable.Options(allowed, province.ZoneCode, weightG, subtotalAfterDiscount.Amount, await FreeShippingThresholdAsync(cancellationToken));
    }

    public async Task<long?> FreeShippingThresholdAsync(CancellationToken cancellationToken) =>
        (await db.Set<ShippingSettings>().AsNoTracking().FirstOrDefaultAsync(cancellationToken))?.FreeShippingThreshold;

    public async Task<IReadOnlyList<Province>> ProvincesAsync(CancellationToken cancellationToken) =>
        await db.Set<ProvinceRow>().AsNoTracking().OrderBy(p => p.Name).Select(p => new Province(p.Code, p.Name)).ToListAsync(cancellationToken);
}

public sealed record CarrierOptions
{
    public const string Section = "Shipping";

    /// <summary>"sandbox" until a carrier contract is signed (ADR-011).</summary>
    public string Carrier { get; init; } = "sandbox";

    public string WebhookSecret { get; init; } = "sandbox-carrier-secret-change-me";
}

internal sealed record CarrierLabel(string TrackingNumber, string? TrackingUrl, byte[] Label, string ContentType);

internal sealed record CarrierEvent(string TrackingNumber, string Status, string? Description, DateTimeOffset At);

internal interface ICarrierGateway
{
    string Name { get; }

    Task<Result<CarrierLabel>> CreateShipmentAsync(OrderInfo order, int weightG, CancellationToken cancellationToken);

    /// <returns>Null when the signature is not valid.</returns>
    IReadOnlyList<CarrierEvent>? ParseWebhook(string body, string? signature);
}

/// <summary>
/// Stand-in carrier for development and tests: real tracking numbers and printable labels, and a signed webhook
/// driven from /dev/shipping. Replaced by the contracted carrier's API adapter behind the same interface.
/// </summary>
internal sealed class SandboxCarrier(IOptions<CarrierOptions> options, IOptions<StoreOptions> store) : ICarrierGateway
{
    public string Name => "sandbox";

    public Task<Result<CarrierLabel>> CreateShipmentAsync(OrderInfo order, int weightG, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var tracking = $"SBX{RandomNumberGenerator.GetInt32(100_000_000, 999_999_999)}";
        var a = order.ShippingAddress;
        var fragile = order.Lines.Any(l => l.ShippingClass is "fragile" or "liquid");
        var flammable = order.Lines.Any(l => l.ShippingClass == "flammable");
        var cod = order.PaymentMethod == PaymentMethods.CashOnDelivery ? $" · KAPIDA ÖDEME {order.Total / 100m:0.00} {order.Currency}" : string.Empty;
        var warnings = (fragile ? "<div class=\"w\">KIRILACAK / SIVI — DİKKAT</div>" : string.Empty)
            + (flammable ? "<div class=\"w\">YANICI MADDE — SADECE KARAYOLU</div>" : string.Empty);
        string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var html = $$"""
            <!doctype html><html lang="tr"><meta charset="utf-8"><title>{{tracking}}</title>
            <style>body{font:14px system-ui;margin:0}.l{width:100mm;height:150mm;padding:6mm;box-sizing:border-box;border:1px solid #000}
            h1{font-size:22px;margin:0 0 4mm}.b{font:700 28px monospace;letter-spacing:2px;margin:4mm 0}.w{border:2px solid #000;padding:2mm;margin-top:3mm;font-weight:700}</style>
            <div class="l"><h1>RAHIQ · {{E(order.Number)}}</h1>
            <div class="b">{{tracking}}</div>
            <p><strong>{{E(a.FullName)}}</strong><br>{{E(a.Line1)}} {{E(a.Line2)}}<br>{{E(a.District)}} / {{E(a.ProvinceName)}}<br>{{E(a.Phone)}}</p>
            <p>{{weightG}} g · {{order.Lines.Sum(l => l.Qty)}} parça{{cod}}</p>
            {{warnings}}
            </div></html>
            """;
        var url = $"{store.Value.PublicWebUrl.TrimEnd('/')}/track?code={tracking}";
        return Task.FromResult<Result<CarrierLabel>>(new CarrierLabel(tracking, url, Encoding.UTF8.GetBytes(html), "text/html"));
    }

    public IReadOnlyList<CarrierEvent>? ParseWebhook(string body, string? signature)
    {
        if (!SignatureIsValid(body, signature, options.Value.WebhookSecret))
        {
            return null;
        }

        var events = JsonSerializer.Deserialize<List<CarrierEvent>>(body, JsonDefaults.Options);
        return events ?? [];
    }

    public static string Sign(string body, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static bool SignatureIsValid(string body, string? signature, string secret) =>
        signature is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(body, secret)), Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
}
