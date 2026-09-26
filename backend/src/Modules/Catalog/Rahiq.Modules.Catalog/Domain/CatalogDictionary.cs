using System.Text.Json;
using Rahiq.SharedKernel;
using Rahiq.SharedKernel.Compliance;

namespace Rahiq.Modules.Catalog.Domain;

internal sealed record DictionaryEntry(string Code, LocalizedText Name, string? Family = null);

internal sealed record WarningDefinition(string Code, IReadOnlyList<string> AppliesTo, LocalizedText Text);

/// <summary>The closed vocabularies products are described with (no free-text attributes, product-domain.md §4.1).</summary>
internal sealed class CatalogDictionary
{
    private CatalogDictionary()
    {
    }

    public static CatalogDictionary Instance { get; } = Load(Taxonomy.Root);

    public IReadOnlyList<DictionaryEntry> Concentrations { get; private init; } = [];

    public IReadOnlyList<DictionaryEntry> Families { get; private init; } = [];

    public IReadOnlyList<DictionaryEntry> Genders { get; private init; } = [];

    public IReadOnlyList<DictionaryEntry> Seasons { get; private init; } = [];

    public IReadOnlyList<DictionaryEntry> Notes { get; private init; } = [];

    public IReadOnlyList<DictionaryEntry> FloralSources { get; private init; } = [];

    public IReadOnlyList<DictionaryEntry> Textures { get; private init; } = [];

    public IReadOnlyList<DictionaryEntry> Allergens { get; private init; } = [];

    public IReadOnlyList<DictionaryEntry> ProductTypes { get; private init; } = [];

    public IReadOnlyList<DictionaryEntry> Sections { get; private init; } = [];

    public IReadOnlyList<WarningDefinition> Warnings { get; private init; } = [];

    public LocalizedText CrystallizationNote { get; private init; } = [];

    public static bool Has(IReadOnlyList<DictionaryEntry> entries, string? code) => code is not null && entries.Any(e => e.Code == code);

    public static CatalogDictionary Load(JsonElement root)
    {
        static LocalizedText Text(JsonElement e) => new(e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty));

        static List<DictionaryEntry> Entries(JsonElement array, string idProperty = "code") =>
            [.. array.EnumerateArray().Select(e => new DictionaryEntry(
                e.GetProperty(idProperty).GetString()!,
                Text(e.GetProperty("name")),
                e.TryGetProperty("family", out var f) ? f.GetString() : null))];

        var perfume = root.GetProperty("perfume");
        var honey = root.GetProperty("honey");
        return new CatalogDictionary
        {
            Sections = Entries(root.GetProperty("sections")),
            ProductTypes = Entries(root.GetProperty("productTypes")),
            Concentrations = Entries(perfume.GetProperty("concentrations")),
            Families = Entries(perfume.GetProperty("families")),
            Genders = Entries(perfume.GetProperty("genders")),
            Seasons = Entries(perfume.GetProperty("seasons")),
            Notes = Entries(perfume.GetProperty("notes"), "id"),
            FloralSources = Entries(honey.GetProperty("floralSources")),
            Textures = Entries(honey.GetProperty("textures")),
            CrystallizationNote = Text(honey.GetProperty("crystallizationNote")),
            Allergens = Entries(root.GetProperty("allergens")),
            Warnings = [.. root.GetProperty("warnings").EnumerateArray().Select(w => new WarningDefinition(
                w.GetProperty("code").GetString()!,
                [.. w.GetProperty("appliesTo").EnumerateArray().Select(a => a.GetString()!)],
                Text(w.GetProperty("text"))))],
        };
    }
}

/// <summary>
/// Law 2: warnings are data. The mandatory ones for a type are added automatically; an editor can add more but
/// cannot remove these. They are rendered in a fixed place by code, never typed into a description.
/// </summary>
internal static class WarningRules
{
    public const string AllergenContains = "allergen.contains";

    public static IReadOnlyList<string> Mandatory(string productType, IReadOnlyCollection<string> allergens) =>
        [.. CatalogDictionary.Instance.Warnings
            .Where(w => w.AppliesTo.Contains(productType))
            .Where(w => w.Code != AllergenContains || allergens.Count > 0)
            .Select(w => w.Code)];

    public static IReadOnlyList<string> Normalize(string productType, IEnumerable<string> chosen, IReadOnlyCollection<string> allergens)
    {
        var known = CatalogDictionary.Instance.Warnings.Select(w => w.Code).ToHashSet();
        return [.. Mandatory(productType, allergens).Concat(chosen.Where(known.Contains)).Distinct()];
    }

    public static IReadOnlyList<string> Render(IEnumerable<string> codes, IReadOnlyCollection<string> allergens, string locale)
    {
        var dictionary = CatalogDictionary.Instance;
        var allergenNames = string.Join(", ", allergens.Select(a => dictionary.Allergens.FirstOrDefault(x => x.Code == a)?.Name.For(locale) ?? a));
        return [.. codes
            .Select(code => dictionary.Warnings.FirstOrDefault(w => w.Code == code))
            .Where(w => w is not null)
            .Select(w => w!.Text.For(locale).Replace("{allergens}", allergenNames, StringComparison.Ordinal))];
    }
}
