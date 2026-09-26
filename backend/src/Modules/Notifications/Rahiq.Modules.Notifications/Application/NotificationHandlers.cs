using System.Text;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Modules.Customers.Contracts;
using Rahiq.Modules.Documents.Contracts;
using Rahiq.Modules.Notifications.Contracts;
using Rahiq.Modules.Notifications.Infrastructure;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Payments.Contracts;
using Rahiq.SharedKernel;
using static Rahiq.Modules.Notifications.Application.EmailTemplates;

namespace Rahiq.Modules.Notifications.Application;

/// <summary>Order and shipping messages are transactional: no marketing consent needed (compliance.md, İYS).</summary>
internal sealed class NotificationHandlers(
    Mailer mailer,
    IOrderReader orders,
    IContractDocuments documents,
    IOptions<EmailOptions> email,
    IOptions<StoreOptions> store)
    : IEventHandler<OrderConfirmed>,
      IEventHandler<OrderCancelled>,
      IEventHandler<OrderShipped>,
      IEventHandler<OrderDelivered>,
      IEventHandler<ReturnRequested>,
      IEventHandler<ReturnResolved>,
      IEventHandler<RefundIssued>,
      IEventHandler<InvoiceIssued>,
      IEventHandler<StaffAlertRaised>,
      IOtpDelivery
{
    private string TrackUrl(string locale, string number) => $"{store.Value.PublicWebUrl.TrimEnd('/')}/{locale}/track?order={Uri.EscapeDataString(number)}";

    public async Task Handle(OrderConfirmed e, CancellationToken cancellationToken)
    {
        var docs = await documents.LoadAsync(e.OrderId, cancellationToken);
        var body = $"<p>{T(e.Locale, "confirmed.body", e.Number)}</p>";
        if (e.PaymentMethod == PaymentMethods.CashOnDelivery)
        {
            body += $"<p>{T(e.Locale, "cod.note", Money(e.Total, e.Currency))}</p>";
        }

        var order = await orders.GetAsync(e.OrderId, cancellationToken);
        if (order is not null && !order.HidePrices)
        {
            body += "<table style=\"width:100%;border-collapse:collapse;margin-top:16px\">"
                + string.Concat(order.Lines.Select(l => $"<tr><td style=\"padding:4px 0\">{Html.E(l.Name)} · {Html.E(l.VariantLabel)} × {l.Qty}</td><td style=\"text-align:end\">{Money(l.LineTotal, order.Currency)}</td></tr>"))
                + $"<tr><td style=\"padding-top:8px;font-weight:700\">Σ</td><td style=\"text-align:end;font-weight:700\">{Money(order.Total, order.Currency)}</td></tr></table>";
        }

        var attachments = docs.Select(d => ($"{e.Number}-{d.Kind}.html", "text/html", Encoding.UTF8.GetBytes(d.Html))).ToList();
        await mailer.SendOnceAsync($"order_confirmed:{e.OrderId}", "order_confirmed", e.OrderId,
            new EmailMessage(e.Email, T(e.Locale, "confirmed.subject", e.Number), Layout(e.Locale, body, TrackUrl(e.Locale, e.Number)), attachments), cancellationToken);
    }

    public Task Handle(OrderCancelled e, CancellationToken cancellationToken) =>
        mailer.SendOnceAsync($"order_cancelled:{e.OrderId}", "order_cancelled", e.OrderId,
            new EmailMessage(e.Email, T(e.Locale, "cancelled.subject", e.Number),
                Layout(e.Locale, $"<p>{T(e.Locale, e.WasPaid ? "cancelled.body" : "cancelled.unpaid", e.Number)}</p>")), cancellationToken);

    public Task Handle(OrderShipped e, CancellationToken cancellationToken) =>
        mailer.SendOnceAsync($"order_shipped:{e.OrderId}", "order_shipped", e.OrderId,
            new EmailMessage(e.Email, T(e.Locale, "shipped.subject", e.Number),
                Layout(e.Locale, $"<p>{T(e.Locale, "shipped.body", e.Number, e.TrackingNumber)}</p>", e.TrackingUrl ?? TrackUrl(e.Locale, e.Number))), cancellationToken);

    public Task Handle(OrderDelivered e, CancellationToken cancellationToken) =>
        mailer.SendOnceAsync($"order_delivered:{e.OrderId}", "order_delivered", e.OrderId,
            new EmailMessage(e.Email, T(e.Locale, "delivered.subject", e.Number),
                Layout(e.Locale, $"<p>{T(e.Locale, "delivered.body", e.Number)}</p>", TrackUrl(e.Locale, e.Number))), cancellationToken);

    public Task Handle(ReturnRequested e, CancellationToken cancellationToken) =>
        mailer.SendOnceAsync($"return_requested:{e.ReturnId}", "return_requested", e.OrderId,
            new EmailMessage(e.Email, T(e.Locale, "return.subject", e.Number), Layout(e.Locale, $"<p>{T(e.Locale, "return.requested", e.Number)}</p>")), cancellationToken);

    public Task Handle(ReturnResolved e, CancellationToken cancellationToken) =>
        mailer.SendOnceAsync($"return_resolved:{e.ReturnId}:{e.Status}", "return_resolved", e.OrderId,
            new EmailMessage(e.Email, T(e.Locale, "return.subject", e.Number), Layout(e.Locale, $"<p>{T(e.Locale, "return.resolved", e.Number, e.Status)}</p>")), cancellationToken);

    public async Task Handle(RefundIssued e, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(e.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        await mailer.SendOnceAsync($"refund:{e.RefundId}", "refund_issued", e.OrderId,
            new EmailMessage(order.Email, T(order.Locale, "refund.subject", order.Number),
                Layout(order.Locale, $"<p>{T(order.Locale, "refund.body", order.Number, Money(e.Amount, e.Currency))}</p>")), cancellationToken);
    }

    public async Task Handle(InvoiceIssued e, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(e.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        await mailer.SendOnceAsync($"invoice:{e.OrderId}:{e.Kind}:{e.InvoiceNumber}", "invoice_issued", e.OrderId,
            new EmailMessage(order.Email, T(order.Locale, "invoice.subject", order.Number),
                Layout(order.Locale, $"<p>{T(order.Locale, "invoice.body", order.Number, e.InvoiceNumber)}</p>", TrackUrl(order.Locale, order.Number))), cancellationToken);
    }

    public async Task Handle(StaffAlertRaised e, CancellationToken cancellationToken)
    {
        foreach (var recipient in email.Value.StaffAlertRecipients)
        {
            await mailer.SendOnceAsync($"alert:{e.DedupeKey ?? e.EventId.ToString()}:{recipient}", $"alert:{e.Kind}", null,
                new EmailMessage(recipient, $"[Rahiq] {e.Subject}", Layout("en", $"<p><strong>{Html.E(e.Kind)}</strong></p><pre style=\"white-space:pre-wrap\">{Html.E(e.Body)}</pre>")), cancellationToken);
        }
    }

    public Task SendAsync(string emailAddress, string code, string locale, CancellationToken cancellationToken) =>
        mailer.SendOnceAsync($"otp:{Ids.New()}", "otp", null,
            new EmailMessage(emailAddress, T(locale, "otp.subject", code), Layout(locale, $"<p>{T(locale, "otp.body", code)}</p>")), cancellationToken);
}
