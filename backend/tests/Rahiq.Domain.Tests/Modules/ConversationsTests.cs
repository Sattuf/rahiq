using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;
using Rahiq.Modules.Cart.Domain;
using Rahiq.Modules.Conversations.Application.Agent;
using Rahiq.Modules.Conversations.Domain;
using Rahiq.Modules.Conversations.Infrastructure.Channels;

namespace Rahiq.Domain.Tests.Modules;

public class ChannelSecurityTests
{
    private const string Secret = "app-secret-for-tests";

    private static string Sign(byte[] body, string secret = Secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    [Fact]
    public void Meta_signature_must_match_the_exact_bytes()
    {
        var body = """{"object":"page","entry":[]}"""u8.ToArray();

        Assert.True(WebhookSignatures.MetaSha256(Sign(body), body, Secret));
        Assert.False(WebhookSignatures.MetaSha256(Sign(body, "other-secret"), body, Secret));
        Assert.False(WebhookSignatures.MetaSha256(Sign(body), """{"object":"page","entry":[] }"""u8.ToArray(), Secret)); // One space changed.
        Assert.False(WebhookSignatures.MetaSha256(null, body, Secret));
        Assert.False(WebhookSignatures.MetaSha256("sha1=abc", body, Secret));
        Assert.False(WebhookSignatures.MetaSha256("sha256=not-hex", body, Secret));
        Assert.False(WebhookSignatures.MetaSha256(Sign(body), body, null)); // Unconfigured never accepts.
    }

    [Fact]
    public void Meta_handshake_answers_only_with_the_right_verify_token()
    {
        var ok = new Dictionary<string, string> { ["hub.mode"] = "subscribe", ["hub.verify_token"] = "vt", ["hub.challenge"] = "12345" };

        Assert.Equal("12345", WebhookSignatures.MetaHandshake(ok, "vt"));
        Assert.Null(WebhookSignatures.MetaHandshake(new Dictionary<string, string>(ok) { ["hub.verify_token"] = "wrong" }, "vt"));
        Assert.Null(WebhookSignatures.MetaHandshake(ok, null));
    }

    [Fact]
    public void Telegram_requires_its_secret_header()
    {
        var adapter = Telegram();
        Assert.True(adapter.VerifySignature(new Dictionary<string, string> { ["x-telegram-bot-api-secret-token"] = "telegram-secret-0123456789" }, []));
        Assert.False(adapter.VerifySignature(new Dictionary<string, string> { ["x-telegram-bot-api-secret-token"] = "guess" }, []));
        Assert.False(adapter.VerifySignature(new Dictionary<string, string>(), []));
    }

    internal static TelegramAdapter Telegram() => new(null!, Options.Create(new ChannelOptions
    {
        Telegram = new TelegramOptions { BotToken = "123:abc", WebhookSecret = "telegram-secret-0123456789" },
    }));
}

public class ChannelParsingTests
{
    [Fact]
    public void Telegram_private_text_becomes_one_message()
    {
        var body = """
            {"update_id":1,"message":{"message_id":7,"from":{"id":42,"is_bot":false,"first_name":"Leyla","language_code":"ar"},
             "chat":{"id":42,"type":"private"},"date":1700000000,"text":"مرحبا، عندكم عسل سدر؟"}}
            """u8.ToArray();

        var m = Assert.Single(ChannelSecurityTests.Telegram().Parse(body));
        Assert.Equal(("telegram", "42", "42:7", "text", "Leyla", "ar"), (m.Channel, m.ExternalUserId, m.ExternalMessageId, m.Kind, m.DisplayName, m.LanguageHint));
        Assert.Contains("عسل", m.Text, StringComparison.Ordinal);
        Assert.Null(m.Phone);
    }

    [Fact]
    public void Telegram_ignores_groups_and_bots_and_trusts_only_the_users_own_contact()
    {
        var adapter = ChannelSecurityTests.Telegram();
        Assert.Empty(adapter.Parse("""{"message":{"message_id":1,"from":{"id":1},"chat":{"id":-5,"type":"group"},"text":"hi"}}"""u8.ToArray()));
        Assert.Empty(adapter.Parse("""{"message":{"message_id":1,"from":{"id":1,"is_bot":true},"chat":{"id":1,"type":"private"},"text":"hi"}}"""u8.ToArray()));

        var own = Assert.Single(adapter.Parse("""{"message":{"message_id":2,"from":{"id":9},"chat":{"id":9,"type":"private"},"contact":{"phone_number":"+905321112233","user_id":9}}}"""u8.ToArray()));
        Assert.Equal("+905321112233", own.Phone);

        var someoneElse = Assert.Single(adapter.Parse("""{"message":{"message_id":3,"from":{"id":9},"chat":{"id":9,"type":"private"},"contact":{"phone_number":"+905320000000","user_id":10}}}"""u8.ToArray()));
        Assert.Null(someoneElse.Phone);
    }

    [Fact]
    public void Meta_splits_messenger_and_instagram_and_skips_echoes()
    {
        var adapter = new MetaAdapter(null!, Options.Create(new ChannelOptions { Meta = new MetaOptions { AppSecret = "s", PageAccessToken = "t" } }));
        var page = """
            {"object":"page","entry":[{"id":"PAGE","time":1,"messaging":[
              {"sender":{"id":"PSID1"},"recipient":{"id":"PAGE"},"timestamp":1,"message":{"mid":"m1","text":"Merhaba"}},
              {"sender":{"id":"PAGE"},"recipient":{"id":"PSID1"},"timestamp":2,"message":{"mid":"m2","text":"echo","is_echo":true}},
              {"sender":{"id":"PSID1"},"recipient":{"id":"PAGE"},"timestamp":3,"read":{"watermark":3}},
              {"sender":{"id":"PSID1"},"recipient":{"id":"PAGE"},"timestamp":4,"message":{"mid":"m3","attachments":[{"type":"image"}]}}
            ]}]}
            """u8.ToArray();

        var messages = adapter.Parse(page);
        Assert.Equal(["m1", "m3"], messages.Select(m => m.ExternalMessageId));
        Assert.All(messages, m => Assert.Equal("messenger", m.Channel));
        Assert.Equal(("image", "[image]"), (messages[1].Kind, messages[1].Text));

        var ig = adapter.Parse("""{"object":"instagram","entry":[{"id":"IG","messaging":[{"sender":{"id":"IGSID"},"recipient":{"id":"IG"},"message":{"mid":"ig1","text":"price?"}}]}]}"""u8.ToArray());
        Assert.Equal("instagram", Assert.Single(ig).Channel);
    }

    [Fact]
    public void WhatsApp_takes_messages_to_our_number_only_and_ignores_statuses()
    {
        var adapter = new WhatsAppAdapter(null!, Options.Create(new ChannelOptions { WhatsApp = new WhatsAppOptions { AppSecret = "s", AccessToken = "t", PhoneNumberId = "111" } }));
        var body = """
            {"object":"whatsapp_business_account","entry":[{"id":"WABA","changes":[
              {"field":"messages","value":{"messaging_product":"whatsapp","metadata":{"phone_number_id":"111"},
                "contacts":[{"profile":{"name":"Ahmet"},"wa_id":"905321112233"}],
                "messages":[{"from":"905321112233","id":"wamid.A","timestamp":"1700000000","type":"text","text":{"body":"Sipariş durumum?"}}]}},
              {"field":"messages","value":{"messaging_product":"whatsapp","metadata":{"phone_number_id":"111"},
                "statuses":[{"id":"wamid.B","status":"read"}]}},
              {"field":"messages","value":{"messaging_product":"whatsapp","metadata":{"phone_number_id":"999"},
                "messages":[{"from":"1","id":"wamid.C","type":"text","text":{"body":"other number"}}]}}
            ]}]}
            """u8.ToArray();

        var m = Assert.Single(adapter.Parse(body));
        Assert.Equal(("whatsapp", "905321112233", "wamid.A", "Ahmet", "+905321112233"), (m.Channel, m.ExternalUserId, m.ExternalMessageId, m.DisplayName, m.Phone));
    }
}

public class ConversationRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("telegram", 1000, false, true, false)]
    [InlineData("whatsapp", 23, false, true, false)]
    [InlineData("whatsapp", 25, true, false, false)] // WhatsApp needs a template after 24 h, even for staff.
    [InlineData("messenger", 25, false, false, false)] // The assistant may not use the human-agent tag.
    [InlineData("messenger", 25, true, true, true)]
    [InlineData("instagram", 24 * 8, true, false, false)]
    public void Reply_windows_follow_each_platform(string channel, int hoursSinceCustomer, bool byStaff, bool open, bool tag)
    {
        var w = ReplyWindow.For(channel, Now.AddHours(-hoursSinceCustomer), Now, byStaff);
        Assert.Equal((open, tag), (w.Open, w.NeedsHumanAgentTag));
    }

    [Fact]
    public void Long_replies_split_on_boundaries_within_the_limit()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Paragraph {i}: " + new string('x', 80)));
        var parts = MessageText.Split(text, 1000);

        Assert.All(parts, p => Assert.True(p.Length <= 1000));
        Assert.Equal(text.Replace("\n", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal),
            string.Concat(parts).Replace("\n", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.All(parts, p => Assert.StartsWith("Paragraph", p, StringComparison.Ordinal));
    }

    [Fact]
    public void History_joins_turns_hides_notes_and_needs_a_customer_message_last()
    {
        ChatMessage M(string dir, string author, string body) => new() { Direction = dir, Author = author, Body = body, Status = dir == "in" ? "received" : "sent" };

        var turns = ChatHistory.Build(
        [
            M("out", "agent", "stray greeting"),
            M("in", "customer", "hi"),
            M("in", "customer", "honey?"),
            M("out", "system", "Taken over by staff"),
            M("out", "staff", "Yes, we have."),
            M("in", "customer", "price?"),
        ]);

        Assert.Equal(3, turns.Count);
        Assert.Equal(("hi\nhoney?", true), (turns[0].Text, turns[0].FromCustomer));
        Assert.Contains("Rahiq team member", turns[1].Text, StringComparison.Ordinal);
        Assert.True(turns[2].FromCustomer);

        Assert.Empty(ChatHistory.Build([M("in", "customer", "hi"), M("out", "agent", "Hello!")]));
    }

    [Theory]
    [InlineData("ayse@example.com", null, true)]
    [InlineData("AYSE@example.com", null, true)]
    [InlineData("someone@example.com", null, false)]
    [InlineData("0532 111 22 33", null, true)]
    [InlineData("+90 (532) 111-2233", null, true)]
    [InlineData("2233", null, false)] // Last digits alone are not proof.
    [InlineData(null, "+905321112233", true)] // The channel verified the phone (WhatsApp).
    [InlineData(null, "+905329999999", false)]
    [InlineData(null, null, false)]
    public void Order_details_need_matching_proof(string? proof, string? verifiedPhone, bool expected) =>
        Assert.Equal(expected, OrderVerification.Matches("ayse@example.com", "+90 532 111 22 33", proof, verifiedPhone));

    [Fact]
    public void Server_side_fallback_is_sent_as_the_default_mode()
    {
        BetaFallbacksParam fallbacks = new Default();
        Assert.Equal("\"default\"", JsonSerializer.Serialize(fallbacks));
    }
}

public class CartHandoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_handoff_code_works_once_and_rotates_the_cart_token()
    {
        var (cart, originalToken) = ShoppingCart.Create(null, Now);
        var code = cart.StartHandoff(Now, TimeSpan.FromHours(24));

        Assert.Equal(ShoppingCart.HashToken(code), cart.HandoffCodeHash);
        Assert.True(cart.CanClaimHandoff(Now.AddHours(23)));
        Assert.False(cart.CanClaimHandoff(Now.AddHours(25)));

        var token = cart.ClaimHandoff(Now.AddMinutes(5));
        Assert.NotEqual(ShoppingCart.HashToken(originalToken), cart.TokenHash);
        Assert.Equal(ShoppingCart.HashToken(token), cart.TokenHash);
        Assert.False(cart.CanClaimHandoff(Now.AddMinutes(6)));
    }
}
