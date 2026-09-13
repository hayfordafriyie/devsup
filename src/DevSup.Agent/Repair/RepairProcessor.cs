namespace DevSup.Agent.Repair;

using DevSup.Agent.Git;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

public sealed class RepairWorkerOptions
{
    public int IntervalSeconds { get; init; } = 20;
    public int BatchSize { get; init; } = 5;
    public string GitUserName { get; init; } = "DevSup Bot";
    public string GitUserEmail { get; init; } = "devsup@localhost";
}

/// <summary>
/// Drives one repair ticket through its lifecycle:
/// New → Investigating → (FixPushed | NeedsHumanReview).
/// Claims are idempotent (only tickets still New are picked up), no failure is
/// auto-retried forever, and every outcome — a pushed fix or a hand-off to a
/// human — is reported on the email outbox.
/// </summary>
public sealed class RepairProcessor(
    DevSupDbContext db,
    IGitAdapter git,
    IRepairProvider provider,
    IKeyProtector protector,
    RepairWorkerOptions options,
    ILogger<RepairProcessor> logger)
{
    public async Task<int> ProcessPendingAsync(int batchSize, CancellationToken ct)
    {
        var tickets = await db.RepairTickets
            .Where(t => t.Status == TicketStatus.New && t.Category == FailureCategory.CodeError)
            .OrderBy(t => t.UpdatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        var handled = 0;

        foreach (var ticket in tickets)
        {
            var ticketEntry = db.Entry(ticket);
            ticketEntry.Property(t => t.Status).CurrentValue = TicketStatus.Investigating;
            ticketEntry.Property(t => t.UpdatedAt).CurrentValue = DateTimeOffset.UtcNow;
            handled++;

            var repository = await db.ConnectedRepositories.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == ticket.RepositoryId, ct);
            if (repository is null)
            {
                FinishWithoutPatch(ticket, "The repository for this ticket no longer exists.");
                continue;
            }

            var failure = await db.FailureEvents.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == ticket.FailureEventId, ct);
            if (failure is null)
            {
                FinishWithoutPatch(ticket, "The failure context for this ticket is missing.");
                continue;
            }

            var token = await LookupAccessTokenAsync(repository, ticket, ct);
            if (token is null)
            {
                continue;
            }

            string? workspace = null;
            try
            {
                workspace = await git.CloneAsync(repository.CloneUrl, repository.DefaultBranch, token, ct);
                var proposal = provider.Repair(failure, ticket.Kind, workspace);

                if (proposal.HasPatch && proposal.RelativeFilePath is not null)
                {
                    await ApplyAndPush(ticket, ticketEntry, failure, proposal, workspace, ct);
                    await EnqueueEmailAsync(ticket, failure, repository, pushed: true, ct);
                }
                else
                {
                    FinishWithoutPatch(ticket, proposal.Analysis);
                    await EnqueueEmailAsync(ticket, failure, repository, pushed: false, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Repair failed for ticket {TicketId}", ticket.Id);
                FinishWithoutPatch(ticket, ex.Message);
                await EnqueueEmailAsync(ticket, failure, repository, pushed: false, ct);
            }
            finally
            {
                if (workspace is not null)
                {
                    try
                    {
                        await git.CleanupAsync(workspace, ct);
                    }
                    catch (Exception)
                    {
                        // Best-effort cleanup only.
                    }
                }
            }
        }

        if (tickets.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return handled;
    }

    private async Task ApplyAndPush(
        RepairTicket ticket,
        EntityEntry<RepairTicket> entry,
        FailureEvent failure,
        RepairProposal proposal,
        string workspace,
        CancellationToken ct)
    {
        EnsureWithinWorkspace(workspace, proposal.RelativeFilePath!);
        var fullPath = Path.GetFullPath(Path.Combine(workspace, proposal.RelativeFilePath!));
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        await File.WriteAllTextAsync(fullPath, proposal.RepairedContent ?? string.Empty, ct);

        var commitMessage = proposal.Summary ?? $"DevSup: auto-repair for {failure.Method} {failure.Path}";
        var sha = await git.CommitAndPushAsync(workspace, commitMessage, options.GitUserName, options.GitUserEmail, ct);

        entry.Property(t => t.Status).CurrentValue = TicketStatus.FixPushed;
        entry.Property(t => t.PatchSummary).CurrentValue = proposal.Summary;
        entry.Property(t => t.CommitSha).CurrentValue = sha;
        entry.Property(t => t.Analysis).CurrentValue = proposal.Analysis;
        entry.Property(t => t.LastError).CurrentValue = null;
        entry.Property(t => t.UpdatedAt).CurrentValue = DateTimeOffset.UtcNow;
    }

    /// <summary>Resolves the repo owner's linked provider token, unencrypting it in memory only.</summary>
    private async Task<string?> LookupAccessTokenAsync(ConnectedRepository repository, RepairTicket ticket, CancellationToken ct)
    {
        var tokenRow = await db.OAuthTokens.AsNoTracking()
            .FirstOrDefaultAsync(o => o.UserId == repository.OwnerUserId && o.Provider == repository.Provider, ct);
        if (tokenRow is null)
        {
            FinishWithoutPatch(ticket, "No linked provider token for the repository owner; repair cannot push.");
            return null;
        }

        try
        {
            return protector.Unprotect(tokenRow.EncryptedAccessToken);
        }
        catch (Exception ex)
        {
            FinishWithoutPatch(ticket, $"Could not decrypt the linked provider token: {ex.Message}");
            return null;
        }
    }

    private void FinishWithoutPatch(RepairTicket ticket, string analysis)
    {
        var entry = db.Entry(ticket);
        entry.Property(t => t.Status).CurrentValue = TicketStatus.NeedsHumanReview;
        entry.Property(t => t.Analysis).CurrentValue = analysis;
        if (analysis.Length <= 2048)
        {
            entry.Property(t => t.LastError).CurrentValue = analysis;
        }
        entry.Property(t => t.UpdatedAt).CurrentValue = DateTimeOffset.UtcNow;
    }

    private async Task EnqueueEmailAsync(
        RepairTicket ticket,
        FailureEvent failure,
        ConnectedRepository repository,
        bool pushed,
        CancellationToken ct)
    {
        var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == repository.OwnerUserId, ct);
        if (owner is null)
        {
            return;
        }

        var analysis = ticket.Analysis ?? "";
        var subject = pushed
            ? $"DevSup: fix pushed for {failure.Method} {failure.Path}"
            : $"DevSup: repair for {failure.Method} {failure.Path} needs your review";
        var body = pushed
            ? $"<p>DevSup pushed an automatic fix for the failure on <code>{failure.Method} {failure.Path}</code>.</p>" +
              $"<p><strong>Patch:</strong> {HtmlEncode(ticket.PatchSummary) ?? "auto-repair"}</p>" +
              $"<p><strong>Commit:</strong> <code>{HtmlEncode(ticket.CommitSha)}</code></p>" +
              $"<p>Status: <strong>FixPushed</strong>.</p>"
            : $"<p>DevSup could not safely auto-repair the failure on <code>{failure.Method} {failure.Path}</code>.</p>" +
              $"<p>Ticket status: <strong>NeedsHumanReview</strong>.</p>" +
              $"<p>Agent notes: <code>{HtmlEncode(analysis)}</code></p>";

        db.EmailMessages.Add(new EmailMessage
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            To = owner.Email,
            Subject = subject,
            HtmlBody = body,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static void EnsureWithinWorkspace(string workspace, string relativePath)
    {
        var root = Path.GetFullPath(workspace);
        var candidate = Path.GetFullPath(Path.Combine(workspace, relativePath));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Repaired content resolves outside the repository; refusing to write.");
        }
    }

    private static string? HtmlEncode(string? value) =>
        value is null ? null : System.Net.WebUtility.HtmlEncode(value);
}