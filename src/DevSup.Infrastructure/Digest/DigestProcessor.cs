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
        var window = DateTimeOffset.UtcNow.AddHours(-options.IntervalHours);
        var users = await db.Users.AsNoTracking().Where(u => u.Active).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var generated = 0;

        foreach (var user in users)
        {
            var repoIds = await db.ConnectedRepositories.AsNoTracking()
                .Where(r => r.OwnerUserId == user.Id)
                .Select(r => r.Id)
                .ToListAsync(ct);
            if (repoIds.Count == 0)
            {
                continue;
            }

            var failures = await db.FailureEvents.AsNoTracking()
                .CountAsync(f => repoIds.Contains(f.RepositoryId) && f.OccurredAt >= window, ct);

            var open = await db.RepairTickets.AsNoTracking()
                .Where(t => repoIds.Contains(t.RepositoryId)
                    && (t.Status == TicketStatus.New
                        || t.Status == TicketStatus.Triaged
                        || t.Status == TicketStatus.Investigating
                        || t.Status == TicketStatus.PatchProposed
                        || t.Status == TicketStatus.FixPendingReview))
                .ToListAsync(ct);

            var fixedRecently = await db.RepairTickets.AsNoTracking()
                .CountAsync(t => repoIds.Contains(t.RepositoryId)
                    && t.Status == TicketStatus.FixPushed
                    && t.UpdatedAt >= window, ct);

            if (failures == 0 && open.Count == 0 && fixedRecently == 0)
            {
                continue;
            }

            db.EmailMessages.Add(new EmailMessage
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                To = user.Email,
                Subject = $"DevSup daily summary: {failures} failure(s), {open.Count} open ticket(s), {fixedRecently} fix(es)",
                HtmlBody = BuildDigest(user, repoIds.Count, failures, open, fixedRecently),
                CreatedAt = now
            });
            generated++;
        }

        if (generated > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return generated;
    }

    private string BuildDigest(User user, int repositoryCount, int failures, List<RepairTicket> open, int fixedRecently)
    {
        var sb = new StringBuilder();
        sb.Append("<h3>DevSup daily summary</h3>");
        sb.Append($"<p>Hi <strong>{Escape(user.DisplayName)}</strong>, here's what happened across your " +
                  $"<strong>{repositoryCount}</strong> connected repositories in the last <strong>{options.IntervalHours}</strong> hour(s):</p>");
        sb.Append("<ul>");
        sb.Append($"<li>Failures detected: <strong>{failures}</strong></li>");
        sb.Append($"<li>Open repair tickets: <strong>{open.Count}</strong></li>");
        sb.Append($"<li>Fixes pushed: <strong>{fixedRecently}</strong></li>");
        sb.Append("</ul>");

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

    private static string Escape(string? value)
        => (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}