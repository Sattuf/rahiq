using Rahiq.SharedKernel;

namespace Rahiq.Modules.Pricing.Contracts;

/// <param name="PreviousPrice">
/// Shown struck-through only when the current price is lower than the lowest price of the previous 30 days,
/// and it is always that lowest price, never a hand-typed "was" price (compliance.md §3).
/// </param>
public sealed record PriceInfo(Guid VariantId, Money Price, Money? PreviousPrice);

public interface IPriceReader
{
    Task<IReadOnlyDictionary<Guid, PriceInfo>> GetCurrentAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

/// <param name="GiftBoxComponents">For a gift box: the chosen variant per slot.</param>
public sealed record QuoteItem(Guid VariantId, int Qty, IReadOnlyList<Guid>? GiftBoxComponents = null, string? GiftMessage = null);

public sealed record QuoteRequest
{
    public required IReadOnlyList<QuoteItem> Items { get; init; }

    public required string Locale { get; init; }

    public string? CouponCode { get; init; }

    /// <summary>Customer id or e-mail, for per-customer coupon limits.</summary>
    public string? CustomerKey { get; init; }

    public int? ProvinceCode { get; init; }

    public string? ShippingMethod { get; init; }

    /// <summary>"card" or "cod"; COD adds its fee.</summary>
    public string? PaymentMethod { get; init; }
}

/// <summary>A priced, taxed line exactly as it will be frozen into the order (Law 6).</summary>
public sealed record QuoteLine
{
    public required int ItemIndex { get; init; }

    public required Guid VariantId { get; init; }

    public required Guid ProductId { get; init; }

    public required string Sku { get; init; }

    public required string Section { get; init; }

    public required string ProductType { get; init; }

    public required string Name { get; init; }

    public required string VariantLabel { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required bool IsSample { get; init; }

    public required string ShippingClass { get; init; }

    public Guid? GroupId { get; init; }

    public string? GroupLabel { get; init; }

    public required long UnitPrice { get; init; }

    public required int Qty { get; init; }

    public required long Discount { get; init; }

    public required int TaxRateBp { get; init; }

    public required long TaxAmount { get; init; }

    public required long LineTotal { get; init; }

    /// <summary>Withdrawal return possible (cosmetics with the seal intact). Food never, except damaged or wrong.</summary>
    public required bool Returnable { get; init; }
}

public sealed record QuoteShippingOption(string Method, long Amount, bool IsFree);

public sealed record QuoteProblem(Guid VariantId, string Code);

public sealed record Quote
{
    public required string Currency { get; init; }

    public required IReadOnlyList<QuoteLine> Lines { get; init; }

    /// <summary>Σ unit price × qty, before any discount.</summary>
    public required long Subtotal { get; init; }

    /// <summary>Bundle savings + coupon discount, allocated to lines.</summary>
    public required long Discount { get; init; }

    public required long CouponDiscount { get; init; }

    public required long Shipping { get; init; }

    public required long CodFee { get; init; }

    /// <summary>Tax contained in the total (prices are tax-inclusive).</summary>
    public required long Tax { get; init; }

    public required long Total { get; init; }

    public required int ShippingWeightG { get; init; }

    public string? AppliedCoupon { get; init; }

    public string? CouponError { get; init; }

    public required bool FreeShippingApplied { get; init; }

    public long? FreeShippingThreshold { get; init; }

    public required IReadOnlyList<QuoteShippingOption> ShippingOptions { get; init; }

    public required IReadOnlyList<QuoteProblem> Problems { get; init; }

    public required bool CodAvailable { get; init; }

    public bool HasFlammable => Lines.Any(l => l.ShippingClass == "flammable");
}

public interface IQuoteService
{
    Task<Quote> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken);
}

/// <summary>Coupon usage follows the stock pattern: held at pay, committed on confirmation, released on failure (ADR-016).</summary>
public interface ICouponUsage
{
    Task<Result> HoldAsync(string code, Guid orderId, string customerKey, CancellationToken cancellationToken);

    Task CommitAsync(Guid orderId, CancellationToken cancellationToken);

    Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed record PriceChanged(IReadOnlyList<Guid> VariantIds) : DomainEvent;
