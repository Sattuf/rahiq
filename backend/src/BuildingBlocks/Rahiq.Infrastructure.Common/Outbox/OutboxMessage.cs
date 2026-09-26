using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.SharedKernel;

namespace Rahiq.Infrastructure.Common.Outbox;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    public required string Type { get; init; }

    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public static OutboxMessage From(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var type = domainEvent.GetType();
        return new OutboxMessage
        {
            Id = domainEvent.EventId,
            Type = EventTypeRegistry.NameOf(type),
            Payload = JsonSerializer.Serialize(domainEvent, type, JsonDefaults.Options),
            OccurredAt = domainEvent.OccurredAt,
        };
    }
}

/// <summary>Maps stored event names back to CLR types. Names are stable: the type's full name.</summary>
public static class EventTypeRegistry
{
    private static readonly ConcurrentDictionary<string, Type> Types = new(StringComparer.Ordinal);

    public static string NameOf(Type type) => type.FullName!;

    public static void Register(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsClass: true } && typeof(IDomainEvent).IsAssignableFrom(t)))
        {
            Types[NameOf(type)] = type;
        }
    }

    public static Type? Resolve(string name) => Types.GetValueOrDefault(name);
}
