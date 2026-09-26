using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Modules.Customers.Application;
using Rahiq.Modules.Shipping.Application;
using Rahiq.Testing;

namespace Rahiq.IntegrationTests;

public sealed class ApiTests(RahiqApp app) : IClassFixture<RahiqApp>
{
    [Fact]
    public async Task Health_and_security_headers()
    {
        var client = app.CreateClient();

        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("nosniff", live.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", live.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", live.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Customer_signs_in_with_a_one_time_code_and_the_guest_cart_follows()
    {
        var variant = await app.CreateHoneyAsync(30_000, 10);
        var client = app.CreateClient();
        var add = await client.PostAsJsonAsync("/api/cart/lines", new { variantId = variant, qty = 2 });
        var guestToken = add.Headers.GetValues("X-Cart-Token").First();
        var email = $"c-{Guid.NewGuid():N}@example.com";

        await RahiqApp.EnsureOk(await client.PostAsJsonAsync("/api/auth/otp/request", new { email }));
        var wrong = await client.PostAsJsonAsync("/api/auth/otp/verify", new { email, code = "000000", guestCartToken = guestToken });
        var session = await client.PostAsJsonAsync("/api/auth/otp/verify", new { email, code = app.Otp.CodeFor(email), guestCartToken = guestToken });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        await RahiqApp.EnsureOk(session);
        var tokens = (await session.Content.ReadFromJsonAsync<JsonObject>())!["tokens"]!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens["accessToken"]!.GetValue<string>());

        var cart = await client.GetFromJsonAsync<JsonObject>("/api/cart");
        var me = await client.GetFromJsonAsync<JsonObject>("/api/me");
        Assert.Equal(2, cart!["itemCount"]!.GetValue<int>());
        Assert.Equal(email, me!["email"]!.GetValue<string>());

        // Refresh rotation with reuse detection: the old refresh token must not work twice.
        var refresh = tokens["refreshToken"]!.GetValue<string>();
        var first = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        var reused = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        var next = (await first.Content.ReadFromJsonAsync<JsonObject>())!["refreshToken"]!.GetValue<string>();
        var afterTheft = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = next });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode); // The whole family was revoked.
    }

    [Fact]
    public async Task Admin_needs_a_second_factor_and_forbidden_claims_block_publishing()
    {
        var email = $"owner-{Guid.NewGuid():N}@rahiq.local";
        RahiqApp.Ok(await app.Send(new BootstrapOwnerCommand(email, "Owner", "a-long-owner-passphrase-2026")));
        var client = app.CreateClient();

        var anonymous = await client.GetAsync(new Uri("/api/admin/products", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var login = await (await client.PostAsJsonAsync("/api/auth/staff/login", new { email, password = "a-long-owner-passphrase-2026" })).Content.ReadFromJsonAsync<JsonObject>();
        var ticket = login!["enrollmentTicket"]!.GetValue<string>();
        Assert.Null(login["tokens"]);

        var enrollment = await (await client.PostAsJsonAsync("/api/auth/staff/totp/start", new { ticket })).Content.ReadFromJsonAsync<JsonObject>();
        var secret = enrollment!["secret"]!.GetValue<string>();
        var confirmed = await (await client.PostAsJsonAsync("/api/auth/staff/totp/confirm", new { ticket, code = TotpCode(secret) })).Content.ReadFromJsonAsync<JsonObject>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", confirmed!["tokens"]!["accessToken"]!.GetValue<string>());

        var created = await client.PostAsJsonAsync("/api/admin/products", new { type = "honey", slug = $"sifali-{Guid.NewGuid():N}" });
        var id = await created.Content.ReadFromJsonAsync<Guid>();
        var product = await client.GetFromJsonAsync<JsonObject>($"/api/admin/products/{id}");
        var saved = await client.PutAsJsonAsync($"/api/admin/products/{id}", new
        {
            version = product!["version"]!.GetValue<uint>(),
            slug = product["slug"]!.GetValue<string>(),
            translations = new[]
            {
                new { locale = "tr", name = "Şifalı bal", shortDescription = "Öksürüğü tedavi eder" },
                new { locale = "ar", name = "عسل", shortDescription = "يقوي المناعة" },
                new { locale = "en", name = "Honey", shortDescription = "A natural remedy" },
            },
            attributes = JsonSerializer.Deserialize<JsonElement>("""{"floralSource":"chestnut"}"""),
            warnings = Array.Empty<string>(),
            allergens = Array.Empty<string>(),
            isFeatured = false,
            sortOrder = 0,
        });
        var findings = (await saved.Content.ReadFromJsonAsync<JsonObject>())!["findings"]!.AsArray();
        Assert.Contains(findings, f => f!["severity"]!.GetValue<string>() == "blocking" && f["term"]!.GetValue<string>() == "şifalı");
        Assert.Contains(findings, f => f!["term"]!.GetValue<string>() == "المناعة");
        Assert.Contains(findings, f => f!["term"]!.GetValue<string>() == "remedy");

        await client.PostAsJsonAsync($"/api/admin/products/{id}/variants", new { sku = $"TST-{Guid.NewGuid():N}"[..16], weightG = 500, shippingWeightG = 700, shippingClass = "liquid", isSample = false, isActive = true, sortOrder = 0 });
        var blocked = await client.PostAsJsonAsync($"/api/admin/products/{id}/publish", new { approveClaims = false });
        var noReason = await client.PostAsJsonAsync($"/api/admin/products/{id}/publish", new { approveClaims = true, approvalReason = "ok" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("product.forbidden_claims", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var approved = await client.PostAsJsonAsync($"/api/admin/products/{id}/publish", new { approveClaims = true, approvalReason = "Test only: documenting the override path for the audit." });
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        Assert.Equal(1, await app.Scalar<int>("SELECT count(*)::int FROM catalog.claim_approvals WHERE entity_id = @id", new { id }));
        Assert.True(await app.Scalar<bool>("SELECT EXISTS (SELECT 1 FROM infra.audit_log WHERE action = 'claims.override' AND entity_id = @id)", new { id = id.ToString() }));

        // Mandatory warnings were added without the editor asking (Law 2).
        var warnings = await app.Scalar<string[]>("SELECT warnings FROM catalog.products WHERE id = @id", new { id });
        Assert.Contains("honey.infant-under-12-months", warnings);
    }

    [Fact]
    public async Task Audit_and_movement_logs_are_append_only()
    {
        var variant = await app.CreateHoneyAsync(10_000, 3);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.Exec("DELETE FROM inventory.movements WHERE batch_id IN (SELECT id FROM inventory.batches WHERE variant_id = @variant)", new { variant }));
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.Exec("UPDATE infra.audit_log SET action = 'x'"));
    }

    [Fact]
    public async Task The_database_itself_refuses_to_reserve_more_than_is_on_hand()
    {
        var variant = await app.CreateHoneyAsync(10_000, 3);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.Exec("UPDATE inventory.batches SET qty_reserved = 4 WHERE variant_id = @variant", new { variant }));

        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task The_batch_page_behind_the_qr_code_shows_the_batch_and_nothing_else()
    {
        var variant = await app.CreateHoneyAsync(20_000, 5);
        var token = await app.Scalar<string>("SELECT public_token FROM inventory.batches WHERE variant_id = @variant", new { variant });
        var client = app.CreateClient();

        var page = await client.GetFromJsonAsync<JsonObject>($"/api/batches/{token}?locale=ar");
        var guess = await client.GetAsync(new Uri("/api/batches/aaaaaaaaaaaaaaaaaaaaaaaa", UriKind.Relative));

        Assert.Equal("B1", page!["code"]!.GetValue<string>());
        Assert.True(page["isCurrentBatch"]!.GetValue<bool>());
        Assert.False(page["analysed"]!.GetValue<bool>()); // No lab report, no "analysed" badge.
        Assert.Equal(HttpStatusCode.NotFound, guess.StatusCode);
        Assert.DoesNotContain("email", page.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Guest_tracking_needs_the_number_and_the_email_together()
    {
        var variant = await app.CreateHoneyAsync(20_000, 5);
        var guest = await app.GuestWithCheckoutAsync((variant, 1));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(guest, "cod"));
        var stranger = app.CreateClient();

        var withEmail = await stranger.PostAsJsonAsync("/api/orders/lookup", new { number, email = guest.Email });
        var withoutEmail = await stranger.PostAsJsonAsync("/api/orders/lookup", new { number, email = "someone@else.com" });

        Assert.Equal(HttpStatusCode.OK, withEmail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withoutEmail.StatusCode);
    }

    [Fact]
    public async Task Honey_cannot_be_returned_on_withdrawal_but_can_if_it_arrived_damaged()
    {
        var variant = await app.CreateHoneyAsync(20_000, 5);
        var guest = await app.GuestWithCheckoutAsync((variant, 1));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(guest, "cod"));
        var orderId = await app.Scalar<Guid>("SELECT id FROM ordering.orders WHERE number = @number", new { number });
        await Deliver(orderId);
        var lineId = await app.Scalar<Guid>("SELECT id FROM ordering.order_lines WHERE order_id = @orderId", new { orderId });

        var withdrawal = await guest.Client.PostAsJsonAsync("/api/orders/returns", new { number, email = guest.Email, kind = "withdrawal", reason = "Changed my mind", lines = new[] { new { lineId, qty = 1, sealIntact = true } }, photoKeys = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Conflict, withdrawal.StatusCode);
        Assert.Contains("return.line_not_returnable", await withdrawal.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var photo = new MultipartFormDataContent { { new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0]), "file", "jar.jpg" } };
        var key = await (await guest.Client.PostAsync(new Uri("/api/orders/returns/photos", UriKind.Relative), photo)).Content.ReadFromJsonAsync<string>();
        var damaged = await guest.Client.PostAsJsonAsync("/api/orders/returns", new { number, email = guest.Email, kind = "damaged", reason = "The jar arrived cracked", lines = new[] { new { lineId, qty = 1, sealIntact = false } }, photoKeys = new[] { key } });
        await RahiqApp.EnsureOk(damaged);
        Assert.Equal("return_requested", await app.OrderStatusAsync(number));
    }

    private async Task Deliver(Guid orderId)
    {
        await app.Exec("SELECT 1");
        using var scope = app.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<Application.Abstractions.ISender>();
        RahiqApp.OkResult(await sender.Send(new Modules.Ordering.Application.StartPreparingCommand(orderId)));
        var shipment = RahiqApp.Ok(await sender.Send(new CreateShipmentCommand(orderId)));
        RahiqApp.OkResult(await sender.Send(new HandOverShipmentCommand(shipment.Id)));
        await app.DrainOutboxAsync();
        var client = app.CreateClient();
        await RahiqApp.EnsureOk(await client.PostAsJsonAsync($"/dev/shipping/{shipment.TrackingNumber}", new { status = "delivered" }));
        await app.DrainOutboxAsync();
        Assert.Equal("delivered", await app.Scalar<string>("SELECT status FROM ordering.orders WHERE id = @orderId", new { orderId }));
    }

    private string TotpCode(string secret)
    {
        _ = app.Services.GetRequiredService<IDataProtectionProvider>();
        var key = Base32(secret);
        var counter = app.Clock.UtcNow.ToUnixTimeSeconds() / 30;
        var message = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);
#pragma warning disable CA5350 // RFC 6238.
        var hash = System.Security.Cryptography.HMACSHA1.HashData(key, message);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in input)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
