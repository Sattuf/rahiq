using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Ordering.Domain;

namespace Rahiq.Domain.Tests.Ordering;

internal static class OrderFactory
{
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    public static readonly AddressData Address = new()
    {
        FullName = "Ayşe Yılmaz",
        Phone = "+905551112233",
        ProvinceCode = 34,
        ProvinceName = "İstanbul",
        District = "Kadıköy",
        Line1 = "Moda Cd. 1",
    };

    public static OrderLineDraft Line(long unit = 45_000, int qty = 1, long discount = 0, string section = "perfume", Guid? variant = null) =>
        new(variant ?? Guid.NewGuid(), Guid.NewGuid(), "RHQ-OUD-50", section, section == "perfume" ? "perfume" : "honey", "Oud & Rose", "50 ml",
            ["Flammable."], false, "flammable", null, null, unit, qty, discount, 2_000, 0, (unit * qty) - discount, section == "perfume");

    public static OrderDraft Draft(string method = PaymentMethods.Card, params OrderLineDraft[] lines)
    {
        lines = lines.Length == 0 ? [Line()] : lines;
        var subtotal = lines.Sum(l => l.UnitPrice * l.Qty);
        var discount = lines.Sum(l => l.Discount);
        var cod = method == PaymentMethods.CashOnDelivery ? 4_990 : 0;
        return new OrderDraft
        {
            Number = "RHQ-26-000001",
            CheckoutId = Guid.NewGuid(),
            Email = "ayse@example.com",
            PaymentMethod = method,
            Currency = "TRY",
            Subtotal = subtotal,
            Discount = discount,
            Shipping = 8_990,
            CodFee = cod,
            Tax = 0,
            Total = subtotal - discount + 8_990 + cod,
            ShippingMethod = "standard",
            ShippingAddress = Address,
            BillingAddress = Address,
            Locale = "tr",
            ContractHash = "HASH",
            Lines = lines,
        };
    }

    public static Order Place(string method = PaymentMethods.Card, params OrderLineDraft[] lines) => Order.Place(Draft(method, lines), Now).Value;
}

public class OrderTests
{
    private static readonly DateTimeOffset Now = OrderFactory.Now;

    [Fact]
    public void A_card_order_waits_for_payment_and_a_cod_order_is_confirmed_at_once()
    {
        var card = OrderFactory.Place();
        var cod = OrderFactory.Place(PaymentMethods.CashOnDelivery);

        Assert.Equal(OrderStatuses.PendingPayment, card.Status);
        Assert.Equal(OrderStatuses.Confirmed, cod.Status);
        Assert.Contains(cod.DomainEvents, e => e is OrderConfirmed);
        Assert.DoesNotContain(card.DomainEvents, e => e is OrderConfirmed);
    }

    [Fact]
    public void Totals_that_do_not_add_up_are_rejected()
    {
        var bad = OrderFactory.Draft() with { Total = 1 };

        Assert.Equal("order.totals_mismatch", Order.Place(bad, Now).Error.Code);
        Assert.Equal("order.empty", Order.Place(OrderFactory.Draft() with { Lines = [] }, Now).Error.Code);
    }

    [Fact]
    public void The_happy_path_follows_the_state_machine()
    {
        var order = OrderFactory.Place();

        Assert.True(order.MarkPaid(Now).IsSuccess);
        Assert.True(order.StartPreparing(null, Now).IsSuccess);
        Assert.True(order.MarkShipped("sandbox", "TRK1", null, Now).IsSuccess);
        Assert.True(order.MarkDelivered(Now).IsSuccess);
        Assert.True(order.MarkReturnRequested(Now).IsSuccess);
        Assert.True(order.MarkReturned(null, Now).IsSuccess);
        Assert.True(order.MarkRefunded(null, Now).IsSuccess);
        Assert.Equal(OrderStatuses.Refunded, order.Status);
        Assert.Equal(8, order.History.Count);
    }

    [Theory]
    [InlineData(OrderStatuses.PendingPayment, OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.Delivered)]
    [InlineData(OrderStatuses.Preparing, OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Delivered, OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Cancelled, OrderStatuses.Confirmed)]
    [InlineData(OrderStatuses.Refunded, OrderStatuses.Delivered)]
    public void Forbidden_transitions_are_refused(string from, string to) => Assert.False(Order.CanMove(from, to));

    [Fact]
    public void Transitions_out_of_order_fail_with_a_clear_error()
    {
        var order = OrderFactory.Place();

        var result = order.MarkShipped("sandbox", "TRK", null, Now);

        Assert.Equal("order.invalid_transition", result.Error.Code);
        Assert.Equal(OrderStatuses.PendingPayment, order.Status);
    }

    [Fact]
    public void Cancelling_a_paid_order_says_money_must_go_back()
    {
        var order = OrderFactory.Place();
        order.MarkPaid(Now);
        order.ClearDomainEvents();

        order.Cancel(CancelReasons.Staff, Guid.NewGuid(), Now);

        var cancelled = Assert.IsType<OrderCancelled>(Assert.Single(order.DomainEvents));
        Assert.True(cancelled.WasPaid);
    }

    [Fact]
    public void Only_a_timeout_cancellation_can_be_revived_by_a_late_payment()
    {
        var timedOut = OrderFactory.Place();
        timedOut.Cancel(CancelReasons.PaymentExpired, null, Now);
        var byCustomer = OrderFactory.Place();
        byCustomer.Cancel(CancelReasons.Customer, null, Now);

        Assert.True(timedOut.ReviveAfterLatePayment(Now).IsSuccess);
        Assert.Equal(OrderStatuses.Confirmed, timedOut.Status);
        Assert.False(byCustomer.ReviveAfterLatePayment(Now).IsSuccess);
        Assert.False(OrderFactory.Place().ReviveAfterLatePayment(Now).IsSuccess);
    }

    [Fact]
    public void Returned_to_sender_and_rejected_returns_are_modelled()
    {
        var order = OrderFactory.Place(PaymentMethods.CashOnDelivery);
        order.StartPreparing(null, Now);
        order.MarkShipped("sandbox", "T", null, Now);

        Assert.True(order.MarkReturnedToSender(Now).IsSuccess);
        Assert.Contains(order.DomainEvents, e => e is OrderReturnedToSender);

        var delivered = OrderFactory.Place(PaymentMethods.CashOnDelivery);
        delivered.StartPreparing(null, Now);
        delivered.MarkShipped("sandbox", "T", null, Now);
        delivered.MarkDelivered(Now);
        delivered.MarkReturnRequested(Now);
        Assert.True(delivered.ReopenAfterRejectedReturn(null, Now).IsSuccess);
        Assert.Equal(OrderStatuses.Delivered, delivered.Status);
    }

    [Fact]
    public void Batches_are_split_across_lines_of_the_same_variant()
    {
        var variant = Guid.NewGuid();
        var order = OrderFactory.Place(PaymentMethods.Card, OrderFactory.Line(qty: 2, variant: variant), OrderFactory.Line(qty: 1, variant: variant));
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        order.AssignBatches([(variant, a, "A", 1), (variant, b, "B", 2)]);

        Assert.Equal([("A", 1), ("B", 1)], order.Lines[0].BatchAllocations!.Select(x => (x.Code, x.Qty)));
        Assert.Equal([("B", 1)], order.Lines[1].BatchAllocations!.Select(x => (x.Code, x.Qty)));
    }

    [Fact]
    public void Fraud_flags_are_recorded_once()
    {
        var order = OrderFactory.Place();

        order.AddFraudFlag("big_first_order");
        order.AddFraudFlag("big_first_order");

        Assert.Single(order.FraudFlags);
    }
}

public class ReturnPolicyTests
{
    private static readonly DateTimeOffset Now = OrderFactory.Now;

    private static Order Delivered(params OrderLineDraft[] lines)
    {
        var order = OrderFactory.Place(PaymentMethods.Card, lines);
        order.MarkPaid(Now);
        order.StartPreparing(null, Now);
        order.MarkShipped("sandbox", "T", null, Now);
        order.MarkDelivered(Now);
        return order;
    }

    [Fact]
    public void Honey_cannot_be_returned_on_withdrawal_but_can_if_damaged_with_photos()
    {
        var order = Delivered(OrderFactory.Line(section: "honey"));
        var lines = new[] { new ReturnLine(order.Lines[0].Id, 1, true) };

        Assert.Equal("return.line_not_returnable", ReturnPolicy.Check(order, ReturnKinds.Withdrawal, lines, 0, Now, Now, 14).Error.Code);
        Assert.Equal("return.photos_required", ReturnPolicy.Check(order, ReturnKinds.Damaged, lines, 0, Now, Now, 14).Error.Code);
        Assert.True(ReturnPolicy.Check(order, ReturnKinds.Damaged, lines, 2, Now, Now, 14).IsSuccess);
    }

    [Fact]
    public void Perfume_needs_its_seal_intact_and_the_window_open()
    {
        var order = Delivered(OrderFactory.Line());
        var sealedLine = new[] { new ReturnLine(order.Lines[0].Id, 1, true) };
        var opened = new[] { new ReturnLine(order.Lines[0].Id, 1, false) };

        Assert.True(ReturnPolicy.Check(order, ReturnKinds.Withdrawal, sealedLine, 0, Now, Now.AddDays(10), 14).IsSuccess);
        Assert.Equal("return.seal_broken", ReturnPolicy.Check(order, ReturnKinds.Withdrawal, opened, 0, Now, Now, 14).Error.Code);
        Assert.Equal("return.window_closed", ReturnPolicy.Check(order, ReturnKinds.Withdrawal, sealedLine, 0, Now, Now.AddDays(15), 14).Error.Code);
    }

    [Fact]
    public void Quantities_and_status_are_checked()
    {
        var order = Delivered(OrderFactory.Line());

        Assert.Equal("return.qty_invalid", ReturnPolicy.Check(order, ReturnKinds.Damaged, [new(order.Lines[0].Id, 2, true)], 1, Now, Now, 14).Error.Code);
        Assert.Equal("return.no_lines", ReturnPolicy.Check(order, ReturnKinds.Damaged, [], 1, Now, Now, 14).Error.Code);
        Assert.Equal("return.not_delivered", ReturnPolicy.Check(OrderFactory.Place(), ReturnKinds.Damaged, [], 1, Now, Now, 14).Error.Code);
    }

    [Fact]
    public void Refund_is_what_was_paid_for_the_returned_quantity()
    {
        var order = Delivered(OrderFactory.Line(unit: 10_000, qty: 3, discount: 3_000));
        var request = ReturnRequest.Open(order.Id, ReturnKinds.Damaged, "Broken jar", [new(order.Lines[0].Id, 1, false)], ["p.jpg"], Now);

        Assert.Equal(9_000, request.ComputeRefund(order));
        Assert.True(request.Approve(null, Now).IsSuccess);
        Assert.True(request.MarkReceived("ok", Now).IsSuccess);
        Assert.True(request.MarkRefunded(9_000, Now).IsSuccess);
        Assert.False(request.Reject("late", Now).IsSuccess);
        Assert.True(ReturnPolicy.IsReturnableOnWithdrawal("perfume"));
        Assert.False(ReturnPolicy.IsReturnableOnWithdrawal("honey"));
    }
}
