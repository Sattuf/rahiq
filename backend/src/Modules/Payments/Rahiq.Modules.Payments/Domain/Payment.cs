using Rahiq.Modules.Payments.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Payments.Domain;

internal static class PaymentErrors
{
    public static readonly Error AmountMismatch = Error.Conflict("payment.amount_mismatch", "The provider reported a different amount or currency.");
    public static readonly Error NotFound = Error.NotFound("payment.not_found", "Payment not found.");
    public static readonly Error RefundExceedsPaid = Error.Validation("refund.exceeds_paid", "The refund is more than what is left to refund.");
    public static readonly Error NotRefundable = Error.Conflict("refund.not_refundable", "Only a succeeded payment can be refunded.");
}

/// <summary>
/// One attempt to collect an order's total. Its status changes only from a verified provider notification
/// (ADR-006), and only when amount and currency match what the server computed (commerce-flows.md §5).
/// </summary>
internal sealed class Payment : AggregateRoot<Guid>
{
    private Payment()
    {
    }

    public Guid OrderId { get; private set; }

    public string OrderNumber { get; private set; } = string.Empty;

    public string Provider { get; private set; } = string.Empty;

    public string? ProviderRef { get; private set; }

    public string? SessionToken { get; private set; }

    public string Method { get; private set; } = "card";

    public long Amount { get; private set; }

    public long RefundedAmount { get; private set; }

    public string Currency { get; private set; } = "TRY";

    public int Installments { get; private set; } = 1;

    public string Status { get; private set; } = PaymentStatuses.Pending;

    public string? FailureReason { get; private set; }

    public string? RawLastEvent { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Refundable => Status is PaymentStatuses.Succeeded or PaymentStatuses.PartiallyRefunded ? Amount - RefundedAmount : 0;

    public static Payment Create(Guid orderId, string orderNumber, string provider, string method, long amount, string currency, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        OrderId = orderId,
        OrderNumber = orderNumber,
        Provider = provider,
        Method = method,
        Amount = amount,
        Currency = currency,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void AttachSession(string sessionToken, string? providerRef, DateTimeOffset now)
    {
        SessionToken = sessionToken;
        ProviderRef = providerRef ?? ProviderRef;
        UpdatedAt = now;
    }

    /// <returns>
    /// Success when the payment is now succeeded (idempotent: a repeat changes nothing and raises nothing);
    /// <see cref="PaymentErrors.AmountMismatch"/> when the notification must not confirm anything.
    /// </returns>
    public Result ApplySuccess(long amount, string currency, int installments, string? providerRef, string rawEvent, DateTimeOffset now)
    {
        if (Status == PaymentStatuses.Succeeded)
        {
            return Result.Success();
        }

        RawLastEvent = rawEvent;
        UpdatedAt = now;

        if (amount != Amount || !string.Equals(currency, Currency, StringComparison.OrdinalIgnoreCase))
        {
            FailureReason = $"amount_mismatch: expected {Amount} {Currency}, got {amount} {currency}";
            return PaymentErrors.AmountMismatch;
        }

        // A late success after the window closed is still real money: record it and let Ordering revive or refund.
        Status = PaymentStatuses.Succeeded;
        Installments = Math.Max(1, installments);
        ProviderRef = providerRef ?? ProviderRef;
        FailureReason = null;
        Raise(new PaymentSucceeded(Id, OrderId, Amount, Currency, Installments, Provider));
        return Result.Success();
    }

    public void ApplyFailure(string reason, string rawEvent, DateTimeOffset now)
    {
        if (Status != PaymentStatuses.Pending)
        {
            return;
        }

        Status = PaymentStatuses.Failed;
        FailureReason = reason;
        RawLastEvent = rawEvent;
        UpdatedAt = now;
        Raise(new PaymentFailed(Id, OrderId, reason));
    }

    /// <summary>The order was cancelled while waiting (reservation expired). No event: Ordering already knows.</summary>
    public void Abandon(DateTimeOffset now)
    {
        if (Status == PaymentStatuses.Pending && Method == "card")
        {
            Status = PaymentStatuses.Failed;
            FailureReason = "abandoned";
            UpdatedAt = now;
        }
    }

    /// <summary>Cash on delivery: collected by the carrier when the parcel is delivered.</summary>
    public void MarkCollected(DateTimeOffset now)
    {
        if (Method == "cod" && Status == PaymentStatuses.Pending)
        {
            Status = PaymentStatuses.Succeeded;
            UpdatedAt = now;
        }
    }

    public Result CanRefund(long amount)
    {
        if (Status is not (PaymentStatuses.Succeeded or PaymentStatuses.PartiallyRefunded))
        {
            return PaymentErrors.NotRefundable;
        }

        return amount <= 0 || amount > Refundable ? PaymentErrors.RefundExceedsPaid : Result.Success();
    }

    public void RecordRefund(long amount, DateTimeOffset now)
    {
        RefundedAmount += amount;
        Status = RefundedAmount >= Amount ? PaymentStatuses.Refunded : PaymentStatuses.PartiallyRefunded;
        UpdatedAt = now;
    }
}

internal sealed class Refund : AggregateRoot<Guid>
{
    private Refund()
    {
    }

    public Guid PaymentId { get; private set; }

    public Guid OrderId { get; private set; }

    public long Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public string Status { get; private set; } = "pending";

    public string? ProviderRef { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static Refund Request(Payment payment, long amount, string reason, Guid? requestedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return new Refund
        {
            Id = Ids.New(),
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            Amount = amount,
            Reason = reason,
            RequestedBy = requestedBy,
            ApprovedBy = requestedBy,
            CreatedAt = now,
        };
    }

    public void Succeed(string? providerRef, string currency, DateTimeOffset now)
    {
        Status = "succeeded";
        ProviderRef = providerRef;
        CompletedAt = now;
        Raise(new RefundIssued(Id, OrderId, Amount, currency));
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        Status = "failed";
        FailureReason = reason;
        CompletedAt = now;
    }
}
