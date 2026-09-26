using System.Text.Json;
using Rahiq.Modules.Catalog.Contracts;

namespace Rahiq.Modules.Catalog.Domain;

/// <summary>
/// The attribute set of each product type (product-domain.md §4.3), validated in code rather than stored as free
/// key/values. <see cref="Validate"/> runs on every save; <see cref="ReadinessProblems"/> adds what publishing needs.
/// </summary>
internal static class AttributeRules
{
    /// <summary>Türkiye's seven geographic regions (for the honey sources map), plus imports such as Sidr.</summary>
    public static readonly IReadOnlyList<string> RegionCodes =
        ["marmara", "aegean", "mediterranean", "central_anatolia", "black_sea", "eastern_anatolia", "southeastern_anatolia", "imported"];

    public static IReadOnlyDictionary<string, string[]> Validate(string type, JsonElement attributes)
    {
        var errors = new Errors();
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            errors.Add("attributes", "must_be_object");
            return errors.ToDictionary();
        }

        var d = CatalogDictionary.Instance;
        switch (type)
        {
            case ProductTypes.Perfume or ProductTypes.Attar or ProductTypes.HairHomeMist:
                OptionalCode(attributes, "concentration", d.Concentrations, errors);
                OptionalCode(attributes, "gender", d.Genders, errors);
                CodeList(attributes, "families", d.Families, errors, max: 2);
                CodeList(attributes, "seasons", d.Seasons, errors);
                if (attributes.TryGetProperty("notes", out var notes))
                {
                    foreach (var tier in new[] { "top", "heart", "base" })
                    {
                        if (notes.ValueKind == JsonValueKind.Object && notes.TryGetProperty(tier, out _))
                        {
                            CodeList(notes, tier, d.Notes, errors, prefix: "notes.");
                        }
                    }
                }

                foreach (var scale in new[] { "intensity", "longevity", "sillage" })
                {
                    Scale(attributes, scale, errors);
                }

                StringList(attributes, "inci", errors);
                StringList(attributes, "declaredAllergens", errors);
                break;

            case ProductTypes.Honey or ProductTypes.CombHoney:
                OptionalCode(attributes, "floralSource", d.FloralSources, errors);
                OptionalCode(attributes, "texture", d.Textures, errors);
                Scale(attributes, "colorScale", errors);
                if (attributes.TryGetProperty("regionCode", out var region) && !RegionCodes.Contains(region.GetString() ?? string.Empty))
                {
                    errors.Add("regionCode", "unknown_code");
                }

                if (attributes.TryGetProperty("taste", out var taste))
                {
                    foreach (var scale in new[] { "sweetness", "bitterness", "intensity" })
                    {
                        Scale(taste, scale, errors, "taste.");
                    }
                }

                if (attributes.TryGetProperty("altitude", out var altitude) && (!altitude.TryGetInt32(out var metres) || metres is < 0 or > 5000))
                {
                    errors.Add("altitude", "out_of_range");
                }

                break;

            case ProductTypes.NutsInHoney or ProductTypes.HoneyBlend:
                OptionalCode(attributes, "honeyType", d.FloralSources, errors);
                Composition(attributes, errors, requireComplete: false);
                break;
        }

        return errors.ToDictionary();
    }

    public static IReadOnlyList<string> ReadinessProblems(string type, JsonElement attributes, IReadOnlyCollection<string> allergens)
    {
        var problems = new List<string>();
        switch (type)
        {
            case ProductTypes.Perfume or ProductTypes.Attar or ProductTypes.HairHomeMist:
                if (!attributes.TryGetProperty("inci", out var inci) || inci.ValueKind != JsonValueKind.Array || inci.GetArrayLength() == 0)
                {
                    problems.Add("inci_required"); // Legally required on the label and the page (compliance.md §3).
                }

                if (!attributes.TryGetProperty("concentration", out _))
                {
                    problems.Add("concentration_required");
                }

                break;

            case ProductTypes.Honey or ProductTypes.CombHoney:
                if (!attributes.TryGetProperty("floralSource", out _))
                {
                    problems.Add("floral_source_required");
                }

                break;

            case ProductTypes.NutsInHoney or ProductTypes.HoneyBlend:
                var errors = new Errors();
                Composition(attributes, errors, requireComplete: true);
                problems.AddRange(errors.ToDictionary().SelectMany(e => e.Value));
                if (type == ProductTypes.NutsInHoney && allergens.Count == 0)
                {
                    problems.Add("allergens_required"); // Nuts in honey always declare their allergens.
                }

                break;
        }

        return problems;
    }

    /// <summary>Free text inside attributes that the claims guard must also read.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> TextFields(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var key in new[] { "region", "suggestedUse" })
        {
            if (attributes.TryGetProperty(key, out var localized) && localized.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in localized.EnumerateObject())
                {
                    yield return new($"attributes.{key}.{p.Name}", p.Value.GetString());
                }
            }
        }

        if (attributes.TryGetProperty("composition", out var composition) && composition.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in composition.EnumerateArray())
            {
                if (item.TryGetProperty("ingredient", out var ingredient) && ingredient.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in ingredient.EnumerateObject())
                    {
                        yield return new($"attributes.composition[{i}].{p.Name}", p.Value.GetString());
                    }
                }

                i++;
            }
        }
    }

    private static void Composition(JsonElement attributes, Errors errors, bool requireComplete)
    {
        if (!attributes.TryGetProperty("composition", out var composition))
        {
            if (requireComplete)
            {
                errors.Add("composition", "composition_required");
            }

            return;
        }

        if (composition.ValueKind != JsonValueKind.Array)
        {
            errors.Add("composition", "must_be_array");
            return;
        }

        decimal sum = 0;
        foreach (var item in composition.EnumerateArray())
        {
            if (!item.TryGetProperty("percent", out var percent) || !percent.TryGetDecimal(out var value) || value <= 0 || value > 100)
            {
                errors.Add("composition", "percent_invalid");
                return;
            }

            if (!item.TryGetProperty("ingredient", out var ingredient) || ingredient.ValueKind != JsonValueKind.Object)
            {
                errors.Add("composition", "ingredient_required");
                return;
            }

            sum += value;
        }

        // The stacked composition bar and the label must add up to exactly 100% (frontend-experience.md §3.5).
        if (composition.GetArrayLength() > 0 && sum != 100m)
        {
            errors.Add("composition", "composition_must_total_100");
        }
    }

    private static void OptionalCode(JsonElement obj, string property, IReadOnlyList<DictionaryEntry> allowed, Errors errors)
    {
        if (obj.TryGetProperty(property, out var value) && !CatalogDictionary.Has(allowed, value.ValueKind == JsonValueKind.String ? value.GetString() : null))
        {
            errors.Add(property, "unknown_code");
        }
    }

    private static void CodeList(JsonElement obj, string property, IReadOnlyList<DictionaryEntry> allowed, Errors errors, int? max = null, string prefix = "")
    {
        if (!obj.TryGetProperty(property, out var list))
        {
            return;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            errors.Add(prefix + property, "must_be_array");
            return;
        }

        if (max is not null && list.GetArrayLength() > max)
        {
            errors.Add(prefix + property, "too_many");
        }

        if (list.EnumerateArray().Any(v => !CatalogDictionary.Has(allowed, v.ValueKind == JsonValueKind.String ? v.GetString() : null)))
        {
            errors.Add(prefix + property, "unknown_code");
        }
    }

    private static void StringList(JsonElement obj, string property, Errors errors)
    {
        if (obj.TryGetProperty(property, out var list) &&
            (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))))
        {
            errors.Add(property, "must_be_string_array");
        }
    }

    private static void Scale(JsonElement obj, string property, Errors errors, string prefix = "")
    {
        if (obj.TryGetProperty(property, out var value) && (!value.TryGetInt32(out var n) || n is < 1 or > 5))
        {
            errors.Add(prefix + property, "scale_1_to_5");
        }
    }

    private sealed class Errors
    {
        private readonly Dictionary<string, List<string>> _errors = [];

        public void Add(string field, string code)
        {
            if (!_errors.TryGetValue(field, out var list))
            {
                _errors[field] = list = [];
            }

            list.Add(code);
        }

        public Dictionary<string, string[]> ToDictionary() => _errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }
}
