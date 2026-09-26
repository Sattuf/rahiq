using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Idempotency;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.Modules.Ordering.Application;

namespace Rahiq.Modules.Ordering.Presentation;

public sealed record StartCheckoutRequest(string? Email);

public sealed record PayRequest(string PaymentMethod, string AcceptedContractsHash);

public sealed record LookupRequest(string Number, string Email);

public sealed record ReturnRequestBody(string Number, string? Email, string Kind, string Reason, IReadOnlyList<ReturnLineInput> Lines, IReadOnlyList<string> PhotoKeys);

[Route("api/checkout")]
public sealed class CheckoutController(ISender sender, CheckoutPayment payment, ICurrentActor actor) : ApiControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Start(StartCheckoutRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new StartCheckoutCommand(Who, body.Email ?? actor.Email), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromQuery] string? paymentMethod, CancellationToken ct) =>
        FromResult(await sender.Send(new GetCheckoutQuery(id, Who, paymentMethod ?? "card"), ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, CheckoutDetailsInput body, CancellationToken ct) =>
        FromResult(await sender.Send(new UpdateCheckoutCommand(id, Who, body), ct));

    /// <summary>
    /// "Pay": needs an Idempotency-Key (a double click places one order). The amount is never taken from the request.
    /// Card: returns the provider's hosted page. COD: the order is confirmed immediately.
    /// </summary>
    [HttpPost("{id:guid}/pay")]
    [Idempotent]
    [EnableRateLimiting("checkout")]
    public async Task<IActionResult> Pay(Guid id, PayRequest body, CancellationToken ct)
    {
        var key = Request.Headers[IdempotentAttribute.HeaderName].ToString();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        return FromResult(await payment.PayAsync(new PlaceOrderCommand(id, Who, body.PaymentMethod, body.AcceptedContractsHash, key), ip, ct));
    }

    private Requester Who => new(Request.Headers["X-Cart-Token"].FirstOrDefault(), actor.Kind == ActorKind.Customer ? actor.Id : null, Locale);
}

[Route("api/orders")]
public sealed class OrdersController(ISender sender, ICurrentActor actor) : ApiControllerBase
{
    private Guid? Customer => actor.Kind == ActorKind.Customer ? actor.Id : null;

    [HttpGet("{number}/status")]
    [EnableRateLimiting("public-lookup")]
    public async Task<IActionResult> Status(string number, [FromQuery] string? email, CancellationToken ct) =>
        FromResult(await sender.Send(new OrderStatusQuery(number, email, Customer, Request.Headers["X-Cart-Token"].FirstOrDefault()), ct));

    [HttpPost("lookup")]
    [EnableRateLimiting("tracking")]
    public async Task<IActionResult> Lookup(LookupRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new TrackOrderQuery(body.Number, body.Email), ct));

    [HttpGet("{number}/documents")]
    [EnableRateLimiting("tracking")]
    public async Task<IActionResult> Documents(string number, [FromQuery] string? email, CancellationToken ct) =>
        FromResult(await sender.Send(new OrderDocumentsQuery(number, email, Customer), ct));

    [HttpPost("returns/photos")]
    [EnableRateLimiting("tracking")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> ReturnPhoto(IFormFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return FromResult(await sender.Send(new UploadReturnPhotoCommand(buffer.ToArray()), ct));
    }

    [HttpPost("returns")]
    [EnableRateLimiting("tracking")]
    public async Task<IActionResult> Return(ReturnRequestBody body, CancellationToken ct) =>
        FromResult(await sender.Send(new RequestReturnCommand(body.Number, body.Email, Customer, body.Kind, body.Reason, body.Lines, body.PhotoKeys), ct));
}

[Route("api/me/orders")]
[Authorize(Policy = "customer")]
public sealed class MyOrdersController(ISender sender, ICurrentActor actor) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await sender.Send(new MyOrdersQuery(actor.Id!.Value), ct));

    [HttpGet("{number}")]
    public async Task<IActionResult> Get(string number, CancellationToken ct) => FromResult(await sender.Send(new MyOrderQuery(actor.Id!.Value, number), ct));

    [HttpPost("{number}/reorder")]
    public async Task<IActionResult> Reorder(string number, CancellationToken ct) => FromResult(await sender.Send(new ReorderCommand(actor.Id!.Value, number), ct));
}

public sealed record CancelRequest(string? Note);

public sealed record NoteRequest(string Body);

public sealed record RefundRequest(long Amount, string Reason);

public sealed record ResolveRequest(string Decision, string? Note, bool Damaged);

[Route("api/admin/orders")]
[Authorize(Policy = Permissions.OrdersView)]
public sealed class AdminOrdersController(ISender sender, ICurrentActor actor) : ApiControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct) => Ok(await sender.Send(new DashboardQuery(), ct));

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? q, [FromQuery] int page = 1, CancellationToken ct = default) =>
        Ok(await sender.Send(new AdminOrdersQuery(status, q, page), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await sender.Send(new AdminOrderQuery(id), ct));

    [HttpGet("{id:guid}/pick-list")]
    [Authorize(Policy = Permissions.OrdersFulfil)]
    public async Task<IActionResult> PickList(Guid id, CancellationToken ct)
    {
        var html = await sender.Send(new PickListQuery(id), ct);
        return html.IsSuccess ? Content(html.Value, "text/html; charset=utf-8") : ProblemFor(html.Error);
    }

    [HttpPost("{id:guid}/prepare")]
    [Authorize(Policy = Permissions.OrdersFulfil)]
    public async Task<IActionResult> Prepare(Guid id, CancellationToken ct) => FromResult(await sender.Send(new StartPreparingCommand(id), ct));

    [HttpPost("{id:guid}/delivered")]
    [Authorize(Policy = Permissions.OrdersFulfil)]
    public async Task<IActionResult> Delivered(Guid id, CancellationToken ct) => FromResult(await sender.Send(new MarkDeliveredCommand(id), ct));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Permissions.OrdersCancel)]
    public async Task<IActionResult> Cancel(Guid id, CancelRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new CancelOrderCommand(id, Domain.CancelReasons.Staff, actor.Id, body.Note), ct));

    [HttpGet("{id:guid}/notes")]
    public async Task<IActionResult> Notes(Guid id, CancellationToken ct) => Ok(await sender.Send(new OrderNotesQuery(id), ct));

    [HttpPost("{id:guid}/notes")]
    public async Task<IActionResult> AddNote(Guid id, NoteRequest body, CancellationToken ct) => FromResult(await sender.Send(new AddOrderNoteCommand(id, body.Body), ct));

    [HttpPost("{id:guid}/refund")]
    [Authorize(Policy = Permissions.OrdersRefund)]
    public async Task<IActionResult> Refund(Guid id, RefundRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new RefundOrderCommand(id, body.Amount, body.Reason), ct));

    [HttpGet("recall/{batchCode}")]
    public async Task<IActionResult> Recall(string batchCode, CancellationToken ct) => Ok(await sender.Send(new RecallQuery(batchCode), ct));

    [HttpGet("returns")]
    public async Task<IActionResult> Returns([FromQuery] string? status, CancellationToken ct) => Ok(await sender.Send(new ListReturnsQuery(status), ct));

    [HttpPost("returns/{returnId:guid}")]
    [Authorize(Policy = Permissions.ReturnsProcess)]
    public async Task<IActionResult> Resolve(Guid returnId, ResolveRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new ResolveReturnCommand(returnId, body.Decision, body.Note, body.Damaged), ct));
}
