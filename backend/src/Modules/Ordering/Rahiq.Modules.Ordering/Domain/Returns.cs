using Rahiq.Modules.Ordering.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Ordering.Domain;

public static class ReturnKinds
{
    /// <summary>The 14-day right of withdrawal (cayma hakkı).</summary>
    public const string Withdrawal = "withdrawal";
    public const string Damaged = "damaged";
    public const string WrongItem = "wrong_item";
}

public static class ReturnStatuses
{
    public const string Requested = "requested";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Received = "received";
    public const string Refunded = "refunded";
}

internal static class ReturnErrors
{
    public static readonly Error NotDelivered = Error.Conflict("return.not_delivered", "Returns open once the order is delivered.");
    public static readonly Error WindowClosed = Error.Conflict("return.window_closed", "The return period for this order has ended.");
    public static readonly Error LineNotReturnable = Error.Conflict("return.line_not_returnable",
        "Food and opened cosmetics cannot be returned unless they arrived damaged or wrong.");
    public static readonly Error SealBroken = Error.Conflict("return.seal_broken", "A perfume can be returned only with its seal intact, unless it arrived damaged.");
    public static readonly Error PhotosRequired = Error.Validation("return.photos_required", "Please add photos of the damage.");
    public static readonly Error NoLines = Error.Validation("return.no_lines", "Choose at least one item.");
    public static readonly Error QtyInvalid = Error.Validation("return.qty_invalid", "The quantity is more than you ordered.");
    public static readonly Error AlreadyOpen = Error.Conflict("return.already_open", "A return is already open for this order.");
}

public sealed record ReturnLine(Guid LineId, int Qty, bool SealIntact);

/// <summary>
/// Applies the legal return exceptions automatically (commerce-flows.md §10, compliance.md §3): food is never returned
/// on withdrawal; cosmetics only with the seal intact; damaged or wrong items always, with photos.
/// </summary>
internal static class ReturnPolicy
{
    public static Result Check(Order order, string kind, IReadOnlyList<ReturnLine> lines, int photoCount, DateTimeOffset deliveredAt, DateTimeOffset now, int windowDays)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(lines);
        if (order.Status != OrderStatuses.Delivered)
        {
            return ReturnErrors.NotDelivered;
        }

        if (lines.Count == 0)
        {
            return ReturnErrors.NoLines;
        }

        if (kind != ReturnKinds.Withdrawal && photoCount == 0)
        {
            return ReturnErrors.PhotosRequired;
        }

        if (kind == ReturnKinds.Withdrawal && now > deliveredAt.AddDays(windowDays))
        {
            return ReturnErrors.WindowClosed;
        }

        foreach (var requested in lines)
        {
            var line = order.Lines.FirstOrDefault(l => l.Id == requested.LineId);
            if (line is null || requested.Qty <= 0 || requested.Qty > line.Qty)
            {
                return ReturnErrors.QtyInvalid;
            }

            if (kind == ReturnKinds.Withdrawal)
            {
                if (!line.Returnable)
                {
                    return ReturnErrors.LineNotReturnable;
                }

                if (!requested.SealIntact)
                {
                    return ReturnErrors.SealBroken;
                }
            }
        }

        return Result.Success();
    }

    /// <summary>What a line allows at order time: cosmetics yes (seal permitting), food and gift packaging no.</summary>
    public static bool IsReturnableOnWithdrawal(string section) => section == "perfume";
}

internal sealed class ReturnRequest : Entity<Guid>
{
    private ReturnRequest()
    {
    }

    public Guid OrderId { get; private set; }

    public string Status { get; private set; } = ReturnStatuses.Requested;

    public string Kind { get; private set; } = ReturnKinds.Withdrawal;

    public string Reason { get; private set; } = string.Empty;

    public List<ReturnLine> Lines { get; private set; } = [];

    public List<string> PhotoKeys { get; private set; } = [];

    public string? ResolutionNote { get; private set; }

    public long? RefundAmount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ReturnRequest Open(Guid orderId, string kind, string reason, IReadOnlyList<ReturnLine> lines, IReadOnlyList<string> photos, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        OrderId = orderId,
        Kind = kind,
        Reason = reason.Trim(),
        Lines = [.. lines],
        PhotoKeys = [.. photos],
        CreatedAt = now,
        UpdatedAt = now,
    };

    public Result Approve(string? note, DateTimeOffset now) => Move(ReturnStatuses.Requested, ReturnStatuses.Approved, note, now);

    public Result Reject(string note, DateTimeOffset now) => Move(ReturnStatuses.Requested, ReturnStatuses.Rejected, note, now);

    public Result MarkReceived(string? note, DateTimeOffset now) => Move(ReturnStatuses.Approved, ReturnStatuses.Received, note, now);

    public Result MarkRefunded(long amount, DateTimeOffset now)
    {
        var moved = Move(ReturnStatuses.Received, ReturnStatuses.Refunded, ResolutionNote, now);
        if (moved.IsSuccess)
        {
            RefundAmount = amount;
        }

        return moved;
    }

    /// <summary>The refund for the returned lines: what the customer paid for them (line total pro rata by quantity).</summary>
    public long ComputeRefund(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        long amount = 0;
        foreach (var requested in Lines)
        {
            var line = order.Lines.First(l => l.Id == requested.LineId);
            amount += requested.Qty == line.Qty ? line.LineTotal : line.LineTotal * requested.Qty / line.Qty;
        }

        return amount;
    }

    private Result Move(string from, string to, string? note, DateTimeOffset now)
    {
        if (Status != from)
        {
            return Error.Conflict("return.invalid_transition", $"A return in '{Status}' cannot move to '{to}'.");
        }

        Status = to;
        ResolutionNote = note ?? ResolutionNote;
        UpdatedAt = now;
        return Result.Success();
    }
}
