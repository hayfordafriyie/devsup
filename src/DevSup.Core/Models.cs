using DevSup.Core;

namespace DevSup.Core.Models;

public sealed record User
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>PBKDF2 hash produced by PasswordHasher&lt;User&gt;, never the plain-text password.</summary>
    public required string PasswordHash { get; init; }
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

    /// <summary>Signing secret used to compute the X-DevSup-Signature header, encrypted at rest.</summary>
    public required string EncryptedSecret { get; init; }

    /// <summary>Bitmask of <see cref="WebhookEvent"/> this endpoint receives; 0 means all events.</summary>
    public int EventMask { get; init; }

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