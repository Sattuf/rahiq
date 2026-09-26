using Dapper;
using Rahiq.Application.Abstractions;
using Rahiq.Modules.Conversations.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Conversations.Application;

/// <summary>
/// Stores what a webhook delivered and nothing more, so the webhook can answer 200 at once (Meta retries slow
/// webhooks and eventually disables them). The agent runs later, from <c>ConversationWorker</c>.
/// </summary>
/// <returns>The conversations that received a new message (for the live inbox).</returns>
public sealed record IngestMessagesCommand(IReadOnlyList<InboundMessage> Messages) : ICommand<Result<IReadOnlyList<Guid>>>;

internal sealed class IngestionHandler(IDbSession session) : IRequestHandler<IngestMessagesCommand, Result<IReadOnlyList<Guid>>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(IngestMessagesCommand request, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var db = session.Connection;
        var tx = session.Transaction;
        var touched = new HashSet<Guid>();

        foreach (var m in request.Messages)
        {
            var contactId = await db.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO crm.contacts (id, channel, external_id, display_name, phone, locale)
                VALUES (@Id, @Channel, @ExternalUserId, @DisplayName, @Phone, @LanguageHint)
                ON CONFLICT (channel, external_id) DO UPDATE SET
                  display_name = COALESCE(EXCLUDED.display_name, crm.contacts.display_name),
                  phone = COALESCE(EXCLUDED.phone, crm.contacts.phone),
                  locale = COALESCE(crm.contacts.locale, EXCLUDED.locale),
                  last_seen_at = now(),
                  erased_at = NULL
                RETURNING id
                """, new { Id = Ids.New(), m.Channel, m.ExternalUserId, DisplayName = Clip(m.DisplayName, 120), Phone = Clip(m.Phone, 32), LanguageHint = Clip(m.LanguageHint, 8) }, tx, cancellationToken: cancellationToken));

            // One thread per contact. A closed thread reopens with the assistant answering again.
            var conversationId = await db.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO crm.conversations (id, contact_id, channel) VALUES (@Id, @contactId, @Channel)
                ON CONFLICT (contact_id) DO UPDATE SET
                  mode = CASE WHEN crm.conversations.status = 'closed' THEN 'agent' ELSE crm.conversations.mode END,
                  assigned_staff_id = CASE WHEN crm.conversations.status = 'closed' THEN NULL ELSE crm.conversations.assigned_staff_id END,
                  handoff_reason = CASE WHEN crm.conversations.status = 'closed' THEN NULL ELSE crm.conversations.handoff_reason END,
                  status = 'open'
                RETURNING id
                """, new { Id = Ids.New(), contactId, m.Channel }, tx, cancellationToken: cancellationToken));

            var body = Clip(m.Text, MessageText.MaxInbound) ?? string.Empty;
            var inserted = await db.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
                INSERT INTO crm.messages (id, conversation_id, channel, direction, author, kind, body, external_id, status)
                VALUES (@Id, @conversationId, @Channel, 'in', 'customer', @Kind, @body, @ExternalMessageId, 'received')
                ON CONFLICT (channel, external_id) WHERE external_id IS NOT NULL AND direction = 'in' DO NOTHING
                RETURNING id
                """, new { Id = Ids.New(), conversationId, m.Channel, Kind = Clip(m.Kind, 20), body, m.ExternalMessageId }, tx, cancellationToken: cancellationToken));

            if (inserted is null)
            {
                continue; // The platform retried a message we already have.
            }

            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE crm.conversations SET
                  unread = unread + 1,
                  last_message_at = now(),
                  last_inbound_at = now(),
                  last_preview = @preview,
                  agent_pending_seq = agent_pending_seq + CASE WHEN mode = 'agent' THEN 1 ELSE 0 END,
                  agent_attempts = 0,
                  agent_retry_at = NULL
                WHERE id = @conversationId
                """, new { conversationId, preview = MessageText.Preview(body) }, tx, cancellationToken: cancellationToken));
            touched.Add(conversationId);
        }

        return touched.ToList();
    }

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
