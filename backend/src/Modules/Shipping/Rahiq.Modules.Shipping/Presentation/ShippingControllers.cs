using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.Modules.Shipping.Application;
using Rahiq.Modules.Shipping.Contracts;
using Rahiq.Modules.Shipping.Infrastructure;

namespace Rahiq.Modules.Shipping.Presentation;

[Route("api/shipping")]
public sealed class ShippingController(IShippingRates rates) : ApiControllerBase
{
    [HttpGet("provinces")]
    public async Task<IActionResult> Provinces(CancellationToken ct) => Ok(await rates.ProvincesAsync(ct));
}

public sealed record ShippingSettingsRequest(long? FreeShippingThreshold);

[Route("api/admin/shipping")]
[Authorize(Policy = Permissions.OrdersFulfil)]
public sealed class AdminShippingController(ISender sender) : ApiControllerBase
{
    [HttpGet("orders/{orderId:guid}")]
    public async Task<IActionResult> ForOrder(Guid orderId, CancellationToken ct) => Ok(await sender.Send(new ShipmentsForOrderQuery(orderId), ct));

    [HttpPost("orders/{orderId:guid}")]
    public async Task<IActionResult> Create(Guid orderId, CancellationToken ct) => FromResult(await sender.Send(new CreateShipmentCommand(orderId), ct));

    [HttpPost("{id:guid}/handover")]
    public async Task<IActionResult> HandOver(Guid id, CancellationToken ct) => FromResult(await sender.Send(new HandOverShipmentCommand(id), ct));

    [HttpPost("{id:guid}/photo")]
    public async Task<IActionResult> Photo(Guid id, IFormFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return FromResult(await sender.Send(new AttachPackagePhotoCommand(id, buffer.ToArray()), ct));
    }

    [HttpGet("{id:guid}/label")]
    public async Task<IActionResult> Label(Guid id, CancellationToken ct)
    {
        var label = await sender.Send(new LabelQuery(id), ct);
        return label is null ? NotFound() : File(label.Value.Content, label.Value.ContentType);
    }

    [HttpPut("settings")]
    [Authorize(Policy = Permissions.PricesEdit)]
    public async Task<IActionResult> Settings(ShippingSettingsRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new UpdateShippingSettingsCommand(body.FreeShippingThreshold), ct));
}

/// <summary>Carrier tracking webhooks. The body is verified before anything is read (signature over the raw bytes).</summary>
[Route("webhooks/shipping")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ShippingWebhookController(ISender sender) : ApiControllerBase
{
    [HttpPost("{carrier}")]
    public async Task<IActionResult> Receive(string carrier, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        var result = await sender.Send(new ApplyCarrierEventsCommand(carrier, body, Request.Headers["X-Carrier-Signature"].FirstOrDefault()), ct);
        return result.IsSuccess ? Ok(new { applied = result.Value }) : ProblemFor(result.Error);
    }
}

public sealed record SandboxAdvanceRequest(string Status, string? Description);

/// <summary>Development only: plays the carrier, sending a correctly signed tracking event.</summary>
[Route("dev/shipping")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class SandboxCarrierController(ISender sender, IOptions<CarrierOptions> options, IHostEnvironment environment) : ApiControllerBase
{
    [HttpPost("{trackingNumber}")]
    public async Task<IActionResult> Advance(string trackingNumber, SandboxAdvanceRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (environment.IsProduction())
        {
            return NotFound();
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(new[] { new CarrierEvent(trackingNumber, body.Status, body.Description, DateTimeOffset.UtcNow) }, JsonDefaults.Options);
        var result = await sender.Send(new ApplyCarrierEventsCommand("sandbox", payload, SandboxCarrier.Sign(payload, options.Value.WebhookSecret)), ct);
        return result.IsSuccess ? Ok(new { applied = result.Value }) : ProblemFor(result.Error);
    }
}
