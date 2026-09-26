using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.Modules.Cart.Application;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Cart.Presentation;

public sealed record AddLineRequest(Guid VariantId, int Qty, IReadOnlyList<Guid>? Components, string? GiftMessage);

public sealed record QtyRequest(int Qty);

public sealed record CouponRequest(string? Code);

public sealed record ClaimRequest(string Code);

/// <summary>
/// The BFF keeps the guest cart token in an httpOnly cookie and forwards it as <c>X-Cart-Token</c>. A newly created
/// cart returns its token once in the same header, for the BFF to store.
/// </summary>
[Route("api/cart")]
public sealed class CartController(ISender sender, ICurrentActor actor) : ApiControllerBase
{
    public const string TokenHeader = "X-Cart-Token";

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await sender.Send(new GetCartQuery(Who), ct));

    [HttpPost("lines")]
    public async Task<IActionResult> Add(AddLineRequest body, CancellationToken ct) =>
        Respond(await sender.Send(new AddToCartCommand(Who, body.VariantId, body.Qty, body.Components, body.GiftMessage), ct));

    [HttpPatch("lines/{lineId:guid}")]
    public async Task<IActionResult> Update(Guid lineId, QtyRequest body, CancellationToken ct) =>
        Respond(await sender.Send(new UpdateCartLineCommand(Who, lineId, body.Qty), ct));

    [HttpDelete("lines/{lineId:guid}")]
    public async Task<IActionResult> Remove(Guid lineId, CancellationToken ct) =>
        Respond(await sender.Send(new RemoveCartLineCommand(Who, lineId), ct));

    /// <summary>Rate limited: 10 codes an hour, to stop guessing (security.md §5).</summary>
    [HttpPut("coupon")]
    [EnableRateLimiting("coupon")]
    public async Task<IActionResult> Coupon(CouponRequest body, CancellationToken ct) =>
        Respond(await sender.Send(new ApplyCouponCommand(Who, body.Code), ct));

    /// <summary>Opens a cart prepared in a chat. Rate limited: codes are unguessable, but guessing must stay pointless.</summary>
    [HttpPost("claim")]
    [EnableRateLimiting("tracking")]
    public async Task<IActionResult> Claim(ClaimRequest body, CancellationToken ct) =>
        Respond(await sender.Send(new ClaimHandoffCommand(Who, body.Code), ct));

    private CartIdentity Who => new(
        Request.Headers[TokenHeader].FirstOrDefault(),
        actor.Kind == ActorKind.Customer ? actor.Id : null,
        Locale);

    private IActionResult Respond(Result<CartView> result)
    {
        if (result.IsSuccess && result.Value.NewToken is { } token)
        {
            Response.Headers[TokenHeader] = token;
        }

        return FromResult(result);
    }
}
