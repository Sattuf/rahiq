using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.Modules.Customers.Application;
using Rahiq.Modules.Ordering.Contracts;

namespace Rahiq.Modules.Customers.Presentation;

public sealed record OtpRequest(string Email);

public sealed record OtpVerifyRequest(string Email, string Code, string? GuestCartToken);

public sealed record RefreshRequest(string RefreshToken);

public sealed record StaffLoginRequest(string Email, string Password, string? TotpCode);

public sealed record TicketRequest(string Ticket);

public sealed record TicketCodeRequest(string Ticket, string Code);

/// <summary>Called by the Next.js BFF only; tokens never reach browser JavaScript (security.md §2).</summary>
[Route("api/auth")]
public sealed class AuthController(ISender sender) : ApiControllerBase
{
    [HttpPost("otp/request")]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> RequestOtp(OtpRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new RequestOtpCommand(body.Email, Locale), ct));

    [HttpPost("otp/verify")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> VerifyOtp(OtpVerifyRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new VerifyOtpCommand(body.Email, body.Code, body.GuestCartToken, Locale), ct));

    [HttpPost("refresh")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Refresh(RefreshRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new RefreshCommand(body.RefreshToken), ct));

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new LogoutCommand(body.RefreshToken), ct));

    [HttpPost("staff/login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> StaffLogin(StaffLoginRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new StaffLoginCommand(body.Email, body.Password, body.TotpCode), ct));

    [HttpPost("staff/totp/start")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> StartTotp(TicketRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new StartTotpEnrollmentCommand(body.Ticket), ct));

    [HttpPost("staff/totp/confirm")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ConfirmTotp(TicketCodeRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new ConfirmTotpEnrollmentCommand(body.Ticket, body.Code), ct));
}

public sealed record ProfileRequest(string? Name, string? Phone, string Locale);

public sealed record ConsentRequest(string Channel, bool Granted);

public sealed record AddressRequest(string? Label, AddressData Data, bool IsDefault);

[Route("api/me")]
[Authorize(Policy = "customer")]
public sealed class MeController(ISender sender, ICurrentActor actor) : ApiControllerBase
{
    private Guid Me => actor.Id!.Value;

    [HttpGet]
    public async Task<IActionResult> Profile(CancellationToken ct) => FromResult(await sender.Send(new GetProfileQuery(Me), ct));

    [HttpPut]
    public async Task<IActionResult> Update(ProfileRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new UpdateProfileCommand(Me, body.Name, body.Phone, body.Locale), ct));

    [HttpPut("consents")]
    public async Task<IActionResult> Consent(ConsentRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new SetConsentCommand(Me, body.Channel, body.Granted), ct));

    [HttpGet("addresses")]
    public async Task<IActionResult> Addresses(CancellationToken ct) => Ok(await sender.Send(new ListAddressesQuery(Me), ct));

    [HttpPost("addresses")]
    public async Task<IActionResult> AddAddress(AddressRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new SaveAddressCommand(Me, null, body.Label, body.Data, body.IsDefault), ct));

    [HttpPut("addresses/{id:guid}")]
    public async Task<IActionResult> UpdateAddress(Guid id, AddressRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new SaveAddressCommand(Me, id, body.Label, body.Data, body.IsDefault), ct));

    [HttpDelete("addresses/{id:guid}")]
    public async Task<IActionResult> DeleteAddress(Guid id, CancellationToken ct) => FromResult(await sender.Send(new DeleteAddressCommand(Me, id), ct));

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct) => FromResult(await sender.Send(new ExportMyDataQuery(Me), ct));

    [HttpDelete]
    public async Task<IActionResult> Erase(CancellationToken ct) => FromResult(await sender.Send(new EraseMyAccountCommand(Me), ct));
}

public sealed record StaffCreateRequest(string Email, string Name, string Role, string Password);

public sealed record StaffUpdateRequest(string Role, bool IsActive);

[Route("api/admin/staff")]
[Authorize(Policy = Permissions.StaffManage)]
public sealed class AdminStaffController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await sender.Send(new ListStaffQuery(), ct));

    [HttpPost]
    public async Task<IActionResult> Create(StaffCreateRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new CreateStaffCommand(body.Email, body.Name, body.Role, body.Password), ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, StaffUpdateRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new UpdateStaffCommand(id, body.Role, body.IsActive), ct));
}
