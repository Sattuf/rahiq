using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Inventory.Contracts;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Ordering.Domain;
using Rahiq.Modules.Ordering.Infrastructure;
using Rahiq.Modules.Payments.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Ordering.Application;

public sealed record ReturnLineInput(Guid LineId, int Qty, bool SealIntact);

public sealed record RequestReturnCommand(string Number, string? Email, Guid? CustomerId, string Kind, string Reason, IReadOnlyList<ReturnLineInput> Lines, IReadOnlyList<string> PhotoKeys)
    : ICommand<Result<Guid>>;

public sealed record UploadReturnPhotoCommand(byte[] Content) : ICommand<Result<string>>;

public sealed record ReturnView(Guid Id, Guid OrderId, string OrderNumber, string Status, string Kind, string Reason, IReadOnlyList<ReturnLine> Lines, int Photos, string? Note, long? RefundAmount, DateTimeOffset CreatedAt);

public sealed record ListReturnsQuery(string? Status) : IQuery<IReadOnlyList<ReturnView>>;

/// <param name="Decision">approve, reject, receive, refund.</param>
public sealed record ResolveReturnCommand(Guid ReturnId, string Decision, string? Note, bool Damaged) : ICommand<Result>;

public sealed record AdminOrderRow(Guid Id, string Number, string Status, string PaymentMethod, long Total, string Currency, string Email, string City, DateTimeOffset PlacedAt, int Items, bool Flagged, bool HasBothSections);

public sealed record AdminOrdersQuery(string? Status, string? Q, int Page = 1) : IQuery<IReadOnlyList<AdminOrderRow>>;

public sealed record AdminOrderQuery(Guid Id) : IQuery<Result<OrderView>>;

public sealed record StartPreparingCommand(Guid OrderId) : ICommand<Result>;

public sealed record MarkDeliveredCommand(Guid OrderId) : ICommand<Result>;

public sealed record AddOrderNoteCommand(Guid OrderId, string Body) : ICommand<Result>;

public sealed record OrderNoteView(string Body, Guid AuthorId, DateTimeOffset At);

public sealed record OrderNotesQuery(Guid OrderId) : IQuery<IReadOnlyList<OrderNoteView>>;

/// <summary>Refunds above the configured amount need the owner's permission (security.md §3).</summary>
public sealed record RefundOrderCommand(Guid OrderId, long Amount, string Reason) : ICommand<Result>;

public sealed record PickListQuery(Guid OrderId) : IQuery<Result<string>>;

/// <summary>Every order that received units of a batch, for a recall (operations.md §4).</summary>
public sealed record RecallQuery(string BatchCode) : IQuery<IReadOnlyList<AdminOrderRow>>;

public sealed record DashboardView(int OrdersToday, int ToPrepare, int PendingPayment, long RevenueLast7Days, int OrdersLast30Days, int CrossSectionShareBp, int OpenReturns, int Flagged);

public sealed record DashboardQuery : IQuery<DashboardView>;

internal sealed class ReturnsAndAdminHandlers(
    RahiqDbContext db,
    IBlobStorage storage,
    IInventoryService inventory,
    IPaymentGateway payments,
    ICurrentActor actor,
    IAuditLog audit,
    IEventPublisher events,
    ISender sender,
    IClock clock,
    IOptions<StoreOptions> store)
    : IRequestHandler<RequestReturnCommand, Result<Guid>>,
      IRequestHandler<UploadReturnPhotoCommand, Result<string>>,
      IRequestHandler<ListReturnsQuery, IReadOnlyList<ReturnView>>,
      IRequestHandler<ResolveReturnCommand, Result>,
      IRequestHandler<AdminOrdersQuery, IReadOnlyList<AdminOrderRow>>,
      IRequestHandler<AdminOrderQuery, Result<OrderView>>,
      IRequestHandler<StartPreparingCommand, Result>,
      IRequestHandler<MarkDeliveredCommand, Result>,
      IRequestHandler<AddOrderNoteCommand, Result>,
      IRequestHandler<OrderNotesQuery, IReadOnlyList<OrderNoteView>>,
      IRequestHandler<RefundOrderCommand, Result>,
      IRequestHandler<PickListQuery, Result<string>>,
      IRequestHandler<RecallQuery, IReadOnlyList<AdminOrderRow>>,
      IRequestHandler<DashboardQuery, DashboardView>
{
    public async Task<Result<Guid>> Handle(RequestReturnCommand request, CancellationToken cancellationToken)
    {
        var number = request.Number.Trim().ToUpperInvariant();
        var order = await Orders().FirstOrDefaultAsync(o => o.Number == number, cancellationToken);
        var owns = order is not null && ((request.CustomerId is not null && order.CustomerId == request.CustomerId)
            || (!string.IsNullOrWhiteSpace(request.Email) && string.Equals(order.Email, request.Email.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (order is null || !owns)
        {
            return OrderErrors.NotFound;
        }

        if (request.Kind is not (ReturnKinds.Withdrawal or ReturnKinds.Damaged or ReturnKinds.WrongItem) || string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("return.invalid", "Choose a return type and write a short reason.");
        }

        if (await db.Set<ReturnRequest>().AnyAsync(r => r.OrderId == order.Id && (r.Status == ReturnStatuses.Requested || r.Status == ReturnStatuses.Approved), cancellationToken))
        {
            return ReturnErrors.AlreadyOpen;
        }

        var lines = request.Lines.Select(l => new ReturnLine(l.LineId, l.Qty, l.SealIntact)).ToList();
        var deliveredAt = order.History.LastOrDefault(h => h.Status == OrderStatuses.Delivered)?.At ?? clock.UtcNow;
        var photos = request.PhotoKeys.Where(k => k.StartsWith("returns/", StringComparison.Ordinal)).ToList();
        var allowed = ReturnPolicy.Check(order, request.Kind, lines, photos.Count, deliveredAt, clock.UtcNow, store.Value.ReturnWindowDays);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var ret = ReturnRequest.Open(order.Id, request.Kind, request.Reason, lines, photos, clock.UtcNow);
        db.Add(ret);
        order.MarkReturnRequested(clock.UtcNow);
        events.Publish(new ReturnRequested(ret.Id, order.Id, order.Number, order.Email, order.Locale, request.Kind));
        return ret.Id;
    }

    public async Task<Result<string>> Handle(UploadReturnPhotoCommand request, CancellationToken cancellationToken)
    {
        var c = request.Content;
        var isJpeg = c.Length > 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF;
        var isPng = c.Length > 8 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47;
        if (!(isJpeg || isPng) || c.Length > 10 * 1024 * 1024)
        {
            return Error.Validation("return.photo_invalid", "Photos are JPEG or PNG, up to 10 MB.");
        }

        var key = $"returns/{Ids.New():N}.{(isJpeg ? "jpg" : "png")}";
        using var stream = new MemoryStream(c);
        await storage.PutAsync(key, stream, isJpeg ? "image/jpeg" : "image/png", cancellationToken);
        return key;
    }

    public async Task<IReadOnlyList<ReturnView>> Handle(ListReturnsQuery request, CancellationToken cancellationToken)
    {
        var query = db.Set<ReturnRequest>().AsNoTracking().AsQueryable();
        if (request.Status is not null)
        {
            query = query.Where(r => r.Status == request.Status);
        }

        var returns = await query.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(cancellationToken);
        var orderIds = returns.Select(r => r.OrderId).ToList();
        var numbers = await db.Set<Order>().AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, cancellationToken);
        return [.. returns.Select(r => new ReturnView(r.Id, r.OrderId, numbers.GetValueOrDefault(r.OrderId, "?"), r.Status, r.Kind, r.Reason, r.Lines, r.PhotoKeys.Count, r.ResolutionNote, r.RefundAmount, r.CreatedAt))];
    }

    public async Task<Result> Handle(ResolveReturnCommand request, CancellationToken cancellationToken)
    {
        var ret = await db.Set<ReturnRequest>().FirstOrDefaultAsync(r => r.Id == request.ReturnId, cancellationToken);
        var order = ret is null ? null : await Orders().FirstOrDefaultAsync(o => o.Id == ret.OrderId, cancellationToken);
        if (ret is null || order is null)
        {
            return Error.NotFound("return.not_found", "Return not found.");
        }

        var now = clock.UtcNow;
        Result result;
        switch (request.Decision)
        {
            case "approve":
                result = ret.Approve(request.Note, now);
                break;
            case "reject":
                result = ret.Reject(request.Note ?? "rejected", now);
                if (result.IsSuccess)
                {
                    order.ReopenAfterRejectedReturn(actor.Id, now);
                }

                break;
            case "receive":
                result = ret.MarkReceived(request.Note, now);
                if (result.IsSuccess)
                {
                    // Goods back on hand; damaged goods leave again as "damaged" (commerce-flows.md §10).
                    var items = ret.Lines.SelectMany(rl =>
                    {
                        var line = order.Lines.First(l => l.Id == rl.LineId);
                        var left = rl.Qty;
                        return (line.BatchAllocations ?? []).Select(b =>
                        {
                            var take = Math.Min(b.Qty, left);
                            left -= take;
                            return new ReturnedItem(b.BatchId, take, request.Damaged || ret.Kind == ReturnKinds.Damaged);
                        }).Where(i => i.Qty > 0).ToList();
                    }).ToList();
                    await inventory.ReceiveReturnAsync(order.Id, items, actor.Id, cancellationToken);
                    order.MarkReturned(actor.Id, now);
                }

                break;
            case "refund":
                var amount = ret.ComputeRefund(order);
                var refunded = await sender.Send(new RefundOrderCommand(order.Id, amount, $"return:{ret.Id:N}"), cancellationToken);
                result = refunded.IsSuccess ? ret.MarkRefunded(amount, now) : refunded;
                break;
            default:
                result = Error.Validation("return.decision_invalid", "Decision is approve, reject, receive or refund.");
                break;
        }

        if (result.IsSuccess)
        {
            audit.Record($"return.{request.Decision}", "order", order.Id.ToString(), new { ret.Id, request.Note });
            events.Publish(new ReturnResolved(ret.Id, order.Id, order.Number, order.Email, order.Locale, ret.Status));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminOrderRow>> Handle(AdminOrdersQuery request, CancellationToken cancellationToken)
    {
        var query = db.Set<Order>().AsNoTracking().Include(o => o.Lines).AsQueryable();
        if (request.Status is not null)
        {
            query = query.Where(o => o.Status == request.Status);
        }

        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            var q = request.Q.Trim();
            var upper = q.ToUpperInvariant();
            query = query.Where(o => o.Number.Contains(upper) || EF.Functions.ILike(o.Email, $"%{q}%"));
        }

        var page = Math.Max(1, request.Page);
        var orders = await query.OrderByDescending(o => o.PlacedAt).Skip((page - 1) * 50).Take(50).AsSplitQuery().ToListAsync(cancellationToken);
        return [.. orders.Select(Row)];
    }

    public async Task<Result<OrderView>> Handle(AdminOrderQuery request, CancellationToken cancellationToken)
    {
        var order = await Orders().AsNoTracking().FirstOrDefaultAsync(o => o.Id == request.Id, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        return OrderQueryHandlers.ToView(order, await payments.GetAsync(order.Id, cancellationToken), clock.UtcNow, store.Value.ReturnWindowDays);
    }

    public async Task<Result> Handle(StartPreparingCommand request, CancellationToken cancellationToken)
    {
        var order = await Orders().FirstOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);
        return order is null ? OrderErrors.NotFound : order.StartPreparing(actor.Id, clock.UtcNow);
    }

    public async Task<Result> Handle(MarkDeliveredCommand request, CancellationToken cancellationToken)
    {
        var order = await Orders().FirstOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        audit.Record("order.marked_delivered", "order", order.Id.ToString());
        return order.MarkDelivered(clock.UtcNow);
    }

    public Task<Result> Handle(AddOrderNoteCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 2000)
        {
            return Task.FromResult<Result>(Error.Validation("note.invalid", "A note is 1-2000 characters."));
        }

        db.Add(new OrderNote { Id = Ids.New(), OrderId = request.OrderId, Body = request.Body.Trim(), AuthorId = actor.Id ?? Guid.Empty, At = clock.UtcNow });
        return Task.FromResult(Result.Success());
    }

    public async Task<IReadOnlyList<OrderNoteView>> Handle(OrderNotesQuery request, CancellationToken cancellationToken) =>
        await db.Set<OrderNote>().AsNoTracking().Where(n => n.OrderId == request.OrderId).OrderBy(n => n.At)
            .Select(n => new OrderNoteView(n.Body, n.AuthorId, n.At)).ToListAsync(cancellationToken);

    public async Task<Result> Handle(RefundOrderCommand request, CancellationToken cancellationToken)
    {
        if (!actor.HasPermission(Permissions.OrdersRefund))
        {
            return Error.Forbidden("refund.forbidden", "You are not allowed to refund.");
        }

        if (request.Amount > store.Value.RefundApprovalThreshold && !actor.HasPermission(Permissions.OrdersRefundLarge))
        {
            return Error.Forbidden("refund.approval_required", "Refunds above the threshold need the owner.");
        }

        var order = await Orders().FirstOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        var refunded = await payments.RefundAsync(order.Id, request.Amount, request.Reason, actor.Id, cancellationToken);
        if (refunded.IsFailure)
        {
            return refunded.Error;
        }

        var status = await payments.GetAsync(order.Id, cancellationToken);
        if (status?.Status == PaymentStatuses.Refunded && order.Status is OrderStatuses.Returned or OrderStatuses.ReturnedToSender)
        {
            order.MarkRefunded(actor.Id, clock.UtcNow);
        }

        return Result.Success();
    }

    /// <summary>
    /// The pick list the packer works from (operations.md §1): items, the FEFO batch of each line, the packaging class,
    /// the gift message and whether prices must be hidden.
    /// </summary>
    public async Task<Result<string>> Handle(PickListQuery request, CancellationToken cancellationToken)
    {
        var o = await Orders().AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.OrderId, cancellationToken);
        if (o is null)
        {
            return OrderErrors.NotFound;
        }

        static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var packaging = new Dictionary<string, string>
        {
            ["flammable"] = "Divided carton + sealed bag · FRAGILE · road only",
            ["liquid"] = "Seal tape + sealed bag + dividers · treat as leaking",
            ["fragile"] = "Rigid box + padding · FRAGILE",
            ["standard"] = "Standard",
        };
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<!doctype html><html lang=\"tr\"><meta charset=\"utf-8\"><title>{E(o.Number)}</title><style>body{{font:14px system-ui;margin:16px}}table{{border-collapse:collapse;width:100%}}td,th{{border:1px solid #999;padding:6px;text-align:start}}.big{{font-size:20px;font-weight:700}}.warn{{border:2px solid #000;padding:8px;margin:8px 0;font-weight:700}}</style>");
        sb.Append(CultureInfo.InvariantCulture, $"<p class=\"big\">{E(o.Number)} · {o.PlacedAt:yyyy-MM-dd HH:mm}</p><p>{E(o.ShippingAddress.FullName)} · {E(o.ShippingAddress.District)} / {E(o.ShippingAddress.ProvinceName)}</p>");
        if (o.PaymentMethod == PaymentMethods.CashOnDelivery)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<p class=\"warn\">KAPIDA ÖDEME: {o.Total / 100m:0.00} {o.Currency}</p>");
        }

        if (o.HidePrices)
        {
            sb.Append("<p class=\"warn\">HEDİYE — fiyatlı belge koyma (prices hidden)</p>");
        }

        if (o.FraudFlags.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<p class=\"warn\">REVIEW BEFORE SHIPPING: {E(string.Join(", ", o.FraudFlags))}</p>");
        }

        sb.Append("<table><tr><th>✓</th><th>SKU</th><th>Ürün</th><th>Adet</th><th>Parti (FEFO)</th><th>Paketleme</th></tr>");
        foreach (var l in o.Lines.OrderBy(l => l.SortOrder))
        {
            var batches = string.Join(", ", (l.BatchAllocations ?? []).Select(b => $"{b.Code}×{b.Qty}"));
            sb.Append(CultureInfo.InvariantCulture, $"<tr><td>☐</td><td>{E(l.Sku)}</td><td>{E(l.GroupLabelSnapshot is null ? string.Empty : l.GroupLabelSnapshot + ": ")}{E(l.NameSnapshot)} {E(l.VariantLabelSnapshot)}</td><td>{l.Qty}</td><td>{E(batches)}</td><td>{E(packaging.GetValueOrDefault(l.ShippingClass, l.ShippingClass))}</td></tr>");
        }

        sb.Append("</table>");
        if (!string.IsNullOrWhiteSpace(o.GiftMessage))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<h3>Hediye mesajı</h3><p style=\"white-space:pre-wrap;border:1px dashed #999;padding:8px\">{E(o.GiftMessage)}</p>");
        }

        sb.Append("<p>☐ Paket fotoğrafı çekildi ve yüklendi · ☐ Teşekkür kartı eklendi</p></html>");
        return sb.ToString();
    }

    public async Task<IReadOnlyList<AdminOrderRow>> Handle(RecallQuery request, CancellationToken cancellationToken)
    {
        var code = request.BatchCode.Trim().ToUpperInvariant();
        var json = $$"""[{"code":"{{code.Replace("\"", string.Empty, StringComparison.Ordinal)}}"}]""";
        var orders = await db.Set<Order>().FromSql($"""
            SELECT DISTINCT o.* FROM ordering.orders o JOIN ordering.order_lines l ON l.order_id = o.id
            WHERE l.batch_allocations @> {json}::jsonb
            """).AsNoTracking().Include(o => o.Lines).ToListAsync(cancellationToken);
        return [.. orders.OrderByDescending(o => o.PlacedAt).Select(Row)];
    }

    public async Task<DashboardView> Handle(DashboardQuery request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var paid = new[] { OrderStatuses.Confirmed, OrderStatuses.Preparing, OrderStatuses.Shipped, OrderStatuses.Delivered };
        var recent = await db.Set<Order>().AsNoTracking().Include(o => o.Lines).Where(o => o.PlacedAt >= now.AddDays(-30)).ToListAsync(cancellationToken);
        var counted = recent.Where(o => paid.Contains(o.Status)).ToList();

        return new DashboardView(
            recent.Count(o => o.PlacedAt >= today && o.Status != OrderStatuses.Cancelled),
            await db.Set<Order>().CountAsync(o => o.Status == OrderStatuses.Confirmed, cancellationToken),
            recent.Count(o => o.Status == OrderStatuses.PendingPayment),
            counted.Where(o => o.PlacedAt >= now.AddDays(-7)).Sum(o => o.Total),
            counted.Count,
            counted.Count == 0 ? 0 : counted.Count(HasBothSections) * 10_000 / counted.Count, // The one-brand metric (product-domain.md §5), in basis points.
            await db.Set<ReturnRequest>().CountAsync(r => r.Status == ReturnStatuses.Requested || r.Status == ReturnStatuses.Approved, cancellationToken),
            recent.Count(o => o.FraudFlags.Count > 0 && o.Status == OrderStatuses.Confirmed));
    }

    private IQueryable<Order> Orders() => db.Set<Order>().Include(o => o.Lines).Include(o => o.History).AsSplitQuery();

    private static bool HasBothSections(Order o) => o.Lines.Any(l => l.Section == "perfume") && o.Lines.Any(l => l.Section == "honey");

    private static AdminOrderRow Row(Order o) => new(
        o.Id, o.Number, o.Status, o.PaymentMethod, o.Total, o.Currency, o.Email, o.ShippingAddress.ProvinceName, o.PlacedAt,
        o.Lines.Sum(l => l.Qty), o.FraudFlags.Count > 0, HasBothSections(o));
}
