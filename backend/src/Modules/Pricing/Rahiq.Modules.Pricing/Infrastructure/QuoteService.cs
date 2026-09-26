using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Pricing.Contracts;
using Rahiq.Modules.Pricing.Domain;
using Rahiq.Modules.Shipping.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Pricing.Infrastructure;

/// <summary>
/// Turns cart items into the exact priced and taxed lines an order will freeze. Every amount comes from the server's
/// own price table, never from the browser (commerce-flows.md §5). Bundles and gift boxes are expanded into "leaf"
/// lines so each component carries its own tax rate (ADR-017).
/// </summary>
internal sealed class QuoteService(
    RahiqDbContext db,
    ICatalogReader catalog,
    IPriceReader prices,
    IShippingRates shipping,
    IClock clock,
    IOptions<StoreOptions> options) : IQuoteService
{
    public async Task<Quote> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var store = options.Value;
        var currency = Currency.From(store.Currency);
        var problems = new List<QuoteProblem>();

        var ids = request.Items.Select(i => i.VariantId).Concat(request.Items.SelectMany(i => i.GiftBoxComponents ?? [])).Distinct().ToList();
        var variants = new Dictionary<Guid, VariantInfo>(await catalog.GetVariantsAsync(ids, request.Locale, cancellationToken));
        var componentIds = variants.Values.SelectMany(v => v.BundleComponents.Select(c => c.VariantId)).Where(id => !variants.ContainsKey(id)).Distinct().ToList();
        foreach (var (id, info) in await catalog.GetVariantsAsync(componentIds, request.Locale, cancellationToken))
        {
            variants[id] = info;
        }

        var priceTable = await prices.GetCurrentAsync([.. variants.Keys], cancellationToken);
        var taxRates = await CurrentTaxRates(cancellationToken);
        long PriceOf(Guid id) => priceTable.TryGetValue(id, out var p) ? p.Price.Amount : -1;

        var leaves = new List<Leaf>();
        for (var index = 0; index < request.Items.Count; index++)
        {
            var item = request.Items[index];
            if (!variants.TryGetValue(item.VariantId, out var info) || !info.IsSellable || PriceOf(item.VariantId) < 0 || item.Qty <= 0)
            {
                problems.Add(new QuoteProblem(item.VariantId, "unavailable"));
                continue;
            }

            if (info.IsBundle)
            {
                var parts = info.BundleComponents.Select(c => (Info: variants.GetValueOrDefault(c.VariantId), c.Qty)).ToList();
                if (parts.Count == 0 || parts.Any(p => p.Info is null || !p.Info.IsSellable || PriceOf(p.Info.VariantId) < 0))
                {
                    problems.Add(new QuoteProblem(item.VariantId, "unavailable"));
                    continue;
                }

                var weights = parts.Select(p => PriceOf(p.Info!.VariantId) * p.Qty * item.Qty).ToList();
                var saving = Math.Max(0, weights.Sum() - (PriceOf(item.VariantId) * item.Qty));
                var shares = Money.Of(saving, currency).Allocate(weights);
                var groupId = Ids.New();
                for (var k = 0; k < parts.Count; k++)
                {
                    leaves.Add(new Leaf(index, parts[k].Info!, parts[k].Qty * item.Qty, PriceOf(parts[k].Info!.VariantId), shares[k].Amount, groupId, info.ProductName));
                }

                continue;
            }

            if (info.IsGiftBox)
            {
                var chosen = (item.GiftBoxComponents ?? []).Select(id => variants.GetValueOrDefault(id)).ToList();
                var required = info.GiftBoxSlots.Where(s => s.Required).ToList();
                var valid = chosen.Count >= required.Count && chosen.Count <= info.GiftBoxSlots.Count
                    && chosen.All(c => c is not null && c.IsSellable && !c.IsSample && !c.IsBundle && !c.IsGiftBox && PriceOf(c.VariantId) >= 0)
                    && chosen.Select((c, i) => info.GiftBoxSlots[i].AllowedSections.Contains(c!.Section)).All(ok => ok);
                if (!valid)
                {
                    problems.Add(new QuoteProblem(item.VariantId, "gift_box_incomplete"));
                    continue;
                }

                var groupId = Ids.New();
                leaves.Add(new Leaf(index, info, item.Qty, PriceOf(info.VariantId), 0, groupId, info.ProductName));
                leaves.AddRange(chosen.Select(c => new Leaf(index, c!, item.Qty, PriceOf(c!.VariantId), 0, groupId, info.ProductName)));
                continue;
            }

            leaves.Add(new Leaf(index, info, item.Qty, PriceOf(item.VariantId), 0, null, null));
        }

        // Limits the cart also enforces, re-checked here because the quote is what gets paid.
        var samples = leaves.Where(l => l.Info.IsSample).Sum(l => l.Qty);
        if (samples > store.MaxSamplesPerOrder)
        {
            problems.AddRange(leaves.Where(l => l.Info.IsSample).Select(l => new QuoteProblem(l.Info.VariantId, "sample_limit")).DistinctBy(p => p.VariantId));
        }

        problems.AddRange(leaves.GroupBy(l => l.Info.VariantId).Where(g => g.Sum(l => l.Qty) > store.MaxQuantityPerVariant * Math.Max(1, g.Count())).Select(g => new QuoteProblem(g.Key, "max_per_variant")));

        var priced = leaves.Select((l, i) => new PricedLine(i, l.UnitPrice, l.Qty, taxRates.GetValueOrDefault(l.Info.TaxCategory, 2_000), l.Info.Section, l.Preset)).ToList();

        var (rule, couponError, appliedCode) = await ResolveCoupon(request.CouponCode, request.CustomerKey, cancellationToken);
        var codFee = request.PaymentMethod == "cod" ? store.CodFee : 0;
        var servicesRate = taxRates.GetValueOrDefault(TaxCategories.Services, 2_000);
        var weight = leaves.Sum(l => l.Info.ShippingWeightG * l.Qty);
        var hasFlammable = leaves.Any(l => l.Info.ShippingClass == ShippingClasses.Flammable);

        // First pass: merchandise after discounts decides free shipping; second pass: real shipping.
        var first = QuoteMath.Compute(priced, rule, _ => 0, codFee, servicesRate, currency);
        var shippingOptions = request.ProvinceCode is { } province && leaves.Count > 0
            ? await shipping.OptionsAsync(province, weight, Money.Of(first.Subtotal - first.Discount, currency), hasFlammable, cancellationToken)
            : [];
        var method = request.ShippingMethod ?? (shippingOptions.Count > 0 ? shippingOptions[0].Method : null);
        var chosenShipping = shippingOptions.FirstOrDefault(o => o.Method == method);
        if (request.ProvinceCode is not null && leaves.Count > 0 && chosenShipping is null)
        {
            problems.Add(new QuoteProblem(Guid.Empty, "shipping_unavailable"));
        }

        var totals = QuoteMath.Compute(priced, rule, _ => chosenShipping?.Amount ?? 0, codFee, servicesRate, currency);
        if (rule is not null && couponError is null && !rule.AppliesTo(first.Subtotal - leaves.Sum(l => l.Preset)))
        {
            couponError = CouponErrors.MinSubtotal.Code;
        }

        var lines = leaves.Select((l, i) => new QuoteLine
        {
            ItemIndex = l.ItemIndex,
            VariantId = l.Info.VariantId,
            ProductId = l.Info.ProductId,
            Sku = l.Info.Sku,
            Section = l.Info.Section,
            ProductType = l.Info.ProductType,
            Name = l.Info.ProductName,
            VariantLabel = l.Info.VariantLabel,
            Warnings = l.Info.WarningTexts,
            IsSample = l.Info.IsSample,
            ShippingClass = l.Info.ShippingClass,
            GroupId = l.GroupId,
            GroupLabel = l.GroupLabel,
            UnitPrice = l.UnitPrice,
            Qty = l.Qty,
            Discount = totals.Lines[i].Discount,
            TaxRateBp = priced[i].TaxRateBp,
            TaxAmount = totals.Lines[i].Tax,
            LineTotal = totals.Lines[i].LineTotal,
            Returnable = l.Info.Section == Sections.Perfume,
        }).ToList();

        return new Quote
        {
            Currency = currency.Code,
            Lines = lines,
            Subtotal = totals.Subtotal,
            Discount = totals.Discount,
            CouponDiscount = totals.CouponDiscount,
            Shipping = totals.Shipping,
            CodFee = totals.CodFee,
            Tax = totals.Tax,
            Total = totals.Total,
            ShippingWeightG = weight,
            AppliedCoupon = couponError is null ? appliedCode : null,
            CouponError = couponError,
            FreeShippingApplied = totals.FreeShipping || chosenShipping?.IsFree == true,
            FreeShippingThreshold = await shipping.FreeShippingThresholdAsync(cancellationToken),
            ShippingOptions = [.. shippingOptions.Select(o => new QuoteShippingOption(o.Method, rule?.Kind == CouponKind.FreeShipping && couponError is null ? 0 : o.Amount, o.IsFree || (rule?.Kind == CouponKind.FreeShipping && couponError is null)))],
            Problems = problems,
            CodAvailable = totals.Total - totals.CodFee + store.CodFee <= store.CodMaxOrderTotal,
        };
    }

    private async Task<(CouponRule? Rule, string? Error, string? Code)> ResolveCoupon(string? code, string? customerKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (null, null, null);
        }

        var normalized = Coupon.NormalizeCode(code);
        var coupon = await db.Set<Coupon>().AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);
        if (coupon is null)
        {
            return (null, CouponErrors.NotFound.Code, normalized);
        }

        var usable = coupon.CheckUsable(clock.UtcNow);
        if (usable.IsFailure)
        {
            return (null, usable.Error.Code, normalized);
        }

        if (coupon.PerCustomerLimit is { } limit && !string.IsNullOrWhiteSpace(customerKey))
        {
            var used = await db.Set<CouponRedemption>().CountAsync(r => r.CouponId == coupon.Id && r.CustomerKey == customerKey && r.Status != "released", cancellationToken);
            if (used >= limit)
            {
                return (null, CouponErrors.CustomerLimit.Code, normalized);
            }
        }

        return (coupon.ToRule(), null, coupon.Code);
    }

    private async Task<Dictionary<string, int>> CurrentTaxRates(CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var rates = await db.Set<TaxRate>().AsNoTracking().Where(t => t.ValidFrom <= today).ToListAsync(cancellationToken);
        return rates.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ValidFrom).First().RateBp);
    }

    private sealed record Leaf(int ItemIndex, VariantInfo Info, int Qty, long UnitPrice, long Preset, Guid? GroupId, string? GroupLabel);
}
