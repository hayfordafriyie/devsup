namespace DevSup.Agent.Repair;

using DevSup.Agent.Git;
using DevSup.Agent.PullRequests;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Notifications;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Security;
using DevSup.Infrastructure.Webhooks;
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
    AiRepairProvider aiRepair,
    IPullRequestGateway pullRequests,
    IKeyProtector protector,
    RepairWorkerOptions options,
    ILogger<RepairProcessor> logger)
{
    public async Task<int> ProcessPendingAsync(int batchSize, CancellationToken ct)
    {
        var tickets = await db.RepairTickets
            .Where(t => t.Status == TicketStatus.New && t.Category == FailureCategory.CodeError
                && !db.ConnectedRepositories.Any(r => r.Id == t.RepositoryId && (r.Paused || r.Archived)))
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
                workspace = await git.CloneAsync(repository.CloneUrl, repository.DefaultBranch, repository.Provider, token, ct);
                var aiBinding = await db.AiModelKeyBindings.AsNoTracking()
                    .FirstOrDefaultAsync(k => k.UserId == repository.OwnerUserId, ct);

                RepairProposal proposal;
                if (aiBinding is not null && TryDecryptBinding(aiBinding, out var apiKey))
                {
                    proposal = await aiRepair.GenerateAsync(failure, ticket.Kind, workspace, aiBinding, apiKey, ct);
                    if (!proposal.HasPatch)
                    {
                        // Model had nothing safe to offer — fall back to template repairs.
                        proposal = provider.Repair(failure, ticket.Kind, workspace);
                    }
                }
                else
                {
                    proposal = provider.Repair(failure, ticket.Kind, workspace);
                }

                if (proposal.HasPatch && proposal.RelativeFilePath is not null)
                {
                    if (repository.RepairMode == RepairMode.PullRequest)
                    {
                        await OpenPullRequestAsync(ticket, ticketEntry, failure, repository, token, proposal, workspace, ct);
                    }
                    else
                    {
                        await ApplyAndPush(ticket, ticketEntry, failure, proposal, workspace, ct);
                    }

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

    private async Task OpenPullRequestAsync(
        RepairTicket ticket,
        EntityEntry<RepairTicket> entry,
        FailureEvent failure,
        ConnectedRepository repository,
        string accessToken,
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
        var branch = $"devsup/repair/{ticket.Id:N}";
        var sha = await git.CommitAndPushToBranchAsync(workspace, branch, commitMessage, options.GitUserName, options.GitUserEmail, ct);

        var prTitle = $"DevSup: auto-repair for {failure.Method} {failure.Path}";
        var prBody = proposal.Summary ?? "Automatic repair produced by DevSup. Please review and merge.";

        try
        {
            var prUrl = await pullRequests.OpenAsync(
                repository.Provider, repository.CloneUrl, branch, repository.DefaultBranch,
                prTitle, prBody, accessToken, ct);

            entry.Property(t => t.Status).CurrentValue = TicketStatus.FixPendingReview;
            entry.Property(t => t.PatchSummary).CurrentValue = proposal.Summary;
            entry.Property(t => t.CommitSha).CurrentValue = sha;
            entry.Property(t => t.PullRequestUrl).CurrentValue = prUrl;
            entry.Property(t => t.Analysis).CurrentValue = proposal.Analysis;
            entry.Property(t => t.LastError).CurrentValue = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PR open failed for ticket {TicketId}; handing off for manual review", ticket.Id);

            // Opt-in PR repositories must never be silently patched on the default
            // branch: if the pull request cannot be opened we hand the ticket to a
            // human instead of downgrading to a direct push.
            entry.Property(t => t.Status).CurrentValue = TicketStatus.NeedsHumanReview;
            entry.Property(t => t.PatchSummary).CurrentValue = proposal.Summary;
            entry.Property(t => t.CommitSha).CurrentValue = sha;
            entry.Property(t => t.Analysis).CurrentValue = $"Pull request could not be opened: {ex.Message}";
            entry.Property(t => t.LastError).CurrentValue = $"PR failed: {ex.Message}";
        }

        entry.Property(t => t.UpdatedAt).CurrentValue = DateTimeOffset.UtcNow;
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

    private bool TryDecryptBinding(AiModelKeyBinding binding, out string apiKey)
    {
        try
        {
            apiKey = protector.Unprotect(binding.EncryptedApiKey);
            return !string.IsNullOrWhiteSpace(apiKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not decrypt AI key binding {BindingId}", binding.Id);
            apiKey = string.Empty;
            return false;
        }
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

        // Direct pushes and PR-mode hand-offs differ in subject and action required.
        var isPullRequest = ticket.Status == TicketStatus.FixPendingReview;
        var subject = isPullRequest
            ? $"DevSup: pull request opened for {failure.Method} {failure.Path}"
            : pushed
                ? $"DevSup: fix pushed for {failure.Method} {failure.Path}"
                : $"DevSup: repair for {failure.Method} {failure.Path} needs your review";

        var body = ticket.Status == TicketStatus.FixPendingReview
            ? $"<p>DevSup opened a pull request with an automatic fix for <code>{failure.Method} {failure.Path}</code>.</p>" +
              $"<p><strong>Patch:</strong> {HtmlEncode(ticket.PatchSummary) ?? "auto-repair"}</p>" +
              $"<p><strong>Pull request:</strong> <a href=\"{HtmlEncode(ticket.PullRequestUrl)}\">{HtmlEncode(ticket.PullRequestUrl)}</a></p>" +
              $"<p>Review and merge it when ready.</p>"
            : pushed
                ? $"<p>DevSup pushed an automatic fix for the failure on <code>{failure.Method} {failure.Path}</code>.</p>" +
                  $"<p><strong>Patch:</strong> {HtmlEncode(ticket.PatchSummary) ?? "auto-repair"}</p>" +
                  $"<p><strong>Commit:</strong> <code>{HtmlEncode(ticket.CommitSha)}</code></p>" +
                  $"<p>Status: <strong>FixPushed</strong>.</p>"
                : $"<p>DevSup could not safely auto-repair the failure on <code>{failure.Method} {failure.Path}</code>.</p>" +
                  $"<p>Ticket status: <strong>NeedsHumanReview</strong>.</p>" +
                  $"<p>Agent notes: <code>{HtmlEncode(analysis)}</code></p>";

        var webhookEvent = ticket.Status == TicketStatus.FixPushed
            ? WebhookEvent.FixPushed
            : ticket.Status == TicketStatus.FixPendingReview
                ? WebhookEvent.FixPendingReview
                : WebhookEvent.NeedsHumanReview;

        // The owner may have muted email for this repository/event; webhooks always fan out.
        if (NotificationPreferencePolicy.ShouldSendEmail(db, owner.Id, repository.Id, webhookEvent))
        {
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

        // Operator members are copied on ticket-status emails (theirs via their own
        // preferences); webhooks stay scoped to the owner's endpoints.
        var operatorMemberIds = await db.RepositoryMembers.AsNoTracking()
            .Where(m => m.RepositoryId == repository.Id && m.Role == MemberRole.Operator)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        if (operatorMemberIds.Count > 0)
        {
            var members = await db.Users.AsNoTracking()
                .Where(u => operatorMemberIds.Contains(u.Id))
                .ToListAsync(ct);
            foreach (var member in members)
            {
                if (NotificationPreferencePolicy.ShouldSendEmail(db, member.Id, repository.Id, webhookEvent))
                {
                    db.EmailMessages.Add(new EmailMessage
                    {
                        Id = Guid.NewGuid(),
                        UserId = member.Id,
                        To = member.Email,
                        Subject = subject,
                        HtmlBody = body,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        WebhookQueue.Enqueue(db, owner.Id, webhookEvent, new
        {
            Event = webhookEvent,
            failure = new { failure.Method, failure.Path, failure.StatusCode, FailureId = failure.Id },
            repository = new { repository.Id, repository.CloneUrl, repository.DefaultBranch },
            ticket = new
            {
                ticket.Id,
                Status = ticket.Status,
                ticket.Analysis,
                ticket.PatchSummary,
                ticket.CommitSha,
                ticket.PullRequestUrl
            }
        }, repository.Id);
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