using Dapper;
using Rahiq.Application.Abstractions;
using Rahiq.Modules.Customers.Contracts;

namespace Rahiq.Modules.Conversations.Application;

/// <summary>
/// KVKK right to erasure: when a customer account is erased, chats linked to it lose the person's name, phone and
/// message text. The rows stay (counts, audit of what the assistant did) but say nothing about the person.
/// </summary>
internal sealed class ConversationErasure(IDbSession session) : IEventHandler<CustomerDataErased>
{
    public async Task Handle(CustomerDataErased domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await session.EnsureOpenAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("""
            WITH erased AS (
              UPDATE crm.contacts SET display_name = NULL, phone = NULL, erased_at = now()
              WHERE customer_id = @CustomerId
              RETURNING id
            ), threads AS (
              UPDATE crm.conversations SET last_preview = NULL, status = 'closed'
              WHERE contact_id IN (SELECT id FROM erased)
              RETURNING id
            ), texts AS (
              UPDATE crm.messages SET body = '[erased]', last_error = NULL WHERE conversation_id IN (SELECT id FROM threads)
            )
            UPDATE crm.agent_actions SET input = '{}'::jsonb WHERE conversation_id IN (SELECT id FROM threads)
            """, new { domainEvent.CustomerId }, session.Transaction, cancellationToken: cancellationToken));
    }
}
