using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Notifications.Infrastructure;

public sealed record EmailOptions
{
    public const string Section = "Email";

    /// <summary>"smtp" or "log" (tests: nothing leaves the process, the log table still records every message).</summary>
    public string Transport { get; init; } = "smtp";

    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = 1025;

    public bool UseTls { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string FromAddress { get; init; } = "siparis@rahiq.example";

    public string FromName { get; init; } = "Rahiq";

    /// <summary>Who receives staff alerts (devops.md §4).</summary>
    public string[] StaffAlertRecipients { get; init; } = ["ops@rahiq.example"];
}

internal sealed class NotificationLog
{
    public Guid Id { get; init; }

    public required string Channel { get; init; }

    public required string Template { get; init; }

    public required string RecipientMasked { get; init; }

    public Guid? RefId { get; init; }

    public required string DedupeKey { get; init; }

    public required string Status { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset At { get; init; }
}

internal sealed class NotificationsModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<NotificationLog>(b =>
    {
        b.ToTable("log", "notifications");
        b.HasKey(l => l.Id);
    });
}

internal sealed record EmailMessage(string To, string Subject, string Html, IReadOnlyList<(string FileName, string ContentType, byte[] Content)>? Attachments = null);

internal interface IEmailTransport
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

internal sealed class SmtpEmailTransport(IOptions<EmailOptions> options) : IEmailTransport
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var o = options.Value;
        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        var body = new BodyBuilder { HtmlBody = message.Html };
        foreach (var (name, type, content) in message.Attachments ?? [])
        {
            body.Attachments.Add(name, content, ContentType.Parse(type));
        }

        mime.Body = body.ToMessageBody();
        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host, o.Port, o.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, cancellationToken);
        if (!string.IsNullOrEmpty(o.Username))
        {
            await client.AuthenticateAsync(o.Username, o.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}

internal sealed partial class LogOnlyEmailTransport(ILogger<LogOnlyEmailTransport> logger) : IEmailTransport
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        LogEmail(logger, Mask(message.To), message.Subject);
        return Task.CompletedTask;
    }

    internal static string Mask(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? "***" : $"{email[0]}***{email[at..]}";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail (log transport) to {To}: {Subject}")]
    private static partial void LogEmail(ILogger logger, string to, string subject);
}

/// <summary>
/// Sends each business fact once: the dedupe key (e.g. "order_confirmed:{orderId}") is unique in the log, so an event
/// delivered twice, or a webhook received five times, still produces one e-mail (testing.md commerce test 3).
/// Logs never contain the full address (security.md §9).
/// </summary>
internal sealed partial class Mailer(RahiqDbContext db, IEmailTransport transport, IClock clock, ILogger<Mailer> logger)
{
    public async Task<bool> SendOnceAsync(string dedupeKey, string template, Guid? refId, EmailMessage message, CancellationToken cancellationToken)
    {
        if (await db.Set<NotificationLog>().AnyAsync(l => l.DedupeKey == dedupeKey, cancellationToken))
        {
            return false;
        }

        var log = new NotificationLog
        {
            Id = Ids.New(),
            Channel = "email",
            Template = template,
            RecipientMasked = LogOnlyEmailTransport.Mask(message.To),
            RefId = refId,
            DedupeKey = dedupeKey,
            Status = "sent",
            At = clock.UtcNow,
        };
        db.Add(log);
        await db.SaveChangesAsync(cancellationToken); // Unique key: a concurrent duplicate fails here and never sends.

        try
        {
            await transport.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or IOException or System.Net.Sockets.SocketException)
        {
            log.Status = "failed";
            log.Error = ex.Message;
            LogFailed(logger, template, ex.Message);
            throw; // The outbox retries the handler; the consumer row and this log roll back together.
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "E-mail {Template} not sent: {Reason}")]
    private static partial void LogFailed(ILogger logger, string template, string reason);
}

internal static class Html
{
    public static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
