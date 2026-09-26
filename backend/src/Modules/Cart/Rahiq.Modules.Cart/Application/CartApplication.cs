using FluentValidation;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Modules.Cart.Domain;
using Rahiq.Modules.Cart.Infrastructure;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Inventory.Contracts;
using Rahiq.Modules.Pricing.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Cart.Application;

public sealed record CartComponentDto(Guid VariantId, string Name, string Label);

public sealed record PriceChangeDto(long Was, long Now);

public sealed record CartItemDto(
    Guid LineId,
    Guid VariantId,
    Guid ProductId,
    string Slug,
    string Section,
    string Name,
    string Label,
    int Qty,
    long UnitPrice,
    long Total,
    string? Image,
    bool IsSample,
    string? GiftMessage,
    IReadOnlyList<CartComponentDto> Components,
    PriceChangeDto? PriceChanged,
    string Availability,
    int MaxQty);

public sealed record CartView(
    string? NewToken,
    IReadOnlyList<CartItemDto> Items,
    int ItemCount,
    string Currency,
    long Subtotal,
    long Discount,
    long Total,
    string? CouponCode,
    string? CouponError,
    long? FreeShippingThreshold,
    long? RemainingForFreeShipping,
    IReadOnlyList<QuoteProblem> Problems);

public sealed record CartIdentity(string? Token, Guid? CustomerId, string Locale);

public sealed record GetCartQuery(CartIdentity Who) : IQuery<CartView>;

public sealed record AddToCartCommand(CartIdentity Who, Guid VariantId, int Qty, IReadOnlyList<Guid>? Components, string? GiftMessage) : ICommand<Result<CartView>>;

public sealed record UpdateCartLineCommand(CartIdentity Who, Guid LineId, int Qty) : ICommand<Result<CartView>>;

public sealed record RemoveCartLineCommand(CartIdentity Who, Guid LineId) : ICommand<Result<CartView>>;

/// <summary>Only sets the code; whether it applies is shown by the recomputed cart. Usage is counted at pay, not here.</summary>
public sealed record ApplyCouponCommand(CartIdentity Who, string? Code) : ICommand<Result<CartView>>;

/// <summary>Takes over a cart prepared in a chat (ADR-019). Signed-in customers get the items merged into their own cart.</summary>
public sealed record ClaimHandoffCommand(CartIdentity Who, string Code) : ICommand<Result<CartView>>;

internal sealed class AddToCartValidator : AbstractValidator<AddToCartCommand>
{
    public AddToCartValidator()
    {
        RuleFor(x => x.Qty).InclusiveBetween(1, 99);
        RuleFor(x => x.GiftMessage).MaximumLength(300);
        RuleFor(x => x.Components).Must(c => c is null || c.Count <= 6).WithErrorCode("too_many_components");
    }
}

internal sealed class CartHandlers(
    CartStore store,
    ICatalogReader catalog,
    IPriceReader prices,
    IQuoteService quotes,
    IInventoryService inventory,
    IClock clock,
    IOptions<StoreOptions> options)
    : IRequestHandler<GetCartQuery, CartView>,
      IRequestHandler<AddToCartCommand, Result<CartView>>,
      IRequestHandler<UpdateCartLineCommand, Result<CartView>>,
      IRequestHandler<RemoveCartLineCommand, Result<CartView>>,
      IRequestHandler<ApplyCouponCommand, Result<CartView>>,
      IRequestHandler<ClaimHandoffCommand, Result<CartView>>
{
    private CartLimits Limits => new(options.Value.MaxQuantityPerVariant, options.Value.MaxSamplesPerOrder);

    public async Task<CartView> Handle(GetCartQuery request, CancellationToken cancellationToken)
    {
        var cart = await store.FindAsync(request.Who.Token, request.Who.CustomerId, cancellationToken);
        return await View(cart, request.Who, null, cancellationToken);
    }

    public async Task<Result<CartView>> Handle(AddToCartCommand request, CancellationToken cancellationToken)
    {
        var info = (await catalog.GetVariantsAsync([request.VariantId], request.Who.Locale, cancellationToken)).GetValueOrDefault(request.VariantId);
        var price = (await prices.GetCurrentAsync([request.VariantId], cancellationToken)).GetValueOrDefault(request.VariantId);
        if (info is null || !info.IsSellable || price is null)
        {
            return CartErrors.NotSellable;
        }

        if (info.IsGiftBox && (request.Components?.Count ?? 0) < info.GiftBoxSlots.Count(s => s.Required))
        {
            return CartErrors.GiftBoxIncomplete;
        }

        var (cart, newToken) = await store.FindOrCreateAsync(request.Who.Token, request.Who.CustomerId, cancellationToken);
        var samples = await Samples(cart, request.VariantId, cancellationToken);
        var added = cart.Add(request.VariantId, request.Qty, info.IsGiftBox ? request.Components : null, request.GiftMessage, price.Price.Amount, info.IsSample, samples, Limits, clock.UtcNow);
        return added.IsFailure ? added.Error : await View(cart, request.Who, newToken, cancellationToken);
    }

    public async Task<Result<CartView>> Handle(UpdateCartLineCommand request, CancellationToken cancellationToken)
    {
        var cart = await store.FindAsync(request.Who.Token, request.Who.CustomerId, cancellationToken);
        if (cart is null)
        {
            return CartErrors.LineNotFound;
        }

        var changed = cart.ChangeQty(request.LineId, request.Qty, await Samples(cart, null, cancellationToken), Limits, clock.UtcNow);
        return changed.IsFailure ? changed.Error : await View(cart, request.Who, null, cancellationToken);
    }

    public async Task<Result<CartView>> Handle(RemoveCartLineCommand request, CancellationToken cancellationToken)
    {
        var cart = await store.FindAsync(request.Who.Token, request.Who.CustomerId, cancellationToken);
        if (cart is null)
        {
            return CartErrors.LineNotFound;
        }

        var removed = cart.Remove(request.LineId, clock.UtcNow);
        return removed.IsFailure ? removed.Error : await View(cart, request.Who, null, cancellationToken);
    }

    public async Task<Result<CartView>> Handle(ApplyCouponCommand request, CancellationToken cancellationToken)
    {
        var (cart, newToken) = await store.FindOrCreateAsync(request.Who.Token, request.Who.CustomerId, cancellationToken);
        cart.SetCoupon(request.Code, clock.UtcNow);
        return await View(cart, request.Who, newToken, cancellationToken);
    }

    public async Task<Result<CartView>> Handle(ClaimHandoffCommand request, CancellationToken cancellationToken)
    {
        var handoff = string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 64 ? null : await store.FindHandoffForUpdateAsync(request.Code, cancellationToken);
        if (handoff is null || !handoff.CanClaimHandoff(clock.UtcNow))
        {
            return CartErrors.HandoffInvalid;
        }

        if (request.Who.CustomerId is { } customerId)
        {
            var (own, _) = await store.FindOrCreateAsync(null, customerId, cancellationToken);
            own.Absorb(handoff, await Samples(handoff, null, cancellationToken), Limits, clock.UtcNow);
            store.Remove(handoff);
            return await View(own, request.Who, null, cancellationToken);
        }

        // The browser's previous guest cart (if any) is simply left to expire: the customer asked for this one.
        var token = handoff.ClaimHandoff(clock.UtcNow);
        return await View(handoff, request.Who, token, cancellationToken);
    }

    private async Task<HashSet<Guid>> Samples(ShoppingCart cart, Guid? extra, CancellationToken cancellationToken)
    {
        var ids = cart.Lines.Select(l => l.VariantId).Concat(extra is null ? [] : [extra.Value]).Distinct().ToList();
        return (await catalog.GetVariantsAsync(ids, Locales.Default, cancellationToken)).Values.Where(v => v.IsSample).Select(v => v.VariantId).ToHashSet();
    }

    /// <summary>Every view recomputes prices and availability on the server (commerce-flows.md §1).</summary>
    private async Task<CartView> View(ShoppingCart? cart, CartIdentity who, string? newToken, CancellationToken cancellationToken)
    {
        var currency = options.Value.Currency;
        if (cart is null || cart.Lines.Count == 0)
        {
            return new CartView(newToken, [], 0, currency, 0, 0, 0, cart?.CouponCode, null, null, null, []);
        }

        var lines = cart.Lines.OrderBy(l => l.AddedAt).ToList();
        var quote = await quotes.QuoteAsync(new QuoteRequest
        {
            Items = [.. lines.Select(l => new QuoteItem(l.VariantId, l.Qty, l.Components, l.GiftMessage))],
            Locale = who.Locale,
            CouponCode = cart.CouponCode,
            CustomerKey = who.CustomerId?.ToString(),
        }, cancellationToken);

        var ids = lines.Select(l => l.VariantId).Concat(lines.SelectMany(l => l.Components ?? [])).Distinct().ToList();
        var variants = await catalog.GetVariantsAsync(ids, who.Locale, cancellationToken);
        var priceTable = await prices.GetCurrentAsync(ids, cancellationToken);
        var stockIds = ids.Concat(variants.Values.SelectMany(v => v.BundleComponents.Select(c => c.VariantId))).Distinct().ToList();
        var stock = await inventory.GetAvailableAsync(stockIds, cancellationToken);

        var items = lines.Select((line, index) =>
        {
            var info = variants.GetValueOrDefault(line.VariantId);
            var leaves = quote.Lines.Where(q => q.ItemIndex == index).ToList();
            var available = Available(info, line, stock);
            var current = priceTable.GetValueOrDefault(line.VariantId)?.Price.Amount;
            var problem = quote.Problems.Any(p => p.VariantId == line.VariantId);
            return new CartItemDto(
                line.Id,
                line.VariantId,
                info?.ProductId ?? Guid.Empty,
                info?.Slug ?? string.Empty,
                info?.Section ?? string.Empty,
                info?.ProductName ?? string.Empty,
                info?.VariantLabel ?? string.Empty,
                line.Qty,
                line.Qty == 0 ? 0 : leaves.Sum(l => l.LineTotal) / line.Qty,
                leaves.Sum(l => l.LineTotal),
                info?.ImageUrl,
                info?.IsSample ?? false,
                line.GiftMessage,
                [.. (line.Components ?? []).Select(c => variants.GetValueOrDefault(c)).Where(c => c is not null).Select(c => new CartComponentDto(c!.VariantId, c.ProductName, c.VariantLabel))],
                current is not null && current != line.AddedUnitPrice ? new PriceChangeDto(line.AddedUnitPrice, current.Value) : null,
                info is null || !info.IsSellable || problem && leaves.Count == 0 ? "unavailable" : available <= 0 ? "out" : available < line.Qty ? "short" : available <= 3 ? "low" : "ok",
                Math.Min(options.Value.MaxQuantityPerVariant, Math.Max(available, 0)));
        }).ToList();

        var merchandise = quote.Subtotal - quote.Discount;
        return new CartView(
            newToken,
            items,
            items.Sum(i => i.Qty),
            quote.Currency,
            quote.Subtotal,
            quote.Discount,
            merchandise,
            cart.CouponCode,
            quote.CouponError,
            quote.FreeShippingThreshold,
            quote.FreeShippingThreshold is { } threshold ? Math.Max(0, threshold - merchandise) : null,
            quote.Problems);
    }

    private static int Available(VariantInfo? info, CartLine line, IReadOnlyDictionary<Guid, int> stock)
    {
        if (info is null)
        {
            return 0;
        }

        if (info.IsBundle)
        {
            return info.BundleComponents.Count == 0 ? 0 : info.BundleComponents.Min(c => stock.GetValueOrDefault(c.VariantId) / c.Qty);
        }

        var own = stock.GetValueOrDefault(line.VariantId);
        return line.Components is { Count: > 0 } components ? Math.Min(own, components.Min(c => stock.GetValueOrDefault(c))) : own;
    }
}
