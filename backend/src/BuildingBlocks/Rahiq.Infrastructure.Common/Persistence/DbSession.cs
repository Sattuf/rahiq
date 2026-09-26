using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Rahiq.Application.Abstractions;
using Rahiq.SharedKernel;

namespace Rahiq.Infrastructure.Common.Persistence;

internal sealed class DbSession(RahiqDbContext db) : IDbSession
{
    public DbConnection Connection => db.Database.GetDbConnection();

    public DbTransaction? Transaction => db.Database.CurrentTransaction?.GetDbTransaction();

    public async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (Connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

internal sealed class EventPublisher(RahiqDbContext db) : IEventPublisher
{
    public void Publish(IDomainEvent domainEvent) => db.Enqueue(domainEvent);
}

internal sealed class AuditLog(RahiqDbContext db, ICurrentActor actor, IClock clock) : IAuditLog
{
    public void Record(string action, string entity, string? entityId, object? data = null) =>
        db.AuditEntries.Add(new AuditEntry
        {
            ActorId = actor.Id,
            ActorEmail = actor.Email,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            Data = data is null ? null : System.Text.Json.JsonSerializer.Serialize(data, JsonDefaults.Options),
            At = clock.UtcNow,
        });
}
