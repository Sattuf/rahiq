using System.Text.RegularExpressions;

namespace Rahiq.SharedKernel;

/// <summary>Stock keeping unit. Stored upper-case with invariant culture (never tr-TR: "i" ≠ "İ").</summary>
public readonly partial record struct Sku
{
    private Sku(string value) => Value = value;

    public string Value { get; }

    public static Result<Sku> Create(string? raw)
    {
        var normalized = raw?.Trim().ToUpperInvariant() ?? string.Empty;
        return Pattern().IsMatch(normalized)
            ? new Sku(normalized)
            : Error.Validation("sku.invalid", "SKU must be 3-40 characters of A-Z, 0-9 and dashes.");
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Z0-9](?:[A-Z0-9-]{1,38})[A-Z0-9]$")]
    private static partial Regex Pattern();
}

/// <summary>Net weight in grams (honey, nuts, comb).</summary>
public readonly record struct Weight
{
    private Weight(int grams) => Grams = grams;

    public int Grams { get; }

    public static Result<Weight> FromGrams(int grams) => grams is > 0 and <= 100_000
        ? new Weight(grams)
        : Error.Validation("weight.invalid", "Weight must be between 1 g and 100 kg.");

    public override string ToString() => Grams >= 1000 && Grams % 1000 == 0 ? $"{Grams / 1000} kg" : $"{Grams} g";
}

/// <summary>Volume in millilitres (perfume, attar, mists).</summary>
public readonly record struct Volume
{
    private Volume(int millilitres) => Millilitres = millilitres;

    public int Millilitres { get; }

    public static Result<Volume> FromMillilitres(int ml) => ml is > 0 and <= 5_000
        ? new Volume(ml)
        : Error.Validation("volume.invalid", "Volume must be between 1 ml and 5 l.");

    public override string ToString() => $"{Millilitres} ml";
}

public static class Locales
{
    public const string Turkish = "tr";
    public const string Arabic = "ar";
    public const string English = "en";

    public const string Default = Turkish;

    public static readonly IReadOnlyList<string> All = [Turkish, Arabic, English];

    public static bool IsSupported(string? locale) => locale is not null && All.Contains(locale);

    public static string OrDefault(string? locale) => IsSupported(locale) ? locale! : Default;
}

/// <summary>Text in the three store languages, e.g. {"ar": "...", "tr": "...", "en": "..."}.</summary>
public sealed class LocalizedText : Dictionary<string, string>
{
    public LocalizedText()
        : base(StringComparer.Ordinal)
    {
    }

    public LocalizedText(IDictionary<string, string> values)
        : base(values, StringComparer.Ordinal)
    {
    }

    public string For(string locale) =>
        TryGetValue(locale, out var value) && !string.IsNullOrWhiteSpace(value) ? value
        : TryGetValue(Locales.Default, out var fallback) ? fallback
        : Values.FirstOrDefault() ?? string.Empty;
}
