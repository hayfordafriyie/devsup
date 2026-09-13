using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Notifications;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Webhooks;

namespace DevSup.Api.Notifications;

/// <summary>
/// Shared email + webhook fan-out for failure events. Used by the ingest path and by
/// event replay so a replayed failure produces the same notifications as the original.
/// </summary>
public static class FailureReporter
{
    public static void Notify(
        DevSupDbContext db,
        User owner,
        ConnectedRepository repository,
        FailureEvent failure,
        RepairTicket ticket,
        FailureCategory category,
        bool replay = false)
    {
        var notCodeError = category == FailureCategory.NotCodeError;
        var prefix = replay ? "DevSup: replaying — " : "DevSup: ";
        var emailEvent = notCodeError ? WebhookEvent.NotCodeError : WebhookEvent.FailureDetected;

        // The owner may have muted email for this repository/event; webhooks always fan out.
        if (NotificationPreferencePolicy.ShouldSendEmail(db, owner.Id, repository.Id, emailEvent))
        {
            db.EmailMessages.Add(new EmailMessage
            {
                Id = Guid.NewGuid(),
                UserId = owner.Id,
                To = owner.Email,
                Subject = notCodeError
                    ? $"{prefix}not a code error on {failure.Method} {failure.Path}"
                    : $"{prefix}failure detected on {failure.Method} {failure.Path}",
                HtmlBody = notCodeError
                    ? $"<p>DevSup detected a <strong>{ticket.Kind}</strong> failure on <code>{failure.Method} {failure.Path}</code> " +
                      $"(HTTP {failure.StatusCode}).</p>" +
                      $"<p>This was classified as <em>not a code error</em>, so the repair agent will <strong>not</strong> " +
                      $"attempt a code fix and no patch is scheduled. Review the credentials, client, rate limits, or " +
                      $"downstream services instead.</p>"
                    : $"<p>DevSup detected a failure on <code>{failure.Method} {failure.Path}</code> " +
                      $"with status <strong>{failure.StatusCode}</strong>.</p>" +
                      $"<p>Classification: <strong>{ticket.Kind}</strong> ({category}).</p>" +
                      $"<p>Next step: ticket <strong>{ticket.Status}</strong> — the agent will investigate code errors.</p>",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        WebhookQueue.Enqueue(db, owner.Id, emailEvent, new
        {
            failure = new { failure.Method, failure.Path, failure.StatusCode, FailureId = failure.Id },
            repository = new { repository.Id, repository.CloneUrl, repository.DefaultBranch },
            ticket = new { ticket.Id, Status = ticket.Status, Kind = ticket.Kind }
        });
    }
}