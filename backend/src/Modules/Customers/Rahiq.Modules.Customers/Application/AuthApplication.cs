using Dapper;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Cart.Contracts;
using Rahiq.Modules.Customers.Contracts;
using Rahiq.Modules.Customers.Domain;
using Rahiq.Modules.Customers.Infrastructure;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Customers.Application;

public sealed record CustomerSession(TokenPair Tokens, CustomerProfileDto Profile);

public sealed record CustomerProfileDto(Guid Id, string Email, string? Name, string? Phone, string Locale, bool MarketingEmail, bool MarketingSms);

public sealed record StaffSession(TokenPair? Tokens, string? EnrollmentTicket, StaffProfileDto? Profile);

public sealed record StaffProfileDto(Guid Id, string Email, string Name, string Role, IReadOnlyList<string> Permissions);

public sealed record TotpEnrollmentDto(string Secret, string ProvisioningUri);

public sealed record RequestOtpCommand(string Email, string Locale) : ICommand<Result>;

public sealed record VerifyOtpCommand(string Email, string Code, string? GuestCartToken, string Locale) : ICommand<Result<CustomerSession>>, ICommitsOnFailure;

public sealed record RefreshCommand(string RefreshToken) : ICommand<Result<TokenPair>>, ICommitsOnFailure;

public sealed record LogoutCommand(string RefreshToken) : ICommand<Result>;

public sealed record StaffLoginCommand(string Email, string Password, string? TotpCode) : ICommand<Result<StaffSession>>, ICommitsOnFailure;

public sealed record StartTotpEnrollmentCommand(string Ticket) : ICommand<Result<TotpEnrollmentDto>>;

public sealed record ConfirmTotpEnrollmentCommand(string Ticket, string Code) : ICommand<Result<StaffSession>>;

public sealed record CreateStaffCommand(string Email, string Name, string Role, string Password) : ICommand<Result<Guid>>;

public sealed record UpdateStaffCommand(Guid Id, string Role, bool IsActive) : ICommand<Result>;

public sealed record StaffRowDto(Guid Id, string Email, string Name, string Role, bool IsActive, bool TotpEnabled, DateTimeOffset? LastLoginAt);

public sealed record ListStaffQuery : IQuery<IReadOnlyList<StaffRowDto>>;

internal sealed class RequestOtpValidator : AbstractValidator<RequestOtpCommand>
{
    public RequestOtpValidator() => RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254);
}

internal sealed class CreateStaffValidator : AbstractValidator<CreateStaffCommand>
{
    public CreateStaffValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Role).Must(StaffRoles.IsValid).WithErrorCode("role_invalid");
    }
}

internal sealed class AuthHandlers(
    RahiqDbContext db,
    IDbSession session,
    TokenService tokens,
    PasswordPolicy passwords,
    IOtpDelivery otpDelivery,
    ICartAccess carts,
    IDataProtectionProvider protection,
    ICurrentActor actor,
    IAuditLog audit,
    IClock clock)
    : IRequestHandler<RequestOtpCommand, Result>,
      IRequestHandler<VerifyOtpCommand, Result<CustomerSession>>,
      IRequestHandler<RefreshCommand, Result<TokenPair>>,
      IRequestHandler<LogoutCommand, Result>,
      IRequestHandler<StaffLoginCommand, Result<StaffSession>>,
      IRequestHandler<StartTotpEnrollmentCommand, Result<TotpEnrollmentDto>>,
      IRequestHandler<ConfirmTotpEnrollmentCommand, Result<StaffSession>>,
      IRequestHandler<CreateStaffCommand, Result<Guid>>,
      IRequestHandler<UpdateStaffCommand, Result>,
      IRequestHandler<ListStaffQuery, IReadOnlyList<StaffRowDto>>
{
    private static readonly PasswordHasher<StaffUser> Hasher = new();

    private IDataProtector TotpProtector => protection.CreateProtector("Rahiq.Staff.Totp");

    /// <summary>Always "sent", whether or not the address has an account: nothing to learn by probing (security.md §1).</summary>
    public async Task<Result> Handle(RequestOtpCommand request, CancellationToken cancellationToken)
    {
        var email = Customer.NormalizeEmail(request.Email);
        var recent = await db.Set<OtpCode>().CountAsync(o => o.Email == email && o.CreatedAt > clock.UtcNow.AddMinutes(-15), cancellationToken);
        if (recent >= 5)
        {
            return Result.Success(); // Silently throttled; the HTTP rate limiter also applies.
        }

        var (otp, code) = OtpCode.Issue(email, clock.UtcNow);
        db.Add(otp);
        await otpDelivery.SendAsync(email, code, request.Locale, cancellationToken);
        return Result.Success();
    }

    public async Task<Result<CustomerSession>> Handle(VerifyOtpCommand request, CancellationToken cancellationToken)
    {
        var email = Customer.NormalizeEmail(request.Email);
        var otp = await db.Set<OtpCode>().Where(o => o.Email == email && o.ConsumedAt == null).OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (otp is null || !otp.TryConsume(request.Code, clock.UtcNow))
        {
            return AuthErrors.InvalidCredentials; // ICommitsOnFailure: the attempt count is saved.
        }

        var customer = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Email == email && c.DeletedAt == null, cancellationToken);
        if (customer is null)
        {
            customer = Customer.Register(email, request.Locale, clock.UtcNow);
            db.Add(customer);
        }

        await db.SaveChangesAsync(cancellationToken);
        await carts.MergeGuestCartAsync(request.GuestCartToken, customer.Id, cancellationToken);
        var pair = await tokens.IssueCustomerAsync(customer, null, cancellationToken);
        return new CustomerSession(pair, await Profile(customer, cancellationToken));
    }

    /// <summary>Rotation with reuse detection: a used token presented again revokes its whole family.</summary>
    public async Task<Result<TokenPair>> Handle(RefreshCommand request, CancellationToken cancellationToken)
    {
        var hash = RefreshToken.Hash(request.RefreshToken);
        var token = await db.Set<RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is null)
        {
            return AuthErrors.RefreshInvalid;
        }

        if (token.WasReused || token.RevokedAt is not null)
        {
            await RevokeFamily(token.FamilyId, cancellationToken);
            audit.Record("auth.refresh_reuse", token.SubjectKind, token.SubjectId.ToString());
            await db.SaveChangesAsync(cancellationToken);
            return AuthErrors.RefreshInvalid;
        }

        if (!token.IsUsable(clock.UtcNow))
        {
            return AuthErrors.RefreshInvalid;
        }

        token.MarkUsed(clock.UtcNow);
        if (token.SubjectKind == "staff")
        {
            var staff = await db.Set<StaffUser>().FirstOrDefaultAsync(s => s.Id == token.SubjectId && s.IsActive && s.TotpEnabled, cancellationToken);
            return staff is null ? AuthErrors.RefreshInvalid : await tokens.IssueStaffAsync(staff, token.FamilyId, cancellationToken);
        }

        var customer = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Id == token.SubjectId && c.DeletedAt == null, cancellationToken);
        return customer is null ? AuthErrors.RefreshInvalid : await tokens.IssueCustomerAsync(customer, token.FamilyId, cancellationToken);
    }

    public async Task<Result> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        var hash = RefreshToken.Hash(request.RefreshToken);
        var token = await db.Set<RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is not null)
        {
            await RevokeFamily(token.FamilyId, cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result<StaffSession>> Handle(StaffLoginCommand request, CancellationToken cancellationToken)
    {
        var email = Customer.NormalizeEmail(request.Email);
        var staff = await db.Set<StaffUser>().FirstOrDefaultAsync(s => s.Email == email, cancellationToken);
        if (staff is null || !staff.IsActive)
        {
            Hasher.VerifyHashedPassword(StaffUserPlaceholder, PlaceholderHash, request.Password); // Same timing either way.
            return AuthErrors.InvalidCredentials;
        }

        if (staff.IsLocked(clock.UtcNow))
        {
            return AuthErrors.Locked;
        }

        if (Hasher.VerifyHashedPassword(staff, staff.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            return await Fail(staff, cancellationToken);
        }

        if (!staff.TotpEnabled)
        {
            // Two-factor is mandatory for every admin account (security.md §1): enroll before anything else.
            return new StaffSession(null, tokens.IssueEnrollmentTicket(staff), null);
        }

        if (string.IsNullOrWhiteSpace(request.TotpCode))
        {
            return AuthErrors.TotpRequired;
        }

        if (!Totp.Verify(TotpProtector.Unprotect(staff.TotpSecretProtected!), request.TotpCode, clock.UtcNow))
        {
            return await Fail(staff, cancellationToken);
        }

        staff.RecordSuccess(clock.UtcNow);
        audit.Record("staff.login", "staff", staff.Id.ToString());
        return new StaffSession(await tokens.IssueStaffAsync(staff, null, cancellationToken), null, ToProfile(staff));
    }

    public async Task<Result<TotpEnrollmentDto>> Handle(StartTotpEnrollmentCommand request, CancellationToken cancellationToken)
    {
        var staff = await FromTicket(request.Ticket, cancellationToken);
        if (staff is null)
        {
            return AuthErrors.InvalidCredentials;
        }

        var secret = Totp.NewSecret();
        staff.SetPendingTotp(TotpProtector.Protect(secret));
        return new TotpEnrollmentDto(secret, Totp.ProvisioningUri(secret, staff.Email));
    }

    public async Task<Result<StaffSession>> Handle(ConfirmTotpEnrollmentCommand request, CancellationToken cancellationToken)
    {
        var staff = await FromTicket(request.Ticket, cancellationToken);
        if (staff?.TotpSecretProtected is null || !Totp.Verify(TotpProtector.Unprotect(staff.TotpSecretProtected), request.Code, clock.UtcNow))
        {
            return AuthErrors.InvalidCredentials;
        }

        staff.EnableTotp();
        staff.RecordSuccess(clock.UtcNow);
        audit.Record("staff.totp_enabled", "staff", staff.Id.ToString());
        return new StaffSession(await tokens.IssueStaffAsync(staff, null, cancellationToken), null, ToProfile(staff));
    }

    public async Task<Result<Guid>> Handle(CreateStaffCommand request, CancellationToken cancellationToken)
    {
        if (!actor.HasPermission(Permissions.StaffManage))
        {
            return Error.Forbidden("staff.forbidden", "Only the owner manages staff.");
        }

        if (!await passwords.IsAcceptableAsync(request.Password, cancellationToken))
        {
            return AuthErrors.PasswordTooWeak;
        }

        var email = Customer.NormalizeEmail(request.Email);
        if (await db.Set<StaffUser>().AnyAsync(s => s.Email == email, cancellationToken))
        {
            return Error.Conflict("staff.exists", "A staff account with this e-mail exists.");
        }

        var staff = StaffUser.Create(email, request.Name, request.Role, "pending", clock.UtcNow);
        staff.ChangePassword(Hasher.HashPassword(staff, request.Password));
        db.Add(staff);
        audit.Record("staff.created", "staff", staff.Id.ToString(), new { email, request.Role });
        return staff.Id;
    }

    public async Task<Result> Handle(UpdateStaffCommand request, CancellationToken cancellationToken)
    {
        if (!StaffRoles.IsValid(request.Role))
        {
            return Error.Validation("staff.role_invalid", "Unknown role.");
        }

        var staff = await db.Set<StaffUser>().FirstOrDefaultAsync(s => s.Id == request.Id, cancellationToken);
        if (staff is null)
        {
            return Error.NotFound("staff.not_found", "Staff account not found.");
        }

        if (staff.Id == actor.Id && (!request.IsActive || request.Role != StaffRoles.Owner) && staff.Role == StaffRoles.Owner)
        {
            return Error.Conflict("staff.self_demotion", "An owner cannot demote or deactivate themselves.");
        }

        staff.ChangeRole(request.Role);
        if (!request.IsActive)
        {
            staff.Deactivate();
            await RevokeSubject(staff.Id, cancellationToken);
        }

        audit.Record("staff.permissions_changed", "staff", staff.Id.ToString(), request);
        return Result.Success();
    }

    public async Task<IReadOnlyList<StaffRowDto>> Handle(ListStaffQuery request, CancellationToken cancellationToken) =>
        await db.Set<StaffUser>().AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new StaffRowDto(s.Id, s.Email, s.Name, s.Role, s.IsActive, s.TotpEnabled, s.LastLoginAt)).ToListAsync(cancellationToken);

    internal async Task<CustomerProfileDto> Profile(Customer customer, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var consents = (await session.Connection.QueryAsync<(string Channel, bool Granted)>(new CommandDefinition("""
            SELECT DISTINCT ON (channel) channel, granted FROM customers.consents WHERE email = @email AND purpose = 'marketing' ORDER BY channel, at DESC, id DESC
            """, new { email = customer.Email }, session.Transaction, cancellationToken: cancellationToken))).ToDictionary(c => c.Channel, c => c.Granted);
        return new CustomerProfileDto(customer.Id, customer.Email, customer.Name, customer.Phone, customer.Locale,
            consents.GetValueOrDefault("email"), consents.GetValueOrDefault("sms"));
    }

    private static StaffProfileDto ToProfile(StaffUser staff) =>
        new(staff.Id, staff.Email, staff.Name, staff.Role, StaffRoles.PermissionsByRole.GetValueOrDefault(staff.Role) ?? []);

    private async Task<Result<StaffSession>> Fail(StaffUser staff, CancellationToken cancellationToken)
    {
        staff.RecordFailure(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return AuthErrors.InvalidCredentials;
    }

    private async Task<StaffUser?> FromTicket(string ticket, CancellationToken cancellationToken)
    {
        var id = tokens.ReadEnrollmentTicket(ticket);
        return id is null ? null : await db.Set<StaffUser>().FirstOrDefaultAsync(s => s.Id == id && s.IsActive && !s.TotpEnabled, cancellationToken);
    }

    private Task<int> RevokeFamily(Guid familyId, CancellationToken cancellationToken) =>
        db.Set<RefreshToken>().Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.UtcNow), cancellationToken);

    private Task<int> RevokeSubject(Guid subjectId, CancellationToken cancellationToken) =>
        db.Set<RefreshToken>().Where(t => t.SubjectId == subjectId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.UtcNow), cancellationToken);

    private static readonly StaffUser StaffUserPlaceholder = StaffUser.Create("placeholder@invalid.local", "x", StaffRoles.Support, "x", DateTimeOffset.UnixEpoch);
    private static readonly string PlaceholderHash = Hasher.HashPassword(StaffUserPlaceholder, "placeholder-password");
}

/// <summary>Creates the first owner from the command line (`dotnet Rahiq.Api.dll create-owner`), bypassing permissions.</summary>
public sealed record BootstrapOwnerCommand(string Email, string Name, string Password) : ICommand<Result<Guid>>;

internal sealed class BootstrapOwnerHandler(RahiqDbContext db, PasswordPolicy passwords, IClock clock) : IRequestHandler<BootstrapOwnerCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(BootstrapOwnerCommand request, CancellationToken cancellationToken)
    {
        if (!await passwords.IsAcceptableAsync(request.Password, cancellationToken))
        {
            return AuthErrors.PasswordTooWeak;
        }

        var email = Customer.NormalizeEmail(request.Email);
        var existing = await db.Set<StaffUser>().FirstOrDefaultAsync(s => s.Email == email, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var owner = StaffUser.Create(email, request.Name, StaffRoles.Owner, "pending", clock.UtcNow);
        owner.ChangePassword(new PasswordHasher<StaffUser>().HashPassword(owner, request.Password));
        db.Add(owner);
        return owner.Id;
    }
}
