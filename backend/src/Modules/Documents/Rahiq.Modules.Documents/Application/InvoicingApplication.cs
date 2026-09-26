using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Documents.Contracts;
using Rahiq.Modules.Documents.Infrastructure;
using Rahiq.Modules.Notifications.Contracts;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Payments.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Documents.Application;

public sealed record RetryInvoicesCommand : ICommand<Result<int>>;

public sealed record InvoiceDto(string Kind, string? Number, string Status, long Amount, DateTimeOffset? IssuedAt);

public sealed record OrderInvoicesQuery(Guid OrderId) : IQuery<IReadOnlyList<InvoiceDto>>;

public sealed record InvoiceFileQuery(Guid OrderId, string Kind) : IQuery<(Stream Content, string ContentType)?>;

/// <summary>
/// e-Arşiv invoices: issued when the order is confirmed (the accountant may move this to shipping, compliance.md §4),
/// and a refund document when money goes back. A failed call is saved and retried; it never blocks the order.
/// </summary>
internal sealed partial class InvoicingHandlers(
    RahiqDbContext db,
    IOrderReader orders,
    IEInvoiceProvider provider,
    IEventPublisher events,
    IBlobStorage storage,
    IClock clock,
    ILogger<InvoicingHandlers> logger)
    : IEventHandler<OrderConfirmed>,
      IEventHandler<RefundIssued>,
      IRequestHandler<RetryInvoicesCommand, Result<int>>,
      IRequestHandler<OrderInvoicesQuery, IReadOnlyList<InvoiceDto>>,
      IRequestHandler<InvoiceFileQuery, (Stream Content, string ContentType)?>
{
    private const int AlertAfterAttempts = 5;

    public async Task Handle(OrderConfirmed domainEvent, CancellationToken cancellationToken)
    {
        var reference = domainEvent.Number;
        if (await db.Set<Invoice>().AnyAsync(i => i.Reference == reference, cancellationToken))
        {
            return; // One invoice per order, whatever happens upstream.
        }

        var invoice = new Invoice
        {
            Id = Ids.New(),
            OrderId = domainEvent.OrderId,
            OrderNumber = domainEvent.Number,
            Kind = "sale",
            Reference = reference,
            Provider = provider.Name,
            Status = "pending",
            Amount = domainEvent.Total,
            CreatedAt = clock.UtcNow,
        };
        db.Add(invoice);
        await TryIssue(invoice, cancellationToken);
    }

    public async Task Handle(RefundIssued domainEvent, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(domainEvent.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        var reference = $"{order.Number}-R-{domainEvent.RefundId:N}";
        if (await db.Set<Invoice>().AnyAsync(i => i.Reference == reference, cancellationToken))
        {
            return;
        }

        var invoice = new Invoice
        {
            Id = Ids.New(),
            OrderId = order.Id,
            OrderNumber = order.Number,
            Kind = "refund",
            Reference = reference,
            Provider = provider.Name,
            Status = "pending",
            Amount = domainEvent.Amount,
            CreatedAt = clock.UtcNow,
        };
        db.Add(invoice);
        await TryIssue(invoice, cancellationToken);
    }

    public async Task<Result<int>> Handle(RetryInvoicesCommand request, CancellationToken cancellationToken)
    {
        var pending = await db.Set<Invoice>().Where(i => i.Status != "issued" && i.Attempts < 20).OrderBy(i => i.CreatedAt).Take(20).ToListAsync(cancellationToken);
        foreach (var invoice in pending)
        {
            await TryIssue(invoice, cancellationToken);
        }

        return pending.Count;
    }

    public async Task<IReadOnlyList<InvoiceDto>> Handle(OrderInvoicesQuery request, CancellationToken cancellationToken) =>
        await db.Set<Invoice>().AsNoTracking().Where(i => i.OrderId == request.OrderId).OrderBy(i => i.CreatedAt)
            .Select(i => new InvoiceDto(i.Kind, i.InvoiceNumber, i.Status, i.Amount, i.IssuedAt)).ToListAsync(cancellationToken);

    public async Task<(Stream Content, string ContentType)?> Handle(InvoiceFileQuery request, CancellationToken cancellationToken)
    {
        var invoice = await db.Set<Invoice>().AsNoTracking().Where(i => i.OrderId == request.OrderId && i.Kind == request.Kind && i.Status == "issued")
            .OrderByDescending(i => i.IssuedAt).FirstOrDefaultAsync(cancellationToken);
        if (invoice?.PdfUrl is null)
        {
            return null;
        }

        var stream = await storage.GetAsync(invoice.PdfUrl, cancellationToken);
        return stream is null ? null : (stream, invoice.PdfUrl.EndsWith(".pdf", StringComparison.Ordinal) ? "application/pdf" : "text/html; charset=utf-8");
    }

    private async Task TryIssue(Invoice invoice, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(invoice.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        invoice.Attempts++;
        try
        {
            var issued = await provider.IssueAsync(new InvoiceRequest(invoice.Reference, invoice.Kind, order, invoice.Amount), cancellationToken);
            invoice.ProviderRef = issued.ProviderRef;
            invoice.InvoiceNumber = issued.Number;
            invoice.PdfUrl = issued.PdfUrl;
            invoice.Status = "issued";
            invoice.IssuedAt = clock.UtcNow;
            invoice.LastError = null;
            events.Publish(new InvoiceIssued(order.Id, order.Number, invoice.Kind, issued.Number, issued.PdfUrl));
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException or TaskCanceledException)
        {
            invoice.Status = "failed";
            invoice.LastError = ex.Message;
            LogFailed(logger, ex, invoice.Reference, invoice.Attempts);
            if (invoice.Attempts == AlertAfterAttempts)
            {
                events.Publish(new StaffAlertRaised(AlertKinds.InvoiceFailed, "e-Invoice failing",
                    $"Invoice {invoice.Reference} failed {invoice.Attempts} times: {ex.Message}", order.Number));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {Reference} failed (attempt {Attempt})")]
    private static partial void LogFailed(ILogger logger, Exception ex, string reference, int attempt);
}
