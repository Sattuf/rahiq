using Rahiq.SharedKernel;

namespace Rahiq.Domain.Tests.SharedKernel;

public class MoneyTests
{
    private static Money Try(long amount) => Money.Of(amount, Currency.TRY);

    [Fact]
    public void Adds_and_subtracts_in_minor_units()
    {
        Assert.Equal(Try(1_999), Try(1_000) + Try(999));
        Assert.Equal(Try(1), Try(1_000) - Try(999));
        Assert.Equal(Try(3_000), Try(1_000) * 3);
        Assert.Equal(Try(3_000), Try(1_000).Multiply(3));
        Assert.Equal(Try(1_500), Try(1_000).Add(Try(500)));
        Assert.Equal(Try(500), Try(1_000).Subtract(Try(500)));
    }

    [Fact]
    public void Refuses_to_mix_currencies()
    {
        Assert.Throws<InvalidOperationException>(() => Try(1) + Money.Of(1, Currency.EUR));
        Assert.Throws<InvalidOperationException>(() => Try(1) - Money.Of(1, Currency.USD));
        Assert.Throws<InvalidOperationException>(() => Try(1) < Money.Of(1, Currency.USD));
    }

    [Fact]
    public void Overflow_is_an_error_not_a_wrap()
    {
        Assert.Throws<OverflowException>(() => Try(long.MaxValue) + Try(1));
        Assert.Throws<OverflowException>(() => Try(long.MaxValue) * 2);
    }

    [Fact]
    public void Compares_and_picks_min_max()
    {
        Assert.True(Try(1) < Try(2));
        Assert.True(Try(2) > Try(1));
        Assert.True(Try(2) >= Try(2));
        Assert.True(Try(2) <= Try(2));
        Assert.Equal(Try(1), Money.Min(Try(1), Try(2)));
        Assert.Equal(Try(2), Money.Max(Try(1), Try(2)));
        Assert.True(Money.Zero(Currency.TRY).IsZero);
        Assert.True(Try(-1).IsNegative);
        Assert.Equal("150 TRY", Try(150).ToString());
    }

    [Theory]
    [InlineData(10_000, 1_000, 1_000)] // 10% of 100.00
    [InlineData(999, 1_500, 150)]      // 15% of 9.99 = 1.4985 → 1.50
    [InlineData(333, 5_000, 167)]      // 50% of 3.33 = 1.665 → 1.67 (half up)
    [InlineData(1, 5_000, 1)]          // 0.005 → 0.01
    [InlineData(1, 4_999, 0)]
    [InlineData(-333, 5_000, -167)]    // symmetric for negatives
    public void Percent_rounds_half_away_from_zero(long amount, int basisPoints, long expected) =>
        Assert.Equal(expected, Try(amount).PercentOf(basisPoints).Amount);

    [Theory]
    [InlineData(12_000, 2_000, 2_000)] // 120.00 incl. 20% → 20.00 tax
    [InlineData(10_100, 100, 100)]     // 101.00 incl. 1% → 1.00 tax
    [InlineData(999, 2_000, 167)]      // 9.99 × 20/120 = 1.665 → 1.67
    [InlineData(5_000, 0, 0)]
    public void Included_tax_is_extracted_from_gross(long gross, int rateBp, long expectedTax) =>
        Assert.Equal(expectedTax, Try(gross).IncludedTax(rateBp).Amount);

    [Fact]
    public void Allocation_parts_always_sum_to_the_whole_with_remainder_on_the_last()
    {
        var parts = Try(1_000).Allocate([1, 1, 1]);

        Assert.Equal([333, 333, 334], parts.Select(p => p.Amount));
    }

    [Fact]
    public void Allocation_is_proportional_to_weights()
    {
        var parts = Try(900).Allocate([2_000, 1_000]);

        Assert.Equal([600, 300], parts.Select(p => p.Amount));
    }

    [Fact]
    public void Allocation_with_zero_weights_puts_everything_on_the_last_part()
    {
        var parts = Try(100).Allocate([0, 0]);

        Assert.Equal([0, 100], parts.Select(p => p.Amount));
    }

    [Fact]
    public void Allocation_rejects_bad_weights()
    {
        Assert.Throws<ArgumentException>(() => Try(1).Allocate([]));
        Assert.Throws<ArgumentException>(() => Try(1).Allocate([-1, 2]));
        Assert.Throws<ArgumentNullException>(() => Try(1).Allocate(null!));
    }

    [Fact]
    public void Random_allocations_never_lose_or_create_a_kurus()
    {
        var random = new Random(20260925);
        for (var i = 0; i < 2_000; i++)
        {
            var amount = random.NextInt64(0, 10_000_000);
            var weights = Enumerable.Range(0, random.Next(1, 12)).Select(_ => random.NextInt64(0, 1_000_000)).ToList();

            var parts = Try(amount).Allocate(weights);

            Assert.Equal(amount, parts.Sum(p => p.Amount));
            Assert.All(parts, p => Assert.False(p.IsNegative));
        }
    }

    [Fact]
    public void Negative_rates_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Try(1).PercentOf(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Try(1).IncludedTax(-1));
    }

    [Theory]
    [InlineData("try", "TRY")]
    [InlineData(" eur ", "EUR")]
    public void Currency_codes_are_normalised(string raw, string expected) =>
        Assert.Equal(expected, Currency.From(raw).Code);

    [Theory]
    [InlineData("TL")]
    [InlineData("TRYY")]
    [InlineData("1RY")]
    public void Invalid_currency_codes_are_rejected(string raw) =>
        Assert.Throws<ArgumentException>(() => Currency.From(raw));

    [Fact]
    public void Currency_prints_its_code() => Assert.Equal("TRY", Currency.TRY.ToString());
}
