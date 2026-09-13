using System.Text;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DevSup.Infrastructure.Digest;

public sealed class DigestOptions
{
    public bool Enabled { get; init; } = true;
    public int IntervalHours { get; init; } = 24;
    public int MaxOpenTickets { get; init; } = 10;
}

/// <summary>
/// Enqueues one daily summary email per active user, covering failures detected, open
/// repair tickets and recently pushed fixes within the digest window. Users with nothing
/// to report are skipped — no noise, no empty digests.
/// </summary>
public sealed class DigestProcessor(
    DevSupDbContext db,
    DigestOptions options)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var users = await db.Users.Where(u => u.Active && u.DigestEnabled).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var generated = 0;

        foreach (var user in users)
        {
            var cadenceHours = user.DigestFrequency == DigestFrequency.Weekly ? 168 : 24;
            if (user.LastDigestSentAt is not null && now - user.LastDigestSentAt.Value < TimeSpan.FromHours(cadenceHours))
            {
                continue;
            }

            var window = now.AddHours(-cadenceHours);
            var ownedIds = await db.ConnectedRepositories.AsNoTracking()
                .Where(r => r.OwnerUserId == user.Id && !r.Paused && !r.Archived)
                .Select(r => r.Id)
                .ToListAsync(ct);
            var sharedIds = await db.ConnectedRepositories.AsNoTracking()
                .Where(r => r.OwnerUserId != user.Id
                    && db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == user.Id)
                    && !r.Paused && !r.Archived)
                .Select(r => r.Id)
                .ToListAsync(ct);
            var repoIds = ownedIds.Concat(sharedIds).ToList();
            if (repoIds.Count == 0)
            {
                continue;
            }

            var recentFailureRepoIds = await db.FailureEvents.AsNoTracking()
                .Where(f => repoIds.Contains(f.RepositoryId) && f.OccurredAt >= window)
                .Select(f => f.RepositoryId)
                .ToListAsync(ct);

            var open = await db.RepairTickets.AsNoTracking()
                .Where(t => repoIds.Contains(t.RepositoryId)
                    && (t.Status == TicketStatus.New
                        || t.Status == TicketStatus.Triaged
                        || t.Status == TicketStatus.Investigating
                        || t.Status == TicketStatus.PatchProposed
                        || t.Status == TicketStatus.FixPendingReview))
                .ToListAsync(ct);

            var recentFixRepoIds = await db.RepairTickets.AsNoTracking()
                .Where(t => repoIds.Contains(t.RepositoryId)
                    && t.Status == TicketStatus.FixPushed
                    && t.UpdatedAt >= window)
                .Select(t => t.RepositoryId)
                .ToListAsync(ct);

            var ownedSet = ownedIds.ToHashSet();
            var all = new DigestCounts(recentFailureRepoIds.Count, open.Count, recentFixRepoIds.Count);
            var owned = new DigestCounts(
                recentFailureRepoIds.Count(id => ownedSet.Contains(id)),
                open.Count(t => ownedSet.Contains(t.RepositoryId)),
                recentFixRepoIds.Count(id => ownedSet.Contains(id)));
            var shared = new DigestCounts(all.Failures - owned.Failures, all.Open - owned.Open, all.Fixed - owned.Fixed);

            if (all.Failures == 0 && all.Open == 0 && all.Fixed == 0)
            {
                continue;
            }

            db.EmailMessages.Add(new EmailMessage
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                To = user.Email,
                Subject = $"DevSup daily summary: {all.Failures} failure(s), {all.Open} open ticket(s), {all.Fixed} fix(es)",
                HtmlBody = BuildDigest(user, ownedIds.Count, sharedIds.Count, all, owned, shared, open, cadenceHours),
                CreatedAt = now
            });
            db.Entry(user).Property(u => u.LastDigestSentAt).CurrentValue = now;
            generated++;
        }

        if (generated > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return generated;
    }

    private string BuildDigest(User user, int ownedCount, int sharedCount, DigestCounts all, DigestCounts owned, DigestCounts shared, List<RepairTicket> open, int windowHours)
    {
        var sb = new StringBuilder();
        sb.Append("<h3>DevSup daily summary</h3>");
        var scope = sharedCount > 0
            ? $"<strong>{ownedCount + sharedCount}</strong> repositories ({ownedCount} yours, {sharedCount} shared with you)"
            : $"<strong>{ownedCount}</strong> connected repositories";
        sb.Append($"<p>Hi <strong>{Escape(user.DisplayName)}</strong>, here's what happened across your " +
                  $"{scope} in the last <strong>{windowHours}</strong> hour(s):</p>");
        sb.Append("<ul>");
        sb.Append($"<li>Failures detected: <strong>{all.Failures}</strong></li>");
        sb.Append($"<li>Open repair tickets: <strong>{all.Open}</strong></li>");
        sb.Append($"<li>Fixes pushed: <strong>{all.Fixed}</strong></li>");
        sb.Append("</ul>");

        if (sharedCount > 0)
        {
            sb.Append($"<h4>Your repositories ({ownedCount})</h4>");
            sb.Append(CountsList(owned));
            sb.Append($"<h4>Shared with you ({sharedCount})</h4>");
            sb.Append(CountsList(shared));
        }

        if (open.Count > 0)
        {
            sb.Append("<p><strong>Open tickets:</strong></p><ul>");
            foreach (var ticket in open.Take(options.MaxOpenTickets))
            {
                sb.Append($"<li><code>{Escape(ticket.Kind.ToString())}</code> — " +
                          $"{Escape(ticket.Status.ToString())} (updated {ticket.UpdatedAt:g})</li>");
            }

            if (open.Count > options.MaxOpenTickets)
            {
                sb.Append($"<li>…and {open.Count - options.MaxOpenTickets} more</li>");
            }

            sb.Append("</ul>");
        }

        sb.Append("<p style=\"color: #888\">Automated summary · <em>DevSup</em></p>");
        return sb.ToString();
    }

    private static string CountsList(DigestCounts counts)
        => "<ul>" +
           $"<li>Failures detected: <strong>{counts.Failures}</strong></li>" +
           $"<li>Open repair tickets: <strong>{counts.Open}</strong></li>" +
           $"<li>Fixes pushed: <strong>{counts.Fixed}</strong></li>" +
           "</ul>";

    private readonly record struct DigestCounts(int Failures, int Open, int Fixed);

    private static string Escape(string? value)
        => (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}