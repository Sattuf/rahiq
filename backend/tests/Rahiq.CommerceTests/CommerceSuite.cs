using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Rahiq.Modules.Inventory.Application;
using Rahiq.Modules.Pricing.Application;
using Rahiq.Modules.Pricing.Contracts;
using Rahiq.Testing;

namespace Rahiq.CommerceTests;

/// <summary>
/// testing.md §2: the Phase 7 GATE. Every test runs through the real HTTP API against a real PostgreSQL.
/// Numbering follows the plan.
/// </summary>
public sealed class CommerceSuite(RahiqApp app) : IClassFixture<RahiqApp>
{
    private static readonly DateOnly InTwoYears = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(2);

    /// <summary>1. Oversell: 3 in stock, 50 simultaneous checkouts for one each → exactly 3 orders, never negative stock.</summary>
    [Fact]
    public async Task T01_fifty_buyers_for_the_last_three_units_buy_exactly_three()
    {
        var variant = await app.CreateHoneyAsync(50_000, 3);
        var guests = new List<RahiqApp.Guest>();
        for (var i = 0; i < 50; i++)
        {
            guests.Add(await app.GuestWithCheckoutAsync((variant, 1)));
        }

        var hashes = await Task.WhenAll(guests.Select(g => RahiqApp.ContractsHashAsync(g, "card")));
        var responses = await Task.WhenAll(guests.Select((g, i) => RahiqApp.PayAsync(g, "card", hashes[i])));

        var placed = responses.Where(r => r.IsSuccessStatusCode).ToList();
        var outcomes = string.Join(", ", await Task.WhenAll(responses.Select(async r => $"{(int)r.StatusCode}:{(r.IsSuccessStatusCode ? "ok" : await r.Content.ReadAsStringAsync())}").Distinct()));
        Assert.True(placed.Count == 3, outcomes);
        Assert.Equal(47, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var rejected in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Contains("stock.insufficient", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        foreach (var response in placed)
        {
            var number = await RahiqApp.OrderNumberAsync(response);
            var total = await app.Scalar<long>("SELECT total FROM ordering.orders WHERE number = @number", new { number });
            await app.SandboxWebhookAsync(await app.SessionTokenAsync(number), "succeeded", total);
        }

        await app.DrainOutboxAsync();
        Assert.Equal(3, await app.Scalar<int>("SELECT count(*)::int FROM ordering.orders o JOIN ordering.order_lines l ON l.order_id = o.id WHERE l.variant_id = @variant AND o.status = 'confirmed'", new { variant }));
        Assert.Equal(0, await app.Scalar<int>("SELECT qty_on_hand FROM inventory.batches WHERE variant_id = @variant", new { variant }));
        Assert.Equal(0, await app.Scalar<int>("SELECT qty_reserved FROM inventory.batches WHERE variant_id = @variant", new { variant }));
    }

    /// <summary>2. Expired reservation: the release worker gives the stock back and the order is cancelled.</summary>
    [Fact]
    public async Task T02_an_expired_reservation_returns_the_stock_and_cancels_the_order()
    {
        var variant = await app.CreateHoneyAsync(40_000, 2);
        var guest = await app.GuestWithCheckoutAsync((variant, 2));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(guest, "card"));
        Assert.Equal(2, await app.Scalar<int>("SELECT qty_reserved FROM inventory.batches WHERE variant_id = @variant", new { variant }));

        app.Clock.Advance(TimeSpan.FromMinutes(16));
        RahiqApp.Ok(await app.Send(new ReleaseExpiredReservationsCommand()));
        await app.DrainOutboxAsync();

        Assert.Equal(0, await app.Scalar<int>("SELECT qty_reserved FROM inventory.batches WHERE variant_id = @variant", new { variant }));
        Assert.Equal(2, await app.Scalar<int>("SELECT qty_on_hand FROM inventory.batches WHERE variant_id = @variant", new { variant }));
        Assert.Equal("cancelled", await app.OrderStatusAsync(number));
    }

    /// <summary>3. The same webhook five times: one confirmed order, one invoice, one e-mail.</summary>
    [Fact]
    public async Task T03_a_webhook_delivered_five_times_confirms_invoices_and_mails_once()
    {
        var variant = await app.CreateHoneyAsync(30_000, 5);
        var guest = await app.GuestWithCheckoutAsync((variant, 1));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(guest, "card"));
        var token = await app.SessionTokenAsync(number);
        var total = await app.Scalar<long>("SELECT total FROM ordering.orders WHERE number = @number", new { number });

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => app.SandboxWebhookAsync(token, "succeeded", total, eventId: "evt_same")));
        await app.DrainOutboxAsync();
        await app.DrainOutboxAsync();

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal("confirmed", await app.OrderStatusAsync(number));
        Assert.Equal(1, await app.Scalar<int>("SELECT count(*)::int FROM documents.invoices WHERE order_number = @number AND kind = 'sale'", new { number }));
        var orderId = await app.Scalar<Guid>("SELECT id FROM ordering.orders WHERE number = @number", new { number });
        Assert.Equal(1, await app.Scalar<int>("SELECT count(*)::int FROM notifications.log WHERE template = 'order_confirmed' AND ref_id = @orderId", new { orderId }));
        Assert.Equal(1, await app.Scalar<int>("SELECT count(*)::int FROM payments.webhook_events WHERE event_id = 'evt_same'"));
    }

    /// <summary>4. A forged webhook (wrong signature) is refused with 401 and changes nothing.</summary>
    [Fact]
    public async Task T04_a_forged_webhook_is_rejected_and_changes_nothing()
    {
        var variant = await app.CreateHoneyAsync(30_000, 5);
        var guest = await app.GuestWithCheckoutAsync((variant, 1));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(guest, "card"));

        var response = await app.SandboxWebhookAsync(await app.SessionTokenAsync(number), "succeeded", 30_000, secret: "attacker-guess");
        await app.DrainOutboxAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("pending_payment", await app.OrderStatusAsync(number));
        Assert.Equal("pending", await app.Scalar<string>("SELECT status FROM payments.payments WHERE order_number = @number", new { number }));
    }

    /// <summary>5. A success notification for less money does not confirm the order, and staff are alerted.</summary>
    [Fact]
    public async Task T05_a_success_for_a_different_amount_confirms_nothing_and_alerts()
    {
        var variant = await app.CreateHoneyAsync(30_000, 5);
        var guest = await app.GuestWithCheckoutAsync((variant, 1));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(guest, "card"));
        var total = await app.Scalar<long>("SELECT total FROM ordering.orders WHERE number = @number", new { number });

        await app.SandboxWebhookAsync(await app.SessionTokenAsync(number), "succeeded", total - 1);
        await app.DrainOutboxAsync();

        Assert.Equal("pending_payment", await app.OrderStatusAsync(number));
        Assert.True(await app.Scalar<bool>("SELECT EXISTS (SELECT 1 FROM infra.outbox_messages WHERE type LIKE '%StaffAlertRaised' AND payload->>'kind' = 'payment.amount_mismatch' AND payload->>'reference' = @number)", new { number }));
    }

    /// <summary>6a. Paid after the reservation expired, stock still there → the order is completed.</summary>
    [Fact]
    public async Task T06a_a_late_payment_completes_the_order_when_stock_is_still_there()
    {
        var variant = await app.CreateHoneyAsync(30_000, 5);
        var guest = await app.GuestWithCheckoutAsync((variant, 1));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(guest, "card"));
        app.Clock.Advance(TimeSpan.FromMinutes(20));
        RahiqApp.Ok(await app.Send(new ReleaseExpiredReservationsCommand()));
        await app.DrainOutboxAsync();
        Assert.Equal("cancelled", await app.OrderStatusAsync(number));

        var total = await app.Scalar<long>("SELECT total FROM ordering.orders WHERE number = @number", new { number });
        await app.SandboxWebhookAsync(await app.SessionTokenAsync(number), "succeeded", total);
        await app.DrainOutboxAsync();

        Assert.Equal("confirmed", await app.OrderStatusAsync(number));
        Assert.Equal(4, await app.Scalar<int>("SELECT qty_on_hand FROM inventory.batches WHERE variant_id = @variant", new { variant }));
    }

    /// <summary>6b. Paid after the reservation expired and someone else bought the stock → automatic refund and alert.</summary>
    [Fact]
    public async Task T06b_a_late_payment_is_refunded_automatically_when_the_stock_is_gone()
    {
        var variant = await app.CreateHoneyAsync(30_000, 1);
        var late = await app.GuestWithCheckoutAsync((variant, 1));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(late, "card"));
        app.Clock.Advance(TimeSpan.FromMinutes(20));
        RahiqApp.Ok(await app.Send(new ReleaseExpiredReservationsCommand()));
        await app.DrainOutboxAsync();

        var other = await app.GuestWithCheckoutAsync((variant, 1));
        await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(other, "cod")); // Takes the last unit.

        var total = await app.Scalar<long>("SELECT total FROM ordering.orders WHERE number = @number", new { number });
        await app.SandboxWebhookAsync(await app.SessionTokenAsync(number), "succeeded", total);
        await app.DrainOutboxAsync();

        Assert.Equal("cancelled", await app.OrderStatusAsync(number));
        Assert.Equal(total, await app.Scalar<long>("SELECT coalesce(sum(r.amount), 0) FROM payments.refunds r JOIN payments.payments p ON p.id = r.payment_id WHERE p.order_number = @number AND r.status = 'succeeded'", new { number }));
        Assert.True(await app.Scalar<bool>("SELECT EXISTS (SELECT 1 FROM infra.outbox_messages WHERE type LIKE '%StaffAlertRaised' AND payload->>'kind' = 'payment.late_refund' AND payload->>'reference' = @number)", new { number }));
        Assert.Equal(0, await app.Scalar<int>("SELECT qty_on_hand FROM inventory.batches WHERE variant_id = @variant", new { variant }));
    }

    /// <summary>7. Double click on "pay" with the same Idempotency-Key → one order.</summary>
    [Fact]
    public async Task T07_a_double_click_places_one_order()
    {
        var variant = await app.CreateHoneyAsync(30_000, 10);
        var guest = await app.GuestWithCheckoutAsync((variant, 1));
        var hash = await RahiqApp.ContractsHashAsync(guest, "card");
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RahiqApp.PayAsync(guest, "card", hash, key)));
        var again = await RahiqApp.PayAsync(guest, "card", hash, key);

        Assert.Contains(responses, r => r.IsSuccessStatusCode);
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode || r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await app.Scalar<int>("SELECT count(*)::int FROM ordering.orders WHERE checkout_id = @id", new { id = guest.CheckoutId }));
        Assert.Equal(1, await app.Scalar<int>("SELECT qty_reserved FROM inventory.batches WHERE variant_id = @variant", new { variant }));
    }

    /// <summary>8. A thousand random carts priced by the real quote service: every total adds up to the last kuruş.</summary>
    [Fact]
    public async Task T08_a_thousand_random_carts_add_up_to_the_last_kurus()
    {
        var random = new Random(8);
        var variants = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            variants.Add(i % 2 == 0
                ? await app.CreateHoneyAsync(random.NextInt64(1_000, 200_000), 1_000)
                : await app.CreateSellableAsync("perfume", random.NextInt64(1_000, 500_000), ("P1", 1_000, null)));
        }

        RahiqApp.Ok(await app.Send(new SaveCouponCommand(null, new CouponInput("RND15", "percent", 1_500, null, null, null, null, null, null, null, true))));
        RahiqApp.Ok(await app.Send(new SaveCouponCommand(null, new CouponInput("RND250", "fixed", 25_000, 30_000, "honey", null, null, null, null, null, true))));
        string?[] coupons = [null, "RND15", "rnd250", "KARGO-NONE"];

        for (var n = 0; n < 1_000; n++)
        {
            var items = variants.OrderBy(_ => random.Next()).Take(random.Next(1, 5)).Select(v => new QuoteItem(v, random.Next(1, 10))).ToList();
            var quote = await app.Send(new PublicQuoteQuery(new QuoteRequest
            {
                Items = items,
                Locale = "tr",
                CouponCode = coupons[random.Next(coupons.Length)],
                ProvinceCode = random.Next(1, 82),
                PaymentMethod = random.Next(3) == 0 ? "cod" : "card",
            }));

            Assert.Equal(quote.Subtotal - quote.Discount + quote.Shipping + quote.CodFee, quote.Total);
            Assert.Equal(quote.Discount, quote.Lines.Sum(l => l.Discount));
            Assert.Equal(quote.Subtotal, quote.Lines.Sum(l => l.UnitPrice * l.Qty));
            Assert.All(quote.Lines, l => Assert.Equal((l.UnitPrice * l.Qty) - l.Discount, l.LineTotal));
            Assert.True(quote.Tax <= quote.Total && quote.Tax >= quote.Lines.Sum(l => l.TaxAmount));
        }
    }

    /// <summary>9. A coupon limited to 10 uses, 30 simultaneous orders with it → exactly 10 get it.</summary>
    [Fact]
    public async Task T09_a_ten_use_coupon_is_used_ten_times_under_load()
    {
        var variant = await app.CreateHoneyAsync(60_000, 100);
        RahiqApp.Ok(await app.Send(new SaveCouponCommand(null, new CouponInput("TEN10", "percent", 1_000, null, null, 10, null, null, null, null, true))));
        var guests = new List<RahiqApp.Guest>();
        for (var i = 0; i < 30; i++)
        {
            guests.Add(await app.GuestWithCheckoutAsync("ten10", (variant, 1)));
        }

        var hashes = await Task.WhenAll(guests.Select(g => RahiqApp.ContractsHashAsync(g, "cod")));
        var responses = await Task.WhenAll(guests.Select((g, i) => RahiqApp.PayAsync(g, "cod", hashes[i])));

        Assert.Equal(10, responses.Count(r => r.IsSuccessStatusCode));
        Assert.Equal(10, await app.Scalar<int>("SELECT used_count FROM pricing.coupons WHERE code = 'TEN10'"));
        Assert.Equal(10, await app.Scalar<int>("SELECT count(*)::int FROM ordering.orders WHERE coupon_code = 'TEN10'"));
    }

    /// <summary>10. FEFO: the earliest-expiring batch ships first; one too close to expiry never ships.</summary>
    [Fact]
    public async Task T10_the_earliest_expiring_sellable_batch_ships_first()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var variant = await app.CreateSellableAsync("honey", 20_000, ("LATE", 5, today.AddDays(700)), ("EARLY", 5, today.AddDays(200)), ("TOO-SOON", 5, today.AddDays(30)));
        var guest = await app.GuestWithCheckoutAsync((variant, 6));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(guest, "cod"));

        var view = await guest.Client.PostAsJsonAsync("/api/orders/lookup", new { number, email = guest.Email });
        var batches = (await view.Content.ReadFromJsonAsync<JsonObject>())!["lines"]![0]!["batches"]!.AsArray().Select(b => (b!["code"]!.GetValue<string>(), b["qty"]!.GetValue<int>())).ToList();

        Assert.Equal([("EARLY", 5), ("LATE", 1)], batches);
        Assert.Equal(5, await app.Scalar<int>("SELECT qty_on_hand FROM inventory.batches WHERE variant_id = @variant AND code = 'TOO-SOON'", new { variant }));
    }

    /// <summary>11. The frozen snapshot: renaming the product and changing its price leaves the order untouched.</summary>
    [Fact]
    public async Task T11_an_order_keeps_its_name_price_and_documents_after_catalog_changes()
    {
        var variant = await app.CreateHoneyAsync(45_000, 5);
        var guest = await app.GuestWithCheckoutAsync((variant, 1));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(guest, "cod"));
        var before = await guest.Client.PostAsJsonAsync("/api/orders/lookup", new { number, email = guest.Email });
        var beforeJson = (await before.Content.ReadFromJsonAsync<JsonObject>())!;
        var hashBefore = await app.Scalar<string>("SELECT contract_doc_hash FROM ordering.orders WHERE number = @number", new { number });

        await app.Exec("UPDATE catalog.product_translations SET name = 'Renamed' WHERE product_id = (SELECT product_id FROM catalog.variants WHERE id = @variant)", new { variant });
        RahiqApp.OkResult(await app.Send(new SetPriceCommand(variant, 99_000)));

        var after = await guest.Client.PostAsJsonAsync("/api/orders/lookup", new { number, email = guest.Email });
        var afterJson = (await after.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(beforeJson["lines"]![0]!["name"]!.GetValue<string>(), afterJson["lines"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(45_000, afterJson["lines"]![0]!["unitPrice"]!.GetValue<long>());
        Assert.Equal(beforeJson["total"]!.GetValue<long>(), afterJson["total"]!.GetValue<long>());
        Assert.Equal(hashBefore, await app.Scalar<string>("SELECT sha256 FROM documents.documents WHERE order_id = (SELECT id FROM ordering.orders WHERE number = @number) AND kind = 'distance_sales'", new { number }));
    }

    /// <summary>12. The legal "previous price" is the lowest price of the previous 30 days, computed from history.</summary>
    [Fact]
    public async Task T12_the_previous_price_shown_is_the_lowest_of_the_last_30_days()
    {
        var variant = await app.CreateHoneyAsync(50_000, 5);
        var now = DateTimeOffset.UtcNow;
        await app.Exec("DELETE FROM pricing.prices WHERE variant_id = @variant", new { variant });
        await app.Exec("""
            INSERT INTO pricing.prices (variant_id, currency, amount, valid_from, valid_to) VALUES
              (@variant, 'TRY', 60000, @d90, @d20),
              (@variant, 'TRY', 52000, @d20, @d5),
              (@variant, 'TRY', 44000, @d5, NULL)
            """, new { variant, d90 = now.AddDays(-90), d20 = now.AddDays(-20), d5 = now.AddDays(-5) });

        var price = (await app.Send(new PriceQuery([variant])))[variant];

        Assert.Equal(44_000, price.Price.Amount);
        Assert.Equal(52_000, price.PreviousPrice!.Value.Amount);
    }
}
