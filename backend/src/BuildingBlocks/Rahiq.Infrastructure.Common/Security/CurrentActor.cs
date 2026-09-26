using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Rahiq.Application.Abstractions;

namespace Rahiq.Infrastructure.Common.Security;

public static class RahiqClaims
{
    public const string Kind = "kind";
    public const string Permission = "perm";
    public const string Role = "role";
    public const string Email = "email";
    public const string Subject = "sub";
}

internal sealed class CurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public ActorKind Kind => User?.Identity?.IsAuthenticated == true
        ? User.FindFirstValue(RahiqClaims.Kind) switch
        {
            "customer" => ActorKind.Customer,
            "staff" => ActorKind.Staff,
            _ => ActorKind.Anonymous,
        }
        : accessor.HttpContext is null ? ActorKind.System : ActorKind.Anonymous;

    public Guid? Id => Guid.TryParse(User?.FindFirstValue(RahiqClaims.Subject) ?? User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string? Email => User?.FindFirstValue(RahiqClaims.Email) ?? User?.FindFirstValue(ClaimTypes.Email);

    public bool HasPermission(string permission) =>
        Kind == ActorKind.System || (Kind == ActorKind.Staff && User!.HasClaim(RahiqClaims.Permission, permission));
}
