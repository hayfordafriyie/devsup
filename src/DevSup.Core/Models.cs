using DevSup.Core;

namespace DevSup.Core.Models;

public sealed record User
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>PBKDF2 hash produced by PasswordHasher&lt;User&gt;, never the plain-text password.</summary>
    public required string PasswordHash { get; init; }

    /// <summary>Platform administrator; promoted from the Admin:Emails configuration at startup.</summary>
    public bool IsAdmin { get; init; }

    /// <summary>When false the account cannot sign in; used by platform admins to suspend a tenant.</summary>
    public bool Active { get; init; } = true;

    /// <summary>When false the daily digest worker skips this user entirely.</summary>
    public bool DigestEnabled { get; init; } = true;

    /// <summary>Cadence for the digest email when enabled (daily or weekly).</summary>
    public DigestFrequency DigestFrequency { get; init; } = DigestFrequency.Daily;

    /// <summary>When the last digest was enqueued for this user; used to honour the cadence.</summary>
    public DateTimeOffset? LastDigestSentAt { get; init; }

    /// <summary>Opaque one-click unsubscribe token embedded in digest emails; null once used or before the first digest.</summary>
    public string? DigestUnsubscribeToken { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record ConnectedRepository
{
    public required Guid Id { get; init; }
    public required Guid OwnerUserId { get; init; }
    public required GitProvider Provider { get; init; }
    public required string CloneUrl { get; init; }
    public required string DefaultBranch { get; init; }

    /// <summary>Public app URL probed periodically for liveness; failures become FailureEvents without the SDK.</summary>
    public string? AppUrl { get; init; }

    /// <summary>How the repair agent lands fixes into this repository.</summary>
    public RepairMode RepairMode { get; init; } = RepairMode.DirectPush;

    /// <summary>Last observed app-URL health: true, false, or null (not checked yet).</summary>
    public bool? AppHealthy { get; init; }

    public DateTimeOffset? AppHealthCheckedAt { get; init; }

    /// <summary>Diagnostic from the last probe (status code or transport error).</summary>
    public string? AppHealthLastError { get; init; }

    /// <summary>When true monitoring is paused: health probes, failure ingest and repair pick-up stop.</summary>
    public bool Paused { get; init; }

    public DateTimeOffset? PausedAt { get; init; }

    /// <summary>When true the repository is retired from the active surface: hidden from lists,
    /// overview and digests, and skipped by every background pipeline. History is retained.</summary>
    public bool Archived { get; init; }

    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>Per-repository retention override in days: null = inherit the global
    /// window, 0 = keep this repo's history forever, &gt;0 = custom window.</summary>
    public int? RetentionDays { get; init; }

    public DateTimeOffset ConnectedAt { get; init; }
}

public sealed record FailureEvent
{
    public required Guid Id { get; init; }
    public required Guid RepositoryId { get; init; }
    public required int StatusCode { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? RequestPayload { get; init; }
    public string? ResponsePayload { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? StackTrace { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

public sealed record RepairTicket
{
    public required Guid Id { get; init; }
    public required Guid FailureEventId { get; init; }
    public required Guid RepositoryId { get; init; }
    public required FailureCategory Category { get; init; }
    public required ErrorKind Kind { get; init; }
    public required TicketStatus Status { get; init; }
    public string? Analysis { get; init; }
    public string? PatchSummary { get; init; }
    public string? CommitSha { get; init; }

    /// <summary>Set when the fix was landed as a pull request awaiting human review and merge.</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>Last diagnostic from the repair agent (clone/push/provider failures).</summary>
    public string? LastError { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record AiModelKeyBinding
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required AiModelProvider Provider { get; init; }
    /// <summary>Model identifier, e.g. claude-sonnet-4, gemini-2.5-pro.</summary>
    public required string Model { get; init; }
    /// <summary>Encrypted at rest in Infrastructure.</summary>
    public required string EncryptedApiKey { get; init; }

    /// <summary>Last 4 characters of the plaintext key, kept for display only.</summary>
    public string? KeyMask { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record EmailMessage
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required string To { get; init; }
    public required string Subject { get; init; }
    public required string HtmlBody { get; init; }
    public bool Sent { get; init; }

    /// <summary>Set when the outbox worker successfully sent the message.</summary>
    public DateTimeOffset? SentAt { get; init; }

    /// <summary>Number of delivery attempts so far (bounded by Emailing:MaxAttempts).</summary>
    public int Attempts { get; init; }

    /// <summary>Last delivery error, kept for diagnostics.</summary>
    public string? LastError { get; init; }

    /// <summary>When set, the outbox worker holds this message until this UTC instant (e.g. quiet hours).</summary>
    public DateTimeOffset? NotBefore { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record OAuthToken
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required GitProvider Provider { get; init; }

    /// <summary>Provider access token, encrypted at rest by Infrastructure's key protector.</summary>
    public required string EncryptedAccessToken { get; init; }

    /// <summary>Space-separated scopes granted by the provider (e.g. "repo user:email").</summary>
    public string? Scope { get; init; }
    public DateTimeOffset LinkedAt { get; init; }
}

public sealed record WebhookEndpoint
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }

    /// <summary>HTTPS endpoint receiving POSTed JSON events.</summary>
    public required string Url { get; init; }

    /// <summary>Human-friendly label, e.g. "#incidents on Slack".</summary>
    public string? Name { get; init; }

    /// <summary>Destination type; Slack/Teams payloads are formatted at delivery time.</summary>
    public WebhookChannel Channel { get; init; } = WebhookChannel.Http;

    /// <summary>Signing secret used to compute the X-DevSup-Signature header, encrypted at rest.</summary>
    public required string EncryptedSecret { get; init; }

    /// <summary>Bitmask of <see cref="WebhookEvent"/> this endpoint receives; 0 means all events.</summary>
    public int EventMask { get; init; }

    /// <summary>Comma-separated repository ids this endpoint is scoped to; null/empty means all repositories.</summary>
    public string? RepositoryIds { get; init; }

    public bool Active { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record WebhookDelivery
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required Guid WebhookId { get; init; }
    public required WebhookEvent Event { get; init; }

    /// <summary>Serialized JSON body delivered to the endpoint.</summary>
    public required string Payload { get; init; }
    public bool Sent { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A registered user with access to another user's repository.</summary>
public sealed record RepositoryMember
{
    public required Guid RepositoryId { get; init; }
    public required Guid UserId { get; init; }
    public MemberRole Role { get; init; } = MemberRole.Operator;
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record NotificationPreference
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required Guid RepositoryId { get; init; }

    /// <summary>Master email switch for this repository.</summary>
    public bool EmailEnabled { get; init; } = true;

    /// <summary>Bitmask of <see cref="WebhookEvent"/> muted for email; 0 means all allowed.</summary>
    public int MutedEmailEvents { get; init; }

    /// <summary>Quiet-hours window start (UTC hour 0-23); null disables quiet hours. May wrap midnight.</summary>
    public int? QuietHoursStart { get; init; }

    /// <summary>Quiet-hours window end (UTC hour 0-23); emails are held until this hour.</summary>
    public int? QuietHoursEnd { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record AuditEntry
{
    public required Guid Id { get; init; }
    public required Guid ActorUserId { get; init; }
    public required string ActorEmail { get; init; }
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public string? IpAddress { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}