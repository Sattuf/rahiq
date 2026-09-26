using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Outbox;
using Rahiq.Modules.Conversations.Application;
using Rahiq.Modules.Conversations.Application.Agent;
using Rahiq.Modules.Conversations.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Conversations.Infrastructure;

/// <summary>
/// The background half of the conversations module (ADR-019), every second:
/// <list type="number">
/// <item>delivers queued replies (agent and staff) to the platforms, with retries;</item>
/// <item>starts an agent turn for each conversation with unanswered customer messages.</item>
/// </list>
/// Work is claimed in the database (SKIP LOCKED + a lease), so several API instances can run this safely.
/// </summary>
internal sealed partial class ConversationWorker(
    IServiceScopeFactory scopes,
    NpgsqlDataSource dataSource,
    IEnumerable<IChannelAdapter> adapters,
    IConversationNotifier notifier,
    IOptions<AgentOptions> agent,
    IOptions<WorkerOptions> workers,
    IClock clock,
    ILogger<ConversationWorker> logger) : BackgroundService
{
    private const int MaxDeliveryAttempts = 6;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    private bool AgentOn => agent.Value.Enabled && !string.IsNullOrWhiteSpace(agent.Value.ApiKey);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!workers.Value.Enabled)
        {
            return;
        }

        if (!AgentOn)
        {
            LogAgentOff(logger);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await DeliverAsync(stoppingToken);
                    await StartAgentTurnsAsync(stoppingToken);
                }
#pragma warning disable CA1031 // The worker must survive a transient database or network error.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogPassFailed(logger, ex);
                }
            }
        }
        finally
        {
            await Task.WhenAll(_running.Values); // Let running turns finish their bookkeeping on shutdown.
        }
    }

    /// <summary>Test and CLI hook: one delivery pass and one agent pass, waiting for the turns to finish.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await StartAgentTurnsAsync(cancellationToken);
        await Task.WhenAll(_running.Values);
        await DeliverAsync(cancellationToken);
    }

    private async Task DeliverAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var due = (await connection.QueryAsync<Outgoing>(new CommandDefinition("""
            WITH due AS (
              SELECT id FROM crm.messages
              WHERE status = 'pending' AND (next_attempt_at IS NULL OR next_attempt_at <= now())
              ORDER BY created_at
              LIMIT 20
              FOR UPDATE SKIP LOCKED
            )
            UPDATE crm.messages m SET attempts = m.attempts + 1, next_attempt_at = now() + interval '2 minutes'
            FROM due, crm.conversations c, crm.contacts k
            WHERE m.id = due.id AND c.id = m.conversation_id AND k.id = c.contact_id
            RETURNING m.id AS Id, m.conversation_id AS ConversationId, m.channel AS Channel, m.author AS Author, m.body AS Body,
                      m.attempts AS Attempts, m.created_at AS CreatedAt, k.external_id AS ExternalUserId, k.erased_at AS ErasedAt,
                      c.last_inbound_at AS LastInboundAt
            """, cancellationToken: cancellationToken))).OrderBy(m => m.CreatedAt).ToList();

        foreach (var m in due)
        {
            var outcome = await Send(m, cancellationToken);
            if (outcome.Ok)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE crm.messages SET status = 'sent', external_id = @ExternalId, sent_at = now(), last_error = NULL WHERE id = @Id",
                    new { m.Id, outcome.ExternalId }, cancellationToken: cancellationToken));
            }
            else
            {
                var final = outcome.Permanent || m.Attempts >= MaxDeliveryAttempts;
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE crm.messages SET status = CASE WHEN @final THEN 'failed' ELSE 'pending' END, last_error = @Error,
                      next_attempt_at = now() + make_interval(secs => 5 * power(2, attempts))
                    WHERE id = @Id
                    """, new { m.Id, final, outcome.Error }, cancellationToken: cancellationToken));
                LogDeliveryFailed(logger, m.Channel, m.Id, outcome.Error ?? string.Empty, final);
            }
        }

        if (due.Count > 0)
        {
            await notifier.ChangedAsync([.. due.Select(m => m.ConversationId).Distinct()], cancellationToken);
        }
    }

    private async Task<SendOutcome> Send(Outgoing m, CancellationToken cancellationToken)
    {
        if (m.ErasedAt is not null)
        {
            return SendOutcome.Failed("contact erased", permanent: true);
        }

        var adapter = adapters.FirstOrDefault(a => a.IsConfigured && a.Channels.Contains(m.Channel));
        if (adapter is null)
        {
            return SendOutcome.Failed($"{m.Channel} is not configured", permanent: true);
        }

        var window = ReplyWindow.For(m.Channel, m.LastInboundAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(m.LastInboundAt.Value, DateTimeKind.Utc)), clock.UtcNow, m.Author == MessageAuthors.Staff);
        if (!window.Open)
        {
            return SendOutcome.Failed("the platform's reply window is closed", permanent: true);
        }

        try
        {
            SendOutcome last = SendOutcome.Sent(null);
            foreach (var part in MessageText.Split(m.Body, MessageText.MaxOutbound(m.Channel)))
            {
                last = await adapter.SendAsync(new OutboundText(m.Channel, m.ExternalUserId, part, window.NeedsHumanAgentTag), cancellationToken);
                if (!last.Ok)
                {
                    return last;
                }
            }

            return last;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return SendOutcome.Failed(ex.Message, permanent: false);
        }
    }

    private async Task StartAgentTurnsAsync(CancellationToken cancellationToken)
    {
        foreach (var done in _running.Where(r => r.Value.IsCompleted).Select(r => r.Key).ToList())
        {
            _running.TryRemove(done, out _);
        }

        var free = AgentOn ? agent.Value.MaxParallel - _running.Count : 20;
        if (free <= 0)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var claimed = (await connection.QueryAsync<(Guid Id, long Seq)>(new CommandDefinition("""
            UPDATE crm.conversations c SET agent_lease_until = now() + @Lease
            WHERE c.id IN (
              SELECT id FROM crm.conversations
              WHERE mode = 'agent' AND status = 'open' AND agent_pending_seq > agent_done_seq
                AND last_inbound_at <= now() - make_interval(secs => @Debounce)
                AND (agent_lease_until IS NULL OR agent_lease_until < now())
                AND (agent_retry_at IS NULL OR agent_retry_at <= now())
              ORDER BY last_inbound_at
              LIMIT @free
              FOR UPDATE SKIP LOCKED)
            RETURNING c.id, c.agent_pending_seq
            """, new { Lease, Debounce = agent.Value.DebounceSeconds, free }, cancellationToken: cancellationToken))).ToList();

        foreach (var (id, seq) in claimed)
        {
            _running[id] = AgentOn
                ? Task.Run(() => RunTurnAsync(id, seq, cancellationToken), CancellationToken.None)
                : HandOffAsync(id, HandoffReasons.AgentDisabled, "The AI assistant is switched off.", cancellationToken);
        }
    }

    private async Task RunTurnAsync(Guid conversationId, long seq, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        try
        {
            if (await RepliesLastHour(conversationId, cancellationToken) >= agent.Value.MaxRepliesPerHour)
            {
                await sender.Send(new HandOffCommand(conversationId, HandoffReasons.ReplyLimit, "Hourly reply limit reached (possible spam or a long chat)."), cancellationToken);
                return;
            }

            var result = await scope.ServiceProvider.GetRequiredService<AgentTurn>().RunAsync(conversationId, cancellationToken);
            await sender.Send(new CompleteAgentTurnCommand(conversationId, seq, result.Reply), cancellationToken);
        }
#pragma warning disable CA1031 // Any failure (API down, bad response) is recorded and retried with back-off, then handed to a person.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            LogTurnFailed(logger, ex, conversationId);
            await using var retryScope = scopes.CreateAsyncScope();
            await retryScope.ServiceProvider.GetRequiredService<ISender>().Send(new FailAgentTurnCommand(conversationId, ex.GetType().Name), CancellationToken.None);
        }
        finally
        {
            await notifier.ChangedAsync([conversationId], CancellationToken.None);
        }
    }

    private async Task HandOffAsync(Guid conversationId, string reason, string summary, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new HandOffCommand(conversationId, reason, summary), cancellationToken);
        await notifier.ChangedAsync([conversationId], cancellationToken);
    }

    private async Task<int> RepliesLastHour(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM crm.messages WHERE conversation_id = @conversationId AND author = 'agent' AND created_at > now() - interval '1 hour'",
            new { conversationId }, cancellationToken: cancellationToken));
    }

    private sealed record Outgoing(
        Guid Id, Guid ConversationId, string Channel, string Author, string Body, int Attempts, DateTime CreatedAt, string ExternalUserId, DateTime? ErasedAt, DateTime? LastInboundAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The AI assistant is off (Agent:Enabled or Agent:ApiKey missing): new conversations go to staff")]
    private static partial void LogAgentOff(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Conversation worker pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent turn failed for conversation {ConversationId}")]
    private static partial void LogTurnFailed(ILogger logger, Exception ex, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery on {Channel} failed for message {MessageId}: {Error} (final: {Final})")]
    private static partial void LogDeliveryFailed(ILogger logger, string channel, Guid messageId, string error, bool final);
}
