using System.Security.Cryptography;
using System.Text.Json;
using Rahiq.Modules.Inventory.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Inventory.Domain;

internal static class BatchErrors
{
    public static readonly Error BestBeforeRequired = Error.Validation("batch.best_before_required", "Food batches need a best-before date (Law 3).");
    public static readonly Error BestBeforeBeforeProduction = Error.Validation("batch.best_before_invalid", "Best-before must be after the production date.");
    public static readonly Error QuantityInvalid = Error.Validation("batch.qty_invalid", "The received quantity must be positive.");
    public static readonly Error CodeInvalid = Error.Validation("batch.code_invalid", "The batch code is 2-40 characters as printed on the label.");
    public static readonly Error AdjustmentBelowReserved = Error.Conflict("batch.adjustment_below_reserved", "On-hand cannot go below what is reserved for open orders.");
    public static readonly Error AdjustmentReasonRequired = Error.Validation("batch.adjustment_note_required", "A stock adjustment needs a written reason (operations.md §4).");
}

/// <summary>
/// A batch of one variant: what the label prints, when it expires, where it came from and what the lab measured.
/// Stock arithmetic that races with checkouts (reserve, commit, release) is done in SQL; this entity owns the rest.
/// </summary>
internal sealed class Batch : AggregateRoot<Guid>
{
    private Batch()
    {
    }

    public Guid VariantId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public int QtyReceived { get; private set; }

    public int QtyOnHand { get; private set; }

    public int QtyReserved { get; private set; }

    public DateOnly? ProducedAt { get; private set; }

    public DateOnly? BestBefore { get; private set; }

    public JsonDocument? Origin { get; private set; }

    public string? LabReportKey { get; private set; }

    public JsonDocument? LabSummary { get; private set; }

    public string PublicToken { get; private set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; private set; }

    public int Free => QtyOnHand - QtyReserved;

    public static Result<Batch> Receive(
        Guid variantId,
        string code,
        int qty,
        DateOnly? producedAt,
        DateOnly? bestBefore,
        bool requiresExpiry,
        JsonDocument? origin,
        DateTimeOffset now)
    {
        code = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (code.Length is < 2 or > 40)
        {
            return BatchErrors.CodeInvalid;
        }

        if (qty <= 0)
        {
            return BatchErrors.QuantityInvalid;
        }

        if (requiresExpiry && bestBefore is null)
        {
            return BatchErrors.BestBeforeRequired;
        }

        if (bestBefore is not null && producedAt is not null && bestBefore <= producedAt)
        {
            return BatchErrors.BestBeforeBeforeProduction;
        }

        var batch = new Batch
        {
            Id = Ids.New(),
            VariantId = variantId,
            Code = code,
            QtyReceived = qty,
            QtyOnHand = qty,
            ProducedAt = producedAt,
            BestBefore = bestBefore,
            Origin = origin,
            PublicToken = NewPublicToken(),
            ReceivedAt = now,
        };
        batch.Raise(new BatchReceived(batch.Id, variantId, code, qty));
        return batch;
    }

    public void AttachLabReport(string storageKey, JsonDocument? summary)
    {
        LabReportKey = storageKey;
        LabSummary = summary ?? LabSummary;
    }

    public void UpdateLabSummary(JsonDocument? summary) => LabSummary = summary;

    public void UpdateOrigin(JsonDocument? origin) => Origin = origin;

    /// <returns>The applied delta for the movement log.</returns>
    public Result<int> AdjustOnHand(int newOnHand, string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return BatchErrors.AdjustmentReasonRequired;
        }

        if (newOnHand < QtyReserved)
        {
            return BatchErrors.AdjustmentBelowReserved;
        }

        var delta = newOnHand - QtyOnHand;
        QtyOnHand = newOnHand;
        return delta;
    }

    /// <summary>
    /// A batch may be sold only if it does not expire within <paramref name="minShelfDays"/> (product-domain.md §4.2).
    /// </summary>
    public bool IsSellable(DateOnly today, int minShelfDays) =>
        BestBefore is null || BestBefore > today.AddDays(minShelfDays);

    /// <summary>Unguessable token for the public QR page (security.md §3): 120 bits, URL-safe.</summary>
    internal static string NewPublicToken()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return string.Create(24, bytes.ToArray(), (span, data) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[data[i] % alphabet.Length];
            }
        });
    }
}

internal sealed record BatchStock(Guid BatchId, string Code, int Free, DateOnly? BestBefore);

internal sealed record FefoTake(Guid BatchId, string Code, int Qty, DateOnly? BestBefore);

/// <summary>
/// First Expired, First Out: the batch that expires first ships first; batches without a date go last;
/// batches expiring within the minimum shelf life are never picked. Mirrors the reservation SQL exactly.
/// </summary>
internal static class Fefo
{
    public static IReadOnlyList<FefoTake>? Allocate(IEnumerable<BatchStock> batches, int qty, DateOnly today, int minShelfDays)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var cutoff = today.AddDays(minShelfDays);
        var takes = new List<FefoTake>();
        var remaining = qty;

        foreach (var batch in batches
            .Where(b => b.Free > 0 && (b.BestBefore is null || b.BestBefore > cutoff))
            .OrderBy(b => b.BestBefore ?? DateOnly.MaxValue)
            .ThenBy(b => b.BatchId))
        {
            var take = Math.Min(batch.Free, remaining);
            takes.Add(new FefoTake(batch.BatchId, batch.Code, take, batch.BestBefore));
            remaining -= take;
            if (remaining == 0)
            {
                return takes;
            }
        }

        return null;
    }
}
