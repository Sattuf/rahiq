using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Rahiq.Modules.Conversations.Application;
using Rahiq.Modules.Conversations.Contracts;

namespace Rahiq.Modules.Conversations.Infrastructure.Channels;

/// <summary>
/// Telegram Bot API. Telegram does not sign bodies; instead setWebhook registers a secret that Telegram sends back in
/// X-Telegram-Bot-Api-Secret-Token on every call. Only private chats are answered (never groups the bot was added to).
/// </summary>
internal sealed class TelegramAdapter(IHttpClientFactory http, IOptions<ChannelOptions> options) : IChannelAdapter
{
    public const string HttpClientName = "telegram";

    private TelegramOptions O => options.Value.Telegram;

    public string Webhook => "telegram";

    public IReadOnlyList<string> Channels { get; } = [Contracts.Channels.Telegram];

    public bool IsConfigured => !string.IsNullOrEmpty(O.BotToken) && O.WebhookSecret is { Length: >= 16 };

    public bool VerifySignature(IReadOnlyDictionary<string, string> headers, byte[] body) =>
        WebhookSignatures.SecretEquals(headers.GetValueOrDefault("x-telegram-bot-api-secret-token"), O.WebhookSecret);

    public string? VerifySubscription(IReadOnlyDictionary<string, string> query) => null;

    public IReadOnlyList<InboundMessage> Parse(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("message", out var m)
            || !m.TryGetProperty("chat", out var chat) || chat.Str("type") != "private"
            || !m.TryGetProperty("from", out var from) || from.Bool("is_bot"))
        {
            return []; // Edits, channel posts, group messages, bots: nothing to answer.
        }

        var chatId = chat.GetProperty("id").GetRawText();
        var name = string.Join(' ', new[] { from.Str("first_name"), from.Str("last_name") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var (kind, text) = Content(m);
        string? phone = null;

        // A contact the user shared about themselves (Telegram's "share my phone number" button) is a verified number.
        if (m.TryGetProperty("contact", out var contact) && contact.TryGetProperty("user_id", out var owner) && owner.GetRawText() == from.GetProperty("id").GetRawText())
        {
            phone = contact.Str("phone_number");
        }

        return
        [
            new InboundMessage(
                Contracts.Channels.Telegram, chatId, $"{chatId}:{m.GetProperty("message_id").GetRawText()}", kind, text,
                string.IsNullOrWhiteSpace(name) ? from.Str("username") : name, phone, from.Str("language_code")),
        ];
    }

    public async Task<SendOutcome> SendAsync(OutboundText message, CancellationToken cancellationToken)
    {
        using var client = http.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(
            new Uri($"https://api.telegram.org/bot{O.BotToken}/sendMessage"),
            new { chat_id = message.ExternalUserId, text = message.Text },
            cancellationToken);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (response.IsSuccessStatusCode && json.Bool("ok"))
        {
            return SendOutcome.Sent(json.GetProperty("result").GetProperty("message_id").GetRawText());
        }

        // 403: the user blocked the bot. 400: chat not found. 429 and 5xx: try again later.
        var permanent = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest;
        return SendOutcome.Failed($"telegram {(int)response.StatusCode}: {json.Str("description")}", permanent);
    }

    private static (string Kind, string Text) Content(JsonElement m)
    {
        if (m.Str("text") is { } text)
        {
            return ("text", text);
        }

        var caption = m.Str("caption");
        foreach (var kind in new[] { "photo", "voice", "audio", "video", "video_note", "document", "sticker", "location", "contact" })
        {
            if (m.TryGetProperty(kind, out _))
            {
                return (kind, caption is null ? $"[{kind}]" : $"[{kind}] {caption}");
            }
        }

        return ("unknown", "[unsupported message]");
    }
}

internal static class JsonElementExtensions
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Items(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
}
