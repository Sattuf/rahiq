using System.Text.Json;

namespace Rahiq.SharedKernel.Compliance;

/// <summary>The store dictionaries from <c>rahiq-plan/seed/taxonomy.json</c>, embedded at build time.</summary>
public static class Taxonomy
{
    private static readonly Lazy<JsonDocument> Document = new(() =>
    {
        using var stream = typeof(Taxonomy).Assembly.GetManifestResourceStream("Rahiq.taxonomy.json")
            ?? throw new InvalidOperationException("taxonomy.json is not embedded.");
        return JsonDocument.Parse(stream);
    });

    public static JsonElement Root => Document.Value.RootElement;

    /// <summary>All forbidden health-claim terms in every language (Law 1).</summary>
    public static IReadOnlyList<string> ForbiddenClaimTerms() =>
        Root.GetProperty("forbiddenClaimTerms").EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Array)
            .SelectMany(p => p.Value.EnumerateArray().Select(t => t.GetString()!))
            .ToList();

    public static ClaimsGuard CreateClaimsGuard() => new(ForbiddenClaimTerms());
}
