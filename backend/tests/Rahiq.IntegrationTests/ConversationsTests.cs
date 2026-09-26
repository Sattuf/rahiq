using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rahiq.Modules.Cart.Contracts;
using Rahiq.Modules.Conversations.Application;
using Rahiq.Modules.Conversations.Application.Agent;
using Rahiq.Modules.Conversations.Infrastructure;
using Rahiq.Modules.Conversations.Infrastructure.Channels;
using Rahiq.Testing;

namespace Rahiq.IntegrationTests;

/// <summary>Webhook → inbox → hand-off → staff reply → delivery, against a real PostgreSQL (ADR-019).</summary>
public sealed class ConversationsTests(RahiqApp app) : IClassFixture<RahiqApp>
{
    private static int _updateId;

    [Fact]
    public async Task A_telegram_message_is_stored_once_handed_to_staff_and_answered()
    {
        var sent = new ConcurrentQueue<OutboundText>();
        await using var host = app.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IChannelAdapter>();
            s.AddSingleton<IChannelAdapter>(sp => new RecordingTelegram(ActivatorUtilities.CreateInstance<TelegramAdapter>(sp), sent));
        }));
        var chatId = Random.Shared.NextInt64(1_000_000, 9_000_000);
        var chat = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var update = TelegramUpdate(chatId, messageId: 1, "Merhaba, kestane balınız var mı?");

        // Unsigned or wrongly signed: refused, nothing stored.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostTelegram(host, update, "wrong-secret")).StatusCode);
        Assert.Equal(0, await app.Scalar<int>("SELECT count(*)::int FROM crm.contacts WHERE external_id = @id", new { id = chat }));

        // Telegram retries the same update: one message.
        Assert.Equal(HttpStatusCode.OK, (await PostTelegram(host, update)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostTelegram(host, update)).StatusCode);
        var conversationId = await app.Scalar<Guid>(
            "SELECT c.id FROM crm.conversations c JOIN crm.contacts k ON k.id = c.contact_id WHERE k.external_id = @id", new { id = chat });
        Assert.Equal(1, await app.Scalar<int>("SELECT count(*)::int FROM crm.messages WHERE conversation_id = @conversationId", new { conversationId }));
        Assert.Equal(1L, await app.Scalar<long>("SELECT agent_pending_seq FROM crm.conversations WHERE id = @conversationId", new { conversationId }));

        // The assistant is off in tests: the worker hands the conversation to a person instead of leaving it unanswered.
        var worker = host.Services.GetRequiredService<ConversationWorker>();
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(("human", "agent_disabled"), await Mode(conversationId));

        // Staff see it in the "waiting" list, reply, and the reply goes out on Telegram.
        var staff = await app.StaffClientAsync(host.CreateClient());
        var waiting = await staff.GetFromJsonAsync<JsonArray>("/api/admin/conversations?filter=handoff");
        Assert.Contains(waiting!, c => c!["id"]!.GetValue<Guid>() == conversationId);

        var reply = await staff.PostAsJsonAsync($"/api/admin/conversations/{conversationId}/messages", new { text = "Evet, 450 g kestane balımız var." });
        Assert.True(reply.StatusCode == HttpStatusCode.Accepted, await reply.Content.ReadAsStringAsync());
        await worker.RunOnceAsync(CancellationToken.None);

        var delivered = Assert.Single(sent);
        Assert.Equal((chat, "Evet, 450 g kestane balımız var."), (delivered.ExternalUserId, delivered.Text));
        Assert.Equal("sent", await app.Scalar<string>("SELECT status FROM crm.messages WHERE conversation_id = @conversationId AND author = 'staff'", new { conversationId }));

        var detail = await staff.GetFromJsonAsync<JsonObject>($"/api/admin/conversations/{conversationId}");
        Assert.Equal(3, detail!["messages"]!.AsArray().Count); // Customer, hand-off note, staff reply.
        Assert.NotNull(detail["conversation"]!["assignedStaffId"]);

        // Back to the assistant: the old message is not answered again, only new ones.
        await RahiqApp.EnsureOk(await staff.PostAsync(new Uri($"/api/admin/conversations/{conversationId}/release", UriKind.Relative), null));
        Assert.Equal(("agent", (string?)null), await Mode(conversationId));
        Assert.Equal(0L, await app.Scalar<long>("SELECT agent_pending_seq - agent_done_seq FROM crm.conversations WHERE id = @conversationId", new { conversationId }));
        await PostTelegram(host, TelegramUpdate(chatId, messageId: 2, "Fiyatı nedir?"));
        Assert.Equal(1L, await app.Scalar<long>("SELECT agent_pending_seq - agent_done_seq FROM crm.conversations WHERE id = @conversationId", new { conversationId }));
    }

    [Fact]
    public async Task The_live_inbox_token_opens_the_hub_only()
    {
        var anonymous = await app.CreateClient().PostAsync(new Uri("/api/admin/conversations/hub-token", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var staff = await app.StaffClientAsync();
        var token = (await (await staff.PostAsync(new Uri("/api/admin/conversations/hub-token", UriKind.Relative), null)).Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();

        // The hub accepts it (negotiate succeeds)...
        var negotiate = await app.CreateClient().PostAsync(new Uri($"/hubs/conversations/negotiate?negotiateVersion=1&access_token={token}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);

        // ...the rest of the API does not.
        var misuse = app.CreateClient();
        misuse.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await misuse.GetAsync(new Uri("/api/admin/conversations", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsync(new Uri("/hubs/conversations/negotiate?negotiateVersion=1", UriKind.Relative), null)).StatusCode);
    }

    [Fact]
    public async Task A_checkout_link_from_the_chat_opens_the_cart_once()
    {
        var variant = await app.CreateHoneyAsync(45_000, 20);
        var link = RahiqApp.Ok(await app.Send(new CreateCheckoutLinkCommand(Guid.NewGuid(), [new HandoffItem(variant, 2), new HandoffItem(Guid.NewGuid(), 1)], "ar")));

        Assert.Contains("/ar/claim#", link.Url, StringComparison.Ordinal);
        Assert.Equal(1, link.Lines);
        Assert.Single(link.Skipped); // The unknown variant was left out, not failed.

        var code = link.Url[(link.Url.IndexOf('#', StringComparison.Ordinal) + 1)..];
        var browser = app.CreateClient();
        var claimed = await browser.PostAsJsonAsync("/api/cart/claim", new { code });
        await RahiqApp.EnsureOk(claimed);
        var cart = await claimed.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(2, cart!["itemCount"]!.GetValue<int>());
        Assert.True(claimed.Headers.Contains("X-Cart-Token"));

        // The link is spent: a second click (or anyone who saw the chat) gets nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().PostAsJsonAsync("/api/cart/claim", new { code })).StatusCode);
    }

    [Fact]
    public async Task Order_status_needs_the_order_number_and_its_email_or_phone()
    {
        var variant = await app.CreateHoneyAsync(30_000, 10);
        var guest = await app.GuestWithCheckoutAsync((variant, 1));
        var number = await RahiqApp.OrderNumberAsync(await RahiqApp.PayAsync(guest, "cod"));

        await using var scope = app.Services.CreateAsyncScope();
        var tools = scope.ServiceProvider.GetRequiredService<AgentTools>();
        var context = new AgentContext(Guid.NewGuid(), "telegram", "Test", null, "tr");
        await app.Exec(
            "INSERT INTO crm.contacts (id, channel, external_id) VALUES (@id, 'telegram', @ext); INSERT INTO crm.conversations (id, contact_id, channel) VALUES (@cid, @id, 'telegram')",
            new { id = Guid.NewGuid(), ext = Guid.NewGuid().ToString(), cid = context.ConversationId });

        var wrong = await tools.RunAsync(context, AgentTools.GetOrderStatus, Json(new { order_number = number, email_or_phone = "someone@example.com" }), CancellationToken.None);
        var unknown = await tools.RunAsync(context, AgentTools.GetOrderStatus, Json(new { order_number = "RHQ-00-999999", email_or_phone = guest.Email }), CancellationToken.None);
        Assert.True(wrong.IsError);
        Assert.Equal(wrong.Json, unknown.Json); // Same answer: no probing which numbers exist.

        var right = await tools.RunAsync(context, AgentTools.GetOrderStatus, Json(new { order_number = number.ToLowerInvariant(), email_or_phone = guest.Email.ToUpperInvariant() }), CancellationToken.None);
        Assert.False(right.IsError, right.Json);
        Assert.Contains("confirmed", right.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("Atatürk", right.Json, StringComparison.Ordinal); // No street address in the chat.

        var viaWhatsApp = await tools.RunAsync(context with { VerifiedPhone = "+90 555 111 22 33" }, AgentTools.GetOrderStatus, Json(new { order_number = number }), CancellationToken.None);
        Assert.False(viaWhatsApp.IsError, viaWhatsApp.Json);

        var search = await tools.RunAsync(context, AgentTools.SearchProducts, Json(new { query = "Test", section = "honey", language = "tr" }), CancellationToken.None);
        Assert.Contains(variant.ToString(), search.Json, StringComparison.Ordinal);
        Assert.Equal(5, await app.Scalar<int>("SELECT count(*)::int FROM crm.agent_actions WHERE conversation_id = @id", new { id = context.ConversationId }));
    }

    private async Task<(string Mode, string? Reason)> Mode(Guid id)
    {
        await using var c = await app.OpenAsync();
        return await Dapper.SqlMapper.QuerySingleAsync<(string, string?)>(c, "SELECT mode, handoff_reason FROM crm.conversations WHERE id = @id", new { id });
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static string TelegramUpdate(long chatId, int messageId, string text) => JsonSerializer.Serialize(new
    {
        update_id = Interlocked.Increment(ref _updateId),
        message = new { message_id = messageId, from = new { id = chatId, is_bot = false, first_name = "Ayşe" }, chat = new { id = chatId, type = "private" }, date = 1_790_000_000, text },
    });

    private static async Task<HttpResponseMessage> PostTelegram(WebApplicationFactory<Program> host, string body, string secret = RahiqApp.TelegramSecret)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret);
        return await host.CreateClient().PostAsync(new Uri("/webhooks/telegram", UriKind.Relative), content);
    }

    /// <summary>The real Telegram adapter for checking and parsing; sending is recorded instead of calling Telegram.</summary>
    private sealed class RecordingTelegram(IChannelAdapter inner, ConcurrentQueue<OutboundText> sent) : IChannelAdapter
    {
        public string Webhook => inner.Webhook;

        public IReadOnlyList<string> Channels => inner.Channels;

        public bool IsConfigured => inner.IsConfigured;

        public bool VerifySignature(IReadOnlyDictionary<string, string> headers, byte[] body) => inner.VerifySignature(headers, body);

        public string? VerifySubscription(IReadOnlyDictionary<string, string> query) => inner.VerifySubscription(query);

        public IReadOnlyList<InboundMessage> Parse(byte[] body) => inner.Parse(body);

        public Task<SendOutcome> SendAsync(OutboundText message, CancellationToken cancellationToken)
        {
            sent.Enqueue(message);
            return Task.FromResult(SendOutcome.Sent($"tg-{sent.Count}"));
        }
    }
}
