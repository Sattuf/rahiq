using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Application;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Catalog.Domain;
using Rahiq.Modules.Inventory.Contracts;
using Rahiq.Modules.Pricing.Contracts;

namespace Rahiq.Modules.Catalog.Infrastructure;

/// <summary>
/// The storefront's own product search (same matching and ordering as /search), plus the variant-level price and stock
/// that the storefront shows on the product page. Everything is read live; nothing here is cached.
/// </summary>
internal sealed class CatalogSearch(
    ISender sender,
    RahiqDbContext db,
    ICatalogReader reader,
    IPriceReader prices,
    IInventoryService inventory,
    IOptions<StoreOptions> store) : ICatalogSearch
{
    public async Task<IReadOnlyList<ProductMatch>> SearchAsync(string? text, string? section, string locale, int limit, CancellationToken cancellationToken)
    {
        var cards = await sender.Send(new ListProductsQuery(new ProductFilter { Q = text, Section = section, Limit = Math.Clamp(limit, 1, 20) }, locale), cancellationToken);
        if (cards.Count == 0)
        {
            return [];
        }

        var productIds = cards.Select(c => c.Id).ToList();
        var variantIds = await db.Set<Product>().AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .SelectMany(p => p.Variants.Where(v => v.IsActive).Select(v => v.Id))
            .ToListAsync(cancellationToken);
        var offers = await GetOffersAsync(variantIds, locale, cancellationToken);

        return [.. cards.Select(c => new ProductMatch(
            c.Id, c.Slug, c.Name, c.Section, c.Type, c.ShortDescription,
            [.. offers.Values.Where(o => o.ProductId == c.Id).OrderBy(o => o.IsSample).ThenBy(o => o.Price)]))];
    }

    public async Task<IReadOnlyDictionary<Guid, VariantOffer>> GetOffersAsync(IReadOnlyCollection<Guid> variantIds, string locale, CancellationToken cancellationToken)
    {
        var info = await reader.GetVariantsAsync(variantIds, locale, cancellationToken);
        var sellable = info.Values.Where(v => v.IsSellable).ToList();
        var ids = sellable.Select(v => v.VariantId).ToList();
        var priceTable = await prices.GetCurrentAsync(ids, cancellationToken);
        var stockIds = ids.Concat(sellable.SelectMany(v => v.BundleComponents.Select(c => c.VariantId))).Distinct().ToList();
        var stock = await inventory.GetAvailableAsync(stockIds, cancellationToken);

        return sellable.ToDictionary(v => v.VariantId, v => new VariantOffer(
            v.VariantId,
            v.ProductId,
            v.ProductName,
            v.VariantLabel,
            priceTable.GetValueOrDefault(v.VariantId)?.Price.Amount,
            store.Value.Currency,
            Available(v, stock),
            v.IsSample,
            v.IsGiftBox));
    }

    /// <summary>A bundle's stock is the number of complete sets its components allow (same rule as the cart).</summary>
    private static int Available(VariantInfo v, IReadOnlyDictionary<Guid, int> stock) =>
        v.IsBundle
            ? v.BundleComponents.Count == 0 ? 0 : v.BundleComponents.Min(c => stock.GetValueOrDefault(c.VariantId) / Math.Max(1, c.Qty))
            : stock.GetValueOrDefault(v.VariantId);
}
