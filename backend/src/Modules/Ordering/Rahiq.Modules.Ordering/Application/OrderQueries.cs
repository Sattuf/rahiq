using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Cart.Contracts;
using Rahiq.Modules.Documents.Contracts;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Ordering.Domain;
using Rahiq.Modules.Ordering.Infrastructure;
using Rahiq.Modules.Payments.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Ordering.Application;

public sealed record OrderLineView(
    Guid Id, Guid VariantId, string Sku, string Name, string VariantLabel, string Section, int Qty, long UnitPrice, long Discount, long LineTotal,
    int TaxRateBp, IReadOnlyList<string> Warnings, string? GroupLabel, bool Returnable, IReadOnlyList<OrderBatch> Batches);

public sealed record StatusChangeView(string Status, string? Note, DateTimeOffset At);

public sealed record OrderView(
    Guid Id, string Number, string Status, string PaymentMethod, string? PaymentStatus, int Installments, string Currency,
    long Subtotal, long Discount, long Shipping, long CodFee, long Tax, long Total, string Email, string? Phone, AddressData ShippingAddress,
    bool IsGift, string? GiftMessage, bool HidePrices, string Locale, DateTimeOffset PlacedAt, IReadOnlyList<OrderLineView> Lines,
    IReadOnlyList<StatusChangeView> History, string? TrackingNumber, bool CanRequestReturn, IReadOnlyList<string> FraudFlags);

public sealed record OrderSummaryView(Guid Id, string Number, string Status, long Total, string Currency, DateTimeOffset PlacedAt, int Items, string FirstItem);

/// <summary>
/// The result page polls this every two seconds (commerce-flows.md §5). The truth is the webhook, not the browser.
/// Readable by the customer, by number + e-mail, or by whoever holds the cart the order was placed from.
/// </summary>
public sealed record OrderStatusQuery(string Number, string? Email, Guid? CustomerId, string? CartToken = null) : IQuery<Result<OrderStatusView>>;

public sealed record OrderStatusView(string Number, string Status, string PaymentMethod, string? PaymentStatus, long Total, string Currency);

/// <summary>Guest tracking needs the order number AND the e-mail together, and is rate limited (security.md §3).</summary>
public sealed record TrackOrderQuery(string Number, string Email) : IQuery<Result<OrderView>>;

public sealed record MyOrdersQuery(Guid CustomerId) : IQuery<IReadOnlyList<OrderSummaryView>>;

public sealed record MyOrderQuery(Guid CustomerId, string Number) : IQuery<Result<OrderView>>;

public sealed record OrderDocumentsQuery(string Number, string? Email, Guid? CustomerId) : IQuery<Result<IReadOnlyList<StoredDocument>>>;

public sealed record ReorderCommand(Guid CustomerId, string Number) : ICommand<Result<int>>;

internal sealed class OrderQueryHandlers(RahiqDbContext db, IPaymentGateway payments, IContractDocuments documents, ICartAccess carts, IClock clock, IOptions<StoreOptions> store)
    : IRequestHandler<OrderStatusQuery, Result<OrderStatusView>>,
      IRequestHandler<TrackOrderQuery, Result<OrderView>>,
      IRequestHandler<MyOrdersQuery, IReadOnlyList<OrderSummaryView>>,
      IRequestHandler<MyOrderQuery, Result<OrderView>>,
      IRequestHandler<OrderDocumentsQuery, Result<IReadOnlyList<StoredDocument>>>,
      IRequestHandler<ReorderCommand, Result<int>>
{
    public async Task<Result<OrderStatusView>> Handle(OrderStatusQuery request, CancellationToken cancellationToken)
    {
        var order = await Find(request.Number, request.Email, request.CustomerId, cancellationToken)
            ?? await FindByCart(request.Number, request.CartToken, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        var payment = await payments.GetAsync(order.Id, cancellationToken);
        return new OrderStatusView(order.Number, order.Status, order.PaymentMethod, payment?.Status, order.Total, order.Currency);
    }

    public async Task<Result<OrderView>> Handle(TrackOrderQuery request, CancellationToken cancellationToken)
    {
        var order = await Find(request.Number, request.Email, null, cancellationToken);
        return order is null ? OrderErrors.NotFound : await View(order, cancellationToken);
    }

    public async Task<IReadOnlyList<OrderSummaryView>> Handle(MyOrdersQuery request, CancellationToken cancellationToken) =>
        [.. (await db.Set<Order>().AsNoTracking().Include(o => o.Lines).Where(o => o.CustomerId == request.CustomerId)
            .OrderByDescending(o => o.PlacedAt).Take(100).ToListAsync(cancellationToken))
            .Select(o => new OrderSummaryView(o.Id, o.Number, o.Status, o.Total, o.Currency, o.PlacedAt, o.Lines.Sum(l => l.Qty), o.Lines.OrderBy(l => l.SortOrder).First().NameSnapshot))];

    public async Task<Result<OrderView>> Handle(MyOrderQuery request, CancellationToken cancellationToken)
    {
        var order = await Find(request.Number, null, request.CustomerId, cancellationToken);
        return order is null ? OrderErrors.NotFound : await View(order, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<StoredDocument>>> Handle(OrderDocumentsQuery request, CancellationToken cancellationToken)
    {
        var order = await Find(request.Number, request.Email, request.CustomerId, cancellationToken);
        return order is null ? OrderErrors.NotFound : Result.Success(await documents.LoadAsync(order.Id, cancellationToken));
    }

    /// <summary>"Reorder my usual honey" in one click: single items go back to the cart; bundles and gift boxes are skipped.</summary>
    public async Task<Result<int>> Handle(ReorderCommand request, CancellationToken cancellationToken)
    {
        var order = await Find(request.Number, null, request.CustomerId, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        var items = order.Lines.Where(l => l.GroupId is null).Select(l => new ReorderItem(l.VariantId, l.Qty, null)).ToList();
        return await carts.AddItemsAsync(request.CustomerId, items, cancellationToken);
    }

    private async Task<Order?> FindByCart(string number, string? cartToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cartToken))
        {
            return null;
        }

        var cartId = await carts.ResolveAsync(cartToken, null, cancellationToken);
        if (cartId is null)
        {
            return null;
        }

        var normalized = number.Trim().ToUpperInvariant();
        var order = await db.Set<Order>().AsNoTracking().FirstOrDefaultAsync(o => o.Number == normalized, cancellationToken);
        var owns = order is not null && await db.Set<Checkout>().AnyAsync(c => c.Id == order.CheckoutId && c.CartId == cartId, cancellationToken);
        return owns ? order : null;
    }

    private async Task<Order?> Find(string number, string? email, Guid? customerId, CancellationToken cancellationToken)
    {
        var normalized = number.Trim().ToUpperInvariant();
        var order = await db.Set<Order>().AsNoTracking().Include(o => o.Lines).Include(o => o.History).AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Number == normalized, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var byCustomer = customerId is not null && order.CustomerId == customerId;
        var byEmail = !string.IsNullOrWhiteSpace(email) && string.Equals(order.Email, email.Trim(), StringComparison.OrdinalIgnoreCase);
        return byCustomer || byEmail ? order : null;
    }

    private async Task<OrderView> View(Order o, CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(o.Id, cancellationToken);
        return ToView(o, payment, clock.UtcNow, store.Value.ReturnWindowDays);
    }

    internal static OrderView ToView(Order o, PaymentStatusInfo? payment, DateTimeOffset now, int returnWindowDays)
    {
        var delivered = o.History.LastOrDefault(h => h.Status == OrderStatuses.Delivered)?.At;
        var shipped = o.History.LastOrDefault(h => h.Status == OrderStatuses.Shipped)?.Note;
        return new OrderView(
            o.Id, o.Number, o.Status, o.PaymentMethod, payment?.Status, payment?.Installments ?? 1, o.Currency,
            o.Subtotal, o.Discount, o.Shipping, o.CodFee, o.Tax, o.Total, o.Email, o.Phone, o.ShippingAddress, o.IsGift, o.GiftMessage, o.HidePrices,
            o.Locale, o.PlacedAt,
            [.. o.Lines.OrderBy(l => l.SortOrder).Select(l => new OrderLineView(l.Id, l.VariantId, l.Sku, l.NameSnapshot, l.VariantLabelSnapshot, l.Section,
                l.Qty, l.UnitPrice, l.Discount, l.LineTotal, l.TaxRateBp, l.WarningsSnapshot, l.GroupLabelSnapshot, l.Returnable, l.BatchAllocations ?? []))],
            [.. o.History.OrderBy(h => h.At).Select(h => new StatusChangeView(h.Status, h.Status == OrderStatuses.Cancelled || h.Status == OrderStatuses.Shipped ? h.Note : null, h.At))],
            shipped?.Split(' ').LastOrDefault(),
            o.Status == OrderStatuses.Delivered && delivered is not null && now <= delivered.Value.AddDays(Math.Max(returnWindowDays, 30)),
            o.FraudFlags);
    }
}
