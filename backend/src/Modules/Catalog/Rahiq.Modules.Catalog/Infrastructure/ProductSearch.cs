using System.Text.Json;
using Meilisearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Application;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Catalog.Domain;
using Rahiq.SharedKernel.Text;

namespace Rahiq.Modules.Catalog.Infrastructure;

public sealed record SearchOptions
{
    public const string Section = "Search";

    /// <summary>Empty = no Meilisearch; the catalog's own folded matching is used (fine for a small catalog).</summary>
    public string? MeilisearchUrl { get; init; }

    public string? MeilisearchKey { get; init; }

    public string IndexName { get; init; } = "products";
}

public sealed record SearchProductsQuery(string Q, string Locale) : IQuery<IReadOnlyList<ProductCardDto>>;

internal sealed class SearchDocument
{
    public required string Id { get; init; }

    public required string Slug { get; init; }

    public required string Section { get; init; }

    /// <summary>Everything searchable, folded (content-seo.md §5): names in three languages, notes, sources.</summary>
    public required string Text { get; init; }
}

/// <summary>PostgreSQL is the source of truth; this index can be rebuilt at any time (ADR-008).</summary>
internal sealed partial class ProductSearch(
    RahiqDbContext db,
    IOptions<SearchOptions> options,
    ISender sender,
    ILogger<ProductSearch> logger)
    : IRequestHandler<SearchProductsQuery, IReadOnlyList<ProductCardDto>>, IEventHandler<ProductPublished>, IEventHandler<ProductChanged>
{
    private MeilisearchClient? Client => string.IsNullOrWhiteSpace(options.Value.MeilisearchUrl)
        ? null
        : new MeilisearchClient(options.Value.MeilisearchUrl, options.Value.MeilisearchKey);

    public async Task<IReadOnlyList<ProductCardDto>> Handle(SearchProductsQuery request, CancellationToken cancellationToken)
    {
        var q = request.Q.Trim();
        if (q.Length == 0)
        {
            return [];
        }

        var client = Client;
        if (client is null)
        {
            return await sender.Send(new ListProductsQuery(new ProductFilter { Q = q, Limit = 24 }, request.Locale), cancellationToken);
        }

        try
        {
            var hits = await client.Index(options.Value.IndexName).SearchAsync<SearchDocument>(TextFolding.Fold(q), new SearchQuery { Limit = 24 }, cancellationToken);
            var slugs = hits.Hits.Select(h => h.Slug).ToList();
            var all = await sender.Send(new ListProductsQuery(new ProductFilter { Limit = 200 }, request.Locale), cancellationToken);
            return [.. slugs.Select(s => all.FirstOrDefault(c => c.Slug == s)).Where(c => c is not null).Select(c => c!)];
        }
        catch (Exception ex) when (ex is MeilisearchCommunicationError or MeilisearchApiError or HttpRequestException)
        {
            LogSearchDown(logger, ex);
            return await sender.Send(new ListProductsQuery(new ProductFilter { Q = q, Limit = 24 }, request.Locale), cancellationToken);
        }
    }

    public Task Handle(ProductPublished domainEvent, CancellationToken cancellationToken) => Reindex(domainEvent.ProductId, cancellationToken);

    public Task Handle(ProductChanged domainEvent, CancellationToken cancellationToken) => Reindex(domainEvent.ProductId, cancellationToken);

    /// <summary>Rebuilds the whole index with settings and synonyms (CLI: <c>reindex</c>).</summary>
    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var client = Client;
        if (client is null)
        {
            return;
        }

        var index = client.Index(options.Value.IndexName);
        await client.CreateIndexAsync(options.Value.IndexName, "id", cancellationToken);
        await index.UpdateSearchableAttributesAsync(["text"], cancellationToken);
        await index.UpdateFilterableAttributesAsync(["section"], cancellationToken);
        await index.UpdateSynonymsAsync(Synonyms(), cancellationToken);
        await index.DeleteAllDocumentsAsync(cancellationToken);

        var products = await Active().ToListAsync(cancellationToken);
        if (products.Count > 0)
        {
            await index.AddDocumentsAsync(products.Select(ToDocument), "id", cancellationToken);
        }
    }

    private async Task Reindex(Guid productId, CancellationToken cancellationToken)
    {
        var client = Client;
        if (client is null)
        {
            return;
        }

        var index = client.Index(options.Value.IndexName);
        var product = await Active().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
        {
            await index.DeleteOneDocumentAsync(productId.ToString("N"), cancellationToken);
        }
        else
        {
            await index.UpdateDocumentsAsync([ToDocument(product)], "id", cancellationToken);
        }
    }

    private IQueryable<Product> Active() =>
        db.Set<Product>().AsNoTracking().Include(p => p.Translations).Where(p => p.Status == ProductStatuses.Active);

    private static SearchDocument ToDocument(Product p)
    {
        var d = CatalogDictionary.Instance;
        var a = p.AttributesJson;
        var parts = new List<string?>();
        parts.AddRange(p.Translations.SelectMany(t => new[] { t.Name, t.ShortDescription }));
        parts.AddRange(d.ProductTypes.Where(t => t.Code == p.Type).SelectMany(t => t.Name.Values));

        if (a.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Object)
        {
            foreach (var tier in notes.EnumerateObject().Where(t => t.Value.ValueKind == JsonValueKind.Array))
            {
                parts.AddRange(tier.Value.EnumerateArray().SelectMany(n => NamesOf(d.Notes, n.GetString())));
            }
        }

        foreach (var key in new[] { "floralSource", "honeyType" })
        {
            if (a.TryGetProperty(key, out var source))
            {
                parts.AddRange(NamesOf(d.FloralSources, source.GetString()));
            }
        }

        foreach (var key in new[] { "families" })
        {
            if (a.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                parts.AddRange(list.EnumerateArray().SelectMany(f => NamesOf(d.Families, f.GetString())));
            }
        }

        return new SearchDocument { Id = p.Id.ToString("N"), Slug = p.Slug, Section = p.Section, Text = TextFolding.Fold(string.Join(' ', parts.Where(x => x is not null))) };
    }

    private static IEnumerable<string> NamesOf(IReadOnlyList<DictionaryEntry> entries, string? code) =>
        entries.FirstOrDefault(x => x.Code == code)?.Name.Values ?? Enumerable.Empty<string>();

    /// <summary>Each dictionary entry's three names are synonyms: "سدر" ↔ "Sidr", "عود" ↔ "Oud" ↔ "Ud", "كستناء" ↔ "Kestane".</summary>
    private static Dictionary<string, IEnumerable<string>> Synonyms()
    {
        var d = CatalogDictionary.Instance;
        var groups = d.Notes.Concat(d.FloralSources).Concat(d.Families)
            .Select(e => e.Name.Values.Select(TextFolding.Fold).Where(v => v.Length > 0).Distinct().ToList())
            .Append(["عود", "oud", "ud", "agarwood"])
            .Append(["سدر", "sidr", "ziziphus", "sedir"]);

        var map = new Dictionary<string, IEnumerable<string>>();
        foreach (var group in groups.Where(g => g.Count > 1))
        {
            foreach (var word in group)
            {
                map[word] = map.TryGetValue(word, out var existing) ? existing.Concat(group.Where(w => w != word)).Distinct().ToList() : group.Where(w => w != word).ToList();
            }
        }

        return map;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Meilisearch unavailable, using catalog fallback search")]
    private static partial void LogSearchDown(ILogger logger, Exception ex);
}
