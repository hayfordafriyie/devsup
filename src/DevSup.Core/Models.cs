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
    public string? AppUrl { get; init; }
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
}

public sealed record EmailMessage
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required string To { get; init; }
    public required string Subject { get; init; }
    public required string HtmlBody { get; init; }
    public bool Sent { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}