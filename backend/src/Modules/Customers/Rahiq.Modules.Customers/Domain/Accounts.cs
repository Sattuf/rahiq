using System.Security.Cryptography;
using System.Text;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Customers.Domain;

internal static class AuthErrors
{
    /// <summary>One message for every sign-in failure: never reveal whether an e-mail exists (security.md §1).</summary>
    public static readonly Error InvalidCredentials = Error.Unauthorized("auth.invalid", "The details are not correct, or the code has expired.");
    public static readonly Error Locked = Error.TooManyRequests("auth.locked", "Too many attempts. Try again later.");
    public static readonly Error TotpRequired = Error.Unauthorized("auth.totp_required", "Enter the code from your authenticator app.");
    public static readonly Error TotpNotEnrolled = Error.Forbidden("auth.totp_enrollment_required", "Set up two-factor authentication to continue.");
    public static readonly Error RefreshInvalid = Error.Unauthorized("auth.refresh_invalid", "Please sign in again.");
    public static readonly Error PasswordTooWeak = Error.Validation("auth.password_weak", "Passwords are at least 10 characters and not a common password.");
}

internal sealed class Customer : AggregateRoot<Guid>
{
    private Customer()
    {
    }

    public string Email { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    public string? Name { get; private set; }

    public string Locale { get; private set; } = Locales.Default;

    public int CodRefusals { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public static Customer Register(string email, string locale, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        Email = NormalizeEmail(email),
        Locale = Locales.OrDefault(locale),
        CreatedAt = now,
    };

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public void UpdateProfile(string? name, string? phone, string locale)
    {
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        Locale = Locales.OrDefault(locale);
    }

    public void RecordCodRefusal() => CodRefusals++;

    /// <summary>
    /// KVKK erasure: personal data is removed; the row stays (anonymised) because orders must be kept for the
    /// legal accounting period (security.md §9).
    /// </summary>
    public void Erase(DateTimeOffset now)
    {
        Email = $"erased-{Id:N}@invalid.local";
        Phone = null;
        Name = null;
        DeletedAt = now;
    }
}

internal sealed class OtpCode : Entity<Guid>
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private OtpCode()
    {
    }

    public string Email { get; private set; } = string.Empty;

    public string CodeHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static (OtpCode Otp, string Code) Issue(string email, DateTimeOffset now)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var otp = new OtpCode
        {
            Id = Ids.New(),
            Email = Customer.NormalizeEmail(email),
            CodeHash = Hash(email, code),
            ExpiresAt = now + Lifetime,
            CreatedAt = now,
        };
        return (otp, code);
    }

    public bool TryConsume(string code, DateTimeOffset now)
    {
        if (ConsumedAt is not null || now >= ExpiresAt || Attempts >= MaxAttempts)
        {
            return false;
        }

        Attempts++;
        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(CodeHash), Encoding.ASCII.GetBytes(Hash(Email, code.Trim())));
        if (matches)
        {
            ConsumedAt = now;
        }

        return matches;
    }

    private static string Hash(string email, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Customer.NormalizeEmail(email)}:{code}")));
}

internal sealed class StaffUser : AggregateRoot<Guid>
{
    private StaffUser()
    {
    }

    public string Email { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Role { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public string? TotpSecretProtected { get; private set; }

    public bool TotpEnabled { get; private set; }

    public int FailedAttempts { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public static StaffUser Create(string email, string name, string role, string passwordHash, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        Email = Customer.NormalizeEmail(email),
        Name = name.Trim(),
        Role = role,
        PasswordHash = passwordHash,
        IsActive = true,
        CreatedAt = now,
    };

    public bool IsLocked(DateTimeOffset now) => LockedUntil is not null && LockedUntil > now;

    /// <summary>Progressive lock: 5 failures → 1 min, then doubling up to 1 hour (security.md §1).</summary>
    public void RecordFailure(DateTimeOffset now)
    {
        FailedAttempts++;
        if (FailedAttempts >= 5)
        {
            var minutes = Math.Min(60, Math.Pow(2, FailedAttempts - 5));
            LockedUntil = now.AddMinutes(minutes);
        }
    }

    public void RecordSuccess(DateTimeOffset now)
    {
        FailedAttempts = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }

    public void SetPendingTotp(string protectedSecret)
    {
        TotpSecretProtected = protectedSecret;
        TotpEnabled = false;
    }

    public void EnableTotp() => TotpEnabled = true;

    public void ChangeRole(string role) => Role = role;

    public void ChangePassword(string hash) => PasswordHash = hash;

    public void Deactivate() => IsActive = false;
}

internal sealed class RefreshToken : Entity<Guid>
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private RefreshToken()
    {
    }

    public Guid FamilyId { get; private set; }

    public Guid SubjectId { get; private set; }

    public string SubjectKind { get; private set; } = "customer";

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public static (RefreshToken Token, string Raw) Issue(Guid subjectId, string subjectKind, Guid? familyId, DateTimeOffset now)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (new RefreshToken
        {
            Id = Ids.New(),
            FamilyId = familyId ?? Ids.New(),
            SubjectId = subjectId,
            SubjectKind = subjectKind,
            TokenHash = Hash(raw),
            ExpiresAt = now + Lifetime,
            CreatedAt = now,
        }, raw);
    }

    public static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && RevokedAt is null && now < ExpiresAt;

    /// <summary>A token that was already used coming back means it was stolen: the caller revokes the whole family.</summary>
    public bool WasReused => UsedAt is not null;

    public void MarkUsed(DateTimeOffset now) => UsedAt = now;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 30 s, 6 digits), compatible with common authenticator apps.</summary>
internal static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => ToBase32(RandomNumberGenerator.GetBytes(20));

    public static string ProvisioningUri(string secret, string account, string issuer = "Rahiq") =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";

    public static string Code(string secret, DateTimeOffset at)
    {
        var counter = at.ToUnixTimeSeconds() / 30;
        return Compute(FromBase32(secret), counter);
    }

    /// <summary>Accepts the previous, current and next 30-second step to absorb clock drift.</summary>
    public static bool Verify(string secret, string code, DateTimeOffset now)
    {
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var key = FromBase32(secret);
        var counter = now.ToUnixTimeSeconds() / 30;
        for (var drift = -1; drift <= 1; drift++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(key, counter + drift)), Encoding.ASCII.GetBytes(code)))
            {
                return true;
            }
        }

        return false;
    }

    private static string Compute(byte[] key, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);
#pragma warning disable CA5350 // RFC 6238 mandates HMAC-SHA1 for compatibility with authenticator apps.
        var hash = HMACSHA1.HashData(key, message);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    private static byte[] FromBase32(string input)
    {
        var clean = input.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("Invalid base32 secret.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
