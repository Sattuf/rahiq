using Rahiq.SharedKernel;

namespace Rahiq.Modules.Cart.Contracts;

public sealed record CartItem(Guid LineId, Guid VariantId, int Qty, IReadOnlyList<Guid>? Components, string? GiftMessage);

public sealed record CartSnapshot(Guid CartId, Guid? CustomerId, string? CouponCode, IReadOnlyList<CartItem> Items);

public sealed record ReorderItem(Guid VariantId, int Qty, IReadOnlyList<Guid>? Components);

public sealed record HandoffItem(Guid VariantId, int Qty);

/// <param name="Code">One-time code for the claim link; shown once, only its hash is stored.</param>
/// <param name="Skipped">Variants that could not be added (not sellable, gift boxes that need choices, over limits).</param>
public sealed record CartHandoff(Guid CartId, string Code, DateTimeOffset ExpiresAt, int Lines, IReadOnlyList<Guid> Skipped);

public interface ICartAccess
{
    Task<CartSnapshot?> GetAsync(Guid cartId, CancellationToken cancellationToken);

    /// <summary>The cart of a request (customer's when signed in, else the guest token's). Null when there is none.</summary>
    Task<Guid?> ResolveAsync(string? token, Guid? customerId, CancellationToken cancellationToken);

    Task ClearAsync(Guid cartId, CancellationToken cancellationToken);

    /// <summary>After sign-in: moves the guest cart's lines into the customer's cart (quantities add up within limits).</summary>
    Task MergeGuestCartAsync(string? guestToken, Guid customerId, CancellationToken cancellationToken);

    /// <summary>"Reorder my usual honey": adds past items to the customer's cart, within the usual limits.</summary>
    Task<int> AddItemsAsync(Guid customerId, IReadOnlyList<ReorderItem> items, CancellationToken cancellationToken);

    /// <summary>
    /// A cart prepared outside the browser (the chat assistant, ADR-019). Nothing is reserved or ordered: the customer
    /// claims the cart with the code, then goes through the normal checkout, contracts and payment.
    /// </summary>
    Task<Result<CartHandoff>> CreateHandoffAsync(IReadOnlyList<HandoffItem> items, TimeSpan validFor, CancellationToken cancellationToken);
}
