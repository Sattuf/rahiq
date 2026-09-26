using Rahiq.Modules.Shipping.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Shipping.Domain;

internal sealed record RateBand(string ZoneCode, string Method, int MaxWeightG, long Amount);

/// <summary>Weight bands per zone; free above a threshold (commerce-flows.md §7).</summary>
internal static class RateTable
{
    public static IReadOnlyList<ShippingRateOption> Options(IReadOnlyList<RateBand> bands, string zoneCode, int weightG, long merchandise, long? freeThreshold)
    {
        ArgumentNullException.ThrowIfNull(bands);
        var isFree = freeThreshold is not null && merchandise >= freeThreshold;
        return [.. bands
            .Where(b => b.ZoneCode == zoneCode && b.MaxWeightG >= weightG)
            .GroupBy(b => b.Method)
            .Select(g => g.OrderBy(b => b.MaxWeightG).First())
            .Select(b => new ShippingRateOption(b.Method, isFree && b.Method == "standard" ? 0 : b.Amount, isFree && b.Method == "standard"))
            .OrderBy(o => o.Amount)];
    }
}

internal sealed record TrackingEvent(DateTimeOffset At, string Status, string? Description);

internal sealed class Shipment : AggregateRoot<Guid>
{
    private static readonly string[] Order =
    [
        ShipmentStatuses.LabelCreated, ShipmentStatuses.HandedOver, ShipmentStatuses.InTransit,
        ShipmentStatuses.OutForDelivery, ShipmentStatuses.Delivered,
    ];

    private Shipment()
    {
    }

    public Guid OrderId { get; private set; }

    public string OrderNumber { get; private set; } = string.Empty;

    public string Carrier { get; private set; } = string.Empty;

    public string? TrackingNumber { get; private set; }

    public string? TrackingUrl { get; private set; }

    public string? LabelKey { get; private set; }

    public string? PackagePhotoKey { get; private set; }

    public string Status { get; private set; } = ShipmentStatuses.LabelCreated;

    public List<TrackingEvent> Events { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Shipment Create(Guid orderId, string orderNumber, string carrier, string trackingNumber, string? trackingUrl, string? labelKey, DateTimeOffset now)
    {
        var shipment = new Shipment
        {
            Id = Ids.New(),
            OrderId = orderId,
            OrderNumber = orderNumber,
            Carrier = carrier,
            TrackingNumber = trackingNumber,
            TrackingUrl = trackingUrl,
            LabelKey = labelKey,
            CreatedAt = now,
            UpdatedAt = now,
        };
        shipment.Events.Add(new TrackingEvent(now, ShipmentStatuses.LabelCreated, null));
        return shipment;
    }

    public void AttachLabel(string key) => LabelKey = key;

    /// <summary>A photo of the closed parcel, evidence for "arrived incomplete/damaged" disputes (operations.md §1).</summary>
    public void AttachPackagePhoto(string key) => PackagePhotoKey = key;

    public Result HandOver(DateTimeOffset now)
    {
        if (Status != ShipmentStatuses.LabelCreated)
        {
            return Error.Conflict("shipment.already_handed_over", "This parcel was already handed to the carrier.");
        }

        Apply(ShipmentStatuses.HandedOver, null, now);
        Raise(new ShipmentHandedOver(OrderId, Id, Carrier, TrackingNumber!, TrackingUrl));
        return Result.Success();
    }

    /// <summary>Carrier events can arrive out of order or twice; the status never moves backwards.</summary>
    public void ApplyCarrierEvent(string status, string? description, DateTimeOffset at)
    {
        if (Events.Any(e => e.Status == status && e.At == at))
        {
            return;
        }

        Events.Add(new TrackingEvent(at, status, description));
        var terminal = Status is ShipmentStatuses.Delivered or ShipmentStatuses.Returned or ShipmentStatuses.Failed;
        var forward = Array.IndexOf(Order, status) > Array.IndexOf(Order, Status);
        if (!terminal && (forward || status is ShipmentStatuses.Returned or ShipmentStatuses.Failed))
        {
            Apply(status, description, at, recordEvent: false);
            Raise(new ShipmentStatusChanged(OrderId, Id, status));
        }
    }

    private void Apply(string status, string? description, DateTimeOffset at, bool recordEvent = true)
    {
        Status = status;
        UpdatedAt = at;
        if (recordEvent)
        {
            Events.Add(new TrackingEvent(at, status, description));
        }
    }
}
