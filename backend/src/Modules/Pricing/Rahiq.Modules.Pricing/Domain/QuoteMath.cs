using Rahiq.SharedKernel;

namespace Rahiq.Modules.Pricing.Domain;

/// <param name="PresetDiscount">A discount fixed before coupons, e.g. the saving of a fixed-price bundle.</param>
internal sealed record PricedLine(int Index, long UnitPrice, int Qty, int TaxRateBp, string Section, long PresetDiscount = 0)
{
    public long Gross => checked(UnitPrice * Qty);

    public long AfterPreset => Gross - PresetDiscount;
}

internal sealed record LineResult(int Index, long Gross, long Discount, long LineTotal, long Tax);

internal sealed record QuoteTotals(
    IReadOnlyList<LineResult> Lines,
    long Subtotal,
    long Discount,
    long CouponDiscount,
    long Shipping,
    long CodFee,
    long Tax,
    long Total,
    bool FreeShipping);

/// <summary>
/// The order arithmetic, in one place (commerce-flows.md §8): discount on the eligible subtotal → allocate it to lines
/// (remainder to the last line) → shipping on what is left → total. Integers only; the parts always add up.
/// </summary>
internal static class QuoteMath
{
    public static QuoteTotals Compute(
        IReadOnlyList<PricedLine> lines,
        CouponRule? coupon,
        Func<long, long> shippingForMerchandise,
        long codFee,
        int servicesTaxBp,
        Currency currency)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(shippingForMerchandise);

        foreach (var line in lines)
        {
            if (line.Qty <= 0 || line.UnitPrice < 0 || line.PresetDiscount < 0 || line.PresetDiscount > line.Gross)
            {
                throw new ArgumentException($"Line {line.Index} has invalid amounts.", nameof(lines));
            }
        }

        var subtotal = lines.Sum(l => l.Gross);
        var afterPreset = lines.Sum(l => l.AfterPreset);
        var couponShares = new long[lines.Count];
        long couponDiscount = 0;
        var freeShipping = false;

        if (coupon is not null && coupon.AppliesTo(afterPreset))
        {
            var eligible = Enumerable.Range(0, lines.Count).Where(i => coupon.Section is null || lines[i].Section == coupon.Section).ToList();
            var eligibleBase = eligible.Sum(i => lines[i].AfterPreset);

            couponDiscount = coupon.Kind switch
            {
                CouponKind.Percent => Money.Of(eligibleBase, currency).PercentOf((int)coupon.Value).Amount,
                CouponKind.Fixed => Math.Min(coupon.Value, eligibleBase),
                _ => 0,
            };
            freeShipping = coupon.Kind == CouponKind.FreeShipping;

            if (couponDiscount > 0)
            {
                var shares = Money.Of(couponDiscount, currency).Allocate(eligible.Select(i => lines[i].AfterPreset).ToList());
                for (var k = 0; k < eligible.Count; k++)
                {
                    couponShares[eligible[k]] = shares[k].Amount;
                }
            }
        }

        var results = new List<LineResult>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var discount = line.PresetDiscount + couponShares[i];
            var total = line.Gross - discount;
            var lineTax = Money.Of(total, currency).IncludedTax(line.TaxRateBp).Amount;
            results.Add(new LineResult(line.Index, line.Gross, discount, total, lineTax));
        }

        var discountTotal = results.Sum(r => r.Discount);
        var merchandise = subtotal - discountTotal;
        var shipping = freeShipping ? 0 : shippingForMerchandise(merchandise);
        var serviceTax = Money.Of(shipping + codFee, currency).IncludedTax(servicesTaxBp).Amount;
        var tax = results.Sum(r => r.Tax) + serviceTax;
        var totalAmount = subtotal - discountTotal + shipping + codFee;

        return new QuoteTotals(results, subtotal, discountTotal, couponDiscount, shipping, codFee, tax, totalAmount, freeShipping);
    }
}

internal enum CouponKind
{
    Percent,
    Fixed,
    FreeShipping,
}

/// <param name="Value">Basis points for <see cref="CouponKind.Percent"/>, minor units for <see cref="CouponKind.Fixed"/>.</param>
internal sealed record CouponRule(CouponKind Kind, long Value, string? Section, long? MinSubtotal)
{
    public bool AppliesTo(long merchandiseBeforeCoupon) => MinSubtotal is null || merchandiseBeforeCoupon >= MinSubtotal;
}
