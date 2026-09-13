using System.Runtime.CompilerServices;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Notifications;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace DevSup.Api.Notifications;

/// <summary>
/// Shared email + webhook fan-out for failure events. Used by the ingest path and by
/// event replay so a replayed failure produces the same notifications as the original.
/// </summary>
public static class FailureReporter
{
    public static async Task Notify(
        DevSupDbContext db,
        User owner,
        ConnectedRepository repository,
        FailureEvent failure,
        RepairTicket ticket,
        FailureCategory category,
        bool replay = false,
        CancellationToken ct = default)
    {
        var notCodeError = category == FailureCategory.NotCodeError;
        var prefix = replay ? "DevSup: replaying — " : "DevSup: ";
        var emailEvent = notCodeError ? WebhookEvent.NotCodeError : WebhookEvent.FailureDetected;

        var subject = notCodeError
            ? $"{prefix}not a code error on {failure.Method} {failure.Path}"
            : $"{prefix}failure detected on {failure.Method} {failure.Path}";
        var body = notCodeError
            ? $"<p>DevSup detected a <strong>{ticket.Kind}</strong> failure on <code>{failure.Method} {failure.Path}</code> " +
              $"(HTTP {failure.StatusCode}).</p>" +
              $"<p>This was classified as <em>not a code error</em>, so the repair agent will <strong>not</strong> " +
              $"attempt a code fix and no patch is scheduled. Review the credentials, client, rate limits, or " +
              $"downstream services instead.</p>"
            : $"<p>DevSup detected a failure on <code>{failure.Method} {failure.Path}</code> " +
              $"with status <strong>{failure.StatusCode}</strong>.</p>" +
              $"<p>Classification: <strong>{ticket.Kind}</strong> ({category}).</p>" +
              $"<p>Next step: ticket <strong>{ticket.Status}</strong> — the agent will investigate code errors.</p>";

        // The owner may have muted email for this repository/event; webhooks always fan out.
        if (NotificationPreferencePolicy.ShouldSendEmail(db, owner.Id, repository.Id, emailEvent))
        {
            Enqueue(db, owner.Id, repository.Id, owner.Email, subject, body);
        }

        // Operator members are copied on incident emails (theirs via their own preferences);
        // webhooks stay scoped to the owner's endpoints.
        await foreach (var member in MemberRecipients(db, repository.Id, ct))
        {
            if (NotificationPreferencePolicy.ShouldSendEmail(db, member.Id, repository.Id, emailEvent))
            {
                Enqueue(db, member.Id, repository.Id, member.Email, subject, body);
            }
        }

        WebhookQueue.Enqueue(db, owner.Id, emailEvent, new
        {
            failure = new { failure.Method, failure.Path, failure.StatusCode, FailureId = failure.Id },
            repository = new { repository.Id, repository.CloneUrl, repository.DefaultBranch },
            ticket = new { ticket.Id, Status = ticket.Status, Kind = ticket.Kind }
        }, repository.Id);
    }

    private static async IAsyncEnumerable<User> MemberRecipients(DevSupDbContext db, Guid repositoryId, [EnumeratorCancellation] CancellationToken ct)
    {
        var memberIds = await db.RepositoryMembers.AsNoTracking()
            .Where(m => m.RepositoryId == repositoryId && m.Role == MemberRole.Operator)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        if (memberIds.Count == 0)
        {
            yield break;
        }

        await foreach (var user in db.Users.AsNoTracking()
            .Where(u => memberIds.Contains(u.Id))
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            yield return user;
        }
    }

    private static void Enqueue(DevSupDbContext db, Guid userId, Guid repositoryId, string to, string subject, string body)
    {
        var now = DateTimeOffset.UtcNow;
        db.EmailMessages.Add(new EmailMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            To = to,
            Subject = subject,
            HtmlBody = body,
            NotBefore = NotificationPreferencePolicy.QuietHoursEndUtc(db, userId, repositoryId, now),
            CreatedAt = now
        });
    }
}