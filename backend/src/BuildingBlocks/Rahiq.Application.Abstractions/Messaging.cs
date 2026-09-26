using Rahiq.SharedKernel;

namespace Rahiq.Application.Abstractions;

/// <summary>Marker for requests that change state.</summary>
public interface ICommandBase;

/// <summary>A request that changes state. Runs inside one database transaction (TransactionBehavior).</summary>
public interface ICommand<TResponse> : IRequest<TResponse>, ICommandBase;

/// <summary>
/// A command whose failure must still be saved: failed sign-in attempts count towards lock-out and code limits
/// even though the command returns an error.
/// </summary>
public interface ICommitsOnFailure;

/// <summary>A command with no payload in its successful result.</summary>
public interface ICommand : ICommand<Result>;

/// <summary>A request that only reads.</summary>
public interface IQuery<TResponse> : IRequest<TResponse>;

public interface IRequest<TResponse>;

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>
/// Pipeline order (architecture.md §4): Logging → Validation → Transaction (+ outbox).
/// Idempotency is enforced at the HTTP edge with the Idempotency-Key header.
/// </summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

/// <summary>Our own small dispatcher (ADR-009): no third-party mediator.</summary>
public interface ISender
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}

/// <summary>Handles an event after it was committed, from the outbox. Must be safe to run again.</summary>
public interface IEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task Handle(TEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>Adds an event to the outbox inside the current transaction (for events not raised by an aggregate).</summary>
public interface IEventPublisher
{
    void Publish(IDomainEvent domainEvent);
}
