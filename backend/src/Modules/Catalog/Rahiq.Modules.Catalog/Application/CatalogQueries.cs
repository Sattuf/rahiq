using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Catalog.Domain;
using Rahiq.Modules.Catalog.Infrastructure;
using Rahiq.Modules.Inventory.Contracts;
using Rahiq.Modules.Pricing.Contracts;
using Rahiq.SharedKernel;
using Rahiq.SharedKernel.Text;

namespace Rahiq.Modules.Catalog.Application;

public sealed record ListProductsQuery(ProductFilter Filter, string Locale) : IQuery<IReadOnlyList<ProductCardDto>>;

public sealed record GetProductQuery(string Slug, string Locale) : IQuery<ProductLookup>;

/// <summary>
/// Storefront reads. The catalog is small (tens of products), so filtering happens in memory over active
/// products; stock and price come live from Inventory and Pricing (the cart and checkout re-check both anyway).
/// </summary>
internal sealed class CatalogQueryHandlers(
    RahiqDbContext db,
    IPriceReader prices,
    IInventoryService inventory,
    IBlobStorage storage,
    IOptions<StoreOptions> store)
    : IRequestHandler<ListProductsQuery, IReadOnlyList<ProductCardDto>>, IRequestHandler<GetProductQuery, ProductLookup>
{
    private const int LowStockDisplay = 3;

    public async Task<IReadOnlyList<ProductCardDto>> Handle(ListProductsQuery request, CancellationToken cancellationToken)
    {
        var f = request.Filter;
        var query = LoadActive();
        if (f.Section is not null)
        {
            query = query.Where(p => p.Section == f.Section);
        }

        if (f.Type is not null)
        {
            query = query.Where(p => p.Type == f.Type);
        }

        var products = await query.ToListAsync(cancellationToken);
        var context = await LoadContext(products, cancellationToken);
        var folded = TextFolding.Fold(f.Q);

        var cards = products
            .Where(p => Matches(p, f, request.Locale, folded))
            .Select(p => ToCard(p, request.Locale, context))
            .Where(c => (f.MinPrice is null || c.Price >= f.MinPrice) && (f.MaxPrice is null || c.Price <= f.MaxPrice))
            .Where(c => f.Featured is not true || c.IsFeatured);

        cards = f.Sort switch
        {
            "price_asc" => cards.OrderBy(c => c.Price ?? long.MaxValue),
            "price_desc" => cards.OrderByDescending(c => c.Price ?? 0),
            "newest" => cards.OrderByDescending(c => products.First(p => p.Id == c.Id).PublishedAt),
            _ => cards.OrderByDescending(c => c.IsFeatured).ThenByDescending(c => c.Available).ThenBy(c => products.First(p => p.Id == c.Id).SortOrder),
        };

        return [.. cards.Take(Math.Clamp(f.Limit, 1, 200))];
    }

    public async Task<ProductLookup> Handle(GetProductQuery request, CancellationToken cancellationToken)
    {
        var slug = request.Slug.Trim().ToLowerInvariant();
        var product = await LoadActive().FirstOrDefaultAsync(p => p.Slug == slug, cancellationToken);
        if (product is null)
        {
            var moved = await db.Set<SlugRedirect>().AsNoTracking().Where(r => r.OldSlug == slug)
                .Join(db.Set<Product>(), r => r.ProductId, p => p.Id, (r, p) => p.Slug)
                .FirstOrDefaultAsync(cancellationToken);
            return new ProductLookup(null, moved is null ? null : new SlugMoved(moved));
        }

        var locale = request.Locale;
        var slots = await db.Set<GiftBoxSlot>().AsNoTracking().Where(s => s.ProductId == product.Id).OrderBy(s => s.SortOrder).ToListAsync(cancellationToken);
        var variantIds = product.Variants.Select(v => v.Id).ToList();
        var bundleItems = await db.Set<BundleItem>().AsNoTracking().Where(b => variantIds.Contains(b.BundleVariantId)).ToListAsync(cancellationToken);

        // Gift box slot options: sellable full-size products of the allowed sections.
        var optionProducts = new List<Product>();
        if (slots.Count > 0)
        {
            var sections = slots.SelectMany(s => s.AllowedSections).Distinct().ToList();
            optionProducts = await LoadActive().Where(p => sections.Contains(p.Section) && p.Type != ProductTypes.GiftBox && p.Type != ProductTypes.Bundle).ToListAsync(cancellationToken);
        }

        var bundleComponentIds = bundleItems.Select(b => b.ComponentVariantId).Distinct().ToList();
        var bundleProducts = bundleComponentIds.Count == 0 ? [] : await db.Set<Product>().AsNoTracking().Include(p => p.Translations).Include(p => p.Variants)
            .Where(p => p.Variants.Any(v => bundleComponentIds.Contains(v.Id))).ToListAsync(cancellationToken);

        var context = await LoadContext([product, .. optionProducts], cancellationToken, bundleItems);
        var batches = product.Section == Sections.Honey || product.Section == Sections.Perfume
            ? await inventory.GetCurrentBatchesAsync(variantIds, cancellationToken)
            : new Dictionary<Guid, BatchCard>();
        var t = CatalogText.For(product, locale);
        var dictionary = CatalogDictionary.Instance;

        var detail = new ProductDetailDto
        {
            Card = ToCard(product, locale, context),
            Story = t?.Story,
            Usage = t?.Usage,
            SeoTitle = t?.SeoTitle,
            SeoDescription = t?.SeoDescription,
            Attributes = product.AttributesJson.Clone(),
            Warnings = [.. product.Warnings.Zip(WarningRules.Render(product.Warnings, product.Allergens, locale), (code, text) => new WarningDto(code, text))],
            Allergens = [.. product.Allergens.Select(a => new AllergenDto(a, dictionary.Allergens.FirstOrDefault(x => x.Code == a)?.Name.For(locale) ?? a))],
            Variants = [.. product.Variants.Where(v => v.IsActive).OrderBy(v => v.SortOrder).ThenBy(v => v.VolumeMl ?? v.WeightG).Select(v => ToVariant(v, locale, context))],
            Media = [.. product.Media.OrderBy(m => m.SortOrder).Select(m => ToMedia(m, locale))],
            Batches = [.. batches.Values.Select(b => new BatchCardDto(b.VariantId, b.Code, b.PublicToken, b.ProducedAt, b.BestBefore, b.Origin, b.LabSummary, b.HasLabReport))],
            GiftSlots = [.. slots.Select(s => new GiftSlotDto(s.Code, s.Required, s.AllowedSections, [.. optionProducts
                .Where(p => s.AllowedSections.Contains(p.Section))
                .SelectMany(p => p.Variants.Where(v => v.IsActive && !v.IsSample).Select(v => (p, v)))
                .Where(x => context.Available.GetValueOrDefault(x.v.Id) > 0)
                .Select(x => new GiftOptionDto(x.v.Id, x.p.Id, x.p.Slug, CatalogText.Name(x.p, locale), x.v.Label(locale), x.p.Section,
                    context.Prices.GetValueOrDefault(x.v.Id)?.Price.Amount, FirstImage(x.p, locale)))]))],
            BundleParts = [.. bundleItems.Select(b =>
            {
                var owner = bundleProducts.FirstOrDefault(p => p.Variants.Any(v => v.Id == b.ComponentVariantId));
                var variant = owner?.Variants.First(v => v.Id == b.ComponentVariantId);
                return new BundlePartDto(b.ComponentVariantId, owner is null ? "?" : CatalogText.Name(owner, locale), variant?.Label(locale) ?? string.Empty, b.Qty);
            })],
            CrystallizationNote = product.Type is ProductTypes.Honey or ProductTypes.CombHoney ? dictionary.CrystallizationNote.For(locale) : null,
            Locales = [.. product.Translations.Select(x => x.Locale)],
            UpdatedAt = product.UpdatedAt,
        };

        return new ProductLookup(detail, null);
    }

    private IQueryable<Product> LoadActive() => db.Set<Product>().AsNoTracking()
        .Include(p => p.Translations)
        .Include(p => p.Variants)
        .Include(p => p.Media)
        .Where(p => p.Status == ProductStatuses.Active)
        .AsSplitQuery();

    private async Task<Context> LoadContext(IReadOnlyList<Product> products, CancellationToken cancellationToken, IReadOnlyList<BundleItem>? knownBundleItems = null)
    {
        var variantIds = products.SelectMany(p => p.Variants).Where(v => v.IsActive).Select(v => v.Id).ToList();
        var bundleVariantIds = products.SelectMany(p => p.Variants).Where(v => v.StockMode == "components").Select(v => v.Id).ToList();
        var bundleItems = knownBundleItems ?? (bundleVariantIds.Count == 0 ? [] :
            await db.Set<BundleItem>().AsNoTracking().Where(b => bundleVariantIds.Contains(b.BundleVariantId)).ToListAsync(cancellationToken));

        var stockIds = variantIds.Concat(bundleItems.Select(b => b.ComponentVariantId)).Distinct().ToList();
        var available = new Dictionary<Guid, int>(await inventory.GetAvailableAsync(stockIds, cancellationToken));

        // A bundle's stock is the number of complete sets its components allow.
        foreach (var group in bundleItems.GroupBy(b => b.BundleVariantId))
        {
            available[group.Key] = group.Min(b => available.GetValueOrDefault(b.ComponentVariantId) / b.Qty);
        }

        return new Context(await prices.GetCurrentAsync(variantIds, cancellationToken), available);
    }

    private static bool Matches(Product p, ProductFilter f, string locale, string foldedQuery)
    {
        var a = p.AttributesJson;
        if (f.Family is not null && !JsonHas(a, "families", f.Family))
        {
            return false;
        }

        if (f.Season is not null && !JsonHas(a, "seasons", f.Season))
        {
            return false;
        }

        if (f.Note is not null && !(a.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Object &&
            notes.EnumerateObject().Any(tier => tier.Value.ValueKind == JsonValueKind.Array && tier.Value.EnumerateArray().Any(n => n.GetString() == f.Note))))
        {
            return false;
        }

        foreach (var (key, wanted) in new[] { ("gender", f.Gender), ("concentration", f.Concentration), ("floralSource", f.FloralSource), ("texture", f.Texture), ("regionCode", f.RegionCode) })
        {
            if (wanted is not null && JsonString(a, key) != wanted && !(key == "floralSource" && JsonString(a, "honeyType") == wanted))
            {
                return false;
            }
        }

        if (f.WeightG is not null && !p.Variants.Any(v => v.IsActive && v.WeightG == f.WeightG))
        {
            return false;
        }

        if (f.VolumeMl is not null && !p.Variants.Any(v => v.IsActive && v.VolumeMl == f.VolumeMl))
        {
            return false;
        }

        if (foldedQuery.Length > 0)
        {
            var haystack = TextFolding.Fold(string.Join(' ', p.Translations.SelectMany(t => new[] { t.Name, t.ShortDescription }))
                + ' ' + string.Join(' ', NoteNames(a)) + ' ' + JsonString(a, "floralSource") + ' ' + p.Type);
            if (!foldedQuery.Split(' ').All(word => haystack.Contains(word, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> NoteNames(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var tier in notes.EnumerateObject().Where(t => t.Value.ValueKind == JsonValueKind.Array))
        {
            foreach (var id in tier.Value.EnumerateArray().Select(n => n.GetString()))
            {
                var entry = CatalogDictionary.Instance.Notes.FirstOrDefault(n => n.Code == id);
                if (entry is not null)
                {
                    foreach (var name in entry.Name.Values)
                    {
                        yield return name;
                    }
                }
            }
        }
    }

    private ProductCardDto ToCard(Product p, string locale, Context context)
    {
        var active = p.Variants.Where(v => v.IsActive).ToList();
        var full = active.Where(v => !v.IsSample).ToList();
        var priced = (full.Count > 0 ? full : active)
            .Select(v => (Variant: v, Price: context.Prices.GetValueOrDefault(v.Id)))
            .Where(x => x.Price is not null)
            .OrderBy(x => x.Price!.Price.Amount)
            .ToList();
        var cheapest = priced.FirstOrDefault();
        var a = p.AttributesJson;
        var t = CatalogText.For(p, locale);

        return new ProductCardDto
        {
            Id = p.Id,
            Slug = p.Slug,
            Section = p.Section,
            Type = p.Type,
            Name = t?.Name ?? p.Slug,
            ShortDescription = t?.ShortDescription,
            Image = FirstImage(p, locale),
            Price = cheapest.Price?.Price.Amount,
            PreviousPrice = cheapest.Price?.PreviousPrice?.Amount,
            Currency = store.Value.Currency,
            PriceVaries = priced.Select(x => x.Price!.Price.Amount).Distinct().Count() > 1,
            Available = active.Any(v => context.Available.GetValueOrDefault(v.Id) > 0),
            HasSample = active.Any(v => v.IsSample),
            IsFeatured = p.IsFeatured,
            Sizes = [.. full.OrderBy(v => v.VolumeMl ?? v.WeightG).Select(v => v.Label(locale)).Distinct()],
            Highlights = new HighlightsDto(
                JsonString(a, "concentration"),
                JsonStrings(a, "families"),
                JsonString(a, "gender"),
                JsonStrings(a, "seasons"),
                a.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Object ? JsonStrings(notes, "top") : [],
                JsonString(a, "floralSource") ?? JsonString(a, "honeyType"),
                JsonString(a, "texture"),
                JsonString(a, "regionCode")),
        };
    }

    private static VariantDto ToVariant(Variant v, string locale, Context context)
    {
        var price = context.Prices.GetValueOrDefault(v.Id);
        var free = context.Available.GetValueOrDefault(v.Id);
        return new VariantDto(v.Id, v.Sku, v.Label(locale), v.VolumeMl, v.WeightG, v.IsSample, price?.Price.Amount, price?.PreviousPrice?.Amount,
            free <= 0 ? "out" : free <= LowStockDisplay ? "low" : "in_stock", v.Gtin);
    }

    private MediaDto? FirstImage(Product p, string locale) =>
        p.Media.Where(m => !m.IsGenerated).OrderBy(m => m.Role == "catalog" ? 0 : 1).ThenBy(m => m.SortOrder).Select(m => ToMedia(m, locale)).FirstOrDefault();

    private MediaDto ToMedia(ProductMedia m, string locale) =>
        new(m.Id, storage.GetUrl(m.StorageKey).ToString(), m.Width, m.Height, m.Role, m.Alt.GetValueOrDefault(locale) ?? m.Alt.GetValueOrDefault("tr"));

    private static string? JsonString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> JsonStrings(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];

    private static bool JsonHas(JsonElement obj, string key, string value) => JsonStrings(obj, key).Contains(value);

    private sealed record Context(IReadOnlyDictionary<Guid, PriceInfo> Prices, IReadOnlyDictionary<Guid, int> Available);
}
