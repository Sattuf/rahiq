using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Infrastructure.Common.Security;
using Rahiq.Modules.Customers.Contracts;
using Rahiq.Modules.Customers.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Customers.Infrastructure;

public sealed record AuthOptions
{
    public const string Section = "Auth";

    public string Issuer { get; init; } = "rahiq-api";

    public string Audience { get; init; } = "rahiq-web";

    /// <summary>At least 32 bytes. From the secret store in production, never the repository.</summary>
    public string SigningKey { get; init; } = string.Empty;

    public int AccessTokenMinutes { get; init; } = 15;

    /// <summary>Optional Have I Been Pwned k-anonymity check for staff passwords (security.md §1).</summary>
    public bool CheckBreachedPasswords { get; init; }
}

internal sealed class Address
{
    public Guid Id { get; init; }

    public Guid CustomerId { get; init; }

    public string? Label { get; set; }

    public required string Data { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class CustomersModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customers", "customers");
            b.HasKey(c => c.Id);
            b.Ignore(c => c.DomainEvents);
            b.Property(c => c.Email).HasColumnType("citext");
        });
        modelBuilder.Entity<OtpCode>(b =>
        {
            b.ToTable("otp_codes", "customers");
            b.HasKey(o => o.Id);
            b.Property(o => o.Email).HasColumnType("citext");
        });
        modelBuilder.Entity<StaffUser>(b =>
        {
            b.ToTable("staff_users", "customers");
            b.HasKey(s => s.Id);
            b.Ignore(s => s.DomainEvents);
            b.Property(s => s.Email).HasColumnType("citext");
        });
        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.ToTable("refresh_tokens", "customers");
            b.HasKey(t => t.Id);
            b.Ignore(t => t.WasReused);
        });
        modelBuilder.Entity<Address>(b =>
        {
            b.ToTable("addresses", "customers");
            b.HasKey(a => a.Id);
            b.Property(a => a.Data).HasColumnType("jsonb");
        });
    }
}

public sealed record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

internal sealed class TokenService(RahiqDbContext db, IOptions<AuthOptions> options, IClock clock)
{
    public const string EnrollmentKind = "staff_enroll";

    public async Task<TokenPair> IssueCustomerAsync(Customer customer, Guid? family, CancellationToken cancellationToken) =>
        await Issue(customer.Id, "customer", customer.Email, [], family, cancellationToken);

    public async Task<TokenPair> IssueStaffAsync(StaffUser staff, Guid? family, CancellationToken cancellationToken)
    {
        var permissions = StaffRoles.PermissionsByRole.GetValueOrDefault(staff.Role) ?? [];
        var claims = permissions.Select(p => new Claim(RahiqClaims.Permission, p))
            .Append(new Claim(RahiqClaims.Role, staff.Role))
            .Append(new Claim("amr", "mfa"))
            .Append(new Claim("name", staff.Name));
        return await Issue(staff.Id, "staff", staff.Email, claims, family, cancellationToken);
    }

    /// <summary>A five-minute ticket that only allows setting up TOTP (first admin sign-in).</summary>
    public string IssueEnrollmentTicket(StaffUser staff) =>
        Jwt(staff.Id, EnrollmentKind, staff.Email, [], clock.UtcNow.AddMinutes(5));

    public Guid? ReadEnrollmentTicket(string ticket)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        try
        {
            var principal = handler.ValidateToken(ticket, ValidationParameters(options.Value), out _);
            return principal.FindFirstValue(RahiqClaims.Kind) == EnrollmentKind && Guid.TryParse(principal.FindFirstValue(RahiqClaims.Subject), out var id) ? id : null;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }

    public static TokenValidationParameters ValidationParameters(AuthOptions options) => new()
    {
        ValidIssuer = options.Issuer,
        ValidAudience = options.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = RahiqClaims.Email,
        RoleClaimType = RahiqClaims.Role,
    };

    private async Task<TokenPair> Issue(Guid subject, string kind, string email, IEnumerable<Claim> extra, Guid? family, CancellationToken cancellationToken)
    {
        var expires = clock.UtcNow.AddMinutes(options.Value.AccessTokenMinutes);
        var access = Jwt(subject, kind, email, extra, expires);
        var (refresh, raw) = RefreshToken.Issue(subject, kind, family, clock.UtcNow);
        db.Add(refresh);
        await db.SaveChangesAsync(cancellationToken);
        return new TokenPair(access, expires, raw, refresh.ExpiresAt);
    }

    private string Jwt(Guid subject, string kind, string email, IEnumerable<Claim> extra, DateTimeOffset expires)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.SigningKey));
        var claims = new List<Claim>
        {
            new(RahiqClaims.Subject, subject.ToString()),
            new(RahiqClaims.Kind, kind),
            new(RahiqClaims.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(extra);
        var token = new JwtSecurityToken(options.Value.Issuer, options.Value.Audience, claims, clock.UtcNow.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

internal sealed class CustomerDirectory(RahiqDbContext db, IDbSession session) : ICustomerDirectory
{
    public async Task<Guid?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = Customer.NormalizeEmail(email);
        return await db.Set<Customer>().Where(c => c.Email == normalized && c.DeletedAt == null).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<int> CodRefusalsAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = Customer.NormalizeEmail(email);
        return await db.Set<Customer>().Where(c => c.Email == normalized).Select(c => c.CodRefusals).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task RecordCodRefusalAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = Customer.NormalizeEmail(email);
        var customer = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Email == normalized, cancellationToken);
        customer?.RecordCodRefusal();
    }

    public async Task RecordMarketingConsentAsync(string email, Guid? customerId, string channel, bool granted, string source, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customers.consents (customer_id, email, channel, purpose, granted, source) VALUES (@customerId, @email, @channel, 'marketing', @granted, @source)
            """, new { customerId, email = Customer.NormalizeEmail(email), channel, granted, source }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> HasMarketingConsentAsync(string email, string channel, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<bool?>(new CommandDefinition("""
            SELECT granted FROM customers.consents WHERE email = @email AND channel = @channel AND purpose = 'marketing' ORDER BY at DESC, id DESC LIMIT 1
            """, new { email = Customer.NormalizeEmail(email), channel }, session.Transaction, cancellationToken: cancellationToken)) ?? false;
    }
}

/// <summary>Password policy for staff: 10+ characters, not a common or breached password (security.md §1).</summary>
internal sealed class PasswordPolicy(IHttpClientFactory httpClients, IOptions<AuthOptions> options)
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "1234567890", "12345678910", "qwertyuiop", "password123", "password1!", "iloveyou12", "1q2w3e4r5t", "abcdefghij",
        "qwerty1234", "passw0rd12", "admin12345", "sifre12345", "parola1234", "rahiq12345", "welcome123", "letmein123",
    };

    public async Task<bool> IsAcceptableAsync(string password, CancellationToken cancellationToken)
    {
        if (password.Length < 10 || Common.Contains(password) || password.Distinct().Count() < 4)
        {
            return false;
        }

        if (!options.Value.CheckBreachedPasswords)
        {
            return true;
        }

        // k-anonymity: only the first five hex characters of the SHA-1 leave the server.
#pragma warning disable CA5350 // The HIBP range API is defined over SHA-1.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5350
        using var client = httpClients.CreateClient("hibp");
        var body = await client.GetStringAsync(new Uri($"https://api.pwnedpasswords.com/range/{hash[..5]}"), cancellationToken);
        return !body.Split('\n').Any(line => line.StartsWith(hash[5..], StringComparison.OrdinalIgnoreCase));
    }
}
