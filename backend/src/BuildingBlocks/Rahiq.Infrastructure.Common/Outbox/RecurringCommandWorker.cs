using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Rahiq.Application.Abstractions;

namespace Rahiq.Infrastructure.Common.Outbox;

/// <summary>
/// Sends a command on a fixed interval. With two API instances running, a PostgreSQL advisory lock makes sure only
/// one of them runs a given job at a time.
/// </summary>
internal sealed partial class RecurringCommandWorker<TCommand>(
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IOptions<WorkerOptions> options,
    RecurringSchedule<TCommand> schedule,
    ILogger<RecurringCommandWorker<TCommand>> logger)
    : BackgroundService
    where TCommand : class, IRequest<SharedKernel.Result<int>>, new()
{
    private static readonly long LockKey = StableHash(typeof(TCommand).FullName!);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(schedule.Interval);
        do
        {
            try
            {
                await RunOnce(stoppingToken);
            }
#pragma warning disable CA1031 // A failing run is logged and retried on the next tick.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex, typeof(TCommand).Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnce(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var tryLock = new NpgsqlCommand($"SELECT pg_try_advisory_lock({LockKey})", connection))
        {
            if (await tryLock.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return;
            }
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new TCommand(), cancellationToken);
            if (result.IsSuccess && result.Value > 0)
            {
                LogRan(logger, typeof(TCommand).Name, result.Value);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({LockKey})", connection);
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    /// <summary>FNV-1a: the same job name gives the same lock key on every instance and every restart.</summary>
    private static long StableHash(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in value)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return unchecked((long)hash);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Job} processed {Count} items")]
    private static partial void LogRan(ILogger logger, string job, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recurring job {Job} failed")]
    private static partial void LogFailed(ILogger logger, Exception ex, string job);
}

internal sealed record RecurringSchedule<TCommand>(TimeSpan Interval);

public static class RecurringCommandExtensions
{
    public static IServiceCollection AddRecurringCommand<TCommand>(this IServiceCollection services, TimeSpan interval)
        where TCommand : class, IRequest<SharedKernel.Result<int>>, new()
    {
        services.AddSingleton(new RecurringSchedule<TCommand>(interval));
        services.AddHostedService<RecurringCommandWorker<TCommand>>();
        return services;
    }
}
