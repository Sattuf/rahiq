using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.Modules.Conversations.Application;

namespace Rahiq.Modules.Conversations.Presentation;

/// <summary>
/// One webhook per platform: /webhooks/telegram, /webhooks/meta (Messenger + Instagram), /webhooks/whatsapp.
/// Verify the signature over the raw bytes, store, answer 200 at once. The reply is produced in the background.
/// A 500 (database down) is deliberate: the platform retries, and duplicates are dropped by message id.
/// </summary>
[Route("webhooks")]
[ApiExplorerSettings(IgnoreApi = true)]
internal sealed partial class ChannelWebhookController(
    ISender sender,
    IEnumerable<IChannelAdapter> adapters,
    IConversationNotifier notifier,
    ILogger<ChannelWebhookController> logger) : ApiControllerBase
{
    private const int MaxBody = 1024 * 1024;

    /// <summary>The subscription handshake (Meta sends hub.mode, hub.verify_token and hub.challenge).</summary>
    [HttpGet("{webhook}")]
    public IActionResult Verify(string webhook)
    {
        var adapter = Find(webhook);
        if (adapter is null)
        {
            return NotFound();
        }

        var query = Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
        return adapter.VerifySubscription(query) is { } challenge ? Content(challenge, "text/plain") : Forbid();
    }

    [HttpPost("{webhook}")]
    [RequestSizeLimit(MaxBody)]
    public async Task<IActionResult> Receive(string webhook, CancellationToken ct)
    {
        var adapter = Find(webhook);
        if (adapter is null)
        {
            return NotFound();
        }

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();
        var headers = Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());

        if (!adapter.VerifySignature(headers, body))
        {
            LogBadSignature(logger, webhook, HttpContext.Connection.RemoteIpAddress?.ToString());
            return Unauthorized();
        }

        IReadOnlyList<InboundMessage> messages;
        try
        {
            messages = adapter.Parse(body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Signed but unreadable: answering an error would only make the platform resend the same bytes forever.
            LogUnreadable(logger, ex, webhook);
            return Ok();
        }

        if (messages.Count == 0)
        {
            return Ok();
        }

        var stored = await sender.Send(new IngestMessagesCommand(messages), ct);
        if (stored.IsFailure)
        {
            return ProblemFor(stored.Error);
        }

        await notifier.ChangedAsync(stored.Value, ct);
        return Ok();
    }

    private IChannelAdapter? Find(string webhook) => adapters.FirstOrDefault(a => a.Webhook == webhook && a.IsConfigured);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected {Webhook} webhook with a bad signature from {Ip}")]
    private static partial void LogBadSignature(ILogger logger, string webhook, string? ip);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unreadable {Webhook} webhook body")]
    private static partial void LogUnreadable(ILogger logger, Exception ex, string webhook);
}

public sealed record ReplyRequest(string Text);

/// <summary>The staff inbox. Every change is pushed to open screens through <see cref="ConversationsHub"/>.</summary>
[Route("api/admin/conversations")]
[Authorize(Policy = Permissions.ConversationsView)]
internal sealed class ConversationsAdminController(ISender sender, IConversationNotifier notifier) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? filter, [FromQuery] string? channel, [FromQuery] bool closed, CancellationToken ct) =>
        Ok(await sender.Send(new ListConversationsQuery(filter, channel, closed), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await sender.Send(new GetConversationQuery(id), ct));

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct) => await Changed(id, await sender.Send(new MarkConversationReadCommand(id), ct), ct);

    [HttpPost("{id:guid}/take-over")]
    [Authorize(Policy = Permissions.ConversationsReply)]
    public async Task<IActionResult> TakeOver(Guid id, CancellationToken ct) => await Changed(id, await sender.Send(new TakeOverConversationCommand(id), ct), ct);

    [HttpPost("{id:guid}/release")]
    [Authorize(Policy = Permissions.ConversationsReply)]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct) => await Changed(id, await sender.Send(new ReleaseConversationCommand(id), ct), ct);

    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = Permissions.ConversationsReply)]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct) => await Changed(id, await sender.Send(new CloseConversationCommand(id), ct), ct);

    [HttpPost("{id:guid}/messages")]
    [Authorize(Policy = Permissions.ConversationsReply)]
    public async Task<IActionResult> Reply(Guid id, ReplyRequest body, CancellationToken ct)
    {
        var result = await sender.Send(new SendStaffReplyCommand(id, body.Text), ct);
        if (result.IsSuccess)
        {
            await notifier.ChangedAsync([id], ct);
        }

        return result.IsSuccess ? StatusCode(StatusCodes.Status202Accepted, new { id = result.Value }) : ProblemFor(result.Error);
    }

    private async Task<IActionResult> Changed(Guid id, SharedKernel.Result result, CancellationToken ct)
    {
        if (result.IsSuccess)
        {
            await notifier.ChangedAsync([id], ct);
        }

        return FromResult(result);
    }
}

/// <summary>
/// Live updates for the inbox. It only ever says "conversation X changed": the screen then re-reads it through the
/// normal, permission-checked API, so the socket carries no customer data. Connecting needs a short-lived hub token.
/// </summary>
[Authorize(Policy = Policy)]
internal sealed class ConversationsHub : Hub
{
    public const string Policy = "conversations.hub";
    public const string Group = "inbox";
    public const string Path = "/hubs/conversations";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Group);
        await base.OnConnectedAsync();
    }
}

internal sealed class SignalRConversationNotifier(IHubContext<ConversationsHub> hub) : IConversationNotifier
{
    public async Task ChangedAsync(IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken)
    {
        foreach (var id in conversationIds)
        {
            await hub.Clients.Group(ConversationsHub.Group).SendAsync("conversationChanged", id, cancellationToken);
        }
    }
}
