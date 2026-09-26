using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Rahiq.Modules.Conversations.Application;

namespace Rahiq.Modules.Conversations.Infrastructure.Channels;

/// <summary>
/// Messenger and Instagram Direct share one webhook (/webhooks/meta): the payload's "object" says which.
/// Every call is signed with the app secret (X-Hub-Signature-256). Echoes of our own messages are ignored.
/// </summary>
internal sealed class MetaAdapter(IHttpClientFactory http, IOptions<ChannelOptions> options) : IChannelAdapter
{
    public const string HttpClientName = "graph";

    private MetaOptions O => options.Value.Meta;

    public string Webhook => "meta";

    public IReadOnlyList<string> Channels { get; } = [Contracts.Channels.Messenger, Contracts.Channels.Instagram];

    public bool IsConfigured => !string.IsNullOrEmpty(O.AppSecret) && !string.IsNullOrEmpty(O.PageAccessToken);

    public bool VerifySignature(IReadOnlyDictionary<string, string> headers, byte[] body) =>
        WebhookSignatures.MetaSha256(headers.GetValueOrDefault("x-hub-signature-256"), body, O.AppSecret);

    public string? VerifySubscription(IReadOnlyDictionary<string, string> query) => WebhookSignatures.MetaHandshake(query, O.VerifyToken);

    public IReadOnlyList<InboundMessage> Parse(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var channel = root.Str("object") switch
        {
            "page" => Contracts.Channels.Messenger,
            "instagram" => Contracts.Channels.Instagram,
            _ => null,
        };
        if (channel is null)
        {
            return [];
        }

        var messages = new List<InboundMessage>();
        foreach (var entry in root.Items("entry"))
        {
            var ownId = entry.Str("id");
            foreach (var e in entry.Items("messaging"))
            {
                var sender = e.TryGetProperty("sender", out var s) ? s.Str("id") : null;
                if (sender is null || sender == ownId)
                {
                    continue;
                }

                if (e.TryGetProperty("message", out var m) && !m.Bool("is_echo") && m.Str("mid") is { } mid)
                {
                    var (kind, text) = Content(m);
                    messages.Add(new InboundMessage(channel, sender, mid, kind, text, null, null, null));
                }
                else if (e.TryGetProperty("postback", out var p) && p.Str("mid") is { } postbackId)
                {
                    messages.Add(new InboundMessage(channel, sender, postbackId, "postback", p.Str("title") ?? p.Str("payload") ?? "[button]", null, null, null));
                }
            }
        }

        return messages;
    }

    public async Task<SendOutcome> SendAsync(OutboundText message, CancellationToken cancellationToken)
    {
        var token = message.Channel == Contracts.Channels.Instagram ? O.InstagramAccessToken ?? O.PageAccessToken : O.PageAccessToken;
        object payload = message.HumanAgentTag
            ? new { recipient = new { id = message.ExternalUserId }, messaging_type = "MESSAGE_TAG", tag = "HUMAN_AGENT", message = new { text = message.Text } }
            : new { recipient = new { id = message.ExternalUserId }, messaging_type = "RESPONSE", message = new { text = message.Text } };

        return await Graph.PostAsync(http, new Uri($"https://graph.facebook.com/{options.Value.GraphApiVersion}/me/messages"), token!, payload,
            json => json.Str("message_id"), cancellationToken);
    }

    private static (string Kind, string Text) Content(JsonElement m)
    {
        if (m.Str("text") is { } text)
        {
            return ("text", text);
        }

        var kind = m.Items("attachments").Select(a => a.Str("type")).FirstOrDefault(t => t is not null) ?? "unknown";
        return (kind, $"[{kind}]");
    }
}

/// <summary>
/// WhatsApp Cloud API (/webhooks/whatsapp). Signed like the rest of Meta. Only messages to our own number are taken;
/// delivery and read statuses are ignored. Replies are allowed only within 24 hours of the customer's last message.
/// </summary>
internal sealed class WhatsAppAdapter(IHttpClientFactory http, IOptions<ChannelOptions> options) : IChannelAdapter
{
    private WhatsAppOptions O => options.Value.WhatsApp;

    public string Webhook => "whatsapp";

    public IReadOnlyList<string> Channels { get; } = [Contracts.Channels.WhatsApp];

    public bool IsConfigured => !string.IsNullOrEmpty(O.AppSecret) && !string.IsNullOrEmpty(O.AccessToken) && !string.IsNullOrEmpty(O.PhoneNumberId);

    public bool VerifySignature(IReadOnlyDictionary<string, string> headers, byte[] body) =>
        WebhookSignatures.MetaSha256(headers.GetValueOrDefault("x-hub-signature-256"), body, O.AppSecret);

    public string? VerifySubscription(IReadOnlyDictionary<string, string> query) => WebhookSignatures.MetaHandshake(query, O.VerifyToken);

    public IReadOnlyList<InboundMessage> Parse(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.Str("object") != "whatsapp_business_account")
        {
            return [];
        }

        var messages = new List<InboundMessage>();
        foreach (var change in doc.RootElement.Items("entry").SelectMany(e => e.Items("changes")))
        {
            if (change.Str("field") != "messages" || !change.TryGetProperty("value", out var value)
                || !value.TryGetProperty("metadata", out var metadata) || metadata.Str("phone_number_id") != O.PhoneNumberId)
            {
                continue;
            }

            var names = value.Items("contacts").ToDictionary(c => c.Str("wa_id") ?? string.Empty, c => c.TryGetProperty("profile", out var p) ? p.Str("name") : null);
            foreach (var m in value.Items("messages"))
            {
                if (m.Str("from") is not { } from || m.Str("id") is not { } id)
                {
                    continue;
                }

                var (kind, text) = Content(m);
                messages.Add(new InboundMessage(Contracts.Channels.WhatsApp, from, id, kind, text, names.GetValueOrDefault(from), "+" + from, null));
            }
        }

        return messages;
    }

    public async Task<SendOutcome> SendAsync(OutboundText message, CancellationToken cancellationToken) =>
        await Graph.PostAsync(
            http,
            new Uri($"https://graph.facebook.com/{options.Value.GraphApiVersion}/{O.PhoneNumberId}/messages"),
            O.AccessToken!,
            new { messaging_product = "whatsapp", recipient_type = "individual", to = message.ExternalUserId, type = "text", text = new { preview_url = true, body = message.Text } },
            json => json.Items("messages").Select(x => x.Str("id")).FirstOrDefault(),
            cancellationToken);

    private static (string Kind, string Text) Content(JsonElement m)
    {
        var type = m.Str("type") ?? "unknown";
        var text = type switch
        {
            "text" => m.TryGetProperty("text", out var t) ? t.Str("body") : null,
            "button" => m.TryGetProperty("button", out var b) ? b.Str("text") : null,
            "interactive" => m.TryGetProperty("interactive", out var i)
                ? (i.TryGetProperty("button_reply", out var br) ? br.Str("title") : i.TryGetProperty("list_reply", out var lr) ? lr.Str("title") : null)
                : null,
            _ => m.TryGetProperty(type, out var media) && media.Str("caption") is { } caption ? $"[{type}] {caption}" : $"[{type}]",
        };
        return (type, text ?? $"[{type}]");
    }
}

/// <summary>Graph API POST with the token in the Authorization header (never in the URL, where it would be logged).</summary>
internal static class Graph
{
    public static async Task<SendOutcome> PostAsync(IHttpClientFactory http, Uri uri, string token, object payload, Func<JsonElement, string?> readId, CancellationToken cancellationToken)
    {
        using var client = http.CreateClient(MetaAdapter.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return SendOutcome.Sent(readId(json));
        }

        var error = json.TryGetProperty("error", out var e) ? e : default;
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) ? c.GetRawText() : "?";

        // 429, 5xx and Meta's throttling codes (4, 80007, 130429) are temporary; other 4xx will fail again the same way.
        var temporary = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500 || code is "4" or "80007" or "130429";
        return SendOutcome.Failed($"graph {(int)response.StatusCode} code {code}: {error.Str("message")}", !temporary);
    }
}
