using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Catalog.Domain;
using Rahiq.Modules.Catalog.Infrastructure;
using Rahiq.SharedKernel;
using Rahiq.SharedKernel.Compliance;

namespace Rahiq.Modules.Catalog.Application;

public sealed record ClaimFindingDto(string Term, string Field, string Severity);

public sealed record ProductSaved(Guid Id, uint Version, IReadOnlyList<ClaimFindingDto> Findings, IReadOnlyList<string> ReadinessProblems);

public sealed record TranslationDto(string Locale, string Name, string? ShortDescription, string? Story, string? Usage, string? SeoTitle, string? SeoDescription);

public sealed record VariantInput(string Sku, int? VolumeMl, int? WeightG, string? Gtin, int ShippingWeightG, string ShippingClass, bool IsSample, bool IsActive, int SortOrder);

public sealed record AdminVariantDto(Guid Id, string Sku, int? VolumeMl, int? WeightG, string? Gtin, int ShippingWeightG, string ShippingClass, bool IsSample, bool IsActive, int SortOrder, string StockMode);

public sealed record AdminProductDto(
    Guid Id,
    string Section,
    string Type,
    string Slug,
    string Status,
    uint Version,
    JsonElement Attributes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Allergens,
    bool IsFeatured,
    int SortOrder,
    IReadOnlyList<TranslationDto> Translations,
    IReadOnlyList<AdminVariantDto> Variants,
    IReadOnlyList<MediaDto> Media,
    IReadOnlyList<BundleItemDto> BundleItems,
    IReadOnlyList<GiftSlotInput> GiftSlots,
    IReadOnlyList<ClaimFindingDto> Findings,
    IReadOnlyList<string> ReadinessProblems,
    DateTimeOffset UpdatedAt);

public sealed record AdminProductRow(Guid Id, string Slug, string Section, string Type, string Status, string Name, int Variants, bool IsFeatured, DateTimeOffset UpdatedAt);

public sealed record BundleItemDto(Guid ComponentVariantId, int Qty);

public sealed record GiftSlotInput(string Code, IReadOnlyList<string> AllowedSections, bool Required, int SortOrder);

public sealed record CreateProductCommand(string Type, string Slug) : ICommand<Result<Guid>>;

public sealed record UpdateProductCommand(
    Guid Id,
    uint ExpectedVersion,
    string Slug,
    IReadOnlyList<TranslationDto> Translations,
    JsonElement Attributes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Allergens,
    bool IsFeatured,
    int SortOrder) : ICommand<Result<ProductSaved>>;

public sealed record UpsertVariantCommand(Guid ProductId, Guid? VariantId, VariantInput Variant) : ICommand<Result<Guid>>;

/// <param name="ApproveClaims">A manager with claims.override publishes despite forbidden terms; the approval is logged.</param>
public sealed record PublishProductCommand(Guid Id, bool ApproveClaims, string? ApprovalReason) : ICommand<Result>;

public sealed record SetProductStatusCommand(Guid Id, string Status) : ICommand<Result>;

public sealed record SetBundleItemsCommand(Guid ProductId, Guid BundleVariantId, IReadOnlyList<BundleItemDto> Items) : ICommand<Result>;

public sealed record SetGiftSlotsCommand(Guid ProductId, IReadOnlyList<GiftSlotInput> Slots) : ICommand<Result>;

public sealed record AdminListProductsQuery(string? Section, string? Status, string? Q) : IQuery<IReadOnlyList<AdminProductRow>>;

public sealed record AdminGetProductQuery(Guid Id) : IQuery<Result<AdminProductDto>>;

public sealed record AdminVariantRow(Guid VariantId, Guid ProductId, string Sku, string ProductName, string Label, string Section, string Type, bool IsSample, bool IsActive);

/// <summary>Every variant, for pickers in the admin (batch receiving, bundles, recalls).</summary>
public sealed record AdminVariantsQuery : IQuery<IReadOnlyList<AdminVariantRow>>;

internal sealed class AdminVariantsHandler(RahiqDbContext db) : IRequestHandler<AdminVariantsQuery, IReadOnlyList<AdminVariantRow>>
{
    public async Task<IReadOnlyList<AdminVariantRow>> Handle(AdminVariantsQuery request, CancellationToken cancellationToken)
    {
        var products = await db.Set<Product>().AsNoTracking().Include(p => p.Translations).Include(p => p.Variants).Where(p => p.Status != ProductStatuses.Archived).ToListAsync(cancellationToken);
        return [.. products.SelectMany(p => p.Variants.Select(v => new AdminVariantRow(v.Id, p.Id, v.Sku, CatalogText.Name(p, "tr"), v.Label("tr"), p.Section, p.Type, v.IsSample, v.IsActive)))
            .OrderBy(v => v.Section).ThenBy(v => v.ProductName).ThenBy(v => v.Sku)];
    }
}

/// <summary>Runs the claims guard over arbitrary text (the editor's live preview).</summary>
public sealed record CheckClaimsQuery(IReadOnlyDictionary<string, string?> Fields) : IQuery<IReadOnlyList<ClaimFindingDto>>;

internal sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Type).Must(ProductTypes.IsValid).WithErrorCode("type_invalid");
        RuleFor(x => x.Slug).NotEmpty().MaximumLength(120);
    }
}

internal sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator()
    {
        RuleFor(x => x.Slug).NotEmpty().MaximumLength(120);
        RuleForEach(x => x.Translations).ChildRules(t =>
        {
            t.RuleFor(x => x.Locale).Must(Locales.IsSupported).WithErrorCode("locale_invalid");
            t.RuleFor(x => x.Name).NotEmpty().MaximumLength(160);
            t.RuleFor(x => x.ShortDescription).MaximumLength(300);
            t.RuleFor(x => x.Story).MaximumLength(6000);
            t.RuleFor(x => x.Usage).MaximumLength(2000);
            t.RuleFor(x => x.SeoTitle).MaximumLength(70);
            t.RuleFor(x => x.SeoDescription).MaximumLength(170);
        });
    }
}

internal sealed class UpsertVariantValidator : AbstractValidator<UpsertVariantCommand>
{
    public UpsertVariantValidator()
    {
        RuleFor(x => x.Variant.Sku).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Variant.ShippingWeightG).InclusiveBetween(1, 30_000);
        RuleFor(x => x.Variant.Gtin).Matches("^[0-9]{8,14}$").When(x => !string.IsNullOrWhiteSpace(x.Variant.Gtin)).WithErrorCode("gtin_invalid");
    }
}

internal sealed class CatalogAdminHandlers(RahiqDbContext db, ClaimsGuard guard, ICurrentActor actor, IAuditLog audit, IClock clock, IBlobStorage storage)
    : IRequestHandler<CreateProductCommand, Result<Guid>>,
      IRequestHandler<UpdateProductCommand, Result<ProductSaved>>,
      IRequestHandler<UpsertVariantCommand, Result<Guid>>,
      IRequestHandler<PublishProductCommand, Result>,
      IRequestHandler<SetProductStatusCommand, Result>,
      IRequestHandler<SetBundleItemsCommand, Result>,
      IRequestHandler<SetGiftSlotsCommand, Result>,
      IRequestHandler<AdminListProductsQuery, IReadOnlyList<AdminProductRow>>,
      IRequestHandler<AdminGetProductQuery, Result<AdminProductDto>>,
      IRequestHandler<CheckClaimsQuery, IReadOnlyList<ClaimFindingDto>>
{
    private static readonly Error NotFound = Error.NotFound("product.not_found", "Product not found.");
    private static readonly Error SlugTaken = Error.Conflict("product.slug_taken", "Another product already uses this slug.");
    private static readonly Error SkuTaken = Error.Conflict("variant.sku_taken", "Another variant already uses this SKU.");
    private static readonly Error Stale = Error.Conflict("product.stale", "Someone else saved this product meanwhile. Reload and try again.");

    public async Task<Result<Guid>> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        var created = Product.Create(request.Type, request.Slug, clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (await SlugInUse(created.Value.Slug, null, cancellationToken))
        {
            return SlugTaken;
        }

        db.Add(created.Value);
        audit.Record("product.created", "product", created.Value.Id.ToString(), new { request.Type, request.Slug });
        return created.Value.Id;
    }

    public async Task<Result<ProductSaved>> Handle(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await Load(request.Id, cancellationToken);
        if (product is null)
        {
            return NotFound;
        }

        if (product.Version != request.ExpectedVersion)
        {
            return Stale;
        }

        var slug = product.ChangeSlug(request.Slug);
        if (slug.IsFailure)
        {
            return slug.Error;
        }

        if (product.PreviousSlug is not null)
        {
            if (await SlugInUse(product.Slug, product.Id, cancellationToken))
            {
                return SlugTaken;
            }

            await db.Set<SlugRedirect>().Where(r => r.OldSlug == product.Slug).ExecuteDeleteAsync(cancellationToken);
            db.Add(new SlugRedirect { OldSlug = product.PreviousSlug, ProductId = product.Id });
        }

        var updated = product.UpdateContent(
            [.. request.Translations.Select(t => new TranslationInput(t.Locale, t.Name, t.ShortDescription, t.Story, t.Usage, t.SeoTitle, t.SeoDescription))],
            request.Attributes, request.Warnings, request.Allergens, request.IsFeatured, request.SortOrder, clock.UtcNow);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        var findings = guard.Check(product.TextFields());
        if (product.Status == ProductStatuses.Active && findings.Any(f => f.Severity == FindingSeverity.Blocking))
        {
            // A live product edited to include a forbidden claim goes back to draft until reviewed (Law 1).
            product.Unpublish(clock.UtcNow);
            audit.Record("product.unpublished_by_claims", "product", product.Id.ToString(), findings);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new ProductSaved(product.Id, product.Version, Map(findings), product.ReadinessProblems());
    }

    public async Task<Result<Guid>> Handle(UpsertVariantCommand request, CancellationToken cancellationToken)
    {
        var product = await Load(request.ProductId, cancellationToken);
        if (product is null)
        {
            return NotFound;
        }

        var v = request.Variant;
        var spec = new VariantSpec(v.Sku, v.VolumeMl, v.WeightG, v.Gtin, v.ShippingWeightG, v.ShippingClass, v.IsSample, v.IsActive, v.SortOrder);
        var sku = Sku.Create(v.Sku);
        if (sku.IsSuccess && await db.Set<Variant>().AnyAsync(x => x.Sku == sku.Value.Value && x.Id != request.VariantId, cancellationToken))
        {
            return SkuTaken;
        }

        if (request.VariantId is { } variantId)
        {
            var updated = product.UpdateVariant(variantId, spec);
            return updated.IsSuccess ? variantId : updated.Error;
        }

        var added = product.AddVariant(spec);
        if (added.IsFailure)
        {
            return added.Error;
        }

        db.Add(added.Value);
        return added.Value.Id;
    }

    public async Task<Result> Handle(PublishProductCommand request, CancellationToken cancellationToken)
    {
        var product = await Load(request.Id, cancellationToken);
        if (product is null)
        {
            return NotFound;
        }

        var findings = guard.Check(product.TextFields());
        var blocking = findings.Where(f => f.Severity == FindingSeverity.Blocking).ToList();
        var approved = false;

        if (blocking.Count > 0 && request.ApproveClaims)
        {
            if (!actor.HasPermission(Permissions.ClaimsOverride))
            {
                return Error.Forbidden("claims.override_forbidden", "Only a manager can approve text with forbidden terms.");
            }

            if (string.IsNullOrWhiteSpace(request.ApprovalReason) || request.ApprovalReason.Trim().Length < 10)
            {
                return Error.Validation("claims.reason_required", "Explain in a sentence why this text is acceptable.");
            }

            approved = true;
        }

        var published = product.Publish(findings, approved, clock.UtcNow);
        if (published.IsFailure)
        {
            return published;
        }

        if (approved)
        {
            db.Add(new ClaimApproval
            {
                Id = Ids.New(),
                Entity = "product",
                EntityId = product.Id,
                Terms = [.. blocking.Select(b => b.Term).Distinct()],
                Reason = request.ApprovalReason!.Trim(),
                ApprovedBy = actor.Id ?? Guid.Empty,
                ApprovedAt = clock.UtcNow,
            });
            audit.Record("claims.override", "product", product.Id.ToString(), new { terms = blocking.Select(b => b.Term), request.ApprovalReason });
        }

        audit.Record("product.published", "product", product.Id.ToString());
        return Result.Success();
    }

    public async Task<Result> Handle(SetProductStatusCommand request, CancellationToken cancellationToken)
    {
        var product = await Load(request.Id, cancellationToken);
        if (product is null)
        {
            return NotFound;
        }

        var result = request.Status switch
        {
            ProductStatuses.Draft => product.Unpublish(clock.UtcNow),
            ProductStatuses.Archived => product.Archive(clock.UtcNow),
            _ => Error.Validation("product.status_invalid", "Use publish to activate a product."),
        };

        if (result.IsSuccess)
        {
            audit.Record($"product.{request.Status}", "product", product.Id.ToString());
        }

        return result;
    }

    public async Task<Result> Handle(SetBundleItemsCommand request, CancellationToken cancellationToken)
    {
        var product = await Load(request.ProductId, cancellationToken);
        var variant = product?.Variants.FirstOrDefault(v => v.Id == request.BundleVariantId);
        if (product is null || variant is null || product.Type != ProductTypes.Bundle)
        {
            return NotFound;
        }

        var componentIds = request.Items.Select(i => i.ComponentVariantId).ToList();
        var components = await db.Set<Variant>().Where(v => componentIds.Contains(v.Id) && v.StockMode == "batches").CountAsync(cancellationToken);
        if (components != componentIds.Distinct().Count() || request.Items.Any(i => i.Qty <= 0) || request.Items.Count == 0)
        {
            return Error.Validation("bundle.components_invalid", "Bundle parts must be existing, stocked variants with a positive quantity.");
        }

        await db.Set<BundleItem>().Where(b => b.BundleVariantId == variant.Id).ExecuteDeleteAsync(cancellationToken);
        db.AddRange(request.Items.Select(i => new BundleItem { BundleVariantId = variant.Id, ComponentVariantId = i.ComponentVariantId, Qty = i.Qty }));
        return Result.Success();
    }

    public async Task<Result> Handle(SetGiftSlotsCommand request, CancellationToken cancellationToken)
    {
        var product = await Load(request.ProductId, cancellationToken);
        if (product is null || product.Type != ProductTypes.GiftBox)
        {
            return NotFound;
        }

        if (request.Slots.Any(s => s.AllowedSections.Count == 0 || s.AllowedSections.Any(a => a is not (Sections.Perfume or Sections.Honey))))
        {
            return Error.Validation("gift_box.slots_invalid", "Each slot allows the perfume and/or honey section.");
        }

        await db.Set<GiftBoxSlot>().Where(s => s.ProductId == product.Id).ExecuteDeleteAsync(cancellationToken);
        db.AddRange(request.Slots.Select(s => new GiftBoxSlot { ProductId = product.Id, Code = s.Code, AllowedSections = [.. s.AllowedSections], Required = s.Required, SortOrder = s.SortOrder }));
        return Result.Success();
    }

    public async Task<IReadOnlyList<AdminProductRow>> Handle(AdminListProductsQuery request, CancellationToken cancellationToken)
    {
        var query = db.Set<Product>().AsNoTracking().Include(p => p.Translations).Include(p => p.Variants).AsQueryable();
        if (request.Section is not null)
        {
            query = query.Where(p => p.Section == request.Section);
        }

        if (request.Status is not null)
        {
            query = query.Where(p => p.Status == request.Status);
        }

        var rows = (await query.OrderBy(p => p.Section).ThenBy(p => p.SortOrder).ToListAsync(cancellationToken))
            .Select(p => new AdminProductRow(p.Id, p.Slug, p.Section, p.Type, p.Status, CatalogText.Name(p, "tr"), p.Variants.Count, p.IsFeatured, p.UpdatedAt));
        var q = SharedKernel.Text.TextFolding.Fold(request.Q);
        return [.. rows.Where(r => q.Length == 0 || SharedKernel.Text.TextFolding.Fold(r.Name + " " + r.Slug).Contains(q, StringComparison.Ordinal))];
    }

    public async Task<Result<AdminProductDto>> Handle(AdminGetProductQuery request, CancellationToken cancellationToken)
    {
        var p = await db.Set<Product>().AsNoTracking().Include(x => x.Translations).Include(x => x.Variants).Include(x => x.Media)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);
        if (p is null)
        {
            return NotFound;
        }

        var variantIds = p.Variants.Select(v => v.Id).ToList();
        var bundleItems = await db.Set<BundleItem>().AsNoTracking().Where(b => variantIds.Contains(b.BundleVariantId)).ToListAsync(cancellationToken);
        var slots = await db.Set<GiftBoxSlot>().AsNoTracking().Where(s => s.ProductId == p.Id).OrderBy(s => s.SortOrder).ToListAsync(cancellationToken);

        return new AdminProductDto(
            p.Id, p.Section, p.Type, p.Slug, p.Status, p.Version, p.AttributesJson.Clone(), p.Warnings, p.Allergens, p.IsFeatured, p.SortOrder,
            [.. p.Translations.Select(t => new TranslationDto(t.Locale, t.Name, t.ShortDescription, t.Story, t.Usage, t.SeoTitle, t.SeoDescription))],
            [.. p.Variants.OrderBy(v => v.SortOrder).Select(v => new AdminVariantDto(v.Id, v.Sku, v.VolumeMl, v.WeightG, v.Gtin, v.ShippingWeightG, v.ShippingClass, v.IsSample, v.IsActive, v.SortOrder, v.StockMode))],
            [.. p.Media.OrderBy(m => m.SortOrder).Select(m => new MediaDto(m.Id, storage.GetUrl(m.StorageKey).ToString(), m.Width, m.Height, m.Role, m.Alt.GetValueOrDefault("tr")))],
            [.. bundleItems.Select(b => new BundleItemDto(b.ComponentVariantId, b.Qty))],
            [.. slots.Select(s => new GiftSlotInput(s.Code, s.AllowedSections, s.Required, s.SortOrder))],
            Map(guard.Check(p.TextFields())),
            p.ReadinessProblems(),
            p.UpdatedAt);
    }

    public Task<IReadOnlyList<ClaimFindingDto>> Handle(CheckClaimsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(Map(guard.Check(request.Fields)));

    private static IReadOnlyList<ClaimFindingDto> Map(IReadOnlyList<ClaimFinding> findings) =>
        [.. findings.Select(f => new ClaimFindingDto(f.Term, f.Field, f.Severity == FindingSeverity.Blocking ? "blocking" : "advisory"))];

    private Task<Product?> Load(Guid id, CancellationToken cancellationToken) =>
        db.Set<Product>().Include(p => p.Translations).Include(p => p.Variants).Include(p => p.Media).AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    private Task<bool> SlugInUse(string slug, Guid? except, CancellationToken cancellationToken) =>
        db.Set<Product>().AnyAsync(p => p.Slug == slug && p.Id != except, cancellationToken);
}
