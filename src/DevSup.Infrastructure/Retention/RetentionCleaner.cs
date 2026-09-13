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
        if (windowDays <= 0)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-windowDays);

        var expiredFailures = await db.FailureEvents
            .Where(f => f.OccurredAt < cutoff)
            .Take(batchSize)
            .ToListAsync(ct);

        if (expiredFailures.Count == 0)
        {
            return 0;
        }

        var failureIds = expiredFailures.Select(f => f.Id).ToList();

        var expiredTickets = await db.RepairTickets
            .Where(t => failureIds.Contains(t.FailureEventId))
            .Take(batchSize)
            .ToListAsync(ct);

        var expiredDeliveries = await db.WebhookDeliveries
            .Where(d => d.CreatedAt < cutoff)
            .Take(batchSize)
            .ToListAsync(ct);

        var expiredEmails = await db.EmailMessages
            .Where(m => m.Sent && m.CreatedAt < cutoff)
            .Take(batchSize)
            .ToListAsync(ct);

        db.RepairTickets.RemoveRange(expiredTickets);
        db.FailureEvents.RemoveRange(expiredFailures);
        db.WebhookDeliveries.RemoveRange(expiredDeliveries);
        db.EmailMessages.RemoveRange(expiredEmails);

        var purged = expiredFailures.Count + expiredTickets.Count
                     + expiredDeliveries.Count + expiredEmails.Count;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Retention purge removed {Count} expired rows (cutoff {Cutoff:o})", purged, cutoff);
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