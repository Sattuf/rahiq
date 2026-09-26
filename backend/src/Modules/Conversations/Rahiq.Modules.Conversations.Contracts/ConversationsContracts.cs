using Rahiq.SharedKernel;

namespace Rahiq.Modules.Conversations.Contracts;

/// <summary>The messaging channels a conversation can live on. Instagram and Messenger share the Meta webhook.</summary>
public static class Channels
{
    public const string Telegram = "telegram";
    public const string Messenger = "messenger";
    public const string Instagram = "instagram";
    public const string WhatsApp = "whatsapp";

    public static readonly IReadOnlyList<string> All = [Telegram, Messenger, Instagram, WhatsApp];
}

/// <summary>The assistant (or a failure) passed a conversation to a person.</summary>
public sealed record ConversationHandedOff(Guid ConversationId, string Channel, string Reason, string? Summary) : DomainEvent;
