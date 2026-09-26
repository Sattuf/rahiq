using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Cart.Contracts;
using Rahiq.Modules.Customers.Contracts;
using Rahiq.Modules.Documents.Contracts;
using Rahiq.Modules.Inventory.Contracts;
using Rahiq.Modules.Notifications.Contracts;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Ordering.Domain;
using Rahiq.Modules.Ordering.Infrastructure;
using Rahiq.Modules.Payments.Contracts;
using Rahiq.Modules.Pricing.Contracts;
using Rahiq.Modules.Shipping.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Ordering.Application;

public sealed record Requester(string? CartToken, Guid? CustomerId, string Locale);

public sealed record CheckoutDetailsInput(
    string Email,
    string Phone,
    AddressData ShippingAddress,
    AddressData? BillingAddress,
    bool IsGift,
    string? GiftMessage,
    bool HidePrices,
    string? ShippingMethod,
    bool MarketingConsent);

public sealed record ContractsDto(string PreInformationHtml, string DistanceSalesHtml, string Hash);

public sealed record CheckoutView(
    Guid Id,
    string? Email,
    string? Phone,
    AddressData? ShippingAddress,
    AddressData? BillingAddress,
    bool IsGift,
    string? GiftMessage,
    bool HidePrices,
    string? ShippingMethod,
    bool MarketingConsent,
    Quote Quote,
    bool CodAvailable,
    string? CodUnavailableReason,
    ContractsDto? Contracts,
    bool ReadyToPay);

public sealed record StartCheckoutCommand(Requester Who, string? Email) : ICommand<Result<Guid>>;

public sealed record UpdateCheckoutCommand(Guid CheckoutId, Requester Who, CheckoutDetailsInput Details) : ICommand<Result<CheckoutView>>;

public sealed record GetCheckoutQuery(Guid CheckoutId, Requester Who, string PaymentMethod) : IQuery<Result<CheckoutView>>;

public sealed record PlacedOrder(Guid OrderId, string Number, string PaymentMethod, long Total, string Currency, bool AlreadyPlaced, string Email, string Locale);

/// <param name="AcceptedContractsHash">The hash of the contracts the buyer saw and accepted; must match the server's re-render.</param>
public sealed record PlaceOrderCommand(Guid CheckoutId, Requester Who, string PaymentMethod, string AcceptedContractsHash, string? IdempotencyKey)
    : ICommand<Result<PlacedOrder>>;

internal sealed class UpdateCheckoutValidator : AbstractValidator<UpdateCheckoutCommand>
{
    public UpdateCheckoutValidator()
    {
        RuleFor(x => x.Details.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(x => x.Details.Phone).NotEmpty().Matches(@"^\+?[0-9 ()-]{10,20}$").WithErrorCode("phone_invalid");
        RuleFor(x => x.Details.ShippingAddress).NotNull().SetValidator(new AddressValidator());
        RuleFor(x => x.Details.BillingAddress!).SetValidator(new AddressValidator()).When(x => x.Details.BillingAddress is not null);
        RuleFor(x => x.Details.GiftMessage).MaximumLength(300);
    }
}

internal sealed class AddressValidator : AbstractValidator<AddressData>
{
    public AddressValidator()
    {
        RuleFor(a => a.FullName).NotEmpty().MaximumLength(120);
        RuleFor(a => a.Phone).NotEmpty().Matches(@"^\+?[0-9 ()-]{10,20}$").WithErrorCode("phone_invalid");
        RuleFor(a => a.ProvinceCode).InclusiveBetween(1, 81).WithErrorCode("province_invalid");
        RuleFor(a => a.District).NotEmpty().MaximumLength(80);
        RuleFor(a => a.Line1).NotEmpty().MaximumLength(200);
        RuleFor(a => a.Line2).MaximumLength(200);
        RuleFor(a => a.TaxNumber).Matches("^[0-9]{10,11}$").When(a => !string.IsNullOrWhiteSpace(a.TaxNumber)).WithErrorCode("tax_number_invalid");
    }
}

internal sealed class PlaceOrderValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderValidator()
    {
        RuleFor(x => x.PaymentMethod).Must(m => m is PaymentMethods.Card or PaymentMethods.CashOnDelivery).WithErrorCode("payment_method_invalid");
        RuleFor(x => x.AcceptedContractsHash).NotEmpty().WithErrorCode("contracts_not_accepted");
    }
}

internal static class CheckoutErrors
{
    public static readonly Error NotFound = Error.NotFound("checkout.not_found", "Checkout not found.");
    public static readonly Error EmptyCart = Error.Validation("checkout.cart_empty", "Your cart is empty.");
    public static readonly Error Incomplete = Error.Validation("checkout.incomplete", "Please fill in your contact details and address.");
    public static readonly Error ContractsChanged = Error.Conflict("checkout.contracts_changed",
        "Something in your order changed (price, address or items). Please review and accept the updated documents.");

    public static readonly Error ShippingUnavailable = Error.Validation("checkout.shipping_unavailable", "We cannot ship this order to that address.");
    public static readonly Error CodUnavailable = Error.Validation("checkout.cod_unavailable", "Cash on delivery is not available for this order.");

    public static Error Problems(IEnumerable<QuoteProblem> problems) =>
        Error.Conflict("checkout.items_unavailable", "Some items are no longer available as requested.") with
        {
            Details = problems.GroupBy(p => p.Code).ToDictionary(g => g.Key, g => g.Select(p => p.VariantId.ToString()).ToArray()),
        };
}

internal sealed class CheckoutHandlers(
    RahiqDbContext db,
    ICartAccess carts,
    IQuoteService quotes,
    IShippingRates shipping,
    IInventoryService inventory,
    ICouponUsage coupons,
    IContractDocuments documents,
    IPaymentGateway payments,
    ICustomerDirectory customers,
    OrderNumbers numbers,
    IEventPublisher events,
    IClock clock,
    IOptions<StoreOptions> store)
    : IRequestHandler<StartCheckoutCommand, Result<Guid>>,
      IRequestHandler<UpdateCheckoutCommand, Result<CheckoutView>>,
      IRequestHandler<GetCheckoutQuery, Result<CheckoutView>>,
      IRequestHandler<PlaceOrderCommand, Result<PlacedOrder>>
{
    private const long LargeFirstOrder = 500_000;

    public async Task<Result<Guid>> Handle(StartCheckoutCommand request, CancellationToken cancellationToken)
    {
        var cartId = await carts.ResolveAsync(request.Who.CartToken, request.Who.CustomerId, cancellationToken);
        var cart = cartId is null ? null : await carts.GetAsync(cartId.Value, cancellationToken);
        if (cart is null || cart.Items.Count == 0)
        {
            return CheckoutErrors.EmptyCart;
        }

        var open = await db.Set<Checkout>().Where(c => c.CartId == cart.CartId && c.Status == CheckoutStatuses.Open)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (open is not null)
        {
            open.SetCoupon(cart.CouponCode);
            open.SetLocale(request.Who.Locale);
            return open.Id;
        }

        var checkout = Checkout.Start(cart.CartId, request.Who.CustomerId, request.Email, cart.CouponCode, request.Who.Locale, clock.UtcNow);
        db.Add(checkout);
        return checkout.Id;
    }

    public async Task<Result<CheckoutView>> Handle(UpdateCheckoutCommand request, CancellationToken cancellationToken)
    {
        var checkout = await Load(request.CheckoutId, request.Who, cancellationToken);
        if (checkout is null)
        {
            return CheckoutErrors.NotFound;
        }

        var d = request.Details;
        var provinces = await shipping.ProvincesAsync(cancellationToken);
        AddressData Named(AddressData a) => a with { ProvinceName = provinces.FirstOrDefault(p => p.Code == a.ProvinceCode)?.Name ?? a.ProvinceName };
        var updated = checkout.Update(new CheckoutDetails(
            d.Email, d.Phone, Named(d.ShippingAddress), d.BillingAddress is null ? null : Named(d.BillingAddress),
            d.IsGift, d.GiftMessage, d.HidePrices, d.ShippingMethod, d.MarketingConsent), clock.UtcNow);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        return await View(checkout, PaymentMethods.Card, cancellationToken);
    }

    public async Task<Result<CheckoutView>> Handle(GetCheckoutQuery request, CancellationToken cancellationToken)
    {
        var checkout = await Load(request.CheckoutId, request.Who, cancellationToken);
        return checkout is null ? CheckoutErrors.NotFound : await View(checkout, request.PaymentMethod, cancellationToken);
    }

    public async Task<Result<PlacedOrder>> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        var checkout = await db.Set<Checkout>().FromSql($"SELECT * FROM ordering.checkouts WHERE id = {request.CheckoutId} FOR UPDATE").FirstOrDefaultAsync(cancellationToken);
        if (checkout is null || !await Owns(checkout, request.Who, cancellationToken))
        {
            return CheckoutErrors.NotFound;
        }

        // A second "pay" on the same checkout (double click with a new key, back button) returns the same order.
        if (checkout.Status == CheckoutStatuses.Completed && checkout.OrderId is { } existingId)
        {
            var existing = await db.Set<Order>().AsNoTracking().FirstAsync(o => o.Id == existingId, cancellationToken);
            return new PlacedOrder(existing.Id, existing.Number, existing.PaymentMethod, existing.Total, existing.Currency, true, existing.Email, existing.Locale);
        }

        if (!checkout.IsComplete)
        {
            return CheckoutErrors.Incomplete;
        }

        var cart = await carts.GetAsync(checkout.CartId, cancellationToken);
        if (cart is null || cart.Items.Count == 0)
        {
            return CheckoutErrors.EmptyCart;
        }

        var method = request.PaymentMethod;
        var quote = await Quote(checkout, cart, method, cancellationToken);
        if (quote.Problems.Count > 0)
        {
            return quote.Problems.Any(p => p.Code == "shipping_unavailable") ? CheckoutErrors.ShippingUnavailable : CheckoutErrors.Problems(quote.Problems);
        }

        if (method == PaymentMethods.CashOnDelivery && await CodBlockReason(checkout, quote, cancellationToken) is not null)
        {
            return CheckoutErrors.CodUnavailable;
        }

        var contracts = documents.Render(ContractFactory.Build(checkout, quote, method, clock.Today));
        if (!string.Equals(contracts.Hash, request.AcceptedContractsHash, StringComparison.Ordinal))
        {
            return CheckoutErrors.ContractsChanged;
        }

        var now = clock.UtcNow;
        var number = await numbers.NextAsync(cancellationToken);
        var isCard = method == PaymentMethods.Card;
        var reservationExpires = now.AddMinutes(store.Value.ReservationMinutes);
        var customerKey = checkout.CustomerId?.ToString() ?? checkout.Email!.ToLowerInvariant();
        var fraudFlags = await FraudFlags(checkout, quote, cancellationToken);

        var placed = Order.Place(new OrderDraft
        {
            Number = number,
            CheckoutId = checkout.Id,
            CustomerId = checkout.CustomerId ?? await customers.FindByEmailAsync(checkout.Email!, cancellationToken),
            Email = checkout.Email!,
            Phone = checkout.Phone,
            PaymentMethod = method,
            Currency = quote.Currency,
            Subtotal = quote.Subtotal,
            Discount = quote.Discount,
            Shipping = quote.Shipping,
            CodFee = quote.CodFee,
            Tax = quote.Tax,
            Total = quote.Total,
            ShippingMethod = checkout.ShippingMethod ?? quote.ShippingOptions[0].Method,
            ShippingAddress = checkout.ShippingAddress!,
            BillingAddress = checkout.BillingAddress ?? checkout.ShippingAddress!,
            IsGift = checkout.IsGift,
            GiftMessage = checkout.GiftMessage ?? cart.Items.Select(i => i.GiftMessage).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)),
            HidePrices = checkout.HidePrices,
            CouponCode = quote.AppliedCoupon,
            Locale = checkout.Locale,
            ContractHash = contracts.Hash,
            IdempotencyKey = request.IdempotencyKey,
            ReservationExpiresAt = isCard ? reservationExpires : null,
            FraudFlags = fraudFlags,
            Lines = [.. quote.Lines.Select(l => new OrderLineDraft(
                l.VariantId, l.ProductId, l.Sku, l.Section, l.ProductType, l.Name, l.VariantLabel, l.Warnings, l.IsSample, l.ShippingClass,
                l.GroupId, l.GroupLabel, l.UnitPrice, l.Qty, l.Discount, l.TaxRateBp, l.TaxAmount, l.LineTotal,
                l.Returnable && ReturnPolicy.IsReturnableOnWithdrawal(l.Section)))],
        }, now);
        if (placed.IsFailure)
        {
            return placed.Error;
        }

        var order = placed.Value;
        db.Add(order);
        await db.SaveChangesAsync(cancellationToken); // The order row must exist before stock and coupons reference it.

        // Law 4: reserve atomically. A single missing unit fails everything and the transaction rolls back.
        var reserved = await inventory.ReserveAsync(checkout.Id, order.Id,
            [.. quote.Lines.GroupBy(l => l.VariantId).Select(g => new StockRequest(g.Key, g.Sum(l => l.Qty)))],
            isCard ? reservationExpires : now.AddDays(1), cancellationToken);
        if (reserved.IsFailure)
        {
            return reserved.Error;
        }

        if (quote.AppliedCoupon is { } code)
        {
            var held = await coupons.HoldAsync(code, order.Id, customerKey, cancellationToken);
            if (held.IsFailure)
            {
                return held.Error;
            }
        }

        await documents.SaveAsync(order.Id, checkout.Locale, contracts, cancellationToken);
        await payments.RegisterAsync(order.Id, order.Number, method, order.Total, order.Currency, cancellationToken);

        if (!isCard)
        {
            // Cash on delivery: confirmed now, so stock and coupon are committed at once (commerce-flows.md §3).
            var committed = await inventory.CommitAsync(order.Id, cancellationToken);
            order.AssignBatches([.. committed.Select(a => (a.VariantId, a.BatchId, a.BatchCode, a.Qty))]);
            await coupons.CommitAsync(order.Id, cancellationToken);
        }

        if (checkout.MarketingConsent)
        {
            await customers.RecordMarketingConsentAsync(checkout.Email!, order.CustomerId, "email", true, "checkout", cancellationToken);
        }

        if (fraudFlags.Count > 0)
        {
            events.Publish(new StaffAlertRaised(AlertKinds.FraudReview, $"Order {order.Number} flagged for review", string.Join(", ", fraudFlags), order.Number));
        }

        checkout.Complete(order.Id, now);
        await carts.ClearAsync(checkout.CartId, cancellationToken);
        return new PlacedOrder(order.Id, order.Number, method, order.Total, order.Currency, false, order.Email, order.Locale);
    }

    private async Task<Checkout?> Load(Guid id, Requester who, CancellationToken cancellationToken)
    {
        var checkout = await db.Set<Checkout>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return checkout is not null && await Owns(checkout, who, cancellationToken) ? checkout : null;
    }

    /// <summary>A checkout belongs to whoever holds its cart (guest cookie or signed-in customer).</summary>
    private async Task<bool> Owns(Checkout checkout, Requester who, CancellationToken cancellationToken)
    {
        if (checkout.Status == CheckoutStatuses.Completed)
        {
            return checkout.CustomerId is not null ? checkout.CustomerId == who.CustomerId : who.CartToken is not null;
        }

        return await carts.ResolveAsync(who.CartToken, who.CustomerId, cancellationToken) == checkout.CartId;
    }

    private async Task<Quote> Quote(Checkout checkout, CartSnapshot cart, string method, CancellationToken cancellationToken) =>
        await quotes.QuoteAsync(new QuoteRequest
        {
            Items = [.. cart.Items.Select(i => new QuoteItem(i.VariantId, i.Qty, i.Components, i.GiftMessage))],
            Locale = checkout.Locale,
            CouponCode = cart.CouponCode ?? checkout.CouponCode,
            CustomerKey = checkout.CustomerId?.ToString() ?? checkout.Email?.ToLowerInvariant(),
            ProvinceCode = checkout.ShippingAddress?.ProvinceCode,
            ShippingMethod = checkout.ShippingMethod,
            PaymentMethod = method,
        }, cancellationToken);

    private async Task<Result<CheckoutView>> View(Checkout checkout, string method, CancellationToken cancellationToken)
    {
        var cart = await carts.GetAsync(checkout.CartId, cancellationToken);
        if (cart is null || (cart.Items.Count == 0 && checkout.Status == CheckoutStatuses.Open))
        {
            return CheckoutErrors.EmptyCart;
        }

        var quote = await Quote(checkout, cart, method, cancellationToken);
        var codReason = checkout.IsComplete ? await CodBlockReason(checkout, quote, cancellationToken) : null;
        var ready = checkout.IsComplete && quote.Problems.Count == 0 && quote.Lines.Count > 0;
        var contracts = ready ? documents.Render(ContractFactory.Build(checkout, quote, method, clock.Today)) : null;

        return new CheckoutView(
            checkout.Id, checkout.Email, checkout.Phone, checkout.ShippingAddress, checkout.BillingAddress, checkout.IsGift, checkout.GiftMessage,
            checkout.HidePrices, checkout.ShippingMethod, checkout.MarketingConsent, quote, codReason is null, codReason,
            contracts is null ? null : new ContractsDto(contracts.PreInformationHtml, contracts.DistanceSalesHtml, contracts.Hash),
            ready);
    }

    /// <summary>Cash on delivery rules (commerce-flows.md §6): order ceiling, and disabled after two refused parcels.</summary>
    private async Task<string?> CodBlockReason(Checkout checkout, Quote quote, CancellationToken cancellationToken)
    {
        if (!quote.CodAvailable)
        {
            return "order_total_above_cod_limit";
        }

        return await customers.CodRefusalsAsync(checkout.Email!, cancellationToken) >= 2 ? "cod_disabled_after_refusals" : null;
    }

    /// <summary>Simple signals that send an order to manual review (security.md §5). They never block the purchase.</summary>
    private async Task<IReadOnlyList<string>> FraudFlags(Checkout checkout, Quote quote, CancellationToken cancellationToken)
    {
        var flags = new List<string>();
        var email = checkout.Email!;
        var firstOrder = !await db.Set<Order>().AnyAsync(o => o.Email == email, cancellationToken);
        if (firstOrder && quote.Total >= LargeFirstOrder)
        {
            flags.Add("large_first_order");
        }

        if (checkout.BillingAddress is { } billing && billing.ProvinceCode != checkout.ShippingAddress!.ProvinceCode && quote.Total >= LargeFirstOrder / 2)
        {
            flags.Add("billing_far_from_shipping");
        }

        if (firstOrder && quote.Total >= LargeFirstOrder / 2 && quote.CodFee > 0)
        {
            flags.Add("cod_phone_confirmation"); // Call the customer before shipping (SMS provider not chosen yet).
        }

        var cutoff = clock.UtcNow.AddHours(-1);
        var failedToday = await db.Set<Order>().CountAsync(o => o.Email == email && o.Status == OrderStatuses.Cancelled && o.PlacedAt > cutoff, cancellationToken);
        if (failedToday >= 3)
        {
            flags.Add("repeated_failed_payments");
        }

        return flags;
    }
}

/// <summary>The contract model is built from the same quote the order freezes, so what was accepted is what is sold.</summary>
internal static class ContractFactory
{
    public static ContractModel Build(Checkout checkout, Quote quote, string method, DateOnly today)
    {
        var a = checkout.ShippingAddress!;
        static string Format(AddressData x) => string.Join(", ", new[] { x.FullName, x.Line1, x.Line2, x.Neighbourhood, x.District, x.ProvinceName, x.PostalCode }.Where(p => !string.IsNullOrWhiteSpace(p)));

        return new ContractModel
        {
            Locale = checkout.Locale,
            Date = today,
            Buyer = new ContractParty(a.FullName, checkout.Email!, checkout.Phone!, Format(a)),
            DeliveryAddress = Format(a),
            InvoiceAddress = checkout.BillingAddress is null ? null : Format(checkout.BillingAddress),
            Lines = [.. quote.Lines.Select(l => new ContractLine(l.GroupLabel is null ? l.Name : $"{l.GroupLabel}: {l.Name}", l.VariantLabel, l.Qty, l.UnitPrice, l.Discount, l.LineTotal, l.TaxRateBp,
                l.Returnable && ReturnPolicy.IsReturnableOnWithdrawal(l.Section)))],
            Currency = quote.Currency,
            Subtotal = quote.Subtotal,
            Discount = quote.Discount,
            Shipping = quote.Shipping,
            CodFee = quote.CodFee,
            Total = quote.Total,
            PaymentMethod = method,
            ShippingMethod = checkout.ShippingMethod ?? "standard",
        };
    }
}
