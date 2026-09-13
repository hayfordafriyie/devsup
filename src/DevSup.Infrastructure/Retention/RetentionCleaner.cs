using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.Retention;

/// <summary>
/// Purges failure events (and their repair tickets) older than a configurable window,
/// along with webhook deliveries and already-sent emails past that same age. Unsent
/// emails are always kept so pending notifications are never dropped by retention.
/// </summary>
public sealed class RetentionCleaner(
    DevSupDbContext db,
    ILogger<RetentionCleaner> logger)
{
    public async Task<int> PurgeAsync(int windowDays, int batchSize, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var globalCutoff = now.AddDays(-windowDays);

        var overrides = await db.ConnectedRepositories.AsNoTracking()
            .Where(r => r.RetentionDays != null)
            .Select(r => new { r.Id, Days = r.RetentionDays!.Value })
            .ToListAsync(ct);
        var byRepo = overrides.ToDictionary(o => o.Id, o => o.Days);

        bool IsExpired(Guid repositoryId, DateTimeOffset occurredAt)
        {
            if (byRepo.TryGetValue(repositoryId, out var days))
            {
                return days > 0 && occurredAt < now.AddDays(-days);
            }
            return windowDays > 0 && occurredAt < globalCutoff;
        }

        // The most recent cutoff across every rule bounds the candidate set; keep-forever
        // repositories (0 days) are filtered out by IsExpired.
        var cutoffs = new List<DateTimeOffset>();
        if (windowDays > 0)
        {
            cutoffs.Add(globalCutoff);
        }
        cutoffs.AddRange(overrides.Where(o => o.Days > 0).Select(o => now.AddDays(-o.Days)));
        var maxCutoff = cutoffs.Count > 0 ? cutoffs.Max() : DateTimeOffset.MinValue;

        var candidates = await db.FailureEvents
            .Where(f => f.OccurredAt < maxCutoff)
            .OrderBy(f => f.OccurredAt)
            .Take(batchSize * 4)
            .ToListAsync(ct);

        var expiredFailures = candidates
            .Where(f => IsExpired(f.RepositoryId, f.OccurredAt))
            .Take(batchSize)
            .ToList();

        List<WebhookDelivery> expiredDeliveries = [];
        List<EmailMessage> expiredEmails = [];
        if (windowDays > 0)
        {
            expiredDeliveries = await db.WebhookDeliveries
                .Where(d => d.CreatedAt < globalCutoff)
                .Take(batchSize)
                .ToListAsync(ct);

            expiredEmails = await db.EmailMessages
                .Where(m => m.Sent && m.CreatedAt < globalCutoff)
                .Take(batchSize)
                .ToListAsync(ct);
        }

        if (expiredFailures.Count == 0 && expiredDeliveries.Count == 0 && expiredEmails.Count == 0)
        {
            return 0;
        }

        var failureIds = expiredFailures.Select(f => f.Id).ToList();
        var expiredTickets = failureIds.Count == 0
            ? new List<RepairTicket>()
            : await db.RepairTickets
                .Where(t => failureIds.Contains(t.FailureEventId))
                .Take(batchSize)
                .ToListAsync(ct);

        db.RepairTickets.RemoveRange(expiredTickets);
        db.FailureEvents.RemoveRange(expiredFailures);
        db.WebhookDeliveries.RemoveRange(expiredDeliveries);
        db.EmailMessages.RemoveRange(expiredEmails);

        var purged = expiredFailures.Count + expiredTickets.Count
                     + expiredDeliveries.Count + expiredEmails.Count;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Retention purge removed {Count} expired rows (global cutoff {Cutoff:o})", purged, globalCutoff);
        return purged;
    }
}

public sealed class RetentionOptions
{
    /// <summary>Age in days after which events are purged; 0 disables retention.</summary>
    public int WindowDays { get; init; } = 365;

    public int IntervalHours { get; init; } = 24;

    public int BatchSize { get; init; } = 500;
}