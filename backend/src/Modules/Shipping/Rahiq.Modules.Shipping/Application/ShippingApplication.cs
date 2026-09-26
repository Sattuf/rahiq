using Microsoft.EntityFrameworkCore;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Shipping.Contracts;
using Rahiq.Modules.Shipping.Domain;
using Rahiq.Modules.Shipping.Infrastructure;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Shipping.Application;

public sealed record ShipmentDto(
    Guid Id, Guid OrderId, string OrderNumber, string Carrier, string? TrackingNumber, string? TrackingUrl, string Status,
    bool HasLabel, bool HasPackagePhoto, IReadOnlyList<TrackingEventDto> Events, DateTimeOffset CreatedAt);

public sealed record TrackingEventDto(DateTimeOffset At, string Status, string? Description);

public sealed record CreateShipmentCommand(Guid OrderId) : ICommand<Result<ShipmentDto>>;

public sealed record HandOverShipmentCommand(Guid ShipmentId) : ICommand<Result>;

public sealed record AttachPackagePhotoCommand(Guid ShipmentId, byte[] Photo) : ICommand<Result>;

public sealed record ApplyCarrierEventsCommand(string Carrier, string Body, string? Signature) : ICommand<Result<int>>;

public sealed record ShipmentsForOrderQuery(Guid OrderId) : IQuery<IReadOnlyList<ShipmentDto>>;

public sealed record LabelQuery(Guid ShipmentId) : IQuery<(Stream Content, string ContentType)?>;

public sealed record UpdateShippingSettingsCommand(long? FreeShippingThreshold) : ICommand<Result>;

internal sealed class ShippingHandlers(
    RahiqDbContext db,
    IOrderReader orders,
    ICatalogReader catalog,
    ICarrierGateway carrier,
    IBlobStorage storage,
    IAuditLog audit,
    IClock clock)
    : IRequestHandler<CreateShipmentCommand, Result<ShipmentDto>>,
      IRequestHandler<HandOverShipmentCommand, Result>,
      IRequestHandler<AttachPackagePhotoCommand, Result>,
      IRequestHandler<ApplyCarrierEventsCommand, Result<int>>,
      IRequestHandler<ShipmentsForOrderQuery, IReadOnlyList<ShipmentDto>>,
      IRequestHandler<LabelQuery, (Stream Content, string ContentType)?>,
      IRequestHandler<UpdateShippingSettingsCommand, Result>
{
    private static readonly Error NotFound = Error.NotFound("shipment.not_found", "Shipment not found.");

    public async Task<Result<ShipmentDto>> Handle(CreateShipmentCommand request, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("order.not_found", "Order not found.");
        }

        if (order.Status != OrderStatuses.Preparing)
        {
            return Error.Conflict("shipment.order_not_preparing", "Start preparing the order before creating its label.");
        }

        if (await db.Set<Shipment>().AnyAsync(s => s.OrderId == order.Id && s.Status != ShipmentStatuses.Failed, cancellationToken))
        {
            return Error.Conflict("shipment.exists", "This order already has a label.");
        }

        var variants = await catalog.GetVariantsAsync([.. order.Lines.Select(l => l.VariantId)], order.Locale, cancellationToken);
        var weight = order.Lines.Sum(l => (variants.GetValueOrDefault(l.VariantId)?.ShippingWeightG ?? 500) * l.Qty) + 250; // + packaging

        var label = await carrier.CreateShipmentAsync(order, weight, cancellationToken);
        if (label.IsFailure)
        {
            return label.Error;
        }

        var shipment = Shipment.Create(order.Id, order.Number, carrier.Name, label.Value.TrackingNumber, label.Value.TrackingUrl, null, clock.UtcNow);
        var extension = label.Value.ContentType == "application/pdf" ? "pdf" : "html";
        var key = $"labels/{shipment.Id:N}.{extension}";
        using (var stream = new MemoryStream(label.Value.Label))
        {
            await storage.PutAsync(key, stream, label.Value.ContentType, cancellationToken);
        }

        shipment.AttachLabel(key);
        db.Add(shipment);
        audit.Record("shipment.created", "order", order.Id.ToString(), new { shipment.TrackingNumber });
        return ToDto(shipment);
    }

    public async Task<Result> Handle(HandOverShipmentCommand request, CancellationToken cancellationToken)
    {
        var shipment = await db.Set<Shipment>().FirstOrDefaultAsync(s => s.Id == request.ShipmentId, cancellationToken);
        return shipment is null ? NotFound : shipment.HandOver(clock.UtcNow);
    }

    public async Task<Result> Handle(AttachPackagePhotoCommand request, CancellationToken cancellationToken)
    {
        var shipment = await db.Set<Shipment>().FirstOrDefaultAsync(s => s.Id == request.ShipmentId, cancellationToken);
        if (shipment is null)
        {
            return NotFound;
        }

        var isJpeg = request.Photo.Length > 3 && request.Photo[0] == 0xFF && request.Photo[1] == 0xD8;
        var isPng = request.Photo.Length > 8 && request.Photo[0] == 0x89 && request.Photo[1] == 0x50;
        if (!isJpeg && !isPng)
        {
            return Error.Validation("shipment.photo_invalid", "Upload the parcel photo as JPEG or PNG.");
        }

        var key = $"parcels/{shipment.Id:N}.{(isJpeg ? "jpg" : "png")}";
        using (var stream = new MemoryStream(request.Photo))
        {
            await storage.PutAsync(key, stream, isJpeg ? "image/jpeg" : "image/png", cancellationToken);
        }

        shipment.AttachPackagePhoto(key);
        return Result.Success();
    }

    public async Task<Result<int>> Handle(ApplyCarrierEventsCommand request, CancellationToken cancellationToken)
    {
        if (request.Carrier != carrier.Name)
        {
            return Error.NotFound("carrier.unknown", "Unknown carrier.");
        }

        var events = carrier.ParseWebhook(request.Body, request.Signature);
        if (events is null)
        {
            return Error.Unauthorized("carrier.signature_invalid", "Invalid signature.");
        }

        var applied = 0;
        foreach (var e in events)
        {
            var shipment = await db.Set<Shipment>().FirstOrDefaultAsync(s => s.Carrier == carrier.Name && s.TrackingNumber == e.TrackingNumber, cancellationToken);
            if (shipment is null)
            {
                continue;
            }

            shipment.ApplyCarrierEvent(e.Status, e.Description, e.At);
            applied++;
        }

        return applied;
    }

    public async Task<IReadOnlyList<ShipmentDto>> Handle(ShipmentsForOrderQuery request, CancellationToken cancellationToken) =>
        [.. (await db.Set<Shipment>().AsNoTracking().Where(s => s.OrderId == request.OrderId).OrderBy(s => s.CreatedAt).ToListAsync(cancellationToken)).Select(ToDto)];

    public async Task<(Stream Content, string ContentType)?> Handle(LabelQuery request, CancellationToken cancellationToken)
    {
        var shipment = await db.Set<Shipment>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ShipmentId, cancellationToken);
        if (shipment?.LabelKey is null)
        {
            return null;
        }

        var stream = await storage.GetAsync(shipment.LabelKey, cancellationToken);
        return stream is null ? null : (stream, shipment.LabelKey.EndsWith(".pdf", StringComparison.Ordinal) ? "application/pdf" : "text/html; charset=utf-8");
    }

    public async Task<Result> Handle(UpdateShippingSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await db.Set<ShippingSettings>().FirstAsync(cancellationToken);
        settings.FreeShippingThreshold = request.FreeShippingThreshold;
        audit.Record("shipping.settings", "shipping", "1", request);
        return Result.Success();
    }

    private static ShipmentDto ToDto(Shipment s) => new(
        s.Id, s.OrderId, s.OrderNumber, s.Carrier, s.TrackingNumber, s.TrackingUrl, s.Status, s.LabelKey is not null, s.PackagePhotoKey is not null,
        [.. s.Events.OrderBy(e => e.At).Select(e => new TrackingEventDto(e.At, e.Status, e.Description))], s.CreatedAt);
}
