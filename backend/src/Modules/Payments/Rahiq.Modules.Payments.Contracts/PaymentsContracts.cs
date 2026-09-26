using Rahiq.SharedKernel;

namespace Rahiq.Modules.Payments.Contracts;

public static class PaymentStatuses
{
    public const string Pending = "pending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Refunded = "refunded";
    public const string PartiallyRefunded = "partially_refunded";
}

/// <summary>A line sent to the provider's hosted form. The sum always equals the payment amount.</summary>
public sealed record PaymentBasketItem(string Id, string Name, string Category, long Price);

public sealed record PaymentBuyer(
    string Name,
    string Email,
    string Phone,
    string City,
    string Address,
    string? IdentityNumber,
    string Ip);

public sealed record PaymentRequest
{
    public required Guid OrderId { get; init; }

    public required string OrderNumber { get; init; }

    public required string Method { get; init; }

    public required long Amount { get; init; }

    public required string Currency { get; init; }

    public required string Locale { get; init; }

    public required PaymentBuyer Buyer { get; init; }

    public required IReadOnlyList<PaymentBasketItem> Basket { get; init; }
}

/// <param name="RedirectUrl">The provider's hosted page (card); null for cash on delivery.</param>
public sealed record PaymentStart(Guid PaymentId, string? RedirectUrl);

public sealed record PaymentStatusInfo(Guid PaymentId, string Method, string Status, long Amount, long RefundedAmount, int Installments, string Provider);

public sealed record RefundOutcome(Guid RefundId, long Amount, string Status);

public interface IPaymentGateway
{
    /// <summary>Records the pending payment inside the order's transaction (no network call).</summary>
    Task<Guid> RegisterAsync(Guid orderId, string orderNumber, string method, long amount, string currency, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the provider's hosted payment page, outside the stock transaction so no lock is held during the network
    /// call. On failure the caller compensates (cancels the order, releasing stock and coupon).
    /// </summary>
    Task<Result<PaymentStart>> OpenSessionAsync(PaymentRequest request, CancellationToken cancellationToken);

    /// <summary>Refunds through the provider (card) or records a manual refund (COD). Every call is logged.</summary>
    Task<Result<RefundOutcome>> RefundAsync(Guid orderId, long amount, string reason, Guid? requestedBy, CancellationToken cancellationToken);

    Task<PaymentStatusInfo?> GetAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>The payment window closed (cancelled order): later provider notifications are ignored or refunded.</summary>
    Task AbandonAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed record PaymentSucceeded(Guid PaymentId, Guid OrderId, long Amount, string Currency, int Installments, string Provider) : DomainEvent;

public sealed record PaymentFailed(Guid PaymentId, Guid OrderId, string Reason) : DomainEvent;

public sealed record RefundIssued(Guid RefundId, Guid OrderId, long Amount, string Currency) : DomainEvent;
