using Dapper;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Conversations.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Conversations.Application;

/// <summary>Tells open admin screens that a conversation changed. They re-read it through the API (with permission checks).</summary>
public interface IConversationNotifier
{
    Task ChangedAsync(IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken);
}

public sealed record ConversationSummary(
    Guid Id,
    string Channel,
    string? ContactName,
    string Status,
    string Mode,
    Guid? AssignedStaffId,
    string? HandoffReason,
    int Unread,
    DateTimeOffset LastMessageAt,
    string? LastPreview);

public sealed record MessageDto(Guid Id, string Direction, string Author, string Kind, string Body, string Status, string? Error, DateTimeOffset CreatedAt);

public sealed record ContactDto(Guid Id, string Channel, string? Name, string? Phone, string? Locale, Guid? CustomerId, DateTimeOffset CreatedAt);

public sealed record ReplyWindowDto(bool Open, DateTimeOffset? ClosesAt);

public sealed record ConversationDetail(ConversationSummary Conversation, ContactDto Contact, ReplyWindowDto ReplyWindow, IReadOnlyList<MessageDto> Messages);

/// <param name="Filter">all | agent | human | handoff (waiting for a person, nobody assigned) | mine.</param>
public sealed record ListConversationsQuery(string? Filter, string? Channel, bool IncludeClosed) : IQuery<IReadOnlyList<ConversationSummary>>;

public sealed record GetConversationQuery(Guid Id) : IQuery<Result<ConversationDetail>>;

/// <summary>"Take over": the assistant stops answering this conversation and the staff member answers instead.</summary>
public sealed record TakeOverConversationCommand(Guid Id) : ICommand;

/// <summary>Gives the conversation back to the assistant. It answers from the next customer message on.</summary>
public sealed record ReleaseConversationCommand(Guid Id) : ICommand;

public sealed record CloseConversationCommand(Guid Id) : ICommand;

public sealed record MarkConversationReadCommand(Guid Id) : ICommand;

/// <summary>A staff reply. Replying also takes the conversation over, so the assistant cannot talk over a person.</summary>
public sealed record SendStaffReplyCommand(Guid Id, string Text) : ICommand<Result<Guid>>;

internal sealed class SendStaffReplyValidator : AbstractValidator<SendStaffReplyCommand>
{
    public SendStaffReplyValidator() => RuleFor(x => x.Text).NotEmpty().MaximumLength(4000);
}

internal static class InboxErrors
{
    public static readonly Error NotFound = Error.NotFound("conversation.not_found", "Conversation not found.");

    public static readonly Error WindowClosed = Error.Conflict("conversation.window_closed",
        "This platform only allows replies within 24 hours of the customer's last message. Wait for the customer to write again.");

    public static readonly Error Erased = Error.Conflict("conversation.contact_erased", "This contact's data was erased.");
}

internal sealed class InboxHandlers(RahiqDbContext db, IDbSession session, ICurrentActor actor, IAuditLog audit, IClock clock)
    : IRequestHandler<ListConversationsQuery, IReadOnlyList<ConversationSummary>>,
      IRequestHandler<GetConversationQuery, Result<ConversationDetail>>,
      IRequestHandler<TakeOverConversationCommand, Result>,
      IRequestHandler<ReleaseConversationCommand, Result>,
      IRequestHandler<CloseConversationCommand, Result>,
      IRequestHandler<MarkConversationReadCommand, Result>,
      IRequestHandler<SendStaffReplyCommand, Result<Guid>>
{
    public async Task<IReadOnlyList<ConversationSummary>> Handle(ListConversationsQuery request, CancellationToken cancellationToken)
    {
        var q = db.Set<Conversation>().AsNoTracking();
        if (!request.IncludeClosed)
        {
            q = q.Where(c => c.Status == "open");
        }

        if (!string.IsNullOrWhiteSpace(request.Channel))
        {
            q = q.Where(c => c.Channel == request.Channel);
        }

        var me = actor.Id;
        q = request.Filter switch
        {
            "agent" => q.Where(c => c.Mode == ConversationModes.Agent),
            "human" => q.Where(c => c.Mode == ConversationModes.Human),
            "handoff" => q.Where(c => c.Mode == ConversationModes.Human && c.AssignedStaffId == null),
            "mine" => q.Where(c => c.AssignedStaffId == me),
            _ => q,
        };

        return await q.OrderByDescending(c => c.LastMessageAt).Take(100)
            .Join(db.Set<Contact>(), c => c.ContactId, k => k.Id, (c, k) => new ConversationSummary(
                c.Id, c.Channel, k.DisplayName, c.Status, c.Mode, c.AssignedStaffId, c.HandoffReason, c.Unread, c.LastMessageAt, c.LastPreview))
            .ToListAsync(cancellationToken);
    }

    public async Task<Result<ConversationDetail>> Handle(GetConversationQuery request, CancellationToken cancellationToken)
    {
        var c = await db.Set<Conversation>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);
        if (c is null)
        {
            return InboxErrors.NotFound;
        }

        var k = await db.Set<Contact>().AsNoTracking().FirstAsync(x => x.Id == c.ContactId, cancellationToken);
        var messages = await db.Set<ChatMessage>().AsNoTracking()
            .Where(m => m.ConversationId == c.Id)
            .OrderByDescending(m => m.CreatedAt).Take(300)
            .Select(m => new MessageDto(m.Id, m.Direction, m.Author, m.Kind, m.Body, m.Status, m.LastError, m.CreatedAt))
            .ToListAsync(cancellationToken);
        var window = ReplyWindow.For(c.Channel, c.LastInboundAt, clock.UtcNow, byStaff: true);

        return new ConversationDetail(
            new ConversationSummary(c.Id, c.Channel, k.DisplayName, c.Status, c.Mode, c.AssignedStaffId, c.HandoffReason, c.Unread, c.LastMessageAt, c.LastPreview),
            new ContactDto(k.Id, k.Channel, k.DisplayName, k.Phone, k.Locale, k.CustomerId, k.CreatedAt),
            new ReplyWindowDto(window.Open, window.ClosesAt),
            [.. messages.OrderBy(m => m.CreatedAt)]);
    }

    public async Task<Result> Handle(TakeOverConversationCommand request, CancellationToken cancellationToken)
    {
        // Marking the pending work done means a running agent turn will find the thread taken and drop its reply.
        var changed = await Execute("""
            UPDATE crm.conversations SET mode = 'human', assigned_staff_id = @staff, agent_done_seq = agent_pending_seq, status = 'open'
            WHERE id = @id
            """, request.Id, cancellationToken);
        if (!changed)
        {
            return InboxErrors.NotFound;
        }

        await Note(request.Id, $"Taken over by {actor.Email}", cancellationToken);
        audit.Record("conversation.take_over", "conversation", request.Id.ToString());
        return Result.Success();
    }

    public async Task<Result> Handle(ReleaseConversationCommand request, CancellationToken cancellationToken)
    {
        var changed = await Execute("""
            UPDATE crm.conversations SET mode = 'agent', assigned_staff_id = NULL, handoff_reason = NULL, handoff_at = NULL,
              agent_done_seq = agent_pending_seq, agent_attempts = 0, agent_retry_at = NULL
            WHERE id = @id
            """, request.Id, cancellationToken);
        if (!changed)
        {
            return InboxErrors.NotFound;
        }

        await Note(request.Id, $"Returned to the assistant by {actor.Email}", cancellationToken);
        audit.Record("conversation.release", "conversation", request.Id.ToString());
        return Result.Success();
    }

    public async Task<Result> Handle(CloseConversationCommand request, CancellationToken cancellationToken) =>
        await Execute("UPDATE crm.conversations SET status = 'closed', unread = 0, agent_done_seq = agent_pending_seq WHERE id = @id", request.Id, cancellationToken)
            ? Result.Success()
            : InboxErrors.NotFound;

    public async Task<Result> Handle(MarkConversationReadCommand request, CancellationToken cancellationToken) =>
        await Execute("UPDATE crm.conversations SET unread = 0 WHERE id = @id", request.Id, cancellationToken) ? Result.Success() : InboxErrors.NotFound;

    public async Task<Result<Guid>> Handle(SendStaffReplyCommand request, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var c = await session.Connection.QuerySingleOrDefaultAsync<(string Channel, DateTime? LastInboundAt, DateTime? ErasedAt)>(new CommandDefinition("""
            SELECT c.channel, c.last_inbound_at, k.erased_at
            FROM crm.conversations c JOIN crm.contacts k ON k.id = c.contact_id
            WHERE c.id = @Id FOR UPDATE OF c
            """, new { request.Id }, session.Transaction, cancellationToken: cancellationToken));
        if (c.Channel is null)
        {
            return InboxErrors.NotFound;
        }

        if (c.ErasedAt is not null)
        {
            return InboxErrors.Erased;
        }

        DateTimeOffset? lastInbound = c.LastInboundAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(c.LastInboundAt.Value, DateTimeKind.Utc));
        if (!ReplyWindow.For(c.Channel, lastInbound, clock.UtcNow, byStaff: true).Open)
        {
            return InboxErrors.WindowClosed;
        }

        var messageId = Ids.New();
        var text = request.Text.Trim();
        await session.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO crm.messages (id, conversation_id, channel, direction, author, staff_id, body, status)
            VALUES (@messageId, @conversationId, @Channel, 'out', 'staff', @staff, @text, 'pending');
            UPDATE crm.conversations SET
              mode = 'human',
              assigned_staff_id = COALESCE(assigned_staff_id, @staff),
              agent_done_seq = agent_pending_seq,
              unread = 0,
              last_message_at = now(),
              last_preview = @preview
            WHERE id = @conversationId;
            """, new { messageId, conversationId = request.Id, c.Channel, staff = actor.Id, text, preview = MessageText.Preview(text) }, session.Transaction, cancellationToken: cancellationToken));
        return messageId;
    }

    private async Task<bool> Execute(string sql, Guid id, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        return await session.Connection.ExecuteAsync(new CommandDefinition(sql, new { id, staff = actor.Id }, session.Transaction, cancellationToken: cancellationToken)) > 0;
    }

    private async Task Note(Guid conversationId, string text, CancellationToken cancellationToken) =>
        await session.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO crm.messages (id, conversation_id, channel, direction, author, staff_id, kind, body, status)
            SELECT @Id, id, channel, 'out', 'system', @staff, 'note', @text, 'note' FROM crm.conversations WHERE id = @conversationId
            """, new { Id = Ids.New(), conversationId, staff = actor.Id, text }, session.Transaction, cancellationToken: cancellationToken));
}
