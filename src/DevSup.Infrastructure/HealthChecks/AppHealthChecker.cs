using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Core.Services;
using DevSup.Infrastructure.Email;
using DevSup.Infrastructure.Notifications;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.HealthChecks;

/// <summary>
/// Probes the app URL of each connected repository and, when a healthy (or unknown)
/// repo turns unhealthy, records a synthetic failure — failure event, repair ticket,
/// email and webhook — exactly as if the SDK had reported it. Consecutive failures
/// only create one ticket (no spam) until the app recovers.
/// </summary>
public sealed class AppHealthChecker(
    DevSupDbContext db,
    IAppUrlProber prober,
    IFailureClassifier classifier,
    ILogger<AppHealthChecker> logger)
{
    public async Task<int> ProbeDueAsync(int batchSize, TimeSpan timeout, CancellationToken ct)
    {
        var repositories = await db.ConnectedRepositories
            .Where(r => r.AppUrl != null && r.AppUrl != "" && !r.Paused && !r.Archived)
            .Take(batchSize)
            .ToListAsync(ct);

        var newFailures = 0;

        foreach (var repository in repositories)
        {
            var wasHealthy = repository.AppHealthy;
            var result = await prober.ProbeAsync(repository.AppUrl!, timeout, ct);
            var entry = db.Entry(repository);
            entry.Property(r => r.AppHealthCheckedAt).CurrentValue = DateTimeOffset.UtcNow;
            entry.Property(r => r.AppHealthLastError).CurrentValue = result.Error;

            if (result.Healthy)
            {
                entry.Property(r => r.AppHealthy).CurrentValue = true;
                continue;
            }

            entry.Property(r => r.AppHealthy).CurrentValue = false;

            // Only report the very first transition into an unhealthy state.
            if (wasHealthy == false)
            {
                continue;
            }

            await RecordSyntheticFailureAsync(repository, result, ct);
            newFailures++;
        }

        if (repositories.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return newFailures;
    }

    private async Task RecordSyntheticFailureAsync(ConnectedRepository repository, AppProbeResult result, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var failure = new FailureEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            StatusCode = result.StatusCode,
            Method = "GET",
            Path = repository.AppUrl!,
            ExceptionMessage = result.Error,
            OccurredAt = now
        };

        db.FailureEvents.Add(failure);

        var (category, kind) = classifier.Classify(failure);
        var status = category == FailureCategory.NotCodeError ? TicketStatus.SkippedNotCodeError : TicketStatus.New;

        db.RepairTickets.Add(new RepairTicket
        {
            Id = Guid.NewGuid(),
            FailureEventId = failure.Id,
            RepositoryId = repository.Id,
            Category = category,
            Kind = kind,
            Status = status,
            UpdatedAt = now
        });

        var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == repository.OwnerUserId, ct);
        if (owner is null)
        {
            return;
        }

        var notCodeError = category == FailureCategory.NotCodeError;
        db.EmailMessages.Add(new EmailMessage
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            To = owner.Email,
            Subject = $"DevSup: app health check failed on {repository.AppUrl}",
            HtmlBody = notCodeError
                ? $"<p>The health check for <code>{repository.AppUrl}</code> returned <strong>{result.Error ?? "an error"}</strong> " +
                  $"and was classified as <em>not a code error</em>, so no repair is scheduled.</p>"
                : $"<p>The health check for <code>{repository.AppUrl}</code> returned <strong>{result.Error}</strong>.</p>" +
                  $"<p>A ticket has been created and the repair agent will investigate.</p>",
            NotBefore = NotificationPreferencePolicy.QuietHoursEndUtc(db, owner.Id, repository.Id, now),
            CreatedAt = now
        });

        WebhookQueue.Enqueue(db, owner.Id,
            notCodeError ? WebhookEvent.NotCodeError : WebhookEvent.FailureDetected,
            new
            {
                Event = notCodeError ? WebhookEvent.NotCodeError : WebhookEvent.FailureDetected,
                source = "appHealthCheck",
                failure = new { failure.Method, failure.Path, failure.StatusCode, FailureId = failure.Id },
                repository = new { repository.Id, repository.CloneUrl, repository.DefaultBranch, repository.AppUrl }
            }, repository.Id);
    }
}