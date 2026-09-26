using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Notifications.Contracts;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Payments.Contracts;
using Rahiq.Modules.Payments.Domain;
using Rahiq.Modules.Payments.Infrastructure;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Payments.Application;

internal sealed class WebhookEventRow
{
    public required string Provider { get; init; }

    public required string EventId { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? Outcome { get; set; }

    public required string Payload { get; init; }
}

internal sealed class ReconciliationRun
{
    public Guid Id { get; init; }

    public DateOnly Day { get; init; }

    public required string Provider { get; init; }

    public required string Status { get; init; }

    public required string Discrepancies { get; init; }

    public DateTimeOffset RanAt { get; init; }
}

internal sealed class PaymentsModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payments", "payments");
            b.HasKey(p => p.Id);
            b.Ignore(p => p.DomainEvents);
            b.Ignore(p => p.Refundable);
            b.Property(p => p.Currency).HasColumnType("char(3)");
            b.Property(p => p.RawLastEvent).HasColumnType("jsonb");
        });
        modelBuilder.Entity<Refund>(b =>
        {
            b.ToTable("refunds", "payments");
            b.HasKey(r => r.Id);
            b.Ignore(r => r.DomainEvents);
        });
        modelBuilder.Entity<WebhookEventRow>(b =>
        {
            b.ToTable("webhook_events", "payments");
            b.HasKey(e => new { e.Provider, e.EventId });
            b.Property(e => e.Payload).HasColumnType("jsonb");
        });
        modelBuilder.Entity<ReconciliationRun>(b =>
        {
            b.ToTable("reconciliation_runs", "payments");
            b.HasKey(r => r.Id);
            b.Property(r => r.Discrepancies).HasColumnType("jsonb");
        });
    }
}

public sealed record NotificationOutcome(string Status, Guid? OrderId, string? OrderNumber);

/// <summary>A verified notification, applied once (commerce-flows.md §5, testing.md commerce tests 3-5).</summary>
internal sealed record ApplyNotificationCommand(string Provider, ProviderNotification Notification) : ICommand<Result<NotificationOutcome>>;

public sealed record ReceiveWebhookCommand(string Provider, string Body, IReadOnlyDictionary<string, string> Form, IReadOnlyDictionary<string, string> Headers)
    : ICommand<Result<NotificationOutcome>>;

public sealed record PollPendingPaymentsCommand : ICommand<Result<int>>;

public sealed record ReconcilePaymentsCommand : ICommand<Result<int>>;

internal sealed class PaymentGateway(
    RahiqDbContext db,
    IEnumerable<IPaymentProvider> providers,
    IOptions<PaymentOptions> options,
    IOptions<StoreOptions> store,
    IAuditLog audit,
    IClock clock) : IPaymentGateway
{
    private IPaymentProvider Provider => providers.First(p => p.Name == options.Value.Provider);

    public Task<Guid> RegisterAsync(Guid orderId, string orderNumber, string method, long amount, string currency, CancellationToken cancellationToken)
    {
        var payment = Payment.Create(orderId, orderNumber, method == "cod" ? "cod" : Provider.Name, method, amount, currency, clock.UtcNow);
        db.Add(payment);
        return Task.FromResult(payment.Id);
    }

    public async Task<Result<PaymentStart>> OpenSessionAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payment = await db.Set<Payment>().FirstOrDefaultAsync(p => p.OrderId == request.OrderId && p.Method == "card", cancellationToken);
        if (payment is null || payment.Status != PaymentStatuses.Pending)
        {
            return PaymentErrors.NotFound;
        }

        if (payment.Amount != request.Amount || request.Basket.Sum(i => i.Price) != request.Amount)
        {
            return PaymentErrors.AmountMismatch; // The amount is the server's, and the basket must add up to it.
        }

        try
        {
            var callback = $"{store.Value.PublicApiUrl.TrimEnd('/')}/webhooks/payments/{Provider.Name}";
            var session = await Provider.CreateSessionAsync(payment, request, callback, cancellationToken);
            payment.AttachSession(session.Token, session.ProviderRef, clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return new PaymentStart(payment.Id, session.RedirectUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return new Error("payment.provider_unavailable", "The payment page could not be opened. Please try again in a moment.");
        }
    }

    public async Task<Result<RefundOutcome>> RefundAsync(Guid orderId, long amount, string reason, Guid? requestedBy, CancellationToken cancellationToken)
    {
        var payment = await db.Set<Payment>().FirstOrDefaultAsync(p => p.OrderId == orderId, cancellationToken);
        if (payment is null)
        {
            return PaymentErrors.NotFound;
        }

        var allowed = payment.CanRefund(amount);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var refund = Refund.Request(payment, amount, reason, requestedBy, clock.UtcNow);
        db.Add(refund);

        if (payment.Method == "cod")
        {
            // Cash was collected by the carrier: the refund is a bank transfer the staff makes, recorded here.
            refund.Succeed("manual_transfer", payment.Currency, clock.UtcNow);
        }
        else
        {
            try
            {
                var reference = await providers.First(p => p.Name == payment.Provider).RefundAsync(payment, amount, "127.0.0.1", cancellationToken);
                refund.Succeed(reference, payment.Currency, clock.UtcNow);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                refund.Fail(ex.Message, clock.UtcNow);
                audit.Record("refund.failed", "order", orderId.ToString(), new { amount, reason, error = ex.Message });
                return new Error("refund.provider_failed", "The provider did not accept the refund. It was logged; try again or refund manually.");
            }
        }

        payment.RecordRefund(amount, clock.UtcNow);
        audit.Record("refund.issued", "order", orderId.ToString(), new { amount, reason, payment.Method });
        return new RefundOutcome(refund.Id, amount, refund.Status);
    }

    public async Task<PaymentStatusInfo?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Set<Payment>().AsNoTracking().Where(p => p.OrderId == orderId).OrderByDescending(p => p.CreatedAt)
            .Select(p => new PaymentStatusInfo(p.Id, p.Method, p.Status, p.Amount, p.RefundedAmount, p.Installments, p.Provider))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AbandonAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var payment = await db.Set<Payment>().FirstOrDefaultAsync(p => p.OrderId == orderId, cancellationToken);
        payment?.Abandon(clock.UtcNow);
    }
}

internal sealed class PaymentHandlers(
    RahiqDbContext db,
    IDbSession session,
    IEnumerable<IPaymentProvider> providers,
    IPaymentGateway gateway,
    IOptions<PaymentOptions> options,
    IEventPublisher events,
    ISender sender,
    IClock clock)
    : IRequestHandler<ReceiveWebhookCommand, Result<NotificationOutcome>>,
      IRequestHandler<ApplyNotificationCommand, Result<NotificationOutcome>>,
      IRequestHandler<PollPendingPaymentsCommand, Result<int>>,
      IRequestHandler<ReconcilePaymentsCommand, Result<int>>,
      IEventHandler<OrderCancelled>,
      IEventHandler<OrderRefundRequired>,
      IEventHandler<OrderDelivered>
{
    public async Task<Result<NotificationOutcome>> Handle(ReceiveWebhookCommand request, CancellationToken cancellationToken)
    {
        var provider = providers.FirstOrDefault(p => p.Name == request.Provider);
        if (provider is null)
        {
            return Error.NotFound("payment.provider_unknown", "Unknown provider.");
        }

        var notification = await provider.VerifyAsync(new ProviderInbound(request.Body, request.Form, request.Headers), cancellationToken);
        if (notification is null)
        {
            return Error.Unauthorized("payment.signature_invalid", "The notification could not be verified.");
        }

        return await sender.Send(new ApplyNotificationCommand(provider.Name, notification), cancellationToken);
    }

    public async Task<Result<NotificationOutcome>> Handle(ApplyNotificationCommand request, CancellationToken cancellationToken)
    {
        var n = request.Notification;
        await session.EnsureOpenAsync(cancellationToken);

        // (provider, event_id) is the primary key: a repeated event inserts nothing and changes nothing.
        var inserted = await session.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO payments.webhook_events (provider, event_id, payload) VALUES (@provider, @eventId, @payload::jsonb) ON CONFLICT DO NOTHING
            """, new { provider = request.Provider, eventId = n.EventId, payload = PayloadJson(n.Raw) }, session.Transaction, cancellationToken: cancellationToken));

        var payment = await db.Set<Payment>().FirstOrDefaultAsync(p => p.Provider == request.Provider && p.SessionToken == n.SessionToken, cancellationToken);
        if (inserted == 0)
        {
            return new NotificationOutcome("duplicate", payment?.OrderId, payment?.OrderNumber);
        }

        string outcome;
        if (payment is null)
        {
            outcome = "unknown_payment";
            events.Publish(new StaffAlertRaised(AlertKinds.ReconciliationDiscrepancy, "Payment for unknown session", $"{request.Provider} event {n.EventId} matches no payment.", n.EventId));
        }
        else if (n.Succeeded)
        {
            var applied = payment.ApplySuccess(n.Amount, n.Currency, n.Installments, n.ProviderRef, n.Raw, clock.UtcNow);
            outcome = applied.IsSuccess ? "succeeded" : "amount_mismatch";
            if (applied.IsFailure)
            {
                events.Publish(new StaffAlertRaised(AlertKinds.PaymentAmountMismatch, "Payment amount mismatch",
                    $"Order {payment.OrderNumber}: expected {payment.Amount} {payment.Currency}, provider reported {n.Amount} {n.Currency}. The order was NOT confirmed.", payment.OrderNumber));
            }
        }
        else
        {
            payment.ApplyFailure(n.FailureReason ?? "declined", n.Raw, clock.UtcNow);
            outcome = "failed";
        }

        await session.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE payments.webhook_events SET processed_at = now(), outcome = @outcome WHERE provider = @provider AND event_id = @eventId",
            new { outcome, provider = request.Provider, eventId = n.EventId }, session.Transaction, cancellationToken: cancellationToken));
        return new NotificationOutcome(outcome, payment?.OrderId, payment?.OrderNumber);
    }

    /// <summary>A card payment still pending 10 minutes after starting: ask the provider (risks-troubleshooting.md).</summary>
    public async Task<Result<int>> Handle(PollPendingPaymentsCommand request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var pending = await db.Set<Payment>().Where(p => p.Method == "card" && p.Status == PaymentStatuses.Pending && p.SessionToken != null
            && p.CreatedAt < now.AddMinutes(-10) && p.CreatedAt > now.AddDays(-2)).Take(50).ToListAsync(cancellationToken);
        var resolved = 0;

        foreach (var payment in pending)
        {
            var provider = providers.FirstOrDefault(p => p.Name == payment.Provider);
            var notification = provider is null ? null : await provider.RetrieveAsync(payment, cancellationToken);
            if (notification is not null)
            {
                var result = await Handle(new ApplyNotificationCommand(payment.Provider, notification with { EventId = "poll:" + notification.EventId }), cancellationToken);
                resolved += result.IsSuccess ? 1 : 0;
            }
            else if (payment.CreatedAt < now.AddMinutes(-30) && payment.FailureReason is null)
            {
                events.Publish(new StaffAlertRaised(AlertKinds.WebhookMissing, "Payment still pending",
                    $"Order {payment.OrderNumber} has waited more than 30 minutes for the provider's notification.", payment.OrderNumber));
                payment.ApplyFailure("no_notification", "{}", now);
            }
        }

        return resolved;
    }

    /// <summary>Nightly: the provider's transactions against our payments. Any difference is an alert (commerce-flows.md §12).</summary>
    public async Task<Result<int>> Handle(ReconcilePaymentsCommand request, CancellationToken cancellationToken)
    {
        var day = clock.Today.AddDays(-1);
        var providerName = options.Value.Provider;
        if (await db.Set<ReconciliationRun>().AnyAsync(r => r.Day == day && r.Provider == providerName, cancellationToken))
        {
            return 0;
        }

        var provider = providers.First(p => p.Name == providerName);
        var theirs = await provider.TransactionsAsync(day, cancellationToken);
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var ours = await db.Set<Payment>().AsNoTracking()
            .Where(p => p.Provider == providerName && p.Method == "card" && p.UpdatedAt >= from && p.UpdatedAt < from.AddDays(1)
                && (p.Status == PaymentStatuses.Succeeded || p.Status == PaymentStatuses.PartiallyRefunded || p.Status == PaymentStatuses.Refunded))
            .ToListAsync(cancellationToken);

        var discrepancies = new List<object>();
        foreach (var t in theirs)
        {
            var match = ours.FirstOrDefault(p => p.OrderNumber == t.OrderNumber);
            if (match is null)
            {
                discrepancies.Add(new { kind = "payment_without_order", t.OrderNumber, t.Amount });
            }
            else if (match.Amount != t.Amount || !string.Equals(match.Currency, t.Currency, StringComparison.OrdinalIgnoreCase))
            {
                discrepancies.Add(new { kind = "amount_differs", t.OrderNumber, ours = match.Amount, theirs = t.Amount });
            }
        }

        discrepancies.AddRange(ours.Where(p => theirs.All(t => t.OrderNumber != p.OrderNumber)).Select(p => (object)new { kind = "order_without_payment", p.OrderNumber, p.Amount }));

        db.Add(new ReconciliationRun
        {
            Id = Ids.New(),
            Day = day,
            Provider = providerName,
            Status = discrepancies.Count == 0 ? "clean" : "discrepancies",
            Discrepancies = JsonSerializer.Serialize(discrepancies, JsonDefaults.Options),
            RanAt = clock.UtcNow,
        });

        if (discrepancies.Count > 0)
        {
            events.Publish(new StaffAlertRaised(AlertKinds.ReconciliationDiscrepancy, $"Reconciliation {day:yyyy-MM-dd}: {discrepancies.Count} differences",
                JsonSerializer.Serialize(discrepancies, JsonDefaults.Options), day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return discrepancies.Count;
    }

    /// <summary>A paid order cancelled before shipping is refunded in full; an unpaid one stops waiting.</summary>
    public async Task Handle(OrderCancelled domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.WasPaid)
        {
            var status = await gateway.GetAsync(domainEvent.OrderId, cancellationToken);
            if (status is not null && status.Amount - status.RefundedAmount > 0)
            {
                var refunded = await gateway.RefundAsync(domainEvent.OrderId, status.Amount - status.RefundedAmount, $"order_cancelled:{domainEvent.Reason}", null, cancellationToken);
                if (refunded.IsFailure)
                {
                    events.Publish(new StaffAlertRaised(AlertKinds.RefundFailed, "Automatic refund failed", $"Order {domainEvent.Number}: {refunded.Error.Message}", domainEvent.Number));
                }
            }

            return;
        }

        await gateway.AbandonAsync(domainEvent.OrderId, cancellationToken);
    }

    /// <summary>Paid, but the stock was gone after the reservation expired: automatic refund and an alert (commerce test 6).</summary>
    public async Task Handle(OrderRefundRequired domainEvent, CancellationToken cancellationToken)
    {
        var refunded = await gateway.RefundAsync(domainEvent.OrderId, domainEvent.Amount, domainEvent.Reason, null, cancellationToken);
        events.Publish(new StaffAlertRaised(
            refunded.IsSuccess ? AlertKinds.LatePaymentRefund : AlertKinds.RefundFailed,
            refunded.IsSuccess ? "Late payment refunded" : "Late payment refund FAILED",
            $"Order {domainEvent.Number}: paid after its reservation expired and the stock was gone. {(refunded.IsSuccess ? "Refunded automatically." : refunded.Error.Message)}",
            domainEvent.Number));
    }

    public async Task Handle(OrderDelivered domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.PaymentMethod == PaymentMethods.CashOnDelivery)
        {
            var payment = await db.Set<Payment>().FirstOrDefaultAsync(p => p.OrderId == domainEvent.OrderId, cancellationToken);
            payment?.MarkCollected(clock.UtcNow);
        }
    }

    private static string PayloadJson(string raw)
    {
        try
        {
            using var _ = JsonDocument.Parse(raw);
            return raw;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { raw });
        }
    }
}
