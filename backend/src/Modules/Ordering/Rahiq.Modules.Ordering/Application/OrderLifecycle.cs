using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Customers.Contracts;
using Rahiq.Modules.Inventory.Contracts;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Ordering.Domain;
using Rahiq.Modules.Payments.Contracts;
using Rahiq.Modules.Pricing.Contracts;
using Rahiq.Modules.Shipping.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Ordering.Application;

public sealed record PayResult(string Number, string PaymentMethod, string? RedirectUrl, long Total, string Currency);

/// <summary>
/// The light payment saga (architecture.md §5): 1) one transaction reserves stock, holds the coupon and creates the order;
/// 2) outside it, the provider's hosted page is opened; 3) if that fails, compensate by cancelling (which releases both).
/// Confirmation comes later, only from the provider's verified notification.
/// </summary>
public sealed class CheckoutPayment(ISender sender, IPaymentGateway payments, IOptions<StoreOptions> store)
{
    public async Task<Result<PayResult>> PayAsync(PlaceOrderCommand command, string clientIp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var placed = await sender.Send(command, cancellationToken);
        if (placed.IsFailure)
        {
            return placed.Error;
        }

        var order = placed.Value;
        if (order.PaymentMethod == PaymentMethods.CashOnDelivery)
        {
            return new PayResult(order.Number, order.PaymentMethod, null, order.Total, order.Currency);
        }

        var status = await payments.GetAsync(order.OrderId, cancellationToken);
        if (order.AlreadyPlaced && status?.Status != PaymentStatuses.Pending)
        {
            return new PayResult(order.Number, order.PaymentMethod, null, order.Total, order.Currency);
        }

        var details = await sender.Send(new GetOrderForPaymentQuery(order.OrderId), cancellationToken);
        var session = await payments.OpenSessionAsync(details with { Buyer = details.Buyer with { Ip = clientIp } }, cancellationToken);
        if (session.IsFailure)
        {
            await sender.Send(new CancelOrderCommand(order.OrderId, CancelReasons.PaymentFailed, null, "payment_page_unavailable"), cancellationToken);
            return session.Error;
        }

        return new PayResult(order.Number, order.PaymentMethod, session.Value.RedirectUrl, order.Total, order.Currency);
    }

    internal string ResultUrl(string number) => $"{store.Value.PublicWebUrl.TrimEnd('/')}/checkout/result?order={Uri.EscapeDataString(number)}";
}

internal sealed record GetOrderForPaymentQuery(Guid OrderId) : IQuery<PaymentRequest>;

public sealed record CancelOrderCommand(Guid OrderId, string Reason, Guid? ActorId, string? Note = null) : ICommand<Result>;

internal sealed class OrderLifecycleHandlers(
    RahiqDbContext db,
    IInventoryService inventory,
    ICouponUsage coupons,
    ICustomerDirectory customers,
    IEventPublisher events,
    IClock clock,
    IOptions<StoreOptions> store)
    : IRequestHandler<GetOrderForPaymentQuery, PaymentRequest>,
      IRequestHandler<CancelOrderCommand, Result>,
      IEventHandler<PaymentSucceeded>,
      IEventHandler<PaymentFailed>,
      IEventHandler<ReservationsExpired>,
      IEventHandler<ShipmentHandedOver>,
      IEventHandler<ShipmentStatusChanged>
{
    public async Task<PaymentRequest> Handle(GetOrderForPaymentQuery request, CancellationToken cancellationToken)
    {
        var order = await db.Set<Order>().AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == request.OrderId, cancellationToken);
        var a = order.BillingAddress;

        // The provider's basket must add up exactly to the amount; zero-priced lines are left out (providers reject them).
        var basket = order.Lines.Where(l => l.LineTotal > 0)
            .Select(l => new PaymentBasketItem(l.Id.ToString("N"), $"{l.NameSnapshot} {l.VariantLabelSnapshot}".Trim(), l.Section, l.LineTotal))
            .ToList();
        if (order.Shipping + order.CodFee > 0)
        {
            basket.Add(new PaymentBasketItem("shipping", "Kargo", "shipping", order.Shipping + order.CodFee));
        }

        return new PaymentRequest
        {
            OrderId = order.Id,
            OrderNumber = order.Number,
            Method = order.PaymentMethod,
            Amount = order.Total,
            Currency = order.Currency,
            Locale = order.Locale,
            Buyer = new PaymentBuyer(a.FullName, order.Email, order.Phone ?? a.Phone, a.ProvinceName, $"{a.Line1} {a.Line2} {a.District}".Trim(), null, "127.0.0.1"),
            Basket = basket,
        };
    }

    public async Task<Result> Handle(CancelOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await Load(request.OrderId, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        var wasConfirmed = order.Status == OrderStatuses.Confirmed;
        var cancelled = order.Cancel(request.Reason, request.ActorId, clock.UtcNow);
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        if (wasConfirmed)
        {
            await inventory.ReverseCommitAsync(order.Id, request.ActorId, cancellationToken);
        }
        else
        {
            await inventory.ReleaseAsync(order.Id, cancellationToken);
        }

        await coupons.ReleaseAsync(order.Id, cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// The verified payment confirms the order (ADR-006). If the hold expired meanwhile: reserve again; if the stock is
    /// gone, the money goes back automatically and staff are told (testing.md commerce test 6).
    /// </summary>
    public async Task Handle(PaymentSucceeded domainEvent, CancellationToken cancellationToken)
    {
        var order = await Load(domainEvent.OrderId, cancellationToken);
        if (order is null || order.Status is not (OrderStatuses.PendingPayment or OrderStatuses.Cancelled))
        {
            return; // Already confirmed (duplicate) or further along.
        }

        if (order.Status == OrderStatuses.Cancelled && order.CancelReason != CancelReasons.PaymentExpired)
        {
            events.Publish(new OrderRefundRequired(order.Id, order.Number, domainEvent.Amount, "paid_after_cancellation"));
            return;
        }

        var committed = await inventory.CommitAsync(order.Id, cancellationToken);
        if (committed.Count == 0)
        {
            var again = await inventory.ReserveAsync(order.CheckoutId, order.Id,
                [.. order.Lines.GroupBy(l => l.VariantId).Select(g => new StockRequest(g.Key, g.Sum(l => l.Qty)))],
                clock.UtcNow.AddMinutes(store.Value.ReservationMinutes), cancellationToken);
            if (again.IsFailure)
            {
                if (order.Status == OrderStatuses.PendingPayment)
                {
                    order.Cancel(CancelReasons.OutOfStock, null, clock.UtcNow);
                    await coupons.ReleaseAsync(order.Id, cancellationToken);
                }

                events.Publish(new OrderRefundRequired(order.Id, order.Number, domainEvent.Amount, "stock_gone_after_expiry"));
                return;
            }

            committed = await inventory.CommitAsync(order.Id, cancellationToken);
        }

        var confirmed = order.Status == OrderStatuses.Cancelled ? order.ReviveAfterLatePayment(clock.UtcNow) : order.MarkPaid(clock.UtcNow);
        if (confirmed.IsSuccess)
        {
            order.AssignBatches([.. committed.Select(a => (a.VariantId, a.BatchId, a.BatchCode, a.Qty))]);
            await coupons.CommitAsync(order.Id, cancellationToken);
        }
    }

    public async Task Handle(PaymentFailed domainEvent, CancellationToken cancellationToken)
    {
        var order = await Load(domainEvent.OrderId, cancellationToken);
        if (order?.Status == OrderStatuses.PendingPayment)
        {
            order.Cancel(CancelReasons.PaymentFailed, null, clock.UtcNow);
            await inventory.ReleaseAsync(order.Id, cancellationToken);
            await coupons.ReleaseAsync(order.Id, cancellationToken);
        }
    }

    /// <summary>The release worker freed the stock; orders still waiting for payment are cancelled (commerce test 2).</summary>
    public async Task Handle(ReservationsExpired domainEvent, CancellationToken cancellationToken)
    {
        var orders = await db.Set<Order>().Include(o => o.Lines).Include(o => o.History)
            .Where(o => domainEvent.OrderIds.Contains(o.Id) && o.Status == OrderStatuses.PendingPayment).ToListAsync(cancellationToken);
        foreach (var order in orders)
        {
            order.Cancel(CancelReasons.PaymentExpired, null, clock.UtcNow);
            await coupons.ReleaseAsync(order.Id, cancellationToken);
        }
    }

    public async Task Handle(ShipmentHandedOver domainEvent, CancellationToken cancellationToken)
    {
        var order = await Load(domainEvent.OrderId, cancellationToken);
        order?.MarkShipped(domainEvent.Carrier, domainEvent.TrackingNumber, domainEvent.TrackingUrl, clock.UtcNow);
    }

    public async Task Handle(ShipmentStatusChanged domainEvent, CancellationToken cancellationToken)
    {
        var order = await Load(domainEvent.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        if (domainEvent.Status == ShipmentStatuses.Delivered)
        {
            order.MarkDelivered(clock.UtcNow);
        }
        else if (domainEvent.Status == ShipmentStatuses.Returned && order.MarkReturnedToSender(clock.UtcNow).IsSuccess
            && order.PaymentMethod == PaymentMethods.CashOnDelivery)
        {
            // A refused COD parcel counts; two disable cash on delivery for this customer (commerce-flows.md §6).
            await customers.RecordCodRefusalAsync(order.Email, cancellationToken);
        }
    }

    private Task<Order?> Load(Guid id, CancellationToken cancellationToken) =>
        db.Set<Order>().Include(o => o.Lines).Include(o => o.History).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
}
