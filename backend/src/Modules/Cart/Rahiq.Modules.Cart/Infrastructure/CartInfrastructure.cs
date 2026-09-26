using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Cart.Contracts;
using Rahiq.Modules.Cart.Domain;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Pricing.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Cart.Infrastructure;

internal sealed class CartModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShoppingCart>(b =>
        {
            b.ToTable("carts", "cart");
            b.HasKey(c => c.Id);
            b.Ignore(c => c.DomainEvents);
            b.HasMany(c => c.Lines).WithOne().HasForeignKey(l => l.CartId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(c => c.Lines).HasField("_lines");
        });

        modelBuilder.Entity<CartLine>(b =>
        {
            b.ToTable("cart_lines", "cart");
            b.HasKey(l => l.Id);
            b.Property(l => l.Components).HasColumnType("jsonb");
        });
    }
}

/// <summary>Finds the cart of a request: the customer's when signed in, else the guest cart of the cookie token.</summary>
internal sealed class CartStore(RahiqDbContext db, IClock clock)
{
    public async Task<ShoppingCart?> FindAsync(string? token, Guid? customerId, CancellationToken cancellationToken)
    {
        var carts = db.Set<ShoppingCart>().Include(c => c.Lines);
        if (customerId is { } id)
        {
            return await carts.FirstOrDefaultAsync(c => c.CustomerId == id, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = ShoppingCart.HashToken(token);
        var cart = await carts.FirstOrDefaultAsync(c => c.TokenHash == hash && c.CustomerId == null, cancellationToken);
        return cart is not null && cart.ExpiresAt > clock.UtcNow ? cart : null;
    }

    /// <summary>A chat-prepared cart by its one-time code, locked so two tabs cannot claim it at once.</summary>
    public async Task<ShoppingCart?> FindHandoffForUpdateAsync(string code, CancellationToken cancellationToken)
    {
        var hash = ShoppingCart.HashToken(code);
        return await db.Set<ShoppingCart>()
            .FromSql($"SELECT * FROM cart.carts WHERE handoff_code_hash = {hash} FOR UPDATE")
            .Include(c => c.Lines)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public void Remove(ShoppingCart cart) => db.Remove(cart);

    /// <returns>The cart and, when it was just created, its new token for the cookie.</returns>
    public async Task<(ShoppingCart Cart, string? NewToken)> FindOrCreateAsync(string? token, Guid? customerId, CancellationToken cancellationToken)
    {
        var existing = await FindAsync(token, customerId, cancellationToken);
        if (existing is not null)
        {
            return (existing, null);
        }

        var (cart, newToken) = ShoppingCart.Create(customerId, clock.UtcNow);
        db.Add(cart);
        return (cart, customerId is null ? newToken : null);
    }
}

internal sealed class CartAccess(RahiqDbContext db, CartStore store, ICatalogReader catalog, IPriceReader prices, IClock clock, IOptions<StoreOptions> options)
    : ICartAccess
{
    public async Task<CartSnapshot?> GetAsync(Guid cartId, CancellationToken cancellationToken)
    {
        var cart = await db.Set<ShoppingCart>().AsNoTracking().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == cartId, cancellationToken);
        return cart is null ? null : new CartSnapshot(cart.Id, cart.CustomerId, cart.CouponCode,
            [.. cart.Lines.OrderBy(l => l.AddedAt).Select(l => new CartItem(l.Id, l.VariantId, l.Qty, l.Components, l.GiftMessage))]);
    }

    public async Task<Guid?> ResolveAsync(string? token, Guid? customerId, CancellationToken cancellationToken) =>
        (await store.FindAsync(token, customerId, cancellationToken))?.Id;

    public async Task ClearAsync(Guid cartId, CancellationToken cancellationToken)
    {
        var cart = await db.Set<ShoppingCart>().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == cartId, cancellationToken);
        cart?.Clear(clock.UtcNow);
    }

    public async Task MergeGuestCartAsync(string? guestToken, Guid customerId, CancellationToken cancellationToken)
    {
        var guest = await store.FindAsync(guestToken, null, cancellationToken);
        if (guest is null || guest.Lines.Count == 0)
        {
            return;
        }

        var (customerCart, _) = await store.FindOrCreateAsync(null, customerId, cancellationToken);
        var samples = await SampleVariants(guest.Lines.Select(l => l.VariantId).Concat(customerCart.Lines.Select(l => l.VariantId)), cancellationToken);
        customerCart.Absorb(guest, samples, Limits, clock.UtcNow);
        db.Remove(guest);
    }

    public async Task<int> AddItemsAsync(Guid customerId, IReadOnlyList<ReorderItem> items, CancellationToken cancellationToken)
    {
        var (cart, _) = await store.FindOrCreateAsync(null, customerId, cancellationToken);
        var variantIds = items.Select(i => i.VariantId).ToList();
        var info = await catalog.GetVariantsAsync(variantIds, Locales.Default, cancellationToken);
        var priceTable = await prices.GetCurrentAsync(variantIds, cancellationToken);
        var samples = info.Values.Where(v => v.IsSample).Select(v => v.VariantId).ToHashSet();
        var added = 0;

        foreach (var item in items.Where(i => info.TryGetValue(i.VariantId, out var v) && v.IsSellable && priceTable.ContainsKey(i.VariantId)))
        {
            var result = cart.Add(item.VariantId, item.Qty, item.Components, null, priceTable[item.VariantId].Price.Amount, samples.Contains(item.VariantId), samples, Limits, clock.UtcNow);
            added += result.IsSuccess ? 1 : 0;
        }

        return added;
    }

    public async Task<Result<CartHandoff>> CreateHandoffAsync(IReadOnlyList<HandoffItem> items, TimeSpan validFor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var variantIds = items.Select(i => i.VariantId).Distinct().ToList();
        var info = await catalog.GetVariantsAsync(variantIds, Locales.Default, cancellationToken);
        var priceTable = await prices.GetCurrentAsync(variantIds, cancellationToken);
        var samples = info.Values.Where(v => v.IsSample).Select(v => v.VariantId).ToHashSet();
        var (cart, _) = ShoppingCart.Create(null, clock.UtcNow);
        var skipped = new List<Guid>();

        foreach (var item in items)
        {
            // Gift boxes need a choice per slot: that happens on the site, not in a chat.
            if (!info.TryGetValue(item.VariantId, out var v) || !v.IsSellable || v.IsGiftBox || !priceTable.TryGetValue(item.VariantId, out var price)
                || cart.Add(item.VariantId, item.Qty, null, null, price.Price.Amount, v.IsSample, samples, Limits, clock.UtcNow).IsFailure)
            {
                skipped.Add(item.VariantId);
            }
        }

        if (cart.Lines.Count == 0)
        {
            return CartErrors.HandoffEmpty;
        }

        var code = cart.StartHandoff(clock.UtcNow, validFor);
        db.Add(cart);
        return new CartHandoff(cart.Id, code, cart.HandoffExpiresAt!.Value, cart.Lines.Count, skipped);
    }

    private CartLimits Limits => new(options.Value.MaxQuantityPerVariant, options.Value.MaxSamplesPerOrder);

    private async Task<HashSet<Guid>> SampleVariants(IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        (await catalog.GetVariantsAsync([.. ids.Distinct()], Locales.Default, cancellationToken)).Values.Where(v => v.IsSample).Select(v => v.VariantId).ToHashSet();
}
