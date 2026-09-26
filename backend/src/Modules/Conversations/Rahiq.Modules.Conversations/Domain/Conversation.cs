using Rahiq.Modules.Conversations.Contracts;

namespace Rahiq.Modules.Conversations.Domain;

internal static class ConversationModes
{
    /// <summary>The AI assistant answers.</summary>
    public const string Agent = "agent";

    /// <summary>A person answers; the assistant stays silent.</summary>
    public const string Human = "human";
}

internal static class MessageAuthors
{
    public const string Customer = "customer";
    public const string Agent = "agent";
    public const string Staff = "staff";

    /// <summary>Internal notes (take-over, hand-off summary). Never sent to the customer.</summary>
    public const string System = "system";
}

internal static class MessageStatuses
{
    public const string Received = "received";
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Note = "note";
}

internal static class HandoffReasons
{
    public const string AgentDisabled = "agent_disabled";
    public const string AgentUnavailable = "agent_unavailable";
    public const string ReplyLimit = "reply_limit";
    public const string Refusal = "agent_refusal";

    /// <summary>What the assistant may give as its reason (the tool's enum).</summary>
    public static readonly IReadOnlyList<string> AgentChoices =
        ["customer_request", "complaint", "order_problem", "payment_problem", "return_request", "wholesale", "cannot_help", "other"];
}

internal sealed class Contact
{
    public Guid Id { get; set; }

    public string Channel { get; set; } = string.Empty;

    public string ExternalId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>Known to belong to the person (WhatsApp number, or a contact shared on Telegram), so it may verify an order.</summary>
    public string? Phone { get; set; }

    public string? Locale { get; set; }

    public Guid? CustomerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? ErasedAt { get; set; }
}

/// <summary>
/// A thread with one contact. State that races (webhooks, the agent worker, staff clicking "take over") is changed
/// with single SQL statements in the handlers; this type is the read model.
/// </summary>
internal sealed class Conversation
{
    public Guid Id { get; set; }

    public Guid ContactId { get; set; }

    public string Channel { get; set; } = string.Empty;

    public string Status { get; set; } = "open";

    public string Mode { get; set; } = ConversationModes.Agent;

    public Guid? AssignedStaffId { get; set; }

    public string? HandoffReason { get; set; }

    public DateTimeOffset? HandoffAt { get; set; }

    public int Unread { get; set; }

    public DateTimeOffset LastMessageAt { get; set; }

    public DateTimeOffset? LastInboundAt { get; set; }

    public string? LastPreview { get; set; }

    public long AgentPendingSeq { get; set; }

    public long AgentDoneSeq { get; set; }

    public DateTimeOffset? AgentLeaseUntil { get; set; }

    public int AgentAttempts { get; set; }

    public DateTimeOffset? AgentRetryAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ChatMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public string Channel { get; set; } = string.Empty;

    /// <summary>"in" from the customer, "out" to the customer (or an internal note).</summary>
    public string Direction { get; set; } = "in";

    public string Author { get; set; } = MessageAuthors.Customer;

    public Guid? StaffId { get; set; }

    public string Kind { get; set; } = "text";

    public string Body { get; set; } = string.Empty;

    public string? ExternalId { get; set; }

    public string Status { get; set; } = MessageStatuses.Received;

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>When a business may still write to a customer (platform policies, not our choice).</summary>
internal sealed record ReplyWindowState(bool Open, DateTimeOffset? ClosesAt, bool NeedsHumanAgentTag);

internal static class ReplyWindow
{
    /// <summary>WhatsApp, Messenger and Instagram: free-form replies only within 24 hours of the customer's last message.</summary>
    public static readonly TimeSpan Standard = TimeSpan.FromHours(24);

    /// <summary>Messenger and Instagram allow a person (never a bot) to answer for 7 days with the HUMAN_AGENT tag.</summary>
    public static readonly TimeSpan HumanAgent = TimeSpan.FromDays(7);

    public static ReplyWindowState For(string channel, DateTimeOffset? lastInbound, DateTimeOffset now, bool byStaff)
    {
        if (channel == Channels.Telegram)
        {
            return new ReplyWindowState(true, null, false);
        }

        if (lastInbound is not { } last)
        {
            return new ReplyWindowState(false, null, false);
        }

        if (now - last < Standard)
        {
            return new ReplyWindowState(true, last + Standard, false);
        }

        // Outside 24 hours WhatsApp needs a pre-approved template (not implemented): the customer must write first.
        if (byStaff && channel is Channels.Messenger or Channels.Instagram && now - last < HumanAgent)
        {
            return new ReplyWindowState(true, last + HumanAgent, true);
        }

        return new ReplyWindowState(false, last + Standard, false);
    }
}

internal static class MessageText
{
    /// <summary>What a customer's message may be (longer text is cut, the rest is not needed to answer).</summary>
    public const int MaxInbound = 4000;

    /// <summary>Longest single message each platform accepts.</summary>
    public static int MaxOutbound(string channel) => channel switch
    {
        Channels.Instagram => 1000,
        Channels.Messenger => 2000,
        _ => 4096,
    };

    /// <summary>Splits a long reply at paragraph, line or word boundaries so each part fits the platform.</summary>
    public static IReadOnlyList<string> Split(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = new List<string>();
        var rest = text.Trim();
        while (rest.Length > max)
        {
            var window = rest[..max];
            var cut = window.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (cut < max / 2)
            {
                cut = window.LastIndexOf('\n');
            }

            if (cut < max / 2)
            {
                cut = window.LastIndexOf(' ');
            }

            if (cut < max / 2)
            {
                cut = max;
            }

            parts.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0)
        {
            parts.Add(rest);
        }

        return parts;
    }

    public static string Preview(string body) => body.Length <= 140 ? body : string.Concat(body.AsSpan(0, 139), "…");
}

internal sealed record ChatTurn(bool FromCustomer, string Text);

/// <summary>
/// Turns the stored thread into alternating turns for the model: the customer speaks as "user", the assistant and staff
/// as "assistant". Internal notes are never shown. Consecutive messages from one side are joined.
/// </summary>
internal static class ChatHistory
{
    public static IReadOnlyList<ChatTurn> Build(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var turns = new List<ChatTurn>();
        foreach (var m in messages.Where(m => m.Author != MessageAuthors.System && m.Status != MessageStatuses.Failed))
        {
            var fromCustomer = m.Direction == "in";
            var text = m.Author == MessageAuthors.Staff ? $"[Reply written by a Rahiq team member] {m.Body}" : m.Body;
            if (turns.Count > 0 && turns[^1].FromCustomer == fromCustomer)
            {
                turns[^1] = turns[^1] with { Text = $"{turns[^1].Text}\n{text}" };
            }
            else
            {
                turns.Add(new ChatTurn(fromCustomer, text));
            }
        }

        // The model's conversation must start with the customer, and there is nothing to answer if it ends with us.
        while (turns.Count > 0 && !turns[0].FromCustomer)
        {
            turns.RemoveAt(0);
        }

        return turns.Count > 0 && turns[^1].FromCustomer ? turns : [];
    }
}
