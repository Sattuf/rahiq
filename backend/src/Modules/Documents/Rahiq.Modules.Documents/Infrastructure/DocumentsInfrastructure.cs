using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Documents.Contracts;
using Rahiq.Modules.Documents.Templates;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Documents.Infrastructure;

/// <summary>The seller identity printed on contracts and invoices, and shown in the footer (ETBİS, compliance.md §3).</summary>
public sealed record SellerOptions
{
    public const string Section = "Seller";

    public string LegalName { get; init; } = "[Şirket unvanı]";

    public string Address { get; init; } = "[Açık adres]";

    public string Phone { get; init; } = "[Telefon]";

    public string Email { get; init; } = "destek@rahiq.example";

    public string TaxOffice { get; init; } = "[Vergi dairesi]";

    public string TaxNumber { get; init; } = "[Vergi no]";

    public string Mersis { get; init; } = "[MERSİS no]";

    public string EtbisNumber { get; init; } = "[ETBİS no]";

    public string Kep { get; init; } = "[KEP adresi]";

    /// <summary>True until the lawyer approves the texts: every document says "draft".</summary>
    public bool DraftLegalTexts { get; init; } = true;
}

internal sealed class StoredDocumentRow
{
    public Guid Id { get; init; }

    public Guid OrderId { get; init; }

    public required string Kind { get; init; }

    public required string Locale { get; init; }

    public required string Html { get; init; }

    public required string Sha256 { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class Invoice
{
    public Guid Id { get; init; }

    public Guid OrderId { get; init; }

    public required string OrderNumber { get; init; }

    public required string Kind { get; init; }

    /// <summary>Unique reference sent to the provider: a retry can never create a second invoice (risks-troubleshooting.md).</summary>
    public required string Reference { get; init; }

    public required string Provider { get; init; }

    public string? ProviderRef { get; set; }

    public string? InvoiceNumber { get; set; }

    public string? PdfUrl { get; set; }

    public required string Status { get; set; }

    public long Amount { get; init; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? IssuedAt { get; set; }
}

internal sealed class DocumentsModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredDocumentRow>(b =>
        {
            b.ToTable("documents", "documents");
            b.HasKey(d => d.Id);
        });
        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("invoices", "documents");
            b.HasKey(i => i.Id);
        });
    }
}

internal sealed class ContractDocuments(RahiqDbContext db, IOptions<SellerOptions> seller, IClock clock) : IContractDocuments
{
    public RenderedContracts Render(ContractModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var t = ContractTexts.For(model.Locale);
        var preInfo = Page(model, t, t.PreInfoTitle, Body(model, t, includeArticles: false));
        var sales = Page(model, t, t.SalesTitle, Body(model, t, includeArticles: true));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preInfo + "\n\u0000\n" + sales))).ToLowerInvariant();
        return new RenderedContracts(preInfo, sales, hash);
    }

    public async Task SaveAsync(Guid orderId, string locale, RenderedContracts documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        db.AddRange(
            new StoredDocumentRow { Id = Ids.New(), OrderId = orderId, Kind = "pre_information", Locale = locale, Html = documents.PreInformationHtml, Sha256 = documents.Hash, CreatedAt = clock.UtcNow },
            new StoredDocumentRow { Id = Ids.New(), OrderId = orderId, Kind = "distance_sales", Locale = locale, Html = documents.DistanceSalesHtml, Sha256 = documents.Hash, CreatedAt = clock.UtcNow });
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<StoredDocument>> LoadAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Set<StoredDocumentRow>().AsNoTracking().Where(d => d.OrderId == orderId).OrderBy(d => d.Kind)
            .Select(d => new StoredDocument(d.Kind, d.Locale, d.Html, d.Sha256)).ToListAsync(cancellationToken);

    private string Body(ContractModel m, ContractTexts.Texts t, bool includeArticles)
    {
        var s = seller.Value;
        var culture = CultureInfo.GetCultureInfo(m.Locale == "ar" ? "ar-SA" : m.Locale == "en" ? "en-GB" : "tr-TR");
        string Money(long minor) => (minor / 100m).ToString("N2", CultureInfo.InvariantCulture) + " " + m.Currency;
        string E(string? v) => WebUtility.HtmlEncode(v ?? string.Empty);

        var sb = new StringBuilder();
        if (s.DraftLegalTexts)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<p class=\"draft\">{E(t.Draft)}</p>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<p>{E(t.Date)}: {m.Date.ToString("d", culture)}. {E(t.OrderNumberNote)}</p>");
        sb.Append(CultureInfo.InvariantCulture, $"<h2>{E(t.Seller)}</h2><table>");
        foreach (var (label, value) in new[] { (t.Name, s.LegalName), (t.Address, s.Address), (t.Phone, s.Phone), (t.Email, s.Email), (t.TaxInfo, $"{s.TaxOffice} / {s.TaxNumber}"), (t.Mersis, s.Mersis), (t.Etbis, s.EtbisNumber), (t.Kep, s.Kep) })
        {
            sb.Append(CultureInfo.InvariantCulture, $"<tr><th>{E(label)}</th><td>{E(value)}</td></tr>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"</table><h2>{E(t.Buyer)}</h2><table>");
        foreach (var (label, value) in new[] { (t.Name, m.Buyer.Name), (t.Email, m.Buyer.Email), (t.Phone, m.Buyer.Phone), (t.Delivery, m.DeliveryAddress), (t.InvoiceAddress, m.InvoiceAddress ?? m.DeliveryAddress) })
        {
            sb.Append(CultureInfo.InvariantCulture, $"<tr><th>{E(label)}</th><td>{E(value)}</td></tr>");
        }

        if (includeArticles)
        {
            sb.Append("<ol>");
            foreach (var article in t.SalesArticles)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<li>{E(article)}</li>");
            }

            sb.Append("</ol>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"</table><h2>{E(t.Products)}</h2><table class=\"lines\"><tr><th>{E(t.Product)}</th><th>{E(t.Qty)}</th><th>{E(t.UnitPrice)}</th><th>{E(t.Discount)}</th><th>{E(t.Vat)}</th><th>{E(t.LineTotal)}</th></tr>");
        foreach (var line in m.Lines)
        {
            var mark = line.Returnable ? string.Empty : " *";
            sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(line.Name)} · {E(line.VariantLabel)}{mark}</td><td>{line.Qty}</td><td>{Money(line.UnitPrice)}</td><td>{Money(line.Discount)}</td><td>%{line.TaxRateBp / 100m:0.##}</td><td>{Money(line.LineTotal)}</td></tr>");
        }

        sb.Append("</table><table class=\"totals\">");
        sb.Append(CultureInfo.InvariantCulture, $"<tr><th>{E(t.Subtotal)}</th><td>{Money(m.Subtotal)}</td></tr>");
        if (m.Discount > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<tr><th>{E(t.Discount)}</th><td>−{Money(m.Discount)}</td></tr>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<tr><th>{E(t.Shipping)}</th><td>{Money(m.Shipping)}</td></tr>");
        if (m.CodFee > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<tr><th>{E(t.CodFee)}</th><td>{Money(m.CodFee)}</td></tr>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<tr class=\"grand\"><th>{E(t.Total)}</th><td>{Money(m.Total)}</td></tr></table><p>{E(t.VatIncluded)}</p>");
        sb.Append(CultureInfo.InvariantCulture, $"<p><strong>{E(t.PaymentMethod)}:</strong> {E(m.PaymentMethod == PaymentMethods.CashOnDelivery ? t.Cod : t.Card)}</p>");
        sb.Append(CultureInfo.InvariantCulture, $"<p>{E(t.DeliveryTerms)}</p>");
        sb.Append(CultureInfo.InvariantCulture, $"<h2>{E(t.WithdrawalTitle)}</h2><p>{E(t.Withdrawal)}</p><p>{E(t.WithdrawalExceptions)}</p>");
        if (m.Lines.Any(l => !l.Returnable))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<p>* {E(t.NotReturnable)}</p>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<p>{E(t.Complaints)}</p>");
        return sb.ToString();
    }

    private static string Page(ContractModel m, ContractTexts.Texts t, string title, string body) =>
        $$"""
        <!doctype html><html lang="{{m.Locale}}" dir="{{t.Dir}}"><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>body{font:14px/1.6 system-ui,sans-serif;color:#2A1F17;max-width:760px;margin:24px auto;padding:0 16px}h1{font-size:22px}h2{font-size:16px;margin-top:20px}
        table{border-collapse:collapse;width:100%}th,td{border-bottom:1px solid #ddd;padding:4px 6px;text-align:start;vertical-align:top}.totals{width:auto;margin-inline-start:auto}
        .grand th,.grand td{font-weight:700}.draft{background:#fff3cd;padding:6px 10px;border:1px solid #e0c36b}</style></head>
        <body><h1>{{WebUtility.HtmlEncode(title)}}</h1>{{body}}</body></html>
        """;
}

internal sealed record InvoiceRequest(string Reference, string Kind, OrderInfo Order, long Amount);

internal sealed record IssuedInvoice(string ProviderRef, string Number, string? PdfUrl);

/// <summary>e-Arşiv / e-Fatura integrator. The provider is chosen with the accountant (ADR-011); sandbox until then.</summary>
internal interface IEInvoiceProvider
{
    string Name { get; }

    Task<IssuedInvoice> IssueAsync(InvoiceRequest request, CancellationToken cancellationToken);
}

internal sealed class SandboxEInvoiceProvider(IBlobStorage storage) : IEInvoiceProvider
{
    public string Name => "sandbox";

    public async Task<IssuedInvoice> IssueAsync(InvoiceRequest request, CancellationToken cancellationToken)
    {
        var number = $"SBX{DateTime.UtcNow:yyyy}{Math.Abs(request.Reference.GetHashCode(StringComparison.Ordinal)) % 1_000_000_000:000000000}";
        var o = request.Order;
        var html = $"<!doctype html><meta charset=\"utf-8\"><title>{number}</title><h1>e-Arşiv Fatura (SANDBOX)</h1><p>{WebUtility.HtmlEncode(number)} · {WebUtility.HtmlEncode(o.Number)} · {request.Kind}</p><p>{request.Amount / 100m:0.00} {o.Currency}</p>"
            + string.Concat(o.Lines.Select(l => $"<p>{WebUtility.HtmlEncode(l.Name)} {WebUtility.HtmlEncode(l.VariantLabel)} × {l.Qty} — %{l.TaxRateBp / 100m:0.##} KDV — {l.LineTotal / 100m:0.00}</p>"));
        var key = $"invoices/{request.Reference}.html";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        await storage.PutAsync(key, stream, "text/html", cancellationToken);
        return new IssuedInvoice($"sbx-{request.Reference}", number, key);
    }
}
