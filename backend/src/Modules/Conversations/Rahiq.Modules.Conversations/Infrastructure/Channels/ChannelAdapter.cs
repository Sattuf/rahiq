using System.Security.Cryptography;
using System.Text;

namespace Rahiq.Modules.Conversations.Infrastructure.Channels;

public sealed record ChannelOptions
{
    public const string Section = "Channels";

    public TelegramOptions Telegram { get; init; } = new();

    public MetaOptions Meta { get; init; } = new();

    public WhatsAppOptions WhatsApp { get; init; } = new();

    /// <summary>Graph API version for Messenger, Instagram and WhatsApp Cloud API.</summary>
    public string GraphApiVersion { get; init; } = "v23.0";
}

public sealed record TelegramOptions
{
    public string? BotToken { get; init; }

    /// <summary>Sent by Telegram in X-Telegram-Bot-Api-Secret-Token (set with setWebhook). 32+ random characters.</summary>
    public string? WebhookSecret { get; init; }
}

public sealed record MetaOptions
{
    /// <summary>The Meta app secret: signs every webhook (X-Hub-Signature-256).</summary>
    public string? AppSecret { get; init; }

    /// <summary>Our own random string, typed into the Meta dashboard, echoed in the GET handshake.</summary>
    public string? VerifyToken { get; init; }

    public string? PageAccessToken { get; init; }

    /// <summary>Instagram messaging token; falls back to the page token (Instagram accounts linked to the page).</summary>
    public string? InstagramAccessToken { get; init; }
}

public sealed record WhatsAppOptions
{
    /// <summary>Usually the same Meta app as Messenger; can differ when WhatsApp lives in its own app.</summary>
    public string? AppSecret { get; init; }

    public string? VerifyToken { get; init; }

    public string? AccessToken { get; init; }

    public string? PhoneNumberId { get; init; }
}

internal static class WebhookSignatures
{
    /// <summary>
    /// Meta's X-Hub-Signature-256: "sha256=" + hex(HMAC-SHA256(app secret, raw body)), compared in constant time.
    /// </summary>
    public static bool MetaSha256(string? header, byte[] body, string? appSecret)
    {
        if (string.IsNullOrEmpty(appSecret) || header is null || !header.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(header.AsSpan(7));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>Shared-secret headers (Telegram, the Meta verify token), compared in constant time.</summary>
    public static bool SecretEquals(string? provided, string? expected) =>
        !string.IsNullOrEmpty(expected) && provided is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));

    /// <summary>The GET handshake shared by Messenger, Instagram and WhatsApp.</summary>
    public static string? MetaHandshake(IReadOnlyDictionary<string, string> query, string? verifyToken) =>
        query.GetValueOrDefault("hub.mode") == "subscribe" && SecretEquals(query.GetValueOrDefault("hub.verify_token"), verifyToken)
            ? query.GetValueOrDefault("hub.challenge")
            : null;
}
