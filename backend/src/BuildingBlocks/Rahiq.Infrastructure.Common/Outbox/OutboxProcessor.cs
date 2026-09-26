using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.SharedKernel;

namespace Rahiq.Infrastructure.Common.Outbox;

public sealed record WorkerOptions
{
    public const string Section = "Workers";

    /// <summary>Background workers run in the API process unless disabled (tests drive them by hand).</summary>
    public bool Enabled { get; init; } = true;

    public int OutboxPollMilliseconds { get; init; } = 1000;
}

/// <summary>
/// Delivers committed events to their handlers. Each (message, handler) pair runs in its own transaction
/// together with a row in <c>infra.outbox_consumers</c>, so a retry never repeats a handler that already succeeded.
/// </summary>
public sealed partial class OutboxProcessor(IServiceScopeFactory scopeFactory, IClock clock, ILogger<OutboxProcessor> logger)
{
    private const int BatchSize = 20;
    private const int MaxAttempts = 20;
    private const int MaxDelaySeconds = 15 * 60;

    /// <returns>The number of messages handled in this pass.</returns>
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RahiqDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = clock.UtcNow;
        var messages = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM infra.outbox_messages
                WHERE processed_at IS NULL AND attempts < {MaxAttempts} AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                ORDER BY occurred_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                await Dispatch(message, cancellationToken);
                message.ProcessedAt = clock.UtcNow;
                message.LastError = null;
            }
#pragma warning disable CA1031 // A failing handler must not stop the other messages; the error is stored and retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                message.Attempts++;
                message.LastError = ex.ToString();
                // 2, 4, 8 ... seconds, then every 15 minutes: an outage of a few hours loses nothing.
                message.NextAttemptAt = clock.UtcNow.AddSeconds(Math.Min(Math.Pow(2, message.Attempts), MaxDelaySeconds));
                if (IsUnreachable(ex))
                {
                    LogUnreachable(logger, message.Type, message.Attempts, message.NextAttemptAt.Value, Innermost(ex).Message);
                }
                else
                {
                    LogFailed(logger, ex, message.Type, message.Id, message.Attempts);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return messages.Count;
    }

    /// <summary>Runs passes until nothing is left (used by tests and by the one-shot CLI).</summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < 100 && await ProcessPendingAsync(cancellationToken) > 0; i++)
        {
        }
    }

    private async Task Dispatch(OutboxMessage message, CancellationToken cancellationToken)
    {
        var type = EventTypeRegistry.Resolve(message.Type)
            ?? throw new InvalidOperationException($"Unknown event type '{message.Type}'.");
        var domainEvent = (IDomainEvent)JsonSerializer.Deserialize(message.Payload, type, JsonDefaults.Options)!;
        var handlerType = typeof(IEventHandler<>).MakeGenericType(type);

        int handlerCount;
        await using (var probe = scopeFactory.CreateAsyncScope())
        {
            handlerCount = probe.ServiceProvider.GetServices(handlerType).Count();
        }

        for (var index = 0; index < handlerCount; index++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetServices(handlerType).ElementAt(index)!;
            var consumer = handler.GetType().FullName!;
            var db = scope.ServiceProvider.GetRequiredService<RahiqDbContext>();

            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var claimed = await db.Database.ExecuteSqlAsync(
                $"INSERT INTO infra.outbox_consumers (message_id, consumer) VALUES ({message.Id}, {consumer}) ON CONFLICT DO NOTHING",
                cancellationToken);

            if (claimed == 0)
            {
                continue; // Already handled by this consumer in an earlier attempt.
            }

            var handle = handlerType.GetMethod(nameof(IEventHandler<IDomainEvent>.Handle))!;
            await (Task)handle.Invoke(handler, [domainEvent, cancellationToken])!;

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
    }

    /// <summary>A dependency that is down (mail server, carrier API): expected, retried, not a bug.</summary>
    private static bool IsUnreachable(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException or System.Net.Http.HttpRequestException or TimeoutException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    private static Exception Innermost(Exception ex) => ex.InnerException is null ? ex : Innermost(ex.InnerException);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Type} waits for an unreachable service (attempt {Attempt}, next at {NextAt:HH:mm:ss}): {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string type, int attempt, DateTimeOffset nextAt, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {Type} {Id} failed (attempt {Attempt})")]
    private static partial void LogFailed(ILogger logger, Exception ex, string type, Guid id, int attempt);
}

internal sealed partial class OutboxWorker(OutboxProcessor processor, IOptions<WorkerOptions> options, ILogger<OutboxWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.OutboxPollMilliseconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                while (await processor.ProcessPendingAsync(stoppingToken) > 0)
                {
                }
            }
#pragma warning disable CA1031 // The worker must survive a transient database error.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogPollFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox poll failed")]
    private static partial void LogPollFailed(ILogger logger, Exception ex);
}
