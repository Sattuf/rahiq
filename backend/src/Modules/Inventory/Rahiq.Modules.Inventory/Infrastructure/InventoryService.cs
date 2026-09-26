using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Inventory.Contracts;
using Rahiq.Modules.Inventory.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Inventory.Infrastructure;

internal static class InventoryErrors
{
    public static Error OutOfStock(IEnumerable<Guid> variantIds) =>
        Error.Conflict("stock.insufficient", "Some items are no longer available in the requested quantity.") with
        {
            Details = new Dictionary<string, string[]> { ["variants"] = [.. variantIds.Select(v => v.ToString())] },
        };
}

/// <summary>
/// Stock that races with checkouts is only ever changed with atomic SQL, never "read, compute, write" in memory
/// (architecture.md §5, risks-troubleshooting.md). Every method joins the caller's transaction via <see cref="IDbSession"/>.
/// </summary>
internal sealed class InventoryService(IDbSession session, IClock clock, IOptions<StoreOptions> options) : IInventoryService
{
    private DateTime SellableAfter => clock.Today.AddDays(options.Value.MinShelfLifeDays).ToDateTime(TimeOnly.MinValue);

    public async Task<Result<IReadOnlyList<BatchAllocation>>> ReserveAsync(
        Guid checkoutId, Guid orderId, IReadOnlyList<StockRequest> items, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        await session.EnsureOpenAsync(cancellationToken);
        var connection = session.Connection;
        var tx = session.Transaction ?? throw new InvalidOperationException("Stock can only be reserved inside a transaction.");
        var allocations = new List<BatchAllocation>();
        var missing = new List<Guid>();

        // Lock variants in a fixed order so two checkouts with the same items cannot deadlock.
        foreach (var item in items.GroupBy(i => i.VariantId).Select(g => new StockRequest(g.Key, g.Sum(i => i.Qty))).OrderBy(i => i.VariantId))
        {
            var batches = await connection.QueryAsync<BatchRow>(new CommandDefinition("""
                SELECT id AS Id, code AS Code, qty_on_hand - qty_reserved AS Free, best_before AS BestBefore
                FROM inventory.batches
                WHERE variant_id = @VariantId
                  AND qty_on_hand > qty_reserved
                  AND (best_before IS NULL OR best_before > @SellableAfter)
                ORDER BY best_before NULLS LAST, id
                FOR UPDATE
                """, new { item.VariantId, SellableAfter }, tx, cancellationToken: cancellationToken));

            var remaining = item.Qty;
            foreach (var batch in batches)
            {
                if (remaining == 0)
                {
                    break;
                }

                var take = Math.Min(batch.Free, remaining);
                var updated = await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE inventory.batches SET qty_reserved = qty_reserved + @take
                    WHERE id = @id AND qty_on_hand - qty_reserved >= @take
                    """, new { take, id = batch.Id }, tx, cancellationToken: cancellationToken));
                if (updated == 0)
                {
                    continue;
                }

                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO inventory.reservations (id, checkout_id, order_id, batch_id, variant_id, qty, status, expires_at)
                    VALUES (@Id, @checkoutId, @orderId, @BatchId, @VariantId, @take, 'held', @expiresAt)
                    """, new { Id = Ids.New(), checkoutId, orderId, BatchId = batch.Id, item.VariantId, take, expiresAt }, tx, cancellationToken: cancellationToken));

                allocations.Add(new BatchAllocation(item.VariantId, batch.Id, batch.Code, take, batch.BestBefore is null ? null : DateOnly.FromDateTime(batch.BestBefore.Value)));
                remaining -= take;
            }

            if (remaining > 0)
            {
                missing.Add(item.VariantId);
            }
        }

        // All or nothing: the caller's transaction rolls back every hold made above.
        return missing.Count > 0 ? InventoryErrors.OutOfStock(missing) : allocations;
    }

    public async Task<IReadOnlyList<BatchAllocation>> CommitAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var rows = (await session.Connection.QueryAsync<ReservationRow>(new CommandDefinition("""
            WITH held AS (
              UPDATE inventory.reservations SET status = 'committed', closed_at = now()
              WHERE order_id = @orderId AND status = 'held'
              RETURNING batch_id, variant_id, qty
            ), moved AS (
              UPDATE inventory.batches b SET qty_on_hand = b.qty_on_hand - h.qty, qty_reserved = b.qty_reserved - h.qty
              FROM (SELECT batch_id, sum(qty) AS qty FROM held GROUP BY batch_id) h
              WHERE b.id = h.batch_id
              RETURNING b.id
            ), logged AS (
              INSERT INTO inventory.movements (batch_id, delta, reason, ref_id)
              SELECT batch_id, -qty, 'sold', @orderId FROM held
            )
            SELECT h.batch_id AS BatchId, h.variant_id AS VariantId, h.qty AS Qty, b.code AS Code, b.best_before AS BestBefore
            FROM held h JOIN inventory.batches b ON b.id = h.batch_id
            """, new { orderId }, session.Transaction, cancellationToken: cancellationToken))).ToList();

        return [.. rows.Select(r => new BatchAllocation(r.VariantId, r.BatchId, r.Code, r.Qty, r.BestBefore is null ? null : DateOnly.FromDateTime(r.BestBefore.Value)))];
    }

    public async Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("""
            WITH released AS (
              UPDATE inventory.reservations SET status = 'released', closed_at = now()
              WHERE order_id = @orderId AND status = 'held'
              RETURNING batch_id, qty
            )
            UPDATE inventory.batches b SET qty_reserved = b.qty_reserved - r.qty
            FROM (SELECT batch_id, sum(qty) AS qty FROM released GROUP BY batch_id) r
            WHERE b.id = r.batch_id
            """, new { orderId }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task ReverseCommitAsync(Guid orderId, Guid? actorId, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("""
            WITH reversed AS (
              UPDATE inventory.reservations SET status = 'released', closed_at = now()
              WHERE order_id = @orderId AND status = 'committed'
              RETURNING batch_id, qty
            ), logged AS (
              INSERT INTO inventory.movements (batch_id, delta, reason, note, ref_id, actor_id)
              SELECT batch_id, qty, 'returned', 'order_cancelled', @orderId, @actorId FROM reversed
            )
            UPDATE inventory.batches b SET qty_on_hand = b.qty_on_hand + r.qty
            FROM (SELECT batch_id, sum(qty) AS qty FROM reversed GROUP BY batch_id) r
            WHERE b.id = r.batch_id
            """, new { orderId, actorId }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> HasLiveHoldAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM inventory.reservations WHERE order_id = @orderId AND status = 'held')",
            new { orderId }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetAvailableAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        await session.EnsureOpenAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<(Guid VariantId, long Free)>(new CommandDefinition("""
            SELECT variant_id, sum(qty_on_hand - qty_reserved)
            FROM inventory.batches
            WHERE variant_id = ANY(@ids) AND qty_on_hand > qty_reserved AND (best_before IS NULL OR best_before > @SellableAfter)
            GROUP BY variant_id
            """, new { ids = variantIds.ToArray(), SellableAfter }, session.Transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.VariantId, r => (int)r.Free);
    }

    public async Task<IReadOnlyDictionary<Guid, BatchCard>> GetCurrentBatchesAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, BatchCard>();
        }

        await session.EnsureOpenAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<CardRow>(new CommandDefinition("""
            SELECT DISTINCT ON (variant_id)
                   id AS BatchId, variant_id AS VariantId, code AS Code, public_token AS PublicToken,
                   produced_at AS ProducedAt, best_before AS BestBefore, origin::text AS Origin, lab_summary::text AS LabSummary,
                   lab_report_key IS NOT NULL AS HasLabReport
            FROM inventory.batches
            WHERE variant_id = ANY(@ids) AND qty_on_hand > qty_reserved AND (best_before IS NULL OR best_before > @SellableAfter)
            ORDER BY variant_id, best_before NULLS LAST, id
            """, new { ids = variantIds.ToArray(), SellableAfter }, session.Transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.VariantId, r => r.ToCard());
    }

    public async Task ReceiveReturnAsync(Guid orderId, IReadOnlyList<ReturnedItem> items, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        await session.EnsureOpenAsync(cancellationToken);
        foreach (var item in items.Where(i => i.Qty > 0))
        {
            // Back on hand with a "returned" movement; damaged goods then leave again as "damaged" (commerce-flows.md §10).
            await session.Connection.ExecuteAsync(new CommandDefinition("""
                UPDATE inventory.batches SET qty_on_hand = qty_on_hand + @Qty WHERE id = @BatchId;
                INSERT INTO inventory.movements (batch_id, delta, reason, ref_id, actor_id) VALUES (@BatchId, @Qty, 'returned', @orderId, @actorId);
                """, new { item.BatchId, item.Qty, orderId, actorId }, session.Transaction, cancellationToken: cancellationToken));

            if (item.Damaged)
            {
                await session.Connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE inventory.batches SET qty_on_hand = qty_on_hand - @Qty WHERE id = @BatchId;
                    INSERT INTO inventory.movements (batch_id, delta, reason, ref_id, actor_id) VALUES (@BatchId, -@Qty, 'damaged', @orderId, @actorId);
                    """, new { item.BatchId, item.Qty, orderId, actorId }, session.Transaction, cancellationToken: cancellationToken));
            }
        }
    }

    private sealed record BatchRow(Guid Id, string Code, int Free, DateTime? BestBefore);

    private sealed record ReservationRow(Guid BatchId, Guid VariantId, int Qty, string Code, DateTime? BestBefore);

    private sealed record CardRow(Guid BatchId, Guid VariantId, string Code, string PublicToken, DateTime? ProducedAt, DateTime? BestBefore, string? Origin, string? LabSummary, bool HasLabReport)
    {
        public BatchCard ToCard() => new(
            BatchId, VariantId, Code, PublicToken,
            ProducedAt is null ? null : DateOnly.FromDateTime(ProducedAt.Value),
            BestBefore is null ? null : DateOnly.FromDateTime(BestBefore.Value),
            Origin is null ? null : JsonDocument.Parse(Origin).RootElement.Clone(),
            LabSummary is null ? null : JsonDocument.Parse(LabSummary).RootElement.Clone(),
            HasLabReport);
    }
}

internal sealed class InventoryModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Batch>(b =>
        {
            b.ToTable("batches", "inventory");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.Free);
            b.Property(x => x.Origin).HasColumnType("jsonb");
            b.Property(x => x.LabSummary).HasColumnType("jsonb");
        });

        modelBuilder.Entity<StockSetting>(b =>
        {
            b.ToTable("stock_settings", "inventory");
            b.HasKey(x => x.VariantId);
        });
    }
}

internal sealed class StockSetting
{
    public Guid VariantId { get; set; }

    public int LowStockThreshold { get; set; } = 5;

    public bool RequiresExpiry { get; set; }

    public DateTimeOffset? LowStockAlertedAt { get; set; }
}
