using Microsoft.EntityFrameworkCore;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Catalog.Domain;

namespace Rahiq.Modules.Catalog.Infrastructure;

internal sealed class CatalogReader(RahiqDbContext db, IBlobStorage storage) : ICatalogReader
{
    public async Task<IReadOnlyDictionary<Guid, VariantInfo>> GetVariantsAsync(IReadOnlyCollection<Guid> variantIds, string locale, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, VariantInfo>();
        }

        var ids = variantIds.Distinct().ToList();
        var variants = await db.Set<Variant>().AsNoTracking().Where(v => ids.Contains(v.Id)).ToListAsync(cancellationToken);
        var productIds = variants.Select(v => v.ProductId).Distinct().ToList();
        var products = await db.Set<Product>().AsNoTracking()
            .Include(p => p.Translations)
            .Include(p => p.Media.Where(m => m.Role == "catalog" && !m.IsGenerated))
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var bundleItems = await db.Set<BundleItem>().AsNoTracking().Where(b => ids.Contains(b.BundleVariantId)).ToListAsync(cancellationToken);
        var slots = await db.Set<GiftBoxSlot>().AsNoTracking().Where(s => productIds.Contains(s.ProductId)).ToListAsync(cancellationToken);

        return variants.ToDictionary(v => v.Id, v =>
        {
            var product = products[v.ProductId];
            var name = CatalogText.Name(product, locale);
            var image = product.Media.OrderBy(m => m.SortOrder).FirstOrDefault();
            return new VariantInfo
            {
                VariantId = v.Id,
                ProductId = product.Id,
                Slug = product.Slug,
                Sku = v.Sku,
                Section = product.Section,
                ProductType = product.Type,
                ProductName = name,
                VariantLabel = v.Label(locale),
                WarningCodes = product.Warnings,
                WarningTexts = WarningRules.Render(product.Warnings, product.Allergens, locale),
                Allergens = product.Allergens,
                IsSample = v.IsSample,
                ShippingClass = v.ShippingClass,
                ShippingWeightG = v.ShippingWeightG,
                TaxCategory = ProductTypes.TaxCategoryOf(product.Type),
                IsSellable = product.Status == ProductStatuses.Active && v.IsActive,
                IsBundle = v.StockMode == "components",
                IsGiftBox = product.Type == ProductTypes.GiftBox,
                BundleComponents = [.. bundleItems.Where(b => b.BundleVariantId == v.Id).Select(b => new BundleComponent(b.ComponentVariantId, b.Qty))],
                GiftBoxSlots = [.. slots.Where(s => s.ProductId == product.Id).OrderBy(s => s.SortOrder).Select(s => new GiftBoxSlotInfo(s.Code, s.AllowedSections, s.Required))],
                ImageUrl = image is null ? null : storage.GetUrl(image.StorageKey).ToString(),
            };
        });
    }
}

internal static class CatalogText
{
    public static ProductTranslation? For(Product product, string locale) =>
        product.Translations.FirstOrDefault(t => t.Locale == locale)
        ?? product.Translations.FirstOrDefault(t => t.Locale == "tr")
        ?? (product.Translations.Count > 0 ? product.Translations[0] : null);

    public static string Name(Product product, string locale) => For(product, locale)?.Name ?? product.Slug;
}
