using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Security;
using Rahiq.Modules.Conversations.Presentation;
using Rahiq.Modules.Customers.Infrastructure;
using Rahiq.SharedKernel;

namespace Rahiq.Api;

/// <summary>
/// Tokens for the live inbox (ADR-019). The admin's real tokens stay in the BFF's encrypted cookie; the browser gets
/// only this one: two minutes, audience "rahiq-hub", one permission. The API rejects it everywhere except the hub,
/// and the hub only ever sends "conversation X changed".
/// </summary>
internal static class HubTokens
{
    public const string Scheme = "hub";
    public const string Audience = "rahiq-hub";
    public const string Policy = ConversationsHub.Policy;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public static TokenValidationParameters ValidationParameters(AuthOptions options)
    {
        var parameters = TokenService.ValidationParameters(options).Clone();
        parameters.ValidAudience = Audience;
        return parameters;
    }

    public static void MapHubTokens(this WebApplication app) =>
        app.MapPost("/api/admin/conversations/hub-token", (ClaimsPrincipal user, IOptions<AuthOptions> auth, IClock clock) =>
        {
            var o = auth.Value;
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = o.Issuer,
                Audience = Audience,
                IssuedAt = clock.UtcNow.UtcDateTime,
                NotBefore = clock.UtcNow.UtcDateTime,
                Expires = clock.UtcNow.Add(Lifetime).UtcDateTime,
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey)), SecurityAlgorithms.HmacSha256),
                Subject = new ClaimsIdentity(
                [
                    new Claim(RahiqClaims.Subject, user.FindFirstValue(RahiqClaims.Subject) ?? string.Empty),
                    new Claim(RahiqClaims.Kind, "staff"),
                    new Claim("amr", "mfa"),
                    new Claim(RahiqClaims.Permission, Permissions.ConversationsView),
                ]),
            });
            return Results.Ok(new { token });
        })
        .RequireAuthorization(Permissions.ConversationsView)
        .ExcludeFromDescription();
}
