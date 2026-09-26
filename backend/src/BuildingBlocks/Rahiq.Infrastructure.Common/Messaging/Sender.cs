using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.SharedKernel;

namespace Rahiq.Infrastructure.Common.Messaging;

internal sealed class Sender(IServiceProvider services) : ISender
{
    private static readonly ConcurrentDictionary<(Type Request, Type Response), object> Wrappers = new();

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wrapper = (RequestWrapper<TResponse>)Wrappers.GetOrAdd(
            (request.GetType(), typeof(TResponse)),
            key => Activator.CreateInstance(typeof(RequestWrapper<,>).MakeGenericType(key.Request, key.Response))!);
        return wrapper.Handle(request, services, cancellationToken);
    }
}

internal abstract class RequestWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(object request, IServiceProvider services, CancellationToken cancellationToken);
}

internal sealed class RequestWrapper<TRequest, TResponse> : RequestWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override Task<TResponse> Handle(object request, IServiceProvider services, CancellationToken cancellationToken)
    {
        var typed = (TRequest)request;
        var handler = services.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        RequestHandlerDelegate<TResponse> next = () => handler.Handle(typed, cancellationToken);

        foreach (var behavior in services.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
        {
            var inner = next;
            next = () => behavior.Handle(typed, inner, cancellationToken);
        }

        return next();
    }
}

internal sealed partial class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await next();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (response is Result { IsFailure: true } failed)
        {
            LogFailure(logger, typeof(TRequest).Name, failed.Error.Code, elapsed);
        }
        else
        {
            LogSuccess(logger, typeof(TRequest).Name, elapsed);
        }

        return response;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Request} handled in {Elapsed:0.0} ms")]
    private static partial void LogSuccess(ILogger logger, string request, double elapsed);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Request} failed with {Code} in {Elapsed:0.0} ms")]
    private static partial void LogFailure(ILogger logger, string request, string code, double elapsed);
}

internal sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return await next();
        }

        var error = Error.Validation("validation.failed", "One or more fields are invalid.") with
        {
            Details = failures
                .GroupBy(f => ToCamelCase(f.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorCode is { Length: > 0 } code && !code.EndsWith("Validator", StringComparison.Ordinal) ? code : f.ErrorMessage).ToArray()),
        };

        return ResultFactory.Failure<TResponse>(error);
    }

    private static string ToCamelCase(string name) =>
        string.Join('.', name.Split('.').Select(part => part.Length == 0 ? part : char.ToLowerInvariant(part[0]) + part[1..]));
}

/// <summary>
/// Every command is one transaction: all module writes, the hand-written SQL, and the outbox commit or roll back together.
/// A failed <see cref="Result"/> rolls back too, so a handler can return an error after partial work safely.
/// </summary>
internal sealed class TransactionBehavior<TRequest, TResponse>(RahiqDbContext db)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase || db.Database.CurrentTransaction is not null)
        {
            return await next();
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var response = await next();

        if (response is Result { IsFailure: true } && request is not ICommitsOnFailure)
        {
            await tx.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return response;
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return response;
    }
}

internal static class ResultFactory
{
    private static readonly MethodInfo GenericFailure = typeof(Result).GetMethods()
        .Single(m => m is { Name: nameof(Result.Failure), IsGenericMethodDefinition: true });

    public static TResponse Failure<TResponse>(Error error)
    {
        if (typeof(TResponse) == typeof(Result))
        {
            return (TResponse)(object)Result.Failure(error);
        }

        if (typeof(TResponse).IsGenericType && typeof(TResponse).GetGenericTypeDefinition() == typeof(Result<>))
        {
            var method = GenericFailure.MakeGenericMethod(typeof(TResponse).GetGenericArguments()[0]);
            return (TResponse)method.Invoke(null, [error])!;
        }

        throw new ValidationException(error.Message);
    }
}
