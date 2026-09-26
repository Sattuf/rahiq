using Rahiq.SharedKernel;

namespace Rahiq.Modules.Shipping.Contracts;

public sealed record ShippingRateOption(string Method, long Amount, bool IsFree);

public sealed record Province(int Code, string Name);

public interface IShippingRates
{
    /// <summary>Options for a parcel to a province. Empty when nothing can carry it (e.g. too heavy).</summary>
    Task<IReadOnlyList<ShippingRateOption>> OptionsAsync(
        int provinceCode, int weightG, Money subtotalAfterDiscount, bool hasFlammable, CancellationToken cancellationToken);

    Task<long?> FreeShippingThresholdAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Province>> ProvincesAsync(CancellationToken cancellationToken);
}

public static class ShipmentStatuses
{
    public const string LabelCreated = "label_created";
    public const string HandedOver = "handed_over";
    public const string InTransit = "in_transit";
    public const string OutForDelivery = "out_for_delivery";
    public const string Delivered = "delivered";
    public const string Returned = "returned";
    public const string Failed = "failed";
}

public sealed record ShipmentHandedOver(Guid OrderId, Guid ShipmentId, string Carrier, string TrackingNumber, string? TrackingUrl) : DomainEvent;

public sealed record ShipmentStatusChanged(Guid OrderId, Guid ShipmentId, string Status) : DomainEvent;
