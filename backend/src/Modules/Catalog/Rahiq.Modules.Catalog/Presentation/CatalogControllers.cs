using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.Modules.Catalog.Application;
using Rahiq.Modules.Catalog.Infrastructure;

namespace Rahiq.Modules.Catalog.Presentation;

[Route("api/catalog")]
public sealed class CatalogController(ISender sender) : ApiControllerBase
{
    [HttpGet("taxonomy")]
    [OutputCache(PolicyName = "catalog")]
    public async Task<IActionResult> Taxonomy(CancellationToken ct) => Ok(await sender.Send(new GetTaxonomyQuery(Locale), ct));

    [HttpGet("products")]
    [OutputCache(PolicyName = "catalog")]
    public async Task<IActionResult> Products([FromQuery] ProductFilter filter, CancellationToken ct) =>
        Ok(await sender.Send(new ListProductsQuery(filter, Locale), ct));

    /// <summary>A moved slug answers 301 with the new slug, so old links and search results keep working.</summary>
    [HttpGet("products/{slug}")]
    [OutputCache(PolicyName = "catalog")]
    public async Task<IActionResult> Product(string slug, CancellationToken ct)
    {
        var lookup = await sender.Send(new GetProductQuery(slug, Locale), ct);
        if (lookup.Moved is not null)
        {
            Response.Headers.Location = $"/api/catalog/products/{lookup.Moved.NewSlug}?locale={Locale}";
            return StatusCode(StatusCodes.Status301MovedPermanently, lookup.Moved);
        }

        return lookup.Product is null ? NotFound() : Ok(lookup.Product);
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct) =>
        Ok(await sender.Send(new SearchProductsQuery(q ?? string.Empty, Locale), ct));
}

[Route("api/guide")]
public sealed class PerfumeGuideController(ISender sender) : ApiControllerBase
{
    [HttpGet("questions")]
    public async Task<IActionResult> Questions(CancellationToken ct) => Ok(await sender.Send(new GetGuideQuestionsQuery(Locale), ct));

    [HttpPost("recommend")]
    public async Task<IActionResult> Recommend([FromBody] Dictionary<string, string> answers, CancellationToken ct) =>
        Ok(await sender.Send(new RecommendPerfumesQuery(answers, Locale), ct));
}

public sealed record CreateProductRequest(string Type, string Slug);

public sealed record UpdateProductRequest(
    uint Version,
    string Slug,
    IReadOnlyList<TranslationDto> Translations,
    JsonElement Attributes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Allergens,
    bool IsFeatured,
    int SortOrder);

public sealed record PublishRequest(bool ApproveClaims, string? ApprovalReason);

public sealed record StatusRequest(string Status);

[Route("api/admin/products")]
[Authorize(Policy = Permissions.ProductsEdit)]
public sealed class AdminProductsController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? section, [FromQuery] string? status, [FromQuery] string? q, CancellationToken ct) =>
        Ok(await sender.Send(new AdminListProductsQuery(section, status, q), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await sender.Send(new AdminGetProductQuery(id), ct));

    [HttpPost]
    public async Task<IActionResult> Create(CreateProductRequest body, CancellationToken ct) =>
        Created(await sender.Send(new CreateProductCommand(body.Type, body.Slug), ct), id => $"/api/admin/products/{id}");

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateProductRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new UpdateProductCommand(id, body.Version, body.Slug, body.Translations, body.Attributes, body.Warnings, body.Allergens, body.IsFeatured, body.SortOrder), ct));

    [HttpPost("{id:guid}/variants")]
    public async Task<IActionResult> AddVariant(Guid id, VariantInput body, CancellationToken ct) =>
        FromResult(await sender.Send(new UpsertVariantCommand(id, null, body), ct));

    [HttpPut("{id:guid}/variants/{variantId:guid}")]
    public async Task<IActionResult> UpdateVariant(Guid id, Guid variantId, VariantInput body, CancellationToken ct) =>
        FromResult(await sender.Send(new UpsertVariantCommand(id, variantId, body), ct));

    [HttpPut("{id:guid}/variants/{variantId:guid}/bundle")]
    public async Task<IActionResult> SetBundle(Guid id, Guid variantId, IReadOnlyList<BundleItemDto> body, CancellationToken ct) =>
        FromResult(await sender.Send(new SetBundleItemsCommand(id, variantId, body), ct));

    [HttpPut("{id:guid}/gift-slots")]
    public async Task<IActionResult> SetGiftSlots(Guid id, IReadOnlyList<GiftSlotInput> body, CancellationToken ct) =>
        FromResult(await sender.Send(new SetGiftSlotsCommand(id, body), ct));

    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = Permissions.ProductsPublish)]
    public async Task<IActionResult> Publish(Guid id, PublishRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new PublishProductCommand(id, body.ApproveClaims, body.ApprovalReason), ct));

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = Permissions.ProductsPublish)]
    public async Task<IActionResult> SetStatus(Guid id, StatusRequest body, CancellationToken ct) =>
        FromResult(await sender.Send(new SetProductStatusCommand(id, body.Status), ct));

    [HttpPost("{id:guid}/media")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid id, [FromForm] IFormFile file, [FromForm] string role, [FromForm] bool isGenerated, [FromForm] string? altTr, [FromForm] string? altAr, [FromForm] string? altEn, [FromForm] int sortOrder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var alt = new Dictionary<string, string>();
        foreach (var (locale, text) in new[] { ("tr", altTr), ("ar", altAr), ("en", altEn) })
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                alt[locale] = text.Trim();
            }
        }

        return FromResult(await sender.Send(new UploadMediaCommand(id, role, buffer.ToArray(), isGenerated, alt, sortOrder), ct));
    }

    [HttpDelete("{id:guid}/media/{mediaId:guid}")]
    public async Task<IActionResult> DeleteMedia(Guid id, Guid mediaId, CancellationToken ct) =>
        FromResult(await sender.Send(new DeleteMediaCommand(id, mediaId), ct));

    [HttpPost("check-claims")]
    public async Task<IActionResult> CheckClaims(Dictionary<string, string?> fields, CancellationToken ct) =>
        Ok(await sender.Send(new CheckClaimsQuery(fields), ct));
}

[Route("api/admin/variants")]
[Authorize(Policy = Permissions.InventoryEdit)]
public sealed class AdminVariantsController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await sender.Send(new AdminVariantsQuery(), ct));
}
