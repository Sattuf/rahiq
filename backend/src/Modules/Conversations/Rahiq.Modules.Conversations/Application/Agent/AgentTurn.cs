using System.Globalization;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Conversations.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Conversations.Application.Agent;

public sealed record AgentOptions
{
    public const string Section = "Agent";

    /// <summary>Off by default: without an API key every new conversation goes straight to a person.</summary>
    public bool Enabled { get; init; }

    /// <summary>Anthropic API key. Keep it in the secret store (Agent__ApiKey or ANTHROPIC_API_KEY), never in appsettings.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; init; } = "claude-opus-5";

    /// <summary>low | medium | high. Customer chat is short and tool-driven; "low" is fast and cheap. Raise only if quality needs it.</summary>
    public string Effort { get; init; } = "low";

    public int MaxTokens { get; init; } = 4096;

    /// <summary>Tool round-trips per turn before the assistant must answer (stops loops and runaway cost).</summary>
    public int MaxToolRounds { get; init; } = 6;

    /// <summary>Messages of history sent with each turn.</summary>
    public int HistoryMessages { get; init; } = 40;

    /// <summary>Replies per conversation per hour; above it the conversation goes to a person (spam and cost guard).</summary>
    public int MaxRepliesPerHour { get; init; } = 30;

    /// <summary>Conversations answered at the same time by this instance.</summary>
    public int MaxParallel { get; init; } = 4;

    /// <summary>Wait this long after the customer's last message, so "hi" + "I want honey" get one answer.</summary>
    public int DebounceSeconds { get; init; } = 2;

    public string DefaultLanguage { get; init; } = "tr";
}

internal sealed record AgentTurnResult(string? Reply, bool HandedOff);

/// <summary>
/// One answer to a conversation: the stored thread goes to Claude with our tools, tool calls are executed by
/// <see cref="AgentTools"/>, and the loop ends with a text reply (or a hand-off). The model never touches the database.
/// </summary>
internal sealed partial class AgentTurn(
    RahiqDbContext db,
    AgentTools tools,
    AnthropicClient client,
    IOptions<AgentOptions> options,
    IOptions<StoreOptions> store,
    IClock clock,
    ILogger<AgentTurn> logger)
{
    private const string FallbackReply = "Sorry, I could not answer that. A colleague will reply here soon.";

    public async Task<AgentTurnResult> RunAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var conversation = await db.Set<Conversation>().AsNoTracking().FirstAsync(c => c.Id == conversationId, cancellationToken);
        var contact = await db.Set<Contact>().AsNoTracking().FirstAsync(c => c.Id == conversation.ContactId, cancellationToken);
        var recent = await db.Set<ChatMessage>().AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt).Take(o.HistoryMessages)
            .ToListAsync(cancellationToken);

        var turns = ChatHistory.Build(recent.OrderBy(m => m.CreatedAt));
        if (turns.Count == 0)
        {
            return new AgentTurnResult(null, false); // Nothing new from the customer.
        }

        var context = new AgentContext(conversationId, conversation.Channel, contact.DisplayName, contact.Phone, contact.Locale);
        List<BetaMessageParam> messages = [.. turns.Select(t => new BetaMessageParam { Role = t.FromCustomer ? Role.User : Role.Assistant, Content = t.Text })];
        var handedOff = false;

        for (var round = 0; round <= o.MaxToolRounds; round++)
        {
            var response = await client.Beta.Messages.Create(Request(context, messages, allowTools: round < o.MaxToolRounds), cancellationToken);
            LogUsage(logger, conversationId, response.Usage.InputTokens, response.Usage.CacheReadInputTokens ?? 0, response.Usage.OutputTokens);

            if (response.StopReason == "refusal")
            {
                await tools.RunAsync(context, AgentTools.HandoffToHuman, JsonSerializer.SerializeToElement(new { reason = HandoffReasons.Refusal, summary = "The assistant declined to answer." }), cancellationToken);
                return new AgentTurnResult(FallbackReply, true);
            }

            List<BetaContentBlockParam> assistant = [];
            List<BetaContentBlockParam> results = [];
            var text = new List<string>();

            foreach (var block in response.Content)
            {
                if (block.TryPickText(out BetaTextBlock? t))
                {
                    assistant.Add(new BetaTextBlockParam { Text = t.Text });
                    text.Add(t.Text);
                }
                else if (block.TryPickThinking(out BetaThinkingBlock? thinking))
                {
                    assistant.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out BetaRedactedThinkingBlock? redacted))
                {
                    assistant.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out BetaToolUseBlock? use))
                {
                    assistant.Add(new BetaToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                    var result = await tools.RunAsync(context, use.Name, JsonSerializer.SerializeToElement(use.Input), cancellationToken);
                    handedOff |= result.HandedOff;
                    results.Add(new BetaToolResultBlockParam { ToolUseID = use.ID, Content = result.Json, IsError = result.IsError });
                }
            }

            if (results.Count == 0)
            {
                var reply = string.Join("\n\n", text.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
                return new AgentTurnResult(reply.Length == 0 ? null : reply, handedOff);
            }

            messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistant });
            messages.Add(new BetaMessageParam { Role = Role.User, Content = results });
        }

        // Out of tool rounds without an answer: a person takes it from here.
        await tools.RunAsync(context, AgentTools.HandoffToHuman, JsonSerializer.SerializeToElement(new { reason = "cannot_help", summary = "The assistant ran out of steps." }), cancellationToken);
        return new AgentTurnResult(FallbackReply, true);
    }

    private MessageCreateParams Request(AgentContext context, List<BetaMessageParam> messages, bool allowTools)
    {
        var o = options.Value;
        return new MessageCreateParams
        {
            Model = o.Model,
            MaxTokens = o.MaxTokens,
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],

            // If the model declines a request on policy grounds, the API retries it on a fallback model it chooses.
            Fallbacks = new Default(),
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig { Effort = EffortLevel(o.Effort) },

            // Stable first (cached), per-conversation facts after the cache breakpoint.
            System = new List<BetaTextBlockParam>
            {
                new() { Text = AgentPrompt.System, CacheControl = new BetaCacheControlEphemeral() },
                new() { Text = Facts(context) },
            },
            Tools = AgentToolDefinitions.All,
            ToolChoice = allowTools ? new BetaToolChoiceAuto() : new BetaToolChoiceNone(),
            Messages = messages,
        };
    }

    private string Facts(AgentContext c) => $"""
        Conversation facts:
        - Channel: {c.Channel}
        - Customer name on the channel: {c.CustomerName ?? "unknown"}
        - Customer's phone verified by the channel: {(c.VerifiedPhone is null ? "no" : "yes (order lookups by this phone need no extra proof)")}
        - Language hint from the channel: {c.Locale ?? "none"} (always answer in the language the customer writes in)
        - Today: {clock.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}
        - Store website: {store.Value.PublicWebUrl}
        """;

    private static Effort EffortLevel(string value) => value switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        _ => Effort.Low,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent call for {ConversationId}: {Input} input ({Cached} cached), {Output} output tokens")]
    private static partial void LogUsage(ILogger logger, Guid conversationId, long input, long cached, long output);
}
