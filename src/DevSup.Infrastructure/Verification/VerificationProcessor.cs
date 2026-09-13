using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.HealthChecks;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DevSup.Infrastructure.Verification;

public sealed class VerificationOptions
{
    public bool Enabled { get; init; } = true;
    public int IntervalSeconds { get; init; } = 60;
    public int ProbeDelayMinutes { get; init; } = 2;
    public int WindowMinutes { get; init; } = 30;
    public int ProbeTimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// Sweeps tickets that were marked as fixed a while ago (status FixPushed, within the
/// verification window but past the maturity delay) and re-probes the repository's app
/// URL. A healthy probe flips the ticket to FixVerified and mails the owner; anything
/// else is left untouched so a slow deploy still has time to come up.
/// </summary>
public sealed class VerificationProcessor(DevSupDbContext db, IAppUrlProber prober, VerificationOptions options)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!options.Enabled)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var notBefore = now.AddMinutes(-options.ProbeDelayMinutes);
        var deadline = now.AddMinutes(-options.WindowMinutes);

        var candidates = await (from t in db.RepairTickets.AsNoTracking()
                                join r in db.ConnectedRepositories.AsNoTracking() on t.RepositoryId equals r.Id
                                join f in db.FailureEvents.AsNoTracking() on t.FailureEventId equals f.Id
                                where t.Status == TicketStatus.FixPushed
                                    && r.AppUrl != null
                                    && !r.Paused
                                    && !r.Archived
                                    && t.UpdatedAt <= notBefore
                                    && t.UpdatedAt > deadline
                                select new { Ticket = t, Repository = r, Failure = f })
            .ToListAsync(ct);

        var verified = 0;
        foreach (var candidate in candidates)
        {
            var probe = await prober.ProbeAsync(candidate.Repository.AppUrl!,
                TimeSpan.FromSeconds(options.ProbeTimeoutSeconds), ct);
            if (!probe.Healthy)
            {
                continue;
            }

            var ticket = db.RepairTickets.First(t => t.Id == candidate.Ticket.Id);
            var ticketEntry = db.Entry(ticket);
            ticketEntry.Property(t => t.Status).CurrentValue = TicketStatus.FixVerified;
            ticketEntry.Property(t => t.UpdatedAt).CurrentValue = now;

            var owner = db.Users.AsNoTracking().First(u => u.Id == candidate.Repository.OwnerUserId);
            var repoName = Safe(candidate.Repository.CloneUrl);
            db.EmailMessages.Add(new EmailMessage
            {
                Id = Guid.NewGuid(),
                UserId = owner.Id,
                To = owner.Email,
                Subject = $"DevSup: fix verified on {candidate.Failure.Method} {candidate.Failure.Path}",
                HtmlBody = $"<p>DevSup re-checked {repoName} and it now responds healthily.</p>" +
                           $"<p>The fix for <code>{candidate.Failure.Method} {candidate.Failure.Path}</code> " +
                           $"(HTTP {candidate.Failure.StatusCode}) is <strong>verified</strong>.</p>",
                CreatedAt = now
            });

            verified++;
        }

        await db.SaveChangesAsync(ct);
        return verified;
    }

    private static string Safe(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}