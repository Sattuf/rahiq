using System.Text.RegularExpressions;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Pricing.Domain;

internal static class CouponErrors
{
    public static readonly Error NotFound = Error.Validation("coupon.not_found", "This code does not exist.");
    public static readonly Error Inactive = Error.Validation("coupon.inactive", "This code is no longer active.");
    public static readonly Error NotStarted = Error.Validation("coupon.not_started", "This code is not valid yet.");
    public static readonly Error Expired = Error.Validation("coupon.expired", "This code has expired.");
    public static readonly Error Exhausted = Error.Conflict("coupon.exhausted", "This code has been fully used.");
    public static readonly Error CustomerLimit = Error.Conflict("coupon.customer_limit", "You have already used this code.");
    public static readonly Error MinSubtotal = Error.Validation("coupon.min_subtotal", "The order total is below this code's minimum.");
    public static readonly Error InvalidCode = Error.Validation("coupon.code_invalid", "Codes are 3-32 letters, digits or dashes.");
}

internal sealed partial class Coupon : AggregateRoot<Guid>
{
    private Coupon()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string Kind { get; private set; } = "percent";

    public long Value { get; private set; }

    public string Currency { get; private set; } = "TRY";

    public long? MinSubtotal { get; private set; }

    public string? Section { get; private set; }

    public int? MaxUses { get; private set; }

    public int UsedCount { get; private set; }

    public int? PerCustomerLimit { get; private set; }

    public DateTimeOffset? StartsAt { get; private set; }

    public DateTimeOffset? EndsAt { get; private set; }

    public bool Active { get; private set; }

    public string? Description { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Codes are compared case-insensitively with the invariant culture, never tr-TR: "iyi" must match "IYI"
    /// (risks-troubleshooting.md). The database column is citext as a second guard.
    /// </summary>
    public static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    public static Result<Coupon> Create(CouponSpec spec, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var coupon = new Coupon { Id = Ids.New(), CreatedAt = now, Active = true };
        var applied = coupon.Apply(spec);
        return applied.IsSuccess ? coupon : applied.Error;
    }

    public Result Apply(CouponSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var code = NormalizeCode(spec.Code);
        if (!CodePattern().IsMatch(code))
        {
            return CouponErrors.InvalidCode;
        }

        if (spec.Kind is not ("percent" or "fixed" or "free_shipping"))
        {
            return Error.Validation("coupon.kind_invalid", "Kind must be percent, fixed or free_shipping.");
        }

        if (spec.Kind == "percent" && spec.Value is < 1 or > 10_000)
        {
            return Error.Validation("coupon.percent_invalid", "A percentage is 0.01% to 100% (1 to 10000 basis points).");
        }

        if (spec.Kind == "fixed" && spec.Value <= 0)
        {
            return Error.Validation("coupon.amount_invalid", "A fixed discount must be positive.");
        }

        if (spec.StartsAt is not null && spec.EndsAt is not null && spec.EndsAt <= spec.StartsAt)
        {
            return Error.Validation("coupon.window_invalid", "The end must be after the start.");
        }

        if (spec.MaxUses is not null && spec.MaxUses < UsedCount)
        {
            return Error.Validation("coupon.max_uses_below_used", "The limit cannot be below the number already used.");
        }

        Code = code;
        Kind = spec.Kind;
        Value = spec.Kind == "free_shipping" ? 0 : spec.Value;
        Currency = spec.Currency;
        MinSubtotal = spec.MinSubtotal;
        Section = spec.Section;
        MaxUses = spec.MaxUses;
        PerCustomerLimit = spec.PerCustomerLimit;
        StartsAt = spec.StartsAt;
        EndsAt = spec.EndsAt;
        Description = spec.Description;
        Active = spec.Active;
        return Result.Success();
    }

    /// <summary>Checks that do not depend on concurrent usage. Usage limits are enforced atomically in SQL at hold time.</summary>
    public Result CheckUsable(DateTimeOffset now)
    {
        if (!Active)
        {
            return CouponErrors.Inactive;
        }

        if (StartsAt is not null && now < StartsAt)
        {
            return CouponErrors.NotStarted;
        }

        if (EndsAt is not null && now >= EndsAt)
        {
            return CouponErrors.Expired;
        }

        if (MaxUses is not null && UsedCount >= MaxUses)
        {
            return CouponErrors.Exhausted;
        }

        return Result.Success();
    }

    public CouponRule ToRule() => new(
        Kind switch { "percent" => CouponKind.Percent, "fixed" => CouponKind.Fixed, _ => CouponKind.FreeShipping },
        Value,
        Section,
        MinSubtotal);

    [GeneratedRegex("^[A-Z0-9-]{3,32}$")]
    private static partial Regex CodePattern();
}

internal sealed record CouponSpec(
    string Code,
    string Kind,
    long Value,
    string Currency,
    long? MinSubtotal,
    string? Section,
    int? MaxUses,
    int? PerCustomerLimit,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string? Description,
    bool Active = true);
