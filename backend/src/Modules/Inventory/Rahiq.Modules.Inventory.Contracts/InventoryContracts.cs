using System.Text.Json;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Inventory.Contracts;

public sealed record StockRequest(Guid VariantId, int Qty);

public sealed record BatchAllocation(Guid VariantId, Guid BatchId, string BatchCode, int Qty, DateOnly? BestBefore);

public sealed record ReturnedItem(Guid BatchId, int Qty, bool Damaged);

/// <summary>The batch a customer will actually receive (first FEFO batch with free stock).</summary>
public sealed record BatchCard(
    Guid BatchId,
    Guid VariantId,
    string Code,
    string PublicToken,
    DateOnly? ProducedAt,
    DateOnly? BestBefore,
    JsonElement? Origin,
    JsonElement? LabSummary,
    bool HasLabReport);

public interface IInventoryService
{
    /// <summary>
    /// Atomically holds stock for every item (FEFO, skipping batches too close to expiry) in the current transaction.
    /// All or nothing: a single missing unit fails the whole reservation (commerce-flows.md §3).
    /// </summary>
    Task<Result<IReadOnlyList<BatchAllocation>>> ReserveAsync(
        Guid checkoutId, Guid orderId, IReadOnlyList<StockRequest> items, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Held → committed: on-hand and reserved both drop, a "sold" movement is written per batch.</summary>
    Task<IReadOnlyList<BatchAllocation>> CommitAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Held → released. Safe to call more than once.</summary>
    Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>A confirmed order was cancelled before shipping: committed stock goes back on hand ("returned" movement).</summary>
    Task ReverseCommitAsync(Guid orderId, Guid? actorId, CancellationToken cancellationToken);

    Task<bool> HasLiveHoldAsync(Guid orderId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, int>> GetAvailableAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, BatchCard>> GetCurrentBatchesAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>Returned goods: back on hand, or a "damaged" movement if they cannot be resold.</summary>
    Task ReceiveReturnAsync(Guid orderId, IReadOnlyList<ReturnedItem> items, Guid? actorId, CancellationToken cancellationToken);
}

public sealed record ReservationsExpired(IReadOnlyList<Guid> OrderIds) : DomainEvent;

public sealed record BatchReceived(Guid BatchId, Guid VariantId, string Code, int Qty) : DomainEvent;

public sealed record StockLow(Guid VariantId, int Available, int Threshold) : DomainEvent;
