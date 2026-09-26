using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.Modules.Pricing.Application;

namespace Rahiq.Modules.Pricing.Presentation;

public sealed record PriceRequest(long Amount);

[Route("api/admin/pricing")]
public sealed class AdminPricingController(ISender sender) : ApiControllerBase
{
    [HttpPut("variants/{variantId:guid}/price")]
    [Authorize(Policy = Permissions.PricesEdit)]
    public async Task<IActionResult> SetPrice(Guid variantId, PriceRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new SetPriceCommand(variantId, body.Amount), ct));

    [HttpGet("variants/{variantId:guid}/history")]
    [Authorize(Policy = Permissions.PricesEdit)]
    public async Task<IActionResult> History(Guid variantId, CancellationToken ct) => Ok(await sender.Send(new PriceHistoryQuery(variantId), ct));

    [HttpGet("coupons")]
    [Authorize(Policy = Permissions.CouponsEdit)]
    public async Task<IActionResult> Coupons(CancellationToken ct) => Ok(await sender.Send(new ListCouponsQuery(), ct));

    [HttpPost("coupons")]
    [Authorize(Policy = Permissions.CouponsEdit)]
    public async Task<IActionResult> CreateCoupon(CouponInput body, CancellationToken ct) =>
        FromResult(await sender.Send(new SaveCouponCommand(null, body), ct));

    [HttpPut("coupons/{id:guid}")]
    [Authorize(Policy = Permissions.CouponsEdit)]
    public async Task<IActionResult> UpdateCoupon(Guid id, CouponInput body, CancellationToken ct) =>
        FromResult(await sender.Send(new SaveCouponCommand(id, body), ct));
}
