using System.Text.Json;

namespace Rahiq.Modules.Catalog.Application;

public sealed record MediaDto(Guid Id, string Url, int Width, int Height, string Role, string? Alt);

public sealed record HighlightsDto(
    string? Concentration,
    IReadOnlyList<string> Families,
    string? Gender,
    IReadOnlyList<string> Seasons,
    IReadOnlyList<string> TopNotes,
    string? FloralSource,
    string? Texture,
    string? RegionCode);

public sealed record ProductCardDto
{
    public required Guid Id { get; init; }

    public required string Slug { get; init; }

    public required string Section { get; init; }

    public required string Type { get; init; }

    public required string Name { get; init; }

    public string? ShortDescription { get; init; }

    public MediaDto? Image { get; init; }

    public long? Price { get; init; }

    public long? PreviousPrice { get; init; }

    public required string Currency { get; init; }

    /// <summary>"from" price when variants differ.</summary>
    public required bool PriceVaries { get; init; }

    public required bool Available { get; init; }

    public required bool HasSample { get; init; }

    public required bool IsFeatured { get; init; }

    public required IReadOnlyList<string> Sizes { get; init; }

    public required HighlightsDto Highlights { get; init; }
}

public sealed record VariantDto(
    Guid Id,
    string Sku,
    string Label,
    int? VolumeMl,
    int? WeightG,
    bool IsSample,
    long? Price,
    long? PreviousPrice,
    string Availability,
    string? Gtin);

public sealed record WarningDto(string Code, string Text);

public sealed record AllergenDto(string Code, string Name);

public sealed record BatchCardDto(
    Guid VariantId,
    string Code,
    string Token,
    DateOnly? ProducedAt,
    DateOnly? BestBefore,
    JsonElement? Origin,
    JsonElement? LabSummary,
    bool Analysed);

public sealed record GiftOptionDto(Guid VariantId, Guid ProductId, string Slug, string Name, string Label, string Section, long? Price, MediaDto? Image);

public sealed record GiftSlotDto(string Code, bool Required, IReadOnlyList<string> AllowedSections, IReadOnlyList<GiftOptionDto> Options);

public sealed record BundlePartDto(Guid VariantId, string Name, string Label, int Qty);

public sealed record ProductDetailDto
{
    public required ProductCardDto Card { get; init; }

    public string? Story { get; init; }

    public string? Usage { get; init; }

    public string? SeoTitle { get; init; }

    public string? SeoDescription { get; init; }

    public required JsonElement Attributes { get; init; }

    public required IReadOnlyList<WarningDto> Warnings { get; init; }

    public required IReadOnlyList<AllergenDto> Allergens { get; init; }

    public required IReadOnlyList<VariantDto> Variants { get; init; }

    public required IReadOnlyList<MediaDto> Media { get; init; }

    public required IReadOnlyList<BatchCardDto> Batches { get; init; }

    public IReadOnlyList<GiftSlotDto> GiftSlots { get; init; } = [];

    public IReadOnlyList<BundlePartDto> BundleParts { get; init; } = [];

    public string? CrystallizationNote { get; init; }

    public required IReadOnlyList<string> Locales { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Product not found under this slug, but it used to live here: redirect permanently.</summary>
public sealed record SlugMoved(string NewSlug);

public sealed record ProductLookup(ProductDetailDto? Product, SlugMoved? Moved);

public sealed record ProductFilter
{
    public string? Section { get; init; }

    public string? Type { get; init; }

    public string? Family { get; init; }

    public string? Gender { get; init; }

    public string? Concentration { get; init; }

    public string? Season { get; init; }

    public string? Note { get; init; }

    public string? FloralSource { get; init; }

    public string? Texture { get; init; }

    public string? RegionCode { get; init; }

    public int? WeightG { get; init; }

    public int? VolumeMl { get; init; }

    public long? MinPrice { get; init; }

    public long? MaxPrice { get; init; }

    public bool? Featured { get; init; }

    public string? Q { get; init; }

    /// <summary>featured (default), price_asc, price_desc, newest.</summary>
    public string? Sort { get; init; }

    public int Limit { get; init; } = 60;
}
