using Rahiq.Modules.Pricing.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Domain.Tests.Pricing;

public class QuoteMathTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static QuoteTotals Compute(IReadOnlyList<PricedLine> lines, CouponRule? coupon = null, long shipping = 8_990, long codFee = 0, long? freeFrom = null) =>
        QuoteMath.Compute(lines, coupon, merchandise => freeFrom is not null && merchandise >= freeFrom ? 0 : shipping, codFee, 2_000, Currency.TRY);

    [Fact]
    public void Totals_follow_subtotal_minus_discount_plus_shipping()
    {
        var totals = Compute([new(0, 45_000, 1, 2_000, "perfume"), new(1, 32_000, 2, 100, "honey")]);

        Assert.Equal(109_000, totals.Subtotal);
        Assert.Equal(0, totals.Discount);
        Assert.Equal(8_990, totals.Shipping);
        Assert.Equal(117_990, totals.Total);
    }

    [Fact]
    public void Percent_coupon_is_allocated_to_lines_with_the_remainder_on_the_last()
    {
        var totals = Compute([new(0, 1_000, 1, 2_000, "perfume"), new(1, 1_000, 1, 2_000, "perfume"), new(2, 1_000, 1, 2_000, "perfume")],
            new CouponRule(CouponKind.Percent, 1_000, null, null));

        Assert.Equal(300, totals.CouponDiscount);
        Assert.Equal([100, 100, 100], totals.Lines.Select(l => l.Discount));
        Assert.Equal(totals.Discount, totals.Lines.Sum(l => l.Discount));
    }

    [Fact]
    public void Section_coupon_discounts_only_that_section()
    {
        var totals = Compute([new(0, 50_000, 1, 2_000, "perfume"), new(1, 30_000, 1, 100, "honey")],
            new CouponRule(CouponKind.Percent, 1_000, "honey", null));

        Assert.Equal(3_000, totals.CouponDiscount);
        Assert.Equal(0, totals.Lines[0].Discount);
        Assert.Equal(3_000, totals.Lines[1].Discount);
    }

    [Fact]
    public void Fixed_coupon_never_exceeds_the_eligible_amount()
    {
        var totals = Compute([new(0, 2_000, 1, 100, "honey")], new CouponRule(CouponKind.Fixed, 5_000, null, null));

        Assert.Equal(2_000, totals.CouponDiscount);
        Assert.Equal(0, totals.Lines[0].LineTotal);
    }

    [Fact]
    public void Coupon_below_its_minimum_does_nothing()
    {
        var totals = Compute([new(0, 2_000, 1, 100, "honey")], new CouponRule(CouponKind.Percent, 1_000, null, 10_000));

        Assert.Equal(0, totals.CouponDiscount);
    }

    [Fact]
    public void Free_shipping_coupon_zeroes_shipping()
    {
        var totals = Compute([new(0, 2_000, 1, 100, "honey")], new CouponRule(CouponKind.FreeShipping, 0, null, null));

        Assert.True(totals.FreeShipping);
        Assert.Equal(0, totals.Shipping);
        Assert.Equal(2_000, totals.Total);
    }

    [Fact]
    public void Free_shipping_threshold_uses_the_amount_after_discount()
    {
        var lines = new PricedLine[] { new(0, 150_000, 1, 100, "honey") };

        Assert.Equal(0, Compute(lines, freeFrom: 150_000).Shipping);
        Assert.Equal(8_990, Compute(lines, new CouponRule(CouponKind.Percent, 100, null, null), freeFrom: 150_000).Shipping);
    }

    [Fact]
    public void Bundle_savings_are_a_preset_discount_and_the_coupon_applies_after_them()
    {
        var totals = Compute([new(0, 10_000, 1, 2_000, "perfume", PresetDiscount: 2_000), new(1, 10_000, 1, 100, "honey", PresetDiscount: 2_000)],
            new CouponRule(CouponKind.Percent, 1_000, null, null));

        Assert.Equal(20_000, totals.Subtotal);
        Assert.Equal(1_600, totals.CouponDiscount); // 10% of 16,000
        Assert.Equal(5_600, totals.Discount);
        Assert.Equal([7_200, 7_200], totals.Lines.Select(l => l.LineTotal));
    }

    [Fact]
    public void Tax_is_taken_from_each_line_at_its_own_rate_and_from_services()
    {
        var totals = Compute([new(0, 12_000, 1, 2_000, "perfume"), new(1, 10_100, 1, 100, "honey")], shipping: 1_200, codFee: 0);

        Assert.Equal(2_000, totals.Lines[0].Tax);
        Assert.Equal(100, totals.Lines[1].Tax);
        Assert.Equal(2_000 + 100 + 200, totals.Tax);
    }

    [Fact]
    public void Cash_on_delivery_fee_is_added_to_the_total()
    {
        var totals = Compute([new(0, 10_000, 1, 100, "honey")], codFee: 4_990);

        Assert.Equal(10_000 + 8_990 + 4_990, totals.Total);
    }

    [Fact]
    public void Invalid_lines_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Compute([new(0, 100, 0, 100, "honey")]));
        Assert.Throws<ArgumentException>(() => Compute([new(0, -1, 1, 100, "honey")]));
        Assert.Throws<ArgumentException>(() => Compute([new(0, 100, 1, 100, "honey", PresetDiscount: 101)]));
    }

    /// <summary>testing.md commerce test 8, at the arithmetic level (the database-level run is in Rahiq.CommerceTests).</summary>
    [Fact]
    public void A_thousand_random_carts_always_add_up_to_the_last_kurus()
    {
        var random = new Random(42);
        var sections = new[] { "perfume", "honey", "shared" };
        var rates = new[] { 100, 1_000, 2_000 };

        for (var cart = 0; cart < 1_000; cart++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 9)).Select(i =>
            {
                var unit = random.NextInt64(1, 500_000);
                var qty = random.Next(1, 11);
                var preset = random.Next(4) == 0 ? random.NextInt64(0, unit * qty / 3) : 0;
                return new PricedLine(i, unit, qty, rates[random.Next(rates.Length)], sections[random.Next(sections.Length)], preset);
            }).ToList();

            CouponRule? coupon = random.Next(4) switch
            {
                0 => null,
                1 => new CouponRule(CouponKind.Percent, random.Next(1, 10_001), random.Next(2) == 0 ? null : sections[random.Next(3)], null),
                2 => new CouponRule(CouponKind.Fixed, random.NextInt64(1, 200_000), random.Next(2) == 0 ? null : sections[random.Next(3)], random.Next(2) == 0 ? null : random.NextInt64(0, 300_000)),
                _ => new CouponRule(CouponKind.FreeShipping, 0, null, null),
            };
            var codFee = random.Next(2) == 0 ? 0 : 4_990;

            var totals = Compute(lines, coupon, shipping: random.NextInt64(0, 30_000), codFee: codFee, freeFrom: random.Next(2) == 0 ? null : 150_000);

            Assert.Equal(totals.Subtotal - totals.Discount + totals.Shipping + totals.CodFee, totals.Total);
            Assert.Equal(totals.Discount, totals.Lines.Sum(l => l.Discount));
            Assert.Equal(totals.Subtotal - totals.Discount, totals.Lines.Sum(l => l.LineTotal));
            Assert.All(totals.Lines, l => Assert.Equal(l.Gross - l.Discount, l.LineTotal));
            Assert.All(totals.Lines, l => Assert.InRange(l.LineTotal, 0, l.Gross));
            Assert.All(totals.Lines, l => Assert.InRange(l.Tax, 0, l.LineTotal));
            Assert.Equal(lines.Sum(l => l.PresetDiscount) + totals.CouponDiscount, totals.Discount);
            Assert.True(totals.Total >= 0);
        }
    }
}

public class PreviousPriceRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static PriceEntry Price(long amount, int fromDaysAgo, int? toDaysAgo) => new()
    {
        VariantId = Guid.Empty,
        Currency = "TRY",
        Amount = amount,
        ValidFrom = Now.AddDays(-fromDaysAgo),
        ValidTo = toDaysAgo is null ? null : Now.AddDays(-toDaysAgo.Value),
    };

    /// <summary>testing.md commerce test 12.</summary>
    [Fact]
    public void Previous_price_is_the_lowest_of_the_30_days_before_the_discount()
    {
        var history = new[] { Price(50_000, 90, 20), Price(45_000, 20, 5), Price(40_000, 5, null) };

        Assert.Equal(45_000, PreviousPriceRule.Compute(history, Now));
    }

    [Fact]
    public void Prices_older_than_the_window_do_not_count()
    {
        var history = new[] { Price(60_000, 100, 40), Price(50_000, 40, 3), Price(45_000, 3, null) };

        Assert.Equal(50_000, PreviousPriceRule.Compute(history, Now));
    }

    [Fact]
    public void A_price_rise_shows_no_previous_price()
    {
        var history = new[] { Price(40_000, 60, 2), Price(45_000, 2, null) };

        Assert.Null(PreviousPriceRule.Compute(history, Now));
    }

    [Fact]
    public void A_raise_then_a_drop_back_is_not_a_discount()
    {
        // 400 for months, 500 for 3 days, back to 400: the lowest of the window is 400, so no strike-through.
        var history = new[] { Price(40_000, 100, 10), Price(50_000, 10, 7), Price(40_000, 7, null) };

        Assert.Null(PreviousPriceRule.Compute(history, Now));
    }

    [Fact]
    public void No_history_means_no_previous_price()
    {
        Assert.Null(PreviousPriceRule.Compute([Price(40_000, 5, null)], Now));
        Assert.Null(PreviousPriceRule.Compute([], Now));
    }
}

public class CouponTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static CouponSpec Spec(string code = "IYI10", string kind = "percent", long value = 1_000) =>
        new(code, kind, value, "TRY", null, null, null, null, null, null, null);

    [Fact]
    public void Codes_are_normalised_with_the_invariant_culture()
    {
        // "iyi" with tr-TR upper-casing becomes "İYİ" and would never match "IYI" (risks-troubleshooting.md).
        var coupon = Coupon.Create(Spec("iyi10"), Now).Value;

        Assert.Equal("IYI10", coupon.Code);
        Assert.Equal("IYI10", Coupon.NormalizeCode(" iyi10 "));
    }

    [Theory]
    [InlineData("A!")]
    [InlineData("AB")]
    [InlineData("A B C")]
    public void Bad_codes_are_rejected(string code) =>
        Assert.Equal("coupon.code_invalid", Coupon.Create(Spec(code), Now).Error.Code);

    [Theory]
    [InlineData("percent", 0)]
    [InlineData("percent", 10_001)]
    [InlineData("fixed", 0)]
    [InlineData("bogus", 5)]
    public void Bad_values_are_rejected(string kind, long value) =>
        Assert.True(Coupon.Create(Spec(kind: kind, value: value), Now).IsFailure);

    [Fact]
    public void Window_and_activity_are_checked()
    {
        var future = Coupon.Create(Spec() with { StartsAt = Now.AddDays(1) }, Now).Value;
        var past = Coupon.Create(Spec() with { EndsAt = Now.AddDays(-1), StartsAt = Now.AddDays(-5) }, Now).Value;
        var inactive = Coupon.Create(Spec() with { Active = false }, Now).Value;

        Assert.Equal("coupon.not_started", future.CheckUsable(Now).Error.Code);
        Assert.Equal("coupon.expired", past.CheckUsable(Now).Error.Code);
        Assert.Equal("coupon.inactive", inactive.CheckUsable(Now).Error.Code);
        Assert.True(Coupon.Create(Spec(), Now).Value.CheckUsable(Now).IsSuccess);
        Assert.True(Coupon.Create(Spec() with { StartsAt = Now, EndsAt = Now.AddDays(-1) }, Now).IsFailure);
    }

    [Fact]
    public void Rule_reflects_kind_and_free_shipping_has_no_value()
    {
        var free = Coupon.Create(Spec(kind: "free_shipping", value: 999), Now).Value;

        Assert.Equal(0, free.Value);
        Assert.Equal(CouponKind.FreeShipping, free.ToRule().Kind);
        Assert.Equal(CouponKind.Fixed, Coupon.Create(Spec(kind: "fixed", value: 5_000), Now).Value.ToRule().Kind);
        Assert.Equal(CouponKind.Percent, Coupon.Create(Spec(), Now).Value.ToRule().Kind);
    }
}
