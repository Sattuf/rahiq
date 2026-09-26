using Rahiq.Modules.Ordering.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Ordering.Domain;

internal static class OrderErrors
{
    public static Error InvalidTransition(string from, string to) =>
        Error.Conflict("order.invalid_transition", $"An order in '{from}' cannot move to '{to}'.");

    public static readonly Error TotalsMismatch = new("order.totals_mismatch", "Order totals do not add up.");
    public static readonly Error Empty = Error.Validation("order.empty", "An order needs at least one line.");
    public static readonly Error NotFound = Error.NotFound("order.not_found", "Order not found.");
}

public static class CancelReasons
{
    public const string PaymentExpired = "payment_expired";
    public const string PaymentFailed = "payment_failed";
    public const string Customer = "customer";
    public const string Staff = "staff";
    public const string OutOfStock = "out_of_stock";
}

internal sealed record OrderLineDraft(
    Guid VariantId,
    Guid ProductId,
    string Sku,
    string Section,
    string ProductType,
    string Name,
    string VariantLabel,
    IReadOnlyList<string> Warnings,
    bool IsSample,
    string ShippingClass,
    Guid? GroupId,
    string? GroupLabel,
    long UnitPrice,
    int Qty,
    long Discount,
    int TaxRateBp,
    long TaxAmount,
    long LineTotal,
    bool Returnable);

internal sealed record OrderDraft
{
    public required string Number { get; init; }

    public required Guid CheckoutId { get; init; }

    public Guid? CustomerId { get; init; }

    public required string Email { get; init; }

    public string? Phone { get; init; }

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

    public bool IsGift { get; init; }

    public string? GiftMessage { get; init; }

    public bool HidePrices { get; init; }

    public string? CouponCode { get; init; }

    public required string Locale { get; init; }

    public required string ContractHash { get; init; }

    public string? IdempotencyKey { get; init; }

    public DateTimeOffset? ReservationExpiresAt { get; init; }

    public IReadOnlyList<string> FraudFlags { get; init; } = [];

    public required IReadOnlyList<OrderLineDraft> Lines { get; init; }
}

/// <summary>
/// The frozen snapshot of a purchase (Law 6). Names, prices, taxes and warnings are copied at placement and never
/// re-read from the catalog. Status moves only through the methods below (commerce-flows.md §4).
/// </summary>
internal sealed class Order : AggregateRoot<Guid>
{
    /// <summary>Every allowed transition, in one place.</summary>
    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [OrderStatuses.PendingPayment] = [OrderStatuses.Confirmed, OrderStatuses.Cancelled],
        [OrderStatuses.Confirmed] = [OrderStatuses.Preparing, OrderStatuses.Cancelled],
        [OrderStatuses.Preparing] = [OrderStatuses.Shipped],
        [OrderStatuses.Shipped] = [OrderStatuses.Delivered, OrderStatuses.ReturnedToSender],
        [OrderStatuses.Delivered] = [OrderStatuses.ReturnRequested],
        [OrderStatuses.ReturnRequested] = [OrderStatuses.Returned, OrderStatuses.Delivered],
        [OrderStatuses.Returned] = [OrderStatuses.Refunded],
        [OrderStatuses.ReturnedToSender] = [OrderStatuses.Refunded],
        [OrderStatuses.Cancelled] = [],
        [OrderStatuses.Refunded] = [],
    };

    private readonly List<OrderLine> _lines = [];
    private readonly List<OrderStatusChange> _history = [];

    private Order()
    {
    }

    public string Number { get; private set; } = string.Empty;

    public Guid CheckoutId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    public string Status { get; private set; } = OrderStatuses.PendingPayment;

    public string PaymentMethod { get; private set; } = PaymentMethods.Card;

    public string Currency { get; private set; } = "TRY";

    public long Subtotal { get; private set; }

    public long Discount { get; private set; }

    public long Shipping { get; private set; }

    public long CodFee { get; private set; }

    public long Tax { get; private set; }

    public long Total { get; private set; }

    public string ShippingMethod { get; private set; } = string.Empty;

    public AddressData ShippingAddress { get; private set; } = null!;

    public AddressData BillingAddress { get; private set; } = null!;

    public bool IsGift { get; private set; }

    public string? GiftMessage { get; private set; }

    public bool HidePrices { get; private set; }

    public string? CouponCode { get; private set; }

    public string Locale { get; private set; } = Locales.Default;

    public string ContractDocHash { get; private set; } = string.Empty;

    public List<string> FraudFlags { get; private set; } = [];

    public DateTimeOffset PlacedAt { get; private set; }

    public DateTimeOffset? ReservationExpiresAt { get; private set; }

    public string? IdempotencyKey { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public IReadOnlyList<OrderStatusChange> History => _history;

    public string? CancelReason => _history.LastOrDefault(h => h.Status == OrderStatuses.Cancelled)?.Note;

    public bool IsPaidByCard => PaymentMethod == PaymentMethods.Card;

    public static Result<Order> Place(OrderDraft draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Lines.Count == 0)
        {
            return OrderErrors.Empty;
        }

        // Independent re-check of the quote arithmetic: the database CHECKs are the last line, this is the first.
        var linesOk = draft.Lines.All(l => l.Qty > 0 && l.LineTotal == (l.UnitPrice * l.Qty) - l.Discount && l.LineTotal >= 0);
        var subtotalOk = draft.Lines.Sum(l => l.UnitPrice * l.Qty) == draft.Subtotal;
        var discountOk = draft.Lines.Sum(l => l.Discount) == draft.Discount;
        var totalOk = draft.Total == draft.Subtotal - draft.Discount + draft.Shipping + draft.CodFee;
        if (!(linesOk && subtotalOk && discountOk && totalOk) || draft.Total < 0)
        {
            return OrderErrors.TotalsMismatch;
        }

        var order = new Order
        {
            Id = Ids.New(),
            Number = draft.Number,
            CheckoutId = draft.CheckoutId,
            CustomerId = draft.CustomerId,
            Email = draft.Email.Trim(),
            Phone = draft.Phone,
            PaymentMethod = draft.PaymentMethod,
            Currency = draft.Currency,
            Subtotal = draft.Subtotal,
            Discount = draft.Discount,
            Shipping = draft.Shipping,
            CodFee = draft.CodFee,
            Tax = draft.Tax,
            Total = draft.Total,
            ShippingMethod = draft.ShippingMethod,
            ShippingAddress = draft.ShippingAddress,
            BillingAddress = draft.BillingAddress,
            IsGift = draft.IsGift,
            GiftMessage = draft.GiftMessage,
            HidePrices = draft.HidePrices,
            CouponCode = draft.CouponCode,
            Locale = draft.Locale,
            ContractDocHash = draft.ContractHash,
            IdempotencyKey = draft.IdempotencyKey,
            ReservationExpiresAt = draft.ReservationExpiresAt,
            FraudFlags = [.. draft.FraudFlags],
            PlacedAt = now,
            Status = draft.PaymentMethod == PaymentMethods.CashOnDelivery ? OrderStatuses.Confirmed : OrderStatuses.PendingPayment,
        };

        var sort = 0;
        foreach (var line in draft.Lines)
        {
            order._lines.Add(OrderLine.From(order.Id, line, sort++));
        }

        order._history.Add(new OrderStatusChange(order.Id, order.Status, null, null, now));
        order.Raise(new OrderPlaced(order.Id, order.Number, order.PaymentMethod, order.Total, order.Currency));
        if (order.Status == OrderStatuses.Confirmed)
        {
            order.RaiseConfirmed();
        }

        return order;
    }

    public Result MarkPaid(DateTimeOffset now)
    {
        var moved = MoveTo(OrderStatuses.Confirmed, "payment_succeeded", null, now);
        if (moved.IsSuccess)
        {
            RaiseConfirmed();
        }

        return moved;
    }

    /// <summary>
    /// A card payment arrived after its reservation expired and the order was cancelled for timeout, and the stock
    /// could be reserved again (testing.md commerce test 6). The only way out of Cancelled, and only for that reason.
    /// </summary>
    public Result ReviveAfterLatePayment(DateTimeOffset now)
    {
        if (Status != OrderStatuses.Cancelled || CancelReason != CancelReasons.PaymentExpired)
        {
            return OrderErrors.InvalidTransition(Status, OrderStatuses.Confirmed);
        }

        Status = OrderStatuses.Confirmed;
        _history.Add(new OrderStatusChange(Id, Status, "late_payment_revived", null, now));
        RaiseConfirmed();
        return Result.Success();
    }

    public Result Cancel(string reason, Guid? actorId, DateTimeOffset now)
    {
        var wasPaid = IsPaidByCard && Status == OrderStatuses.Confirmed;
        var moved = MoveTo(OrderStatuses.Cancelled, reason, actorId, now);
        if (moved.IsSuccess)
        {
            Raise(new OrderCancelled(Id, Number, Email, Locale, reason, wasPaid));
        }

        return moved;
    }

    public Result StartPreparing(Guid? actorId, DateTimeOffset now)
    {
        var moved = MoveTo(OrderStatuses.Preparing, null, actorId, now);
        if (moved.IsSuccess)
        {
            Raise(new OrderPreparing(Id, Number));
        }

        return moved;
    }

    public Result MarkShipped(string carrier, string trackingNumber, string? trackingUrl, DateTimeOffset now)
    {
        var moved = MoveTo(OrderStatuses.Shipped, $"{carrier} {trackingNumber}", null, now);
        if (moved.IsSuccess)
        {
            Raise(new OrderShipped(Id, Number, Email, Locale, carrier, trackingNumber, trackingUrl));
        }

        return moved;
    }

    public Result MarkDelivered(DateTimeOffset now)
    {
        var moved = MoveTo(OrderStatuses.Delivered, null, null, now);
        if (moved.IsSuccess)
        {
            Raise(new OrderDelivered(Id, Number, Email, Locale, PaymentMethod));
        }

        return moved;
    }

    public Result MarkReturnedToSender(DateTimeOffset now)
    {
        var moved = MoveTo(OrderStatuses.ReturnedToSender, null, null, now);
        if (moved.IsSuccess)
        {
            Raise(new OrderReturnedToSender(Id, Number, Email, CustomerId, PaymentMethod));
        }

        return moved;
    }

    public Result MarkReturnRequested(DateTimeOffset now) => MoveTo(OrderStatuses.ReturnRequested, null, null, now);

    public Result ReopenAfterRejectedReturn(Guid? actorId, DateTimeOffset now) => MoveTo(OrderStatuses.Delivered, "return_rejected", actorId, now);

    public Result MarkReturned(Guid? actorId, DateTimeOffset now) => MoveTo(OrderStatuses.Returned, null, actorId, now);

    public Result MarkRefunded(Guid? actorId, DateTimeOffset now) => MoveTo(OrderStatuses.Refunded, null, actorId, now);

    /// <summary>Records which batch went to which line (needed for recalls, operations.md §4).</summary>
    public void AssignBatches(IReadOnlyList<(Guid VariantId, Guid BatchId, string Code, int Qty)> allocations)
    {
        ArgumentNullException.ThrowIfNull(allocations);
        var remaining = allocations.Select(a => (a.VariantId, a.BatchId, a.Code, Left: a.Qty)).ToList();

        foreach (var line in _lines)
        {
            var need = line.Qty;
            var assigned = new List<OrderBatch>();
            for (var i = 0; i < remaining.Count && need > 0; i++)
            {
                var alloc = remaining[i];
                if (alloc.VariantId != line.VariantId || alloc.Left == 0)
                {
                    continue;
                }

                var take = Math.Min(alloc.Left, need);
                assigned.Add(new OrderBatch(alloc.BatchId, alloc.Code, take));
                remaining[i] = alloc with { Left = alloc.Left - take };
                need -= take;
            }

            line.BatchAllocations = assigned;
        }
    }

    public void AddFraudFlag(string flag)
    {
        if (!FraudFlags.Contains(flag))
        {
            FraudFlags.Add(flag);
        }
    }

    public static bool CanMove(string from, string to) => Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    private Result MoveTo(string target, string? note, Guid? actorId, DateTimeOffset now)
    {
        if (!CanMove(Status, target))
        {
            return OrderErrors.InvalidTransition(Status, target);
        }

        Status = target;
        _history.Add(new OrderStatusChange(Id, target, note, actorId, now));
        return Result.Success();
    }

    private void RaiseConfirmed() => Raise(new OrderConfirmed(Id, Number, Email, Locale, PaymentMethod, Total, Currency));
}

internal sealed class OrderLine : Entity<Guid>
{
    private OrderLine()
    {
    }

    public Guid OrderId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string Section { get; private set; } = string.Empty;

    public string ProductType { get; private set; } = string.Empty;

    public string NameSnapshot { get; private set; } = string.Empty;

    public string VariantLabelSnapshot { get; private set; } = string.Empty;

    public List<string> WarningsSnapshot { get; private set; } = [];

    public bool IsSample { get; private set; }

    public string ShippingClass { get; private set; } = string.Empty;

    public Guid? GroupId { get; private set; }

    public string? GroupLabelSnapshot { get; private set; }

    public long UnitPrice { get; private set; }

    public int Qty { get; private set; }

    public long Discount { get; private set; }

    public int TaxRateBp { get; private set; }

    public long TaxAmount { get; private set; }

    public long LineTotal { get; private set; }

    public List<OrderBatch>? BatchAllocations { get; set; }

    public bool Returnable { get; private set; }

    public int SortOrder { get; private set; }

    public static OrderLine From(Guid orderId, OrderLineDraft d, int sort) => new()
    {
        Id = Ids.New(),
        OrderId = orderId,
        VariantId = d.VariantId,
        ProductId = d.ProductId,
        Sku = d.Sku,
        Section = d.Section,
        ProductType = d.ProductType,
        NameSnapshot = d.Name,
        VariantLabelSnapshot = d.VariantLabel,
        WarningsSnapshot = [.. d.Warnings],
        IsSample = d.IsSample,
        ShippingClass = d.ShippingClass,
        GroupId = d.GroupId,
        GroupLabelSnapshot = d.GroupLabel,
        UnitPrice = d.UnitPrice,
        Qty = d.Qty,
        Discount = d.Discount,
        TaxRateBp = d.TaxRateBp,
        TaxAmount = d.TaxAmount,
        LineTotal = d.LineTotal,
        Returnable = d.Returnable,
        SortOrder = sort,
    };
}

internal sealed class OrderStatusChange
{
    private OrderStatusChange()
    {
    }

    public OrderStatusChange(Guid orderId, string status, string? note, Guid? actorId, DateTimeOffset at)
    {
        OrderId = orderId;
        Status = status;
        Note = note;
        ActorId = actorId;
        At = at;
    }

    public long Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string Status { get; private set; } = string.Empty;

    public string? Note { get; private set; }

    public Guid? ActorId { get; private set; }

    public DateTimeOffset At { get; private set; }
}
