using Dapper;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Modules.Cart.Contracts;
using Rahiq.Modules.Conversations.Contracts;
using Rahiq.Modules.Conversations.Domain;
using Rahiq.Modules.Notifications.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Conversations.Application.Agent;

/// <summary>The assistant (or the worker on its behalf) hands a conversation to a person.</summary>
internal sealed record HandOffCommand(Guid ConversationId, string Reason, string? Summary) : ICommand;

/// <summary>
/// Ends an agent turn: queues the reply unless a person took the conversation over meanwhile, and marks the
/// customer messages up to <paramref name="Seq"/> as answered.
/// </summary>
/// <returns>Whether the reply was queued for sending.</returns>
internal sealed record CompleteAgentTurnCommand(Guid ConversationId, long Seq, string? Reply) : ICommand<Result<bool>>;

/// <returns>Whether the conversation was handed to a person after too many failures.</returns>
internal sealed record FailAgentTurnCommand(Guid ConversationId, string Error) : ICommand<Result<bool>>;

internal sealed record CheckoutLink(string Url, DateTimeOffset ExpiresAt, int Lines, IReadOnlyList<Guid> Skipped);

internal sealed record CreateCheckoutLinkCommand(Guid ConversationId, IReadOnlyList<HandoffItem> Items, string Locale) : ICommand<Result<CheckoutLink>>;

internal sealed class AgentStateHandlers(IDbSession session, ICartAccess carts, IEventPublisher events, IOptions<StoreOptions> store)
    : IRequestHandler<HandOffCommand, Result>,
      IRequestHandler<CompleteAgentTurnCommand, Result<bool>>,
      IRequestHandler<FailAgentTurnCommand, Result<bool>>,
      IRequestHandler<CreateCheckoutLinkCommand, Result<CheckoutLink>>
{
    private const int MaxFailures = 4;

    /// <summary>How long a chat checkout link works. Long enough to finish later today, short enough to go stale safely.</summary>
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);

    public async Task<Result> Handle(HandOffCommand request, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var channel = await session.Connection.ExecuteScalarAsync<string?>(new CommandDefinition("""
            UPDATE crm.conversations SET mode = 'human', handoff_reason = @Reason, handoff_at = now(), agent_done_seq = agent_pending_seq,
              agent_lease_until = NULL, agent_attempts = 0, agent_retry_at = NULL
            WHERE id = @ConversationId AND mode = 'agent'
            RETURNING channel
            """, request, session.Transaction, cancellationToken: cancellationToken));
        if (channel is null)
        {
            return Result.Success(); // Already with a person.
        }

        await session.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO crm.messages (id, conversation_id, channel, direction, author, kind, body, status)
            VALUES (@Id, @ConversationId, @channel, 'out', 'system', 'note', @body, 'note')
            """, new { Id = Ids.New(), request.ConversationId, channel, body = $"Handed to a person ({request.Reason}). {request.Summary}".Trim() },
            session.Transaction, cancellationToken: cancellationToken));

        events.Publish(new ConversationHandedOff(request.ConversationId, channel, request.Reason, request.Summary));
        events.Publish(new StaffAlertRaised(
            AlertKinds.ConversationHandoff,
            $"A {channel} customer is waiting for a person ({request.Reason})",
            request.Summary ?? "Open the conversations inbox.",
            request.ConversationId.ToString()));
        return Result.Success();
    }

    public async Task<Result<bool>> Handle(CompleteAgentTurnCommand request, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var state = await session.Connection.QuerySingleOrDefaultAsync<(string Channel, string Status, Guid? AssignedStaffId)>(new CommandDefinition(
            "SELECT channel, status, assigned_staff_id FROM crm.conversations WHERE id = @ConversationId FOR UPDATE",
            request, session.Transaction, cancellationToken: cancellationToken));
        if (state.Channel is null)
        {
            return false;
        }

        // A person who took over (or closed the thread) while the model was thinking has the last word.
        var queue = !string.IsNullOrWhiteSpace(request.Reply) && state.AssignedStaffId is null && state.Status == "open";
        if (queue)
        {
            var body = request.Reply!.Trim();
            await session.Connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO crm.messages (id, conversation_id, channel, direction, author, body, status)
                VALUES (@Id, @ConversationId, @Channel, 'out', 'agent', @body, 'pending');
                UPDATE crm.conversations SET last_message_at = now(), last_preview = @preview WHERE id = @ConversationId;
                """, new { Id = Ids.New(), request.ConversationId, state.Channel, body, preview = MessageText.Preview(body) },
                session.Transaction, cancellationToken: cancellationToken));
        }

        await session.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE crm.conversations SET agent_done_seq = GREATEST(agent_done_seq, LEAST(@Seq, agent_pending_seq)),
              agent_lease_until = NULL, agent_attempts = 0, agent_retry_at = NULL
            WHERE id = @ConversationId
            """, request, session.Transaction, cancellationToken: cancellationToken));
        return queue;
    }

    public async Task<Result<bool>> Handle(FailAgentTurnCommand request, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var attempts = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            UPDATE crm.conversations SET agent_attempts = agent_attempts + 1, agent_lease_until = NULL,
              agent_retry_at = now() + make_interval(secs => LEAST(300, 10 * power(2, agent_attempts)))
            WHERE id = @ConversationId
            RETURNING agent_attempts
            """, request, session.Transaction, cancellationToken: cancellationToken));

        if (attempts < MaxFailures)
        {
            return false;
        }

        await Handle(new HandOffCommand(request.ConversationId, HandoffReasons.AgentUnavailable, $"The assistant failed {attempts} times: {request.Error}"), cancellationToken);
        return true;
    }

    public async Task<Result<CheckoutLink>> Handle(CreateCheckoutLinkCommand request, CancellationToken cancellationToken)
    {
        var handoff = await carts.CreateHandoffAsync(request.Items, LinkLifetime, cancellationToken);
        if (handoff.IsFailure)
        {
            return handoff.Error;
        }

        // The code sits in the URL fragment: browsers never send it to servers or proxies, so it stays out of access logs.
        var url = $"{store.Value.PublicWebUrl.TrimEnd('/')}/{Locales.OrDefault(request.Locale)}/claim#{handoff.Value.Code}";
        return new CheckoutLink(url, handoff.Value.ExpiresAt, handoff.Value.Lines, handoff.Value.Skipped);
    }
}
