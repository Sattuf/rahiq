using Rahiq.SharedKernel;

namespace Rahiq.Modules.Documents.Contracts;

public sealed record ContractParty(string Name, string Email, string Phone, string Address);

public sealed record ContractLine(string Name, string VariantLabel, int Qty, long UnitPrice, long Discount, long LineTotal, int TaxRateBp, bool Returnable);

/// <summary>Everything the pre-information form and distance sales contract show. Same model → same hash.</summary>
public sealed record ContractModel
{
    public required string Locale { get; init; }

    public required DateOnly Date { get; init; }

    public required ContractParty Buyer { get; init; }

    public required string DeliveryAddress { get; init; }

    public string? InvoiceAddress { get; init; }

    public required IReadOnlyList<ContractLine> Lines { get; init; }

    public required string Currency { get; init; }

    public required long Subtotal { get; init; }

    public required long Discount { get; init; }

    public required long Shipping { get; init; }

    public required long CodFee { get; init; }

    public required long Total { get; init; }

    public required string PaymentMethod { get; init; }

    public required string ShippingMethod { get; init; }
}

public sealed record RenderedContracts(string PreInformationHtml, string DistanceSalesHtml, string Hash);

public interface IContractDocuments
{
    RenderedContracts Render(ContractModel model);

    Task SaveAsync(Guid orderId, string locale, RenderedContracts documents, CancellationToken cancellationToken);

    /// <summary>The frozen documents of an order (attached to the confirmation e-mail, shown in the account).</summary>
    Task<IReadOnlyList<StoredDocument>> LoadAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed record StoredDocument(string Kind, string Locale, string Html, string Sha256);

public sealed record InvoiceIssued(Guid OrderId, string OrderNumber, string Kind, string InvoiceNumber, string? PdfUrl) : DomainEvent;
