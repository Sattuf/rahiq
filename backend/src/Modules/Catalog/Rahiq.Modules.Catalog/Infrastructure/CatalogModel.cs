using Microsoft.EntityFrameworkCore;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Domain;

namespace Rahiq.Modules.Catalog.Infrastructure;

internal sealed class BundleItem
{
    public Guid BundleVariantId { get; set; }

    public Guid ComponentVariantId { get; set; }

    public int Qty { get; set; }
}

internal sealed class GiftBoxSlot
{
    public Guid ProductId { get; set; }

    public required string Code { get; set; }

    public List<string> AllowedSections { get; set; } = [];

    public bool Required { get; set; } = true;

    public int SortOrder { get; set; }
}

internal sealed class SlugRedirect
{
    public required string OldSlug { get; set; }

    public Guid ProductId { get; set; }
}

internal sealed class ClaimApproval
{
    public Guid Id { get; set; }

    public required string Entity { get; set; }

    public Guid EntityId { get; set; }

    public List<string> Terms { get; set; } = [];

    public required string Reason { get; set; }

    public Guid ApprovedBy { get; set; }

    public DateTimeOffset ApprovedAt { get; set; }
}

internal sealed class NoteEntry
{
    public required string Id { get; set; }

    public string? Family { get; set; }

    public Guid? IconAssetId { get; set; }

    public Dictionary<string, string> Names { get; set; } = [];
}

internal sealed class CatalogModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products", "catalog");
            b.HasKey(p => p.Id);
            b.Property(p => p.Attributes).HasColumnType("jsonb");
            b.Property(p => p.Version).IsRowVersion();
            b.Ignore(p => p.DomainEvents);
            b.Ignore(p => p.AttributesJson);
            b.Ignore(p => p.PreviousSlug);
            b.HasMany(p => p.Translations).WithOne().HasForeignKey(t => t.ProductId);
            b.HasMany(p => p.Variants).WithOne().HasForeignKey(v => v.ProductId);
            b.HasMany(p => p.Media).WithOne().HasForeignKey(m => m.ProductId);
            b.Navigation(p => p.Translations).HasField("_translations");
            b.Navigation(p => p.Variants).HasField("_variants");
            b.Navigation(p => p.Media).HasField("_media");
        });

        modelBuilder.Entity<ProductTranslation>(b =>
        {
            b.ToTable("product_translations", "catalog");
            b.HasKey(t => new { t.ProductId, t.Locale });
        });

        modelBuilder.Entity<Variant>(b =>
        {
            b.ToTable("variants", "catalog");
            b.HasKey(v => v.Id);
        });

        modelBuilder.Entity<ProductMedia>(b =>
        {
            b.ToTable("media", "catalog");
            b.HasKey(m => m.Id);
            b.Property(m => m.Alt).HasColumnType("jsonb");
        });

        modelBuilder.Entity<BundleItem>(b =>
        {
            b.ToTable("bundle_items", "catalog");
            b.HasKey(x => new { x.BundleVariantId, x.ComponentVariantId });
        });

        modelBuilder.Entity<GiftBoxSlot>(b =>
        {
            b.ToTable("gift_box_slots", "catalog");
            b.HasKey(x => new { x.ProductId, x.Code });
        });

        modelBuilder.Entity<SlugRedirect>(b =>
        {
            b.ToTable("slug_redirects", "catalog");
            b.HasKey(x => x.OldSlug);
        });

        modelBuilder.Entity<ClaimApproval>(b =>
        {
            b.ToTable("claim_approvals", "catalog");
            b.HasKey(x => x.Id);
        });

        modelBuilder.Entity<NoteEntry>(b =>
        {
            b.ToTable("notes", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Names).HasColumnType("jsonb");
        });
    }
}
