using Rahiq.Modules.Ordering.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Ordering.Domain;

public static class CheckoutStatuses
{
    public const string Open = "open";
    public const string Completed = "completed";
}

internal sealed record CheckoutDetails(
    string Email,
    string? Phone,
    AddressData ShippingAddress,
    AddressData? BillingAddress,
    bool IsGift,
    string? GiftMessage,
    bool HidePrices,
    string? ShippingMethod,
    bool MarketingConsent);

/// <summary>
/// The three-step checkout of one cart (commerce-flows.md §2): contact and address, shipping, payment.
/// Guest checkout is always allowed. A checkout produces at most one order.
/// </summary>
internal sealed class Checkout : Entity<Guid>
{
    private Checkout()
    {
    }

    public Guid CartId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public AddressData? ShippingAddress { get; private set; }

    public AddressData? BillingAddress { get; private set; }

    public bool IsGift { get; private set; }

    public string? GiftMessage { get; private set; }

    public bool HidePrices { get; private set; }

    public string? ShippingMethod { get; private set; }

    public string? CouponCode { get; private set; }

    public string Locale { get; private set; } = Locales.Default;

    public bool MarketingConsent { get; private set; }

    public string Status { get; private set; } = CheckoutStatuses.Open;

    public Guid? OrderId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsComplete => Email is not null && ShippingAddress is not null && Phone is not null;

    public static Checkout Start(Guid cartId, Guid? customerId, string? email, string? couponCode, string locale, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        CartId = cartId,
        CustomerId = customerId,
        Email = email,
        CouponCode = couponCode,
        Locale = Locales.OrDefault(locale),
        CreatedAt = now,
        UpdatedAt = now,
    };

    public Result Update(CheckoutDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (Status != CheckoutStatuses.Open)
        {
            return Error.Conflict("checkout.completed", "This checkout already became an order.");
        }

        if (details.GiftMessage?.Length > 300)
        {
            return Error.Validation("checkout.gift_message_too_long", "A gift message is at most 300 characters.");
        }

        Email = details.Email.Trim();
        Phone = details.Phone?.Trim();
        ShippingAddress = details.ShippingAddress;
        BillingAddress = details.BillingAddress;
        IsGift = details.IsGift;
        GiftMessage = details.IsGift ? details.GiftMessage?.Trim() : null;
        HidePrices = details.IsGift && details.HidePrices;
        ShippingMethod = details.ShippingMethod;
        MarketingConsent = details.MarketingConsent;
        UpdatedAt = now;
        return Result.Success();
    }

    public void SetCoupon(string? code) => CouponCode = code;

    public void SetLocale(string locale) => Locale = Locales.OrDefault(locale);

    public void Complete(Guid orderId, DateTimeOffset now)
    {
        Status = CheckoutStatuses.Completed;
        OrderId = orderId;
        UpdatedAt = now;
    }
}
