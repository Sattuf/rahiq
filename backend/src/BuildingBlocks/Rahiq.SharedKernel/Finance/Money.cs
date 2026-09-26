namespace Rahiq.SharedKernel;

/// <summary>
/// An amount in the smallest unit of its currency (kuruş for TRY). Never a double, never a decimal in
/// intermediate arithmetic (Law 5, ADR-004). All rounding is explicit and lives in this type.
/// </summary>
public readonly record struct Money(long Amount, Currency Currency) : IComparable<Money>
{
    public static Money Zero(Currency currency) => new(0, currency);

    public static Money Of(long amount, Currency currency) => new(amount, currency);

    public bool IsZero => Amount == 0;

    public bool IsNegative => Amount < 0;

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.Amount + right.Amount), left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.Amount - right.Amount), left.Currency);
    }

    public static Money operator *(Money money, int quantity) => new(checked(money.Amount * quantity), money.Currency);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public Money Add(Money other) => this + other;

    public Money Subtract(Money other) => this - other;

    public Money Multiply(int quantity) => this * quantity;

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public static Money Min(Money left, Money right) => left <= right ? left : right;

    public static Money Max(Money left, Money right) => left >= right ? left : right;

    /// <summary>A share of this amount in basis points (1000 = 10%), rounded half away from zero.</summary>
    public Money PercentOf(int basisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(basisPoints);
        return new Money(IntegerMath.DivRoundHalfUp((Int128)Amount * basisPoints, 10_000), Currency);
    }

    /// <summary>
    /// The tax already contained in this tax-inclusive amount: gross × rate / (1 + rate), rounded half up.
    /// Prices in Türkiye are shown tax-inclusive (compliance.md).
    /// </summary>
    public Money IncludedTax(int rateBasisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rateBasisPoints);
        if (rateBasisPoints == 0)
        {
            return Zero(Currency);
        }

        return new Money(IntegerMath.DivRoundHalfUp((Int128)Amount * rateBasisPoints, 10_000 + rateBasisPoints), Currency);
    }

    /// <summary>
    /// Splits this amount across weights proportionally. Each share is floored and the remainder goes to the
    /// last share, so the parts always add up to exactly this amount (commerce-flows.md §8).
    /// </summary>
    public IReadOnlyList<Money> Allocate(IReadOnlyList<long> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0)
        {
            throw new ArgumentException("At least one weight is required.", nameof(weights));
        }

        if (weights.Any(w => w < 0))
        {
            throw new ArgumentException("Weights cannot be negative.", nameof(weights));
        }

        Int128 total = 0;
        foreach (var weight in weights)
        {
            total += weight;
        }

        var parts = new Money[weights.Count];
        if (total == 0)
        {
            for (var i = 0; i < parts.Length - 1; i++)
            {
                parts[i] = Zero(Currency);
            }

            parts[^1] = this;
            return parts;
        }

        long allocated = 0;
        for (var i = 0; i < weights.Count - 1; i++)
        {
            var share = (long)((Int128)Amount * weights[i] / total);
            parts[i] = new Money(share, Currency);
            allocated += share;
        }

        parts[^1] = new Money(Amount - allocated, Currency);
        return parts;
    }

    public override string ToString() => $"{Amount} {Currency.Code}";

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException($"Currency mismatch: {left.Currency} vs {right.Currency}.");
        }
    }
}

internal static class IntegerMath
{
    /// <summary>Integer division rounded half away from zero.</summary>
    public static long DivRoundHalfUp(Int128 numerator, Int128 denominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);

        var negative = numerator < 0;
        var abs = negative ? -numerator : numerator;
        var quotient = (abs + (denominator / 2)) / denominator;
        return (long)(negative ? -quotient : quotient);
    }
}
