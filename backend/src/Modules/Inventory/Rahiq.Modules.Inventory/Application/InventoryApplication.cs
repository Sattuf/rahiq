using System.Text.Json;
using Dapper;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Inventory.Contracts;
using Rahiq.Modules.Inventory.Domain;
using Rahiq.Modules.Inventory.Infrastructure;
using Rahiq.Modules.Notifications.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Inventory.Application;

public sealed record ReceiveBatchCommand(
    Guid VariantId,
    string Code,
    int Qty,
    DateOnly? ProducedAt,
    DateOnly? BestBefore,
    JsonElement? Origin,
    JsonElement? LabSummary) : ICommand<Result<Guid>>;

public sealed record AdjustBatchCommand(Guid BatchId, int NewOnHand, string Note) : ICommand<Result>;

public sealed record UpdateBatchDetailsCommand(Guid BatchId, JsonElement? Origin, JsonElement? LabSummary) : ICommand<Result>;

public sealed record AttachLabReportCommand(Guid BatchId, byte[] Pdf) : ICommand<Result>;

public sealed record SetLowStockThresholdCommand(Guid VariantId, int Threshold) : ICommand<Result>;

public sealed record ReleaseExpiredReservationsCommand : ICommand<Result<int>>;

public sealed record RaiseInventoryAlertsCommand : ICommand<Result<int>>;

public sealed record BatchRowDto(
    Guid Id, Guid VariantId, string Code, int QtyReceived, int QtyOnHand, int QtyReserved, DateOnly? ProducedAt, DateOnly? BestBefore,
    bool HasLabReport, string PublicToken, DateTimeOffset ReceivedAt, JsonElement? Origin, JsonElement? LabSummary);

public sealed record StockRowDto(Guid VariantId, int OnHand, int Reserved, int Free, int Threshold, DateOnly? NextExpiry, int Batches);

public sealed record MovementDto(long Id, int Delta, string Reason, string? Note, Guid? RefId, DateTimeOffset At);

public sealed record ListBatchesQuery(Guid? VariantId, int? ExpiringWithinDays) : IQuery<IReadOnlyList<BatchRowDto>>;

public sealed record StockOverviewQuery : IQuery<IReadOnlyList<StockRowDto>>;

public sealed record BatchMovementsQuery(Guid BatchId) : IQuery<IReadOnlyList<MovementDto>>;

public sealed record PublicBatchDto(
    string ProductName,
    string Slug,
    string VariantLabel,
    Guid VariantId,
    string Code,
    DateOnly? ProducedAt,
    DateOnly? BestBefore,
    JsonElement? Origin,
    JsonElement? LabSummary,
    bool Analysed,
    bool IsCurrentBatch,
    bool Available);

/// <summary>What the QR on a jar opens (frontend-experience.md §3.6). Only batch facts, never order data (security.md §3).</summary>
public sealed record GetPublicBatchQuery(string Token, string Locale) : IQuery<PublicBatchDto?>;

public sealed record GetLabReportQuery(string Token) : IQuery<(Stream Content, string FileName)?>;

internal sealed class ReceiveBatchValidator : AbstractValidator<ReceiveBatchCommand>
{
    public ReceiveBatchValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Qty).InclusiveBetween(1, 100_000);
    }
}

internal sealed class InventoryHandlers(
    RahiqDbContext db,
    IDbSession session,
    ICatalogReader catalog,
    IInventoryService inventory,
    IBlobStorage storage,
    IAuditLog audit,
    ICurrentActor actor,
    IEventPublisher events,
    IClock clock,
    IOptions<StoreOptions> store)
    : IRequestHandler<ReceiveBatchCommand, Result<Guid>>,
      IRequestHandler<AdjustBatchCommand, Result>,
      IRequestHandler<UpdateBatchDetailsCommand, Result>,
      IRequestHandler<AttachLabReportCommand, Result>,
      IRequestHandler<SetLowStockThresholdCommand, Result>,
      IRequestHandler<ReleaseExpiredReservationsCommand, Result<int>>,
      IRequestHandler<RaiseInventoryAlertsCommand, Result<int>>,
      IRequestHandler<ListBatchesQuery, IReadOnlyList<BatchRowDto>>,
      IRequestHandler<StockOverviewQuery, IReadOnlyList<StockRowDto>>,
      IRequestHandler<BatchMovementsQuery, IReadOnlyList<MovementDto>>,
      IRequestHandler<GetPublicBatchQuery, PublicBatchDto?>,
      IRequestHandler<GetLabReportQuery, (Stream Content, string FileName)?>
{
    private static readonly Error BatchNotFound = Error.NotFound("batch.not_found", "Batch not found.");

    public async Task<Result<Guid>> Handle(ReceiveBatchCommand request, CancellationToken cancellationToken)
    {
        var variants = await catalog.GetVariantsAsync([request.VariantId], Locales.Default, cancellationToken);
        if (!variants.TryGetValue(request.VariantId, out var variant) || variant.IsBundle)
        {
            return Error.NotFound("variant.not_found", "Variant not found or not stocked directly.");
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Batch>().AnyAsync(b => b.VariantId == request.VariantId && b.Code == code, cancellationToken))
        {
            return Error.Conflict("batch.code_taken", "This variant already has a batch with this code.");
        }

        var received = Batch.Receive(request.VariantId, request.Code, request.Qty, request.ProducedAt, request.BestBefore,
            requiresExpiry: ProductTypes.IsFood(variant.ProductType), ToDocument(request.Origin), clock.UtcNow);
        if (received.IsFailure)
        {
            return received.Error;
        }

        var batch = received.Value;
        batch.UpdateLabSummary(ToDocument(request.LabSummary));
        db.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        await Movement(batch.Id, request.Qty, "received", null, cancellationToken);
        audit.Record("batch.received", "batch", batch.Id.ToString(), new { variant.Sku, batch.Code, request.Qty, request.BestBefore });
        return batch.Id;
    }

    public async Task<Result> Handle(AdjustBatchCommand request, CancellationToken cancellationToken)
    {
        var batch = await db.Set<Batch>().FromSql($"SELECT * FROM inventory.batches WHERE id = {request.BatchId} FOR UPDATE").FirstOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            return BatchNotFound;
        }

        var adjusted = batch.AdjustOnHand(request.NewOnHand, request.Note);
        if (adjusted.IsFailure)
        {
            return adjusted.Error;
        }

        if (adjusted.Value != 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await Movement(batch.Id, adjusted.Value, "adjustment", request.Note.Trim(), cancellationToken);
        }

        audit.Record("stock.adjusted", "batch", batch.Id.ToString(), new { delta = adjusted.Value, request.Note });
        return Result.Success();
    }

    public async Task<Result> Handle(UpdateBatchDetailsCommand request, CancellationToken cancellationToken)
    {
        var batch = await db.Set<Batch>().FirstOrDefaultAsync(b => b.Id == request.BatchId, cancellationToken);
        if (batch is null)
        {
            return BatchNotFound;
        }

        batch.UpdateOrigin(ToDocument(request.Origin));
        batch.UpdateLabSummary(ToDocument(request.LabSummary));
        return Result.Success();
    }

    public async Task<Result> Handle(AttachLabReportCommand request, CancellationToken cancellationToken)
    {
        var batch = await db.Set<Batch>().FirstOrDefaultAsync(b => b.Id == request.BatchId, cancellationToken);
        if (batch is null)
        {
            return BatchNotFound;
        }

        if (request.Pdf.Length is < 5 or > 20 * 1024 * 1024 || !request.Pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            return Error.Validation("lab_report.not_pdf", "Upload the laboratory report as a PDF up to 20 MB.");
        }

        var key = $"lab-reports/{batch.Id:N}.pdf";
        using (var stream = new MemoryStream(request.Pdf))
        {
            await storage.PutAsync(key, stream, "application/pdf", cancellationToken);
        }

        batch.AttachLabReport(key, null);
        audit.Record("batch.lab_report", "batch", batch.Id.ToString());
        return Result.Success();
    }

    public async Task<Result> Handle(SetLowStockThresholdCommand request, CancellationToken cancellationToken)
    {
        if (request.Threshold < 0)
        {
            return Error.Validation("stock.threshold_invalid", "The threshold cannot be negative.");
        }

        var setting = await db.Set<StockSetting>().FindAsync([request.VariantId], cancellationToken);
        if (setting is null)
        {
            db.Add(new StockSetting { VariantId = request.VariantId, LowStockThreshold = request.Threshold });
        }
        else
        {
            setting.LowStockThreshold = request.Threshold;
        }

        return Result.Success();
    }

    /// <summary>
    /// The release worker (commerce-flows.md §3): held reservations past their deadline give their stock back, and
    /// the orders waiting on them are announced so Ordering cancels them. SKIP LOCKED lets several instances run safely.
    /// </summary>
    public async Task<Result<int>> Handle(ReleaseExpiredReservationsCommand request, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var orderIds = (await session.Connection.QueryAsync<Guid>(new CommandDefinition("""
            WITH expired AS (
              SELECT id FROM inventory.reservations
              WHERE status = 'held' AND expires_at <= @now
              ORDER BY expires_at
              LIMIT 500
              FOR UPDATE SKIP LOCKED
            ), released AS (
              UPDATE inventory.reservations r SET status = 'released', closed_at = @now
              FROM expired e WHERE r.id = e.id
              RETURNING r.batch_id, r.qty, r.order_id
            ), restored AS (
              UPDATE inventory.batches b SET qty_reserved = b.qty_reserved - x.qty
              FROM (SELECT batch_id, sum(qty) AS qty FROM released GROUP BY batch_id) x
              WHERE b.id = x.batch_id
            )
            SELECT DISTINCT order_id FROM released WHERE order_id IS NOT NULL
            """, new { now = clock.UtcNow }, session.Transaction, cancellationToken: cancellationToken))).ToList();

        if (orderIds.Count > 0)
        {
            events.Publish(new ReservationsExpired(orderIds));
        }

        return orderIds.Count;
    }

    /// <summary>Daily: low stock, batches near expiry (a chance for an offer), food batches without a lab report.</summary>
    public async Task<Result<int>> Handle(RaiseInventoryAlertsCommand request, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var alerts = 0;
        var nearExpiry = clock.Today.AddDays(store.Value.NearExpiryAlertDays).ToDateTime(TimeOnly.MinValue);

        var low = await session.Connection.QueryAsync<(Guid VariantId, long Free, int Threshold)>(new CommandDefinition("""
            SELECT s.variant_id, coalesce(sum(b.qty_on_hand - b.qty_reserved), 0), s.low_stock_threshold
            FROM inventory.stock_settings s LEFT JOIN inventory.batches b ON b.variant_id = s.variant_id
            WHERE s.low_stock_alerted_at IS NULL OR s.low_stock_alerted_at < @since
            GROUP BY s.variant_id, s.low_stock_threshold
            HAVING coalesce(sum(b.qty_on_hand - b.qty_reserved), 0) <= s.low_stock_threshold
            """, new { since = clock.UtcNow.AddDays(-1) }, session.Transaction, cancellationToken: cancellationToken));

        foreach (var row in low)
        {
            events.Publish(new StockLow(row.VariantId, (int)row.Free, row.Threshold));
            events.Publish(new StaffAlertRaised(AlertKinds.StockLow, "Low stock", $"Variant {row.VariantId}: {row.Free} left (threshold {row.Threshold}).", row.VariantId.ToString()));
            await session.Connection.ExecuteAsync(new CommandDefinition("UPDATE inventory.stock_settings SET low_stock_alerted_at = @now WHERE variant_id = @id",
                new { now = clock.UtcNow, id = row.VariantId }, session.Transaction, cancellationToken: cancellationToken));
            alerts++;
        }

        var expiring = await session.Connection.QueryAsync<(Guid Id, string Code, DateTime BestBefore, int Free)>(new CommandDefinition("""
            SELECT id, code, best_before, qty_on_hand - qty_reserved FROM inventory.batches
            WHERE best_before IS NOT NULL AND best_before <= @nearExpiry AND qty_on_hand > qty_reserved
            """, new { nearExpiry }, session.Transaction, cancellationToken: cancellationToken));
        foreach (var b in expiring)
        {
            events.Publish(new StaffAlertRaised(AlertKinds.BatchNearExpiry, "Batch near expiry",
                $"Batch {b.Code}: {b.Free} units, best before {b.BestBefore:yyyy-MM-dd}. Consider a bundle or an offer.", b.Id.ToString()));
            alerts++;
        }

        var foodVariantsWithoutReport = await session.Connection.QueryAsync<(Guid Id, string Code)>(new CommandDefinition("""
            SELECT id, code FROM inventory.batches WHERE lab_report_key IS NULL AND best_before IS NOT NULL AND qty_on_hand > 0
            """, transaction: session.Transaction, cancellationToken: cancellationToken));
        foreach (var b in foodVariantsWithoutReport)
        {
            events.Publish(new StaffAlertRaised(AlertKinds.BatchWithoutLabReport, "Batch without lab report",
                $"Batch {b.Code} has no laboratory report: it is sold without the 'analysed' badge.", b.Id.ToString()));
            alerts++;
        }

        return alerts;
    }

    public async Task<IReadOnlyList<BatchRowDto>> Handle(ListBatchesQuery request, CancellationToken cancellationToken)
    {
        var query = db.Set<Batch>().AsNoTracking();
        if (request.VariantId is { } variantId)
        {
            query = query.Where(b => b.VariantId == variantId);
        }

        if (request.ExpiringWithinDays is { } days)
        {
            var limit = clock.Today.AddDays(days);
            query = query.Where(b => b.BestBefore != null && b.BestBefore <= limit && b.QtyOnHand > 0);
        }

        var batches = await query.OrderBy(b => b.BestBefore).ThenBy(b => b.ReceivedAt).Take(500).ToListAsync(cancellationToken);
        return [.. batches.Select(b => new BatchRowDto(b.Id, b.VariantId, b.Code, b.QtyReceived, b.QtyOnHand, b.QtyReserved, b.ProducedAt, b.BestBefore,
            b.LabReportKey is not null, b.PublicToken, b.ReceivedAt, b.Origin?.RootElement.Clone(), b.LabSummary?.RootElement.Clone()))];
    }

    public async Task<IReadOnlyList<StockRowDto>> Handle(StockOverviewQuery request, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<(Guid VariantId, long OnHand, long Reserved, int? Threshold, DateTime? NextExpiry, long Batches)>(new CommandDefinition("""
            SELECT b.variant_id, sum(b.qty_on_hand), sum(b.qty_reserved), max(s.low_stock_threshold),
                   min(b.best_before) FILTER (WHERE b.qty_on_hand > 0), count(*)
            FROM inventory.batches b LEFT JOIN inventory.stock_settings s ON s.variant_id = b.variant_id
            GROUP BY b.variant_id
            """, transaction: session.Transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new StockRowDto(r.VariantId, (int)r.OnHand, (int)r.Reserved, (int)(r.OnHand - r.Reserved), r.Threshold ?? 5,
            r.NextExpiry is null ? null : DateOnly.FromDateTime(r.NextExpiry.Value), (int)r.Batches))];
    }

    public async Task<IReadOnlyList<MovementDto>> Handle(BatchMovementsQuery request, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<MovementDto>(new CommandDefinition("""
            SELECT id AS Id, delta AS Delta, reason AS Reason, note AS Note, ref_id AS RefId, at AS At
            FROM inventory.movements WHERE batch_id = @BatchId ORDER BY at DESC, id DESC
            """, new { request.BatchId }, session.Transaction, cancellationToken: cancellationToken));
        return [.. rows];
    }

    public async Task<PublicBatchDto?> Handle(GetPublicBatchQuery request, CancellationToken cancellationToken)
    {
        var batch = await db.Set<Batch>().AsNoTracking().FirstOrDefaultAsync(b => b.PublicToken == request.Token, cancellationToken);
        if (batch is null)
        {
            return null;
        }

        var variant = (await catalog.GetVariantsAsync([batch.VariantId], request.Locale, cancellationToken)).GetValueOrDefault(batch.VariantId);
        if (variant is null)
        {
            return null;
        }

        var current = (await inventory.GetCurrentBatchesAsync([batch.VariantId], cancellationToken)).GetValueOrDefault(batch.VariantId);
        return new PublicBatchDto(
            variant.ProductName, variant.Slug, variant.VariantLabel, variant.VariantId, batch.Code, batch.ProducedAt, batch.BestBefore,
            batch.Origin?.RootElement.Clone(), batch.LabSummary?.RootElement.Clone(), batch.LabReportKey is not null,
            IsCurrentBatch: current?.BatchId == batch.Id,
            Available: variant.IsSellable && current?.BatchId == batch.Id);
    }

    public async Task<(Stream Content, string FileName)?> Handle(GetLabReportQuery request, CancellationToken cancellationToken)
    {
        var batch = await db.Set<Batch>().AsNoTracking().FirstOrDefaultAsync(b => b.PublicToken == request.Token, cancellationToken);
        if (batch?.LabReportKey is null)
        {
            return null;
        }

        var stream = await storage.GetAsync(batch.LabReportKey, cancellationToken);
        return stream is null ? null : (stream, $"rahiq-{batch.Code}-lab-report.pdf");
    }

    private async Task Movement(Guid batchId, int delta, string reason, string? note, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO inventory.movements (batch_id, delta, reason, note, actor_id) VALUES (@batchId, @delta, @reason, @note, @actorId)",
            new { batchId, delta, reason, note, actorId = actor.Id }, session.Transaction, cancellationToken: cancellationToken));
    }

    private static JsonDocument? ToDocument(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } e ? JsonDocument.Parse(e.GetRawText()) : null;
}
