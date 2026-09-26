using System.Text.Json;
using System.Text.RegularExpressions;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.SharedKernel;
using Rahiq.SharedKernel.Compliance;

namespace Rahiq.Modules.Catalog.Domain;

public static class ProductStatuses
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Archived = "archived";
}

internal static class ProductErrors
{
    public static readonly Error TypeInvalid = Error.Validation("product.type_invalid", "Unknown product type.");
    public static readonly Error SlugInvalid = Error.Validation("product.slug_invalid", "Slugs are lowercase latin letters, digits and dashes.");
    public static readonly Error VariantNotFound = Error.NotFound("variant.not_found", "Variant not found.");
    public static readonly Error SampleNotAllowed = Error.Validation("variant.sample_not_allowed", "Only fragrances have samples.");
    public static readonly Error SizeRequired = Error.Validation("variant.size_required", "Fragrances need a volume, honey products a weight.");
    public static readonly Error FlammableRequired = Error.Validation("variant.flammable_required", "Alcohol-based fragrances ship as 'flammable'.");
    public static readonly Error ShippingClassInvalid = Error.Validation("variant.shipping_class_invalid", "Unknown shipping class.");
    public static readonly Error Archived = Error.Conflict("product.archived", "Archived products cannot be changed.");

    public static Error NotReady(IEnumerable<string> problems) =>
        Error.Validation("product.not_ready", "The product is missing what publishing needs.") with
        {
            Details = new Dictionary<string, string[]> { ["publish"] = [.. problems] },
        };

    public static Error ClaimsBlocked(IEnumerable<ClaimFinding> findings) =>
        Error.Conflict("product.forbidden_claims", "The text contains forbidden health claims. A manager must rewrite it or approve it explicitly.") with
        {
            Details = findings.GroupBy(f => f.Field).ToDictionary(g => g.Key, g => g.Select(f => f.Term).ToArray()),
        };
}

internal sealed record TranslationInput(
    string Locale,
    string Name,
    string? ShortDescription,
    string? Story,
    string? Usage,
    string? SeoTitle,
    string? SeoDescription);

internal sealed record VariantSpec(
    string Sku,
    int? VolumeMl,
    int? WeightG,
    string? Gtin,
    int ShippingWeightG,
    string ShippingClass,
    bool IsSample,
    bool IsActive,
    int SortOrder);

internal sealed partial class Product : AggregateRoot<Guid>
{
    private readonly List<ProductTranslation> _translations = [];
    private readonly List<Variant> _variants = [];
    private readonly List<ProductMedia> _media = [];

    private Product()
    {
    }

    public string Section { get; private set; } = string.Empty;

    public string Type { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public string Status { get; private set; } = ProductStatuses.Draft;

    public string Attributes { get; private set; } = "{}";

    public List<string> Warnings { get; private set; } = [];

    public List<string> Allergens { get; private set; } = [];

    public bool IsFeatured { get; private set; }

    public int SortOrder { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>PostgreSQL xmin: optimistic concurrency for the admin (data-model.md §7).</summary>
    public uint Version { get; private set; }

    public IReadOnlyList<ProductTranslation> Translations => _translations;

    public IReadOnlyList<Variant> Variants => _variants;

    public IReadOnlyList<ProductMedia> Media => _media;

    public string? PreviousSlug { get; private set; }

    public static Result<Product> Create(string type, string slug, DateTimeOffset now)
    {
        if (!ProductTypes.IsValid(type))
        {
            return ProductErrors.TypeInvalid;
        }

        slug = slug.Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug))
        {
            return ProductErrors.SlugInvalid;
        }

        var product = new Product
        {
            Id = Ids.New(),
            Type = type,
            Section = ProductTypes.SectionOf(type),
            Slug = slug,
            CreatedAt = now,
            UpdatedAt = now,
        };
        product.Warnings = [.. WarningRules.Mandatory(type, [])];
        return product;
    }

    public JsonElement AttributesJson => JsonDocument.Parse(Attributes).RootElement;

    public Result UpdateContent(
        IReadOnlyList<TranslationInput> translations,
        JsonElement attributes,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> allergens,
        bool isFeatured,
        int sortOrder,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(translations);
        if (Status == ProductStatuses.Archived)
        {
            return ProductErrors.Archived;
        }

        var attributeErrors = AttributeRules.Validate(Type, attributes);
        if (attributeErrors.Count > 0)
        {
            return Error.Validation("product.attributes_invalid", "Some attributes are invalid.") with { Details = attributeErrors };
        }

        var knownAllergens = CatalogDictionary.Instance.Allergens.Select(a => a.Code).ToHashSet();
        var cleanAllergens = allergens.Where(knownAllergens.Contains).Distinct().ToList();
        if (cleanAllergens.Count != allergens.Distinct().Count())
        {
            return Error.Validation("product.allergen_unknown", "Allergens must come from the allergen list.");
        }

        foreach (var input in translations)
        {
            if (!Locales.IsSupported(input.Locale) || string.IsNullOrWhiteSpace(input.Name))
            {
                return Error.Validation("product.translation_invalid", "Each translation needs a supported locale and a name.");
            }

            var existing = _translations.FirstOrDefault(t => t.Locale == input.Locale);
            if (existing is null)
            {
                _translations.Add(ProductTranslation.Create(Id, input));
            }
            else
            {
                existing.Update(input);
            }
        }

        Attributes = attributes.GetRawText();
        Allergens = cleanAllergens;
        Warnings = [.. WarningRules.Normalize(Type, warnings, cleanAllergens)];
        IsFeatured = isFeatured;
        SortOrder = sortOrder;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result ChangeSlug(string slug)
    {
        slug = slug.Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug))
        {
            return ProductErrors.SlugInvalid;
        }

        if (slug != Slug)
        {
            PreviousSlug = Slug; // Becomes a 301 redirect (content-seo.md §3).
            Slug = slug;
        }

        return Result.Success();
    }

    public Result<Variant> AddVariant(VariantSpec spec)
    {
        var check = CheckVariant(spec);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var variant = Variant.Create(Id, spec, Type == ProductTypes.Bundle ? "components" : "batches");
        _variants.Add(variant);
        return variant;
    }

    public Result UpdateVariant(Guid variantId, VariantSpec spec)
    {
        var variant = _variants.FirstOrDefault(v => v.Id == variantId);
        if (variant is null)
        {
            return ProductErrors.VariantNotFound;
        }

        var check = CheckVariant(spec);
        if (check.IsFailure)
        {
            return check;
        }

        variant.Update(spec);
        return Result.Success();
    }

    public void AddMedia(ProductMedia media) => _media.Add(media);

    public void RemoveMedia(Guid mediaId) => _media.RemoveAll(m => m.Id == mediaId);

    public IReadOnlyList<string> ReadinessProblems()
    {
        var problems = new List<string>();
        foreach (var locale in Locales.All.Where(l => !_translations.Any(t => t.Locale == l && !string.IsNullOrWhiteSpace(t.Name))))
        {
            problems.Add($"name_missing_{locale}");
        }

        if (!_variants.Any(v => v.IsActive))
        {
            problems.Add("no_active_variant");
        }

        problems.AddRange(AttributeRules.ReadinessProblems(Type, AttributesJson, Allergens));
        return problems;
    }

    /// <summary>All text a claims check must read: translations and free text inside attributes.</summary>
    public IEnumerable<KeyValuePair<string, string?>> TextFields() =>
        _translations.SelectMany(t => new KeyValuePair<string, string?>[]
        {
            new($"name.{t.Locale}", t.Name),
            new($"shortDescription.{t.Locale}", t.ShortDescription),
            new($"story.{t.Locale}", t.Story),
            new($"usage.{t.Locale}", t.Usage),
            new($"seoTitle.{t.Locale}", t.SeoTitle),
            new($"seoDescription.{t.Locale}", t.SeoDescription),
        }).Concat(AttributeRules.TextFields(AttributesJson));

    /// <param name="findings">The claims-guard result for the current text.</param>
    /// <param name="approvedByManager">A manager with claims.override approved the listed blocking terms.</param>
    public Result Publish(IReadOnlyList<ClaimFinding> findings, bool approvedByManager, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (Status == ProductStatuses.Archived)
        {
            return ProductErrors.Archived;
        }

        var problems = ReadinessProblems();
        if (problems.Count > 0)
        {
            return ProductErrors.NotReady(problems);
        }

        var blocking = findings.Where(f => f.Severity == FindingSeverity.Blocking).ToList();
        if (blocking.Count > 0 && !approvedByManager)
        {
            return ProductErrors.ClaimsBlocked(blocking);
        }

        Status = ProductStatuses.Active;
        PublishedAt ??= now;
        UpdatedAt = now;
        Raise(new ProductPublished(Id, Slug, Section));
        return Result.Success();
    }

    public Result Unpublish(DateTimeOffset now) => SetStatus(ProductStatuses.Draft, now);

    public Result Archive(DateTimeOffset now) => SetStatus(ProductStatuses.Archived, now);

    private Result SetStatus(string status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
        Raise(new ProductChanged(Id, Slug, status));
        return Result.Success();
    }

    private Result CheckVariant(VariantSpec spec)
    {
        if (!ShippingClasses.All.Contains(spec.ShippingClass))
        {
            return ProductErrors.ShippingClassInvalid;
        }

        if (spec.IsSample && !ProductTypes.AllowsSamples(Type))
        {
            return ProductErrors.SampleNotAllowed;
        }

        var needsVolume = Section == Sections.Perfume && Type != ProductTypes.DiscoverySet;
        var needsWeight = Section == Sections.Honey;
        if ((needsVolume && spec.VolumeMl is null) || (needsWeight && spec.WeightG is null) ||
            (Type == ProductTypes.GiftBox && spec.WeightG is null && spec.VolumeMl is null))
        {
            return ProductErrors.SizeRequired;
        }

        if (Type is ProductTypes.Perfume or ProductTypes.HairHomeMist && spec.ShippingClass != ShippingClasses.Flammable)
        {
            return ProductErrors.FlammableRequired;
        }

        var sku = Sku.Create(spec.Sku);
        return sku.IsFailure ? sku.Error : Result.Success();
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}

internal sealed class ProductTranslation
{
    private ProductTranslation()
    {
    }

    public Guid ProductId { get; private set; }

    public string Locale { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? ShortDescription { get; private set; }

    public string? Story { get; private set; }

    public string? Usage { get; private set; }

    public string? SeoTitle { get; private set; }

    public string? SeoDescription { get; private set; }

    public static ProductTranslation Create(Guid productId, TranslationInput input)
    {
        var translation = new ProductTranslation { ProductId = productId, Locale = input.Locale };
        translation.Update(input);
        return translation;
    }

    public void Update(TranslationInput input)
    {
        Name = input.Name.Trim();
        ShortDescription = input.ShortDescription?.Trim();
        Story = input.Story?.Trim();
        Usage = input.Usage?.Trim();
        SeoTitle = input.SeoTitle?.Trim();
        SeoDescription = input.SeoDescription?.Trim();
    }
}

internal sealed class Variant : Entity<Guid>
{
    private Variant()
    {
    }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public int? VolumeMl { get; private set; }

    public int? WeightG { get; private set; }

    public string? Gtin { get; private set; }

    public int ShippingWeightG { get; private set; }

    public string ShippingClass { get; private set; } = ShippingClasses.Standard;

    public bool IsSample { get; private set; }

    public string StockMode { get; private set; } = "batches";

    public bool IsActive { get; private set; }

    public int SortOrder { get; private set; }

    public static Variant Create(Guid productId, VariantSpec spec, string stockMode)
    {
        var variant = new Variant { Id = Ids.New(), ProductId = productId, StockMode = stockMode };
        variant.Update(spec);
        return variant;
    }

    public void Update(VariantSpec spec)
    {
        Sku = SharedKernel.Sku.Create(spec.Sku).Value.Value;
        VolumeMl = spec.VolumeMl;
        WeightG = spec.WeightG;
        Gtin = string.IsNullOrWhiteSpace(spec.Gtin) ? null : spec.Gtin.Trim();
        ShippingWeightG = spec.ShippingWeightG;
        ShippingClass = spec.ShippingClass;
        IsSample = spec.IsSample;
        IsActive = spec.IsActive;
        SortOrder = spec.SortOrder;
    }

    /// <summary>"50 ml", "500 g", "1 kg", "2 ml · Numune".</summary>
    public string Label(string locale)
    {
        var size = VolumeMl is { } ml ? $"{ml} ml"
            : WeightG is { } g ? (g >= 1000 && g % 1000 == 0 ? $"{g / 1000} kg" : $"{g} g")
            : string.Empty;
        if (!IsSample)
        {
            return size;
        }

        var sample = locale switch { "ar" => "عيّنة", "en" => "Sample", _ => "Numune" };
        return size.Length == 0 ? sample : $"{size} · {sample}";
    }
}

internal sealed class ProductMedia : Entity<Guid>
{
    private ProductMedia()
    {
    }

    public Guid ProductId { get; private set; }

    public string Role { get; private set; } = "catalog";

    public string StorageKey { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Law 8: generated imagery is flagged, allowed for mood images only, never as the product photo.</summary>
    public bool IsGenerated { get; private set; }

    public Dictionary<string, string> Alt { get; private set; } = [];

    public int SortOrder { get; private set; }

    public static Result<ProductMedia> Create(Guid productId, string role, string storageKey, string contentType, int width, int height, bool isGenerated, Dictionary<string, string> alt, int sortOrder)
    {
        if (role is not ("catalog" or "detail" or "scale" or "packaging" or "mood"))
        {
            return Error.Validation("media.role_invalid", "Unknown media role.");
        }

        if (isGenerated && role != "mood")
        {
            return Error.Validation("media.generated_not_allowed", "Generated images are for mood only, never the product photo (Law 8).");
        }

        return new ProductMedia
        {
            Id = Ids.New(),
            ProductId = productId,
            Role = role,
            StorageKey = storageKey,
            ContentType = contentType,
            Width = width,
            Height = height,
            IsGenerated = isGenerated,
            Alt = alt,
            SortOrder = sortOrder,
        };
    }
}
