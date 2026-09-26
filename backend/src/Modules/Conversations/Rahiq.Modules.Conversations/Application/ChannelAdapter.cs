namespace Rahiq.Modules.Conversations.Application;

/// <summary>A customer message in the one shape the rest of the system understands, whatever platform it came from.</summary>
/// <param name="ExternalUserId">The platform's id for the person (Telegram chat id, Meta PSID/IGSID, WhatsApp number).</param>
/// <param name="ExternalMessageId">The platform's message id: a retried webhook carries the same one and is stored once.</param>
/// <param name="Kind">text, image, audio, video, document, sticker, location, contact...</param>
/// <param name="Phone">Only when the platform vouches for it (WhatsApp sender, Telegram shared own contact).</param>
public sealed record InboundMessage(
    string Channel,
    string ExternalUserId,
    string ExternalMessageId,
    string Kind,
    string Text,
    string? DisplayName,
    string? Phone,
    string? LanguageHint);

public sealed record OutboundText(string Channel, string ExternalUserId, string Text, bool HumanAgentTag);

/// <param name="Permanent">Retrying cannot help (user blocked the bot, 24-hour window closed, bad recipient).</param>
public sealed record SendOutcome(bool Ok, string? ExternalId, string? Error, bool Permanent)
{
    public static SendOutcome Sent(string? id) => new(true, id, null, false);

    public static SendOutcome Failed(string error, bool permanent) => new(false, null, error, permanent);
}

/// <summary>
/// One per platform webhook. It checks that a request really comes from the platform, turns its payload into
/// <see cref="InboundMessage"/>s, and sends replies back. Everything platform-specific stays inside the adapter.
/// </summary>
public interface IChannelAdapter
{
    /// <summary>The route segment: /webhooks/{Webhook}.</summary>
    string Webhook { get; }

    /// <summary>The channels this adapter sends on (the Meta adapter serves Messenger and Instagram).</summary>
    IReadOnlyList<string> Channels { get; }

    /// <summary>False when its secrets are not configured: the webhook then answers 404 and nothing is sent.</summary>
    bool IsConfigured { get; }

    /// <summary>Checks the signature over the exact raw bytes received. Headers have lower-case names.</summary>
    bool VerifySignature(IReadOnlyDictionary<string, string> headers, byte[] body);

    /// <summary>The GET handshake some platforms use when a webhook is registered. Null means "refuse".</summary>
    string? VerifySubscription(IReadOnlyDictionary<string, string> query);

    IReadOnlyList<InboundMessage> Parse(byte[] body);

    Task<SendOutcome> SendAsync(OutboundText message, CancellationToken cancellationToken);
}
