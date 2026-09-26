using Rahiq.SharedKernel;

namespace Rahiq.Modules.Ordering.Contracts;

public static class OrderStatuses
{
    public const string PendingPayment = "pending_payment";
    public const string Confirmed = "confirmed";
    public const string Preparing = "preparing";
    public const string Shipped = "shipped";
    public const string Delivered = "delivered";
    public const string ReturnedToSender = "returned_to_sender";
    public const string ReturnRequested = "return_requested";
    public const string Returned = "returned";
    public const string Refunded = "refunded";
    public const string Cancelled = "cancelled";
}

public static class PaymentMethods
{
    public const string Card = "card";
    public const string CashOnDelivery = "cod";
}

/// <summary>A Turkish delivery address: province (il) and district (ilçe) from lists, then the street.</summary>
public sealed record AddressData
{
    public required string FullName { get; init; }

    public required string Phone { get; init; }

    public required int ProvinceCode { get; init; }

    public string ProvinceName { get; init; } = string.Empty;

    public required string District { get; init; }

    public string? Neighbourhood { get; init; }

    public required string Line1 { get; init; }

    public string? Line2 { get; init; }

    public string? PostalCode { get; init; }

    /// <summary>For company invoices (e-Fatura): title and tax number.</summary>
    public string? CompanyName { get; init; }

    public string? TaxOffice { get; init; }

    public string? TaxNumber { get; init; }

    public string Country { get; init; } = "TR";
}

public sealed record OrderBatch(Guid BatchId, string Code, int Qty);

public sealed record OrderLineInfo(
    Guid LineId,
    Guid VariantId,
    string Sku,
    string Name,
    string VariantLabel,
    string ProductType,
    int Qty,
    long UnitPrice,
    long Discount,
    long LineTotal,
    int TaxRateBp,
    long TaxAmount,
    string ShippingClass,
    string? GroupLabel,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<OrderBatch> Batches);

public sealed record OrderInfo
{
    public required Guid Id { get; init; }

    public required string Number { get; init; }

    public required string Status { get; init; }

    public Guid? CustomerId { get; init; }

    public required string Email { get; init; }

    public string? Phone { get; init; }

    public required string Locale { get; init; }

    public required string PaymentMethod { get; init; }

    public required string Currency { get; init; }

    public required long Subtotal { get; init; }

    public required long Discount { get; init; }

    public required long Shipping { get; init; }

    public required long CodFee { get; init; }

    public required long Tax { get; init; }

    public required long Total { get; init; }

    public required string ShippingMethod { get; init; }

    public required AddressData ShippingAddress { get; init; }

    public required AddressData BillingAddress { get; init; }

    public required bool IsGift { get; init; }

    public string? GiftMessage { get; init; }

    public required bool HidePrices { get; init; }

    public required DateTimeOffset PlacedAt { get; init; }

    public required IReadOnlyList<OrderLineInfo> Lines { get; init; }
}

public interface IOrderReader
{
    Task<OrderInfo?> GetAsync(Guid orderId, CancellationToken cancellationToken);

    Task<OrderInfo?> GetByNumberAsync(string number, CancellationToken cancellationToken);

    /// <summary>For the customer's data export (KVKK right of access).</summary>
    Task<IReadOnlyList<OrderInfo>> ForCustomerAsync(Guid customerId, CancellationToken cancellationToken);
}

public sealed record OrderPlaced(Guid OrderId, string Number, string PaymentMethod, long Total, string Currency) : DomainEvent;

public sealed record OrderConfirmed(Guid OrderId, string Number, string Email, string Locale, string PaymentMethod, long Total, string Currency) : DomainEvent;

public sealed record OrderCancelled(Guid OrderId, string Number, string Email, string Locale, string Reason, bool WasPaid) : DomainEvent;

public sealed record OrderPreparing(Guid OrderId, string Number) : DomainEvent;

public sealed record OrderShipped(Guid OrderId, string Number, string Email, string Locale, string Carrier, string TrackingNumber, string? TrackingUrl) : DomainEvent;

public sealed record OrderDelivered(Guid OrderId, string Number, string Email, string Locale, string PaymentMethod) : DomainEvent;

public sealed record OrderReturnedToSender(Guid OrderId, string Number, string Email, Guid? CustomerId, string PaymentMethod) : DomainEvent;

public sealed record ReturnRequested(Guid ReturnId, Guid OrderId, string Number, string Email, string Locale, string Kind) : DomainEvent;

public sealed record ReturnResolved(Guid ReturnId, Guid OrderId, string Number, string Email, string Locale, string Status) : DomainEvent;

/// <summary>A card payment succeeded but the order cannot be fulfilled (stock gone): the money must go back.</summary>
public sealed record OrderRefundRequired(Guid OrderId, string Number, long Amount, string Reason) : DomainEvent;
