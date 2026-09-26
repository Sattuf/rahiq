using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.Modules.Inventory.Application;

namespace Rahiq.Modules.Inventory.Presentation;

[Route("api/batches")]
public sealed class BatchesController(ISender sender) : ApiControllerBase
{
    /// <summary>The page behind the QR code on each jar. The token is random, never a sequential id.</summary>
    [HttpGet("{token}")]
    [EnableRateLimiting("public-lookup")]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        var batch = await sender.Send(new GetPublicBatchQuery(token, Locale), ct);
        return batch is null ? NotFound() : Ok(batch);
    }

    [HttpGet("{token}/report")]
    [EnableRateLimiting("public-lookup")]
    public async Task<IActionResult> Report(string token, CancellationToken ct)
    {
        var report = await sender.Send(new GetLabReportQuery(token), ct);
        return report is null ? NotFound() : File(report.Value.Content, "application/pdf", report.Value.FileName);
    }
}

public sealed record AdjustRequest(int NewOnHand, string Note);

public sealed record BatchDetailsRequest(JsonElement? Origin, JsonElement? LabSummary);

public sealed record ThresholdRequest(int Threshold);

[Route("api/admin/inventory")]
[Authorize(Policy = Permissions.InventoryEdit)]
public sealed class AdminInventoryController(ISender sender) : ApiControllerBase
{
    [HttpGet("stock")]
    public async Task<IActionResult> Stock(CancellationToken ct) => Ok(await sender.Send(new StockOverviewQuery(), ct));

    [HttpGet("batches")]
    public async Task<IActionResult> Batches([FromQuery] Guid? variantId, [FromQuery] int? expiringWithinDays, CancellationToken ct) =>
        Ok(await sender.Send(new ListBatchesQuery(variantId, expiringWithinDays), ct));

    [HttpPost("batches")]
    public async Task<IActionResult> Receive(ReceiveBatchCommand body, CancellationToken ct) =>
        Created(await sender.Send(body, ct), id => $"/api/admin/inventory/batches/{id}");

    [HttpGet("batches/{id:guid}/movements")]
    public async Task<IActionResult> Movements(Guid id, CancellationToken ct) => Ok(await sender.Send(new BatchMovementsQuery(id), ct));

    [HttpPost("batches/{id:guid}/adjust")]
    public async Task<IActionResult> Adjust(Guid id, AdjustRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new AdjustBatchCommand(id, body.NewOnHand, body.Note), ct));

    [HttpPut("batches/{id:guid}")]
    public async Task<IActionResult> Details(Guid id, BatchDetailsRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new UpdateBatchDetailsCommand(id, body.Origin, body.LabSummary), ct));

    [HttpPost("batches/{id:guid}/lab-report")]
    [RequestSizeLimit(21 * 1024 * 1024)]
    public async Task<IActionResult> LabReport(Guid id, IFormFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return FromResult(await sender.Send(new AttachLabReportCommand(id, buffer.ToArray()), ct));
    }

    [HttpPut("variants/{variantId:guid}/threshold")]
    public async Task<IActionResult> Threshold(Guid variantId, ThresholdRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new SetLowStockThresholdCommand(variantId, body.Threshold), ct));
}
