namespace Rahiq.SharedKernel;

public abstract class Entity<TId>
    where TId : notnull
{
    protected Entity(TId id) => Id = id;

    protected Entity()
    {
        Id = default!;
    }

    public TId Id { get; protected set; }
}

public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}

public abstract class AggregateRoot<TId> : Entity<TId>, IHasDomainEvents
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}

/// <summary>
/// A fact that happened. Events are persisted to the outbox in the same transaction as the change
/// and handled in the background (product-domain.md §4.5).
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

public static class Ids
{
    /// <summary>UUID v7 (time ordered), per data-model.md.</summary>
    public static Guid New() => Guid.CreateVersion7();
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
