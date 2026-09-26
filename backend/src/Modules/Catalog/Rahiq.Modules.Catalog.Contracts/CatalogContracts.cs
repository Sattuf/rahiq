using Rahiq.SharedKernel;

namespace Rahiq.Modules.Catalog.Contracts;

public static class Sections
{
    public const string Perfume = "perfume";
    public const string Honey = "honey";
    public const string Shared = "shared";

    public static readonly IReadOnlyList<string> All = [Perfume, Honey, Shared];
}

public static class TaxCategories
{
    public const string Food = "food";
    public const string Cosmetics = "cosmetics";
    public const string Packaging = "packaging";
    public const string Services = "services";
}

public static class ShippingClasses
{
    public const string Standard = "standard";
    public const string Fragile = "fragile";
    public const string Liquid = "liquid";
    public const string Flammable = "flammable";

    public static readonly IReadOnlyList<string> All = [Standard, Fragile, Liquid, Flammable];
}

/// <summary>The ten product types (product-domain.md §4.1) and the rules that follow from each.</summary>
public static class ProductTypes
{
    public const string Perfume = "perfume";
    public const string Attar = "attar";
    public const string HairHomeMist = "hair_home_mist";
    public const string DiscoverySet = "discovery_set";
    public const string Honey = "honey";
    public const string CombHoney = "comb_honey";
    public const string NutsInHoney = "nuts_in_honey";
    public const string HoneyBlend = "honey_blend";
    public const string GiftBox = "gift_box";
    public const string Bundle = "bundle";

    public static readonly IReadOnlyList<string> All =
        [Perfume, Attar, HairHomeMist, DiscoverySet, Honey, CombHoney, NutsInHoney, HoneyBlend, GiftBox, Bundle];

    public static bool IsValid(string type) => All.Contains(type);

    public static string SectionOf(string type) => type switch
    {
        Perfume or Attar or HairHomeMist or DiscoverySet => Sections.Perfume,
        Honey or CombHoney or NutsInHoney or HoneyBlend => Sections.Honey,
        GiftBox or Bundle => Sections.Shared,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown product type."),
    };

    /// <summary>Food: batch with a best-before date is mandatory, FEFO applies, no withdrawal return (Law 3).</summary>
    public static bool IsFood(string type) => SectionOf(type) == Sections.Honey;

    public static bool IsCosmetic(string type) => SectionOf(type) == Sections.Perfume;

    public static string TaxCategoryOf(string type) => SectionOf(type) switch
    {
        Sections.Honey => TaxCategories.Food,
        Sections.Perfume => TaxCategories.Cosmetics,
        _ => TaxCategories.Packaging,
    };

    /// <summary>Samples exist for fragrances only; limited to three per order.</summary>
    public static bool AllowsSamples(string type) => type is Perfume or Attar;
}

public sealed record BundleComponent(Guid VariantId, int Qty);

public sealed record GiftBoxSlotInfo(string Code, IReadOnlyList<string> AllowedSections, bool Required);

/// <summary>Everything other modules need to know about a purchasable variant, in one language.</summary>
public sealed record VariantInfo
{
    public required Guid VariantId { get; init; }

    public required Guid ProductId { get; init; }

    public required string Slug { get; init; }

    public required string Sku { get; init; }

    public required string Section { get; init; }

    public required string ProductType { get; init; }

    public required string ProductName { get; init; }

    public required string VariantLabel { get; init; }

    /// <summary>Warning codes (honey.infant-under-12-months, ...).</summary>
    public required IReadOnlyList<string> WarningCodes { get; init; }

    /// <summary>The warnings rendered in the requested language; frozen into orders (Law 6).</summary>
    public required IReadOnlyList<string> WarningTexts { get; init; }

    public required IReadOnlyList<string> Allergens { get; init; }

    public required bool IsSample { get; init; }

    public required string ShippingClass { get; init; }

    public required int ShippingWeightG { get; init; }

    public required string TaxCategory { get; init; }

    /// <summary>Published product and active variant.</summary>
    public required bool IsSellable { get; init; }

    public required bool IsBundle { get; init; }

    public required bool IsGiftBox { get; init; }

    public IReadOnlyList<BundleComponent> BundleComponents { get; init; } = [];

    public IReadOnlyList<GiftBoxSlotInfo> GiftBoxSlots { get; init; } = [];

    public string? ImageUrl { get; init; }
}

public interface ICatalogReader
{
    Task<IReadOnlyDictionary<Guid, VariantInfo>> GetVariantsAsync(IReadOnlyCollection<Guid> variantIds, string locale, CancellationToken cancellationToken);
}

/// <param name="Available">Sellable units right now (bundles: complete sets). Callers decide how much of it to reveal.</param>
public sealed record VariantOffer(Guid VariantId, Guid ProductId, string ProductName, string Label, long? Price, string Currency, int Available, bool IsSample, bool IsGiftBox);

public sealed record ProductMatch(Guid ProductId, string Slug, string Name, string Section, string Type, string? ShortDescription, IReadOnlyList<VariantOffer> Variants);

/// <summary>Catalog search with live price and stock per variant, for callers outside the storefront (the chat assistant).</summary>
public interface ICatalogSearch
{
    Task<IReadOnlyList<ProductMatch>> SearchAsync(string? text, string? section, string locale, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, VariantOffer>> GetOffersAsync(IReadOnlyCollection<Guid> variantIds, string locale, CancellationToken cancellationToken);
}

public sealed record ProductPublished(Guid ProductId, string Slug, string Section) : DomainEvent;

public sealed record ProductChanged(Guid ProductId, string Slug, string Status) : DomainEvent;
