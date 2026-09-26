using System.Security.Cryptography;
using System.Text;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Cart.Domain;

internal static class CartErrors
{
    public static readonly Error QtyInvalid = Error.Validation("cart.qty_invalid", "Quantity must be at least 1.");
    public static Error MaxPerVariant(int max) => Error.Validation("cart.max_per_variant", $"At most {max} of one item per order.");
    public static Error MaxSamples(int max) => Error.Validation("cart.max_samples", $"At most {max} samples per order.");
    public static readonly Error LineNotFound = Error.NotFound("cart.line_not_found", "This item is no longer in your cart.");
    public static readonly Error NotSellable = Error.Validation("cart.not_sellable", "This item is not available.");
    public static readonly Error GiftBoxIncomplete = Error.Validation("cart.gift_box_incomplete", "Choose an item for every slot of the gift box.");
    public static readonly Error GiftMessageTooLong = Error.Validation("cart.gift_message_too_long", "A gift message is at most 300 characters.");
    public static readonly Error HandoffInvalid = Error.NotFound("cart.handoff_invalid", "This link has expired or was already used. Ask us for a new one.");
    public static readonly Error HandoffEmpty = Error.Validation("cart.handoff_empty", "None of these items can be added to a cart right now.");
}

internal sealed record CartLimits(int MaxPerVariant, int MaxSamples);

/// <summary>
/// A cart holds intentions, never stock (ADR-007): prices are recomputed on every view, stock is reserved at pay.
/// Guest carts are found by a random token kept in an httpOnly cookie; only its hash is stored.
/// </summary>
internal sealed class ShoppingCart : AggregateRoot<Guid>
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly List<CartLine> _lines = [];

    private ShoppingCart()
    {
    }

    public string TokenHash { get; private set; } = string.Empty;

    public Guid? CustomerId { get; private set; }

    public string? CouponCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public IReadOnlyList<CartLine> Lines => _lines;

    /// <summary>A cart prepared in a chat (ADR-019): the one-time code that lets the customer claim it in their browser.</summary>
    public string? HandoffCodeHash { get; private set; }

    public DateTimeOffset? HandoffExpiresAt { get; private set; }

    public static (ShoppingCart Cart, string Token) Create(Guid? customerId, DateTimeOffset now)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var cart = new ShoppingCart
        {
            Id = Ids.New(),
            TokenHash = HashToken(token),
            CustomerId = customerId,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        return (cart, token);
    }

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Opens a one-time claim link. The code travels in a chat, so it is short-lived and single-use.</summary>
    /// <returns>The raw code; only its hash is stored.</returns>
    public string StartHandoff(DateTimeOffset now, TimeSpan validFor)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        HandoffCodeHash = HashToken(code);
        HandoffExpiresAt = now + validFor;
        Touch(now);
        return code;
    }

    public bool CanClaimHandoff(DateTimeOffset now) => HandoffCodeHash is not null && HandoffExpiresAt > now && CustomerId is null;

    /// <summary>
    /// The browser takes the cart over: the code is spent and the cart gets a fresh token, so neither the chat link
    /// nor anyone who saw it can reach the cart (and later the checkout with its address) again.
    /// </summary>
    /// <returns>The new cart token for the httpOnly cookie.</returns>
    public string ClaimHandoff(DateTimeOffset now)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        TokenHash = HashToken(token);
        HandoffCodeHash = null;
        HandoffExpiresAt = null;
        Touch(now);
        return token;
    }

    /// <param name="isSample">Whether the variant is a sample (for the per-order sample limit).</param>
    /// <param name="sampleVariants">Which existing lines are samples.</param>
    public Result<CartLine> Add(
        Guid variantId,
        int qty,
        IReadOnlyList<Guid>? components,
        string? giftMessage,
        long currentUnitPrice,
        bool isSample,
        IReadOnlySet<Guid> sampleVariants,
        CartLimits limits,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(sampleVariants);
        if (qty <= 0)
        {
            return CartErrors.QtyInvalid;
        }

        if (giftMessage?.Length > 300)
        {
            return CartErrors.GiftMessageTooLong;
        }

        var existing = components is null or { Count: 0 }
            ? _lines.FirstOrDefault(l => l.VariantId == variantId && l.Components is null or { Count: 0 })
            : null;
        var newQty = (existing?.Qty ?? 0) + qty;

        var check = CheckLimits(variantId, newQty, existing, isSample, sampleVariants, limits);
        if (check.IsFailure)
        {
            return check.Error;
        }

        Touch(now);
        if (existing is not null)
        {
            existing.SetQty(newQty);
            return existing;
        }

        var line = CartLine.Create(Id, variantId, qty, components, giftMessage, currentUnitPrice, now);
        _lines.Add(line);
        return line;
    }

    public Result ChangeQty(Guid lineId, int qty, IReadOnlySet<Guid> sampleVariants, CartLimits limits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sampleVariants);
        var line = _lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return CartErrors.LineNotFound;
        }

        if (qty <= 0)
        {
            return CartErrors.QtyInvalid;
        }

        var check = CheckLimits(line.VariantId, qty, line, sampleVariants.Contains(line.VariantId), sampleVariants, limits);
        if (check.IsFailure)
        {
            return check;
        }

        line.SetQty(qty);
        Touch(now);
        return Result.Success();
    }

    public Result Remove(Guid lineId, DateTimeOffset now)
    {
        var removed = _lines.RemoveAll(l => l.Id == lineId);
        Touch(now);
        return removed == 0 ? CartErrors.LineNotFound : Result.Success();
    }

    public void SetCoupon(string? code, DateTimeOffset now)
    {
        CouponCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        Touch(now);
    }

    public void Clear(DateTimeOffset now)
    {
        _lines.Clear();
        CouponCode = null;
        Touch(now);
    }

    public void AssignCustomer(Guid customerId) => CustomerId = customerId;

    /// <summary>Guest lines move into this (customer) cart; quantities add up but stay within the limits.</summary>
    public void Absorb(ShoppingCart guest, IReadOnlySet<Guid> sampleVariants, CartLimits limits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(guest);
        ArgumentNullException.ThrowIfNull(limits);
        foreach (var line in guest.Lines)
        {
            var existing = line.Components is null or { Count: 0 }
                ? _lines.FirstOrDefault(l => l.VariantId == line.VariantId && l.Components is null or { Count: 0 })
                : null;

            if (existing is not null)
            {
                existing.SetQty(Math.Min(limits.MaxPerVariant, existing.Qty + line.Qty));
                continue;
            }

            if (sampleVariants.Contains(line.VariantId) && _lines.Where(l => sampleVariants.Contains(l.VariantId)).Sum(l => l.Qty) + line.Qty > limits.MaxSamples)
            {
                continue;
            }

            _lines.Add(CartLine.Create(Id, line.VariantId, Math.Min(line.Qty, limits.MaxPerVariant), line.Components, line.GiftMessage, line.AddedUnitPrice, now));
        }

        CouponCode ??= guest.CouponCode;
        Touch(now);
    }

    private Result CheckLimits(Guid variantId, int newQty, CartLine? current, bool isSample, IReadOnlySet<Guid> sampleVariants, CartLimits limits)
    {
        var sameVariant = _lines.Where(l => l.VariantId == variantId && l != current).Sum(l => l.Qty) + newQty;
        if (sameVariant > limits.MaxPerVariant)
        {
            return CartErrors.MaxPerVariant(limits.MaxPerVariant);
        }

        if (isSample)
        {
            var samples = _lines.Where(l => sampleVariants.Contains(l.VariantId) && l != current).Sum(l => l.Qty) + newQty;
            if (samples > limits.MaxSamples)
            {
                return CartErrors.MaxSamples(limits.MaxSamples);
            }
        }

        return Result.Success();
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        ExpiresAt = now + Lifetime;
    }
}

internal sealed class CartLine : Entity<Guid>
{
    private CartLine()
    {
    }

    public Guid CartId { get; private set; }

    public Guid VariantId { get; private set; }

    public int Qty { get; private set; }

    public List<Guid>? Components { get; private set; }

    public string? GiftMessage { get; private set; }

    /// <summary>The price when added, to tell the customer clearly if it changed since (commerce-flows.md §1).</summary>
    public long AddedUnitPrice { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    public static CartLine Create(Guid cartId, Guid variantId, int qty, IReadOnlyList<Guid>? components, string? giftMessage, long unitPrice, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        CartId = cartId,
        VariantId = variantId,
        Qty = qty,
        Components = components is { Count: > 0 } ? [.. components] : null,
        GiftMessage = string.IsNullOrWhiteSpace(giftMessage) ? null : giftMessage.Trim(),
        AddedUnitPrice = unitPrice,
        AddedAt = now,
    };

    public void SetQty(int qty) => Qty = qty;
}
