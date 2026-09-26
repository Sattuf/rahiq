using System.Text.Json;
using Rahiq.Modules.Cart.Domain;
using Rahiq.Modules.Catalog.Domain;
using Rahiq.Modules.Customers.Domain;
using Rahiq.Modules.Payments.Contracts;
using Rahiq.Modules.Payments.Domain;
using Rahiq.Modules.Shipping.Contracts;
using Rahiq.Modules.Shipping.Domain;
using Rahiq.SharedKernel.Compliance;

namespace Rahiq.Domain.Tests.Modules;

public class CartTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly CartLimits Limits = new(10, 3);

    [Fact]
    public void Same_variant_adds_up_to_the_per_variant_limit()
    {
        var (cart, token) = ShoppingCart.Create(null, Now);
        var variant = Guid.NewGuid();

        Assert.True(cart.Add(variant, 6, null, null, 100, false, new HashSet<Guid>(), Limits, Now).IsSuccess);
        Assert.Equal("cart.max_per_variant", cart.Add(variant, 5, null, null, 100, false, new HashSet<Guid>(), Limits, Now).Error.Code);
        Assert.Single(cart.Lines);
        Assert.Equal(6, cart.Lines[0].Qty);
        Assert.Equal(ShoppingCart.HashToken(token), cart.TokenHash);
    }

    [Fact]
    public void No_more_than_three_samples_per_order()
    {
        var (cart, _) = ShoppingCart.Create(null, Now);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var samples = new HashSet<Guid> { a, b };

        Assert.True(cart.Add(a, 2, null, null, 100, true, samples, Limits, Now).IsSuccess);
        Assert.Equal("cart.max_samples", cart.Add(b, 2, null, null, 100, true, samples, Limits, Now).Error.Code);
        Assert.True(cart.Add(b, 1, null, null, 100, true, samples, Limits, Now).IsSuccess);
        Assert.Equal("cart.max_samples", cart.ChangeQty(cart.Lines[1].Id, 2, samples, Limits, Now).Error.Code);
    }

    [Fact]
    public void Quantities_messages_and_lines_are_validated()
    {
        var (cart, _) = ShoppingCart.Create(null, Now);

        Assert.Equal("cart.qty_invalid", cart.Add(Guid.NewGuid(), 0, null, null, 1, false, new HashSet<Guid>(), Limits, Now).Error.Code);
        Assert.Equal("cart.gift_message_too_long", cart.Add(Guid.NewGuid(), 1, null, new string('x', 301), 1, false, new HashSet<Guid>(), Limits, Now).Error.Code);
        Assert.Equal("cart.line_not_found", cart.Remove(Guid.NewGuid(), Now).Error.Code);
        Assert.Equal("cart.line_not_found", cart.ChangeQty(Guid.NewGuid(), 1, new HashSet<Guid>(), Limits, Now).Error.Code);
    }

    [Fact]
    public void Gift_boxes_with_components_stay_separate_lines()
    {
        var (cart, _) = ShoppingCart.Create(null, Now);
        var box = Guid.NewGuid();

        cart.Add(box, 1, [Guid.NewGuid(), Guid.NewGuid()], "İyi ki doğdun", 100, false, new HashSet<Guid>(), Limits, Now);
        cart.Add(box, 1, [Guid.NewGuid(), Guid.NewGuid()], null, 100, false, new HashSet<Guid>(), Limits, Now);

        Assert.Equal(2, cart.Lines.Count);
        Assert.Equal("İyi ki doğdun", cart.Lines[0].GiftMessage);
    }

    [Fact]
    public void Merging_at_sign_in_adds_quantities_within_limits()
    {
        var (customer, _) = ShoppingCart.Create(Guid.NewGuid(), Now);
        var (guest, _) = ShoppingCart.Create(null, Now);
        var honey = Guid.NewGuid();
        customer.Add(honey, 8, null, null, 100, false, new HashSet<Guid>(), Limits, Now);
        guest.Add(honey, 5, null, null, 100, false, new HashSet<Guid>(), Limits, Now);
        guest.Add(Guid.NewGuid(), 1, null, null, 100, false, new HashSet<Guid>(), Limits, Now);
        guest.SetCoupon("iyi10", Now);

        customer.Absorb(guest, new HashSet<Guid>(), Limits, Now);

        Assert.Equal(10, customer.Lines.First(l => l.VariantId == honey).Qty);
        Assert.Equal(2, customer.Lines.Count);
        Assert.Equal("IYI10", customer.CouponCode);
        customer.Clear(Now);
        Assert.Empty(customer.Lines);
    }
}

public class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static Payment Card() => Payment.Create(Guid.NewGuid(), "RHQ-26-000001", "sandbox", "card", 117_990, "TRY", Now);

    [Fact]
    public void Success_with_the_right_amount_confirms_once()
    {
        var payment = Card();

        Assert.True(payment.ApplySuccess(117_990, "TRY", 3, "ref", "{}", Now).IsSuccess);
        Assert.True(payment.ApplySuccess(117_990, "TRY", 3, "ref", "{}", Now).IsSuccess);

        Assert.Equal(PaymentStatuses.Succeeded, payment.Status);
        Assert.Equal(3, payment.Installments);
        Assert.Single(payment.DomainEvents.OfType<PaymentSucceeded>());
    }

    /// <summary>testing.md commerce test 5, at the domain level.</summary>
    [Theory]
    [InlineData(117_989, "TRY")]
    [InlineData(117_990, "EUR")]
    public void A_different_amount_or_currency_confirms_nothing(long amount, string currency)
    {
        var payment = Card();

        Assert.Equal("payment.amount_mismatch", payment.ApplySuccess(amount, currency, 1, null, "{}", Now).Error.Code);
        Assert.Equal(PaymentStatuses.Pending, payment.Status);
        Assert.Empty(payment.DomainEvents);
    }

    [Fact]
    public void Failure_after_success_changes_nothing()
    {
        var payment = Card();
        payment.ApplySuccess(117_990, "TRY", 1, null, "{}", Now);

        payment.ApplyFailure("declined", "{}", Now);

        Assert.Equal(PaymentStatuses.Succeeded, payment.Status);
    }

    [Fact]
    public void A_late_success_after_abandonment_is_still_recorded()
    {
        var payment = Card();
        payment.Abandon(Now);

        Assert.True(payment.ApplySuccess(117_990, "TRY", 1, null, "{}", Now).IsSuccess);
        Assert.Equal(PaymentStatuses.Succeeded, payment.Status);
    }

    [Fact]
    public void Refunds_are_bounded_by_what_was_paid()
    {
        var payment = Card();
        Assert.Equal("refund.not_refundable", payment.CanRefund(1).Error.Code);
        payment.ApplySuccess(117_990, "TRY", 1, null, "{}", Now);

        Assert.True(payment.CanRefund(50_000).IsSuccess);
        payment.RecordRefund(50_000, Now);
        Assert.Equal(PaymentStatuses.PartiallyRefunded, payment.Status);
        Assert.Equal("refund.exceeds_paid", payment.CanRefund(67_991).Error.Code);
        payment.RecordRefund(67_990, Now);
        Assert.Equal(PaymentStatuses.Refunded, payment.Status);
        Assert.Equal(0, payment.Refundable);
    }

    [Fact]
    public void Failures_and_cod_collection()
    {
        var failed = Card();
        failed.ApplyFailure("3ds_failed", "{}", Now);
        Assert.IsType<PaymentFailed>(Assert.Single(failed.DomainEvents));

        var cod = Payment.Create(Guid.NewGuid(), "N", "cod", "cod", 1_000, "TRY", Now);
        cod.MarkCollected(Now);
        Assert.Equal(PaymentStatuses.Succeeded, cod.Status);

        var refund = Refund.Request(cod, 500, "damaged", null, Now);
        refund.Succeed("r1", "TRY", Now);
        Assert.IsType<RefundIssued>(Assert.Single(refund.DomainEvents));
        var failedRefund = Refund.Request(cod, 500, "x", null, Now);
        failedRefund.Fail("provider_down", Now);
        Assert.Equal("failed", failedRefund.Status);
    }
}

public class ShippingTests
{
    private static readonly RateBand[] Bands =
    [
        new("near", "standard", 2_000, 8_990),
        new("near", "standard", 5_000, 11_990),
        new("far", "standard", 2_000, 10_990),
    ];

    [Fact]
    public void Picks_the_smallest_band_that_fits()
    {
        Assert.Equal(11_990, Assert.Single(RateTable.Options(Bands, "near", 2_500, 10_000, null)).Amount);
        Assert.Equal(8_990, Assert.Single(RateTable.Options(Bands, "near", 2_000, 10_000, null)).Amount);
    }

    [Fact]
    public void Free_above_the_threshold_and_nothing_when_too_heavy()
    {
        var free = Assert.Single(RateTable.Options(Bands, "near", 1_000, 150_000, 150_000));

        Assert.True(free.IsFree);
        Assert.Equal(0, free.Amount);
        Assert.Empty(RateTable.Options(Bands, "far", 9_000, 1, null));
    }

    [Fact]
    public void Carrier_events_never_move_a_shipment_backwards()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "N", "sandbox", "TRK", null, null, DateTimeOffset.UtcNow);
        Assert.True(shipment.HandOver(DateTimeOffset.UtcNow).IsSuccess);
        Assert.False(shipment.HandOver(DateTimeOffset.UtcNow).IsSuccess);

        shipment.ApplyCarrierEvent(ShipmentStatuses.OutForDelivery, null, DateTimeOffset.UtcNow);
        shipment.ApplyCarrierEvent(ShipmentStatuses.InTransit, null, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(ShipmentStatuses.OutForDelivery, shipment.Status);

        shipment.ApplyCarrierEvent(ShipmentStatuses.Delivered, null, DateTimeOffset.UtcNow.AddMinutes(2));
        shipment.ApplyCarrierEvent(ShipmentStatuses.Returned, null, DateTimeOffset.UtcNow.AddMinutes(3));
        Assert.Equal(ShipmentStatuses.Delivered, shipment.Status);
        Assert.Contains(shipment.DomainEvents, e => e is ShipmentStatusChanged { Status: ShipmentStatuses.Delivered });
    }
}

public class AccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>RFC 6238 appendix B vectors (SHA-1), truncated to 6 digits.</summary>
    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Totp_matches_the_rfc_test_vectors(long unixSeconds, string expected)
    {
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"; // "12345678901234567890"

        Assert.Equal(expected, Totp.Code(secret, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
    }

    [Fact]
    public void Totp_accepts_one_step_of_drift_only()
    {
        var secret = Totp.NewSecret();
        var code = Totp.Code(secret, Now);

        Assert.True(Totp.Verify(secret, code, Now.AddSeconds(29)));
        Assert.False(Totp.Verify(secret, code, Now.AddMinutes(5)));
        Assert.False(Totp.Verify(secret, "12ab56", Now));
        Assert.StartsWith("otpauth://totp/Rahiq:", Totp.ProvisioningUri(secret, "owner@rahiq.local"), StringComparison.Ordinal);
    }

    [Fact]
    public void Otp_codes_expire_and_allow_five_attempts()
    {
        var (otp, code) = OtpCode.Issue("Ayse@Example.com", Now);

        for (var i = 0; i < 4; i++)
        {
            Assert.False(otp.TryConsume("000000" == code ? "111111" : "000000", Now));
        }

        Assert.True(otp.TryConsume(code, Now));
        Assert.False(otp.TryConsume(code, Now)); // single use

        var (expired, expiredCode) = OtpCode.Issue("a@b.c", Now);
        Assert.False(expired.TryConsume(expiredCode, Now.AddMinutes(11)));
    }

    [Fact]
    public void Staff_lock_out_grows_with_failures()
    {
        var staff = StaffUser.Create("Owner@Rahiq.local", "Owner", "owner", "hash", Now);
        for (var i = 0; i < 5; i++)
        {
            staff.RecordFailure(Now);
        }

        Assert.True(staff.IsLocked(Now));
        Assert.False(staff.IsLocked(Now.AddMinutes(2)));
        staff.RecordSuccess(Now);
        Assert.Equal(0, staff.FailedAttempts);
        Assert.Equal("owner@rahiq.local", staff.Email);
    }

    [Fact]
    public void Refresh_tokens_rotate_and_detect_reuse()
    {
        var (token, raw) = RefreshToken.Issue(Guid.NewGuid(), "customer", null, Now);

        Assert.Equal(RefreshToken.Hash(raw), token.TokenHash);
        Assert.True(token.IsUsable(Now));
        token.MarkUsed(Now);
        Assert.True(token.WasReused);
        Assert.False(token.IsUsable(Now));
    }

    [Fact]
    public void Erasure_removes_personal_data()
    {
        var customer = Customer.Register(" Ayse@Example.com ", "ar", Now);
        customer.UpdateProfile("Ayşe", "+90555", "ar");

        customer.Erase(Now);

        Assert.Null(customer.Name);
        Assert.Null(customer.Phone);
        Assert.EndsWith("@invalid.local", customer.Email, StringComparison.Ordinal);
    }
}

public class CatalogDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static TranslationInput[] Names(string name) =>
        [new("tr", name, null, null, null, null, null), new("ar", name, null, null, null, null, null), new("en", name, null, null, null, null, null)];

    [Fact]
    public void Mandatory_warnings_are_added_automatically_and_cannot_be_removed()
    {
        var product = Product.Create("nuts_in_honey", "ballı-ceviz".Replace("ı", "i", StringComparison.Ordinal), Now).Value;

        product.UpdateContent(Names("Cevizli bal"), Json("""{"composition":[{"ingredient":{"tr":"Bal"},"percent":60},{"ingredient":{"tr":"Ceviz"},"percent":40}]}"""), [], ["tree-nuts"], false, 0, Now);

        Assert.Contains("honey.infant-under-12-months", product.Warnings);
        Assert.Contains("allergen.contains", product.Warnings);
        Assert.Contains("honey.sugar-content", product.Warnings);
    }

    [Fact]
    public void Warnings_render_with_allergen_names_in_the_order_language()
    {
        var texts = WarningRules.Render(["allergen.contains"], ["tree-nuts", "sesame"], "ar");

        Assert.Equal("يحتوي على: مكسرات شجرية, سمسم.", Assert.Single(texts));
    }

    [Fact]
    public void Attributes_are_validated_against_the_dictionaries()
    {
        Assert.Empty(AttributeRules.Validate("perfume", Json("""{"concentration":"edp","families":["oud","woody"],"notes":{"top":["bergamot"],"heart":["taif-rose"],"base":["oud"]},"intensity":4}""")));
        Assert.NotEmpty(AttributeRules.Validate("perfume", Json("""{"families":["oud","woody","floral"]}""")));
        Assert.NotEmpty(AttributeRules.Validate("perfume", Json("""{"notes":{"top":["unicorn"]}}""")));
        Assert.NotEmpty(AttributeRules.Validate("perfume", Json("""{"sillage":9}""")));
        Assert.NotEmpty(AttributeRules.Validate("honey", Json("""{"floralSource":"plastic"}""")));
        Assert.NotEmpty(AttributeRules.Validate("honey_blend", Json("""{"composition":[{"ingredient":{"tr":"Bal"},"percent":90}]}""")));
        Assert.NotEmpty(AttributeRules.Validate("honey", Json("[]")));
    }

    [Fact]
    public void Publishing_requires_every_language_a_variant_and_the_legal_attributes()
    {
        var product = Product.Create("perfume", "oud-rose", Now).Value;
        product.UpdateContent(Names("Oud & Rose"), Json("""{"concentration":"edp"}"""), [], [], false, 0, Now);

        var notReady = product.Publish([], false, Now);
        Assert.Equal("product.not_ready", notReady.Error.Code);
        Assert.Contains("inci_required", notReady.Error.Details!["publish"]);
        Assert.Contains("no_active_variant", notReady.Error.Details["publish"]);

        product.UpdateContent(Names("Oud & Rose"), Json("""{"concentration":"edp","inci":["ALCOHOL DENAT.","PARFUM"]}"""), [], [], false, 0, Now);
        product.AddVariant(new VariantSpec("RHQ-OUD-50", 50, null, null, 400, "flammable", false, true, 0));
        Assert.True(product.Publish([], false, Now).IsSuccess);
        Assert.Equal(ProductStatuses.Active, product.Status);
    }

    [Fact]
    public void Forbidden_claims_block_publishing_unless_a_manager_approves()
    {
        var product = Product.Create("honey", "kestane-bali", Now).Value;
        product.UpdateContent(
            [new("tr", "Kestane balı", "Şifalı bal", null, null, null, null), new("ar", "عسل كستناء", null, null, null, null, null), new("en", "Chestnut honey", null, null, null, null, null)],
            Json("""{"floralSource":"chestnut"}"""), [], [], false, 0, Now);
        product.AddVariant(new VariantSpec("RHQ-KST-500", null, 500, null, 700, "liquid", false, true, 0));
        var findings = Taxonomy.CreateClaimsGuard().Check(product.TextFields());

        Assert.Equal("product.forbidden_claims", product.Publish(findings, false, Now).Error.Code);
        Assert.True(product.Publish(findings, true, Now).IsSuccess);
    }

    [Fact]
    public void Variant_rules_follow_the_product_type()
    {
        var perfume = Product.Create("perfume", "p", Now).Value;
        var honey = Product.Create("honey", "h", Now).Value;

        Assert.Equal("variant.flammable_required", perfume.AddVariant(new VariantSpec("P-50", 50, null, null, 400, "fragile", false, true, 0)).Error.Code);
        Assert.Equal("variant.size_required", perfume.AddVariant(new VariantSpec("P-50", null, null, null, 400, "flammable", false, true, 0)).Error.Code);
        Assert.Equal("variant.sample_not_allowed", honey.AddVariant(new VariantSpec("H-50", null, 50, null, 100, "liquid", true, true, 0)).Error.Code);
        Assert.Equal("variant.shipping_class_invalid", honey.AddVariant(new VariantSpec("H-50", null, 50, null, 100, "boat", false, true, 0)).Error.Code);
        var sample = perfume.AddVariant(new VariantSpec("P-2", 2, null, null, 30, "flammable", true, true, 0)).Value;
        Assert.Equal("2 ml · عيّنة", sample.Label("ar"));
        Assert.Equal("1 kg", honey.AddVariant(new VariantSpec("H-1000", null, 1000, null, 1300, "liquid", false, true, 0)).Value.Label("tr"));
    }

    [Fact]
    public void Slugs_and_media_are_guarded()
    {
        Assert.Equal("product.slug_invalid", Product.Create("honey", "Kestane Balı", Now).Error.Code);
        Assert.Equal("product.type_invalid", Product.Create("candle", "x", Now).Error.Code);
        var product = Product.Create("honey", "kestane", Now).Value;
        Assert.True(product.ChangeSlug("kestane-bali-500g").IsSuccess);
        Assert.Equal("kestane", product.PreviousSlug);
        Assert.Equal("media.generated_not_allowed", ProductMedia.Create(product.Id, "catalog", "k", "image/webp", 1, 1, true, [], 0).Error.Code);
        Assert.True(ProductMedia.Create(product.Id, "mood", "k", "image/webp", 1, 1, true, [], 0).IsSuccess);
    }
}
