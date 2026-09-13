using DevSup.Core;

namespace DevSup.Api;

public sealed record RegisterUserRequest(string Email, string DisplayName, string Password);

public sealed record LoginRequest(string Email, string Password);

public sealed record UserResponse(Guid Id, string Email, string DisplayName);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, UserResponse User);

public sealed record CreateRepositoryRequest(GitProvider Provider, string CloneUrl, string DefaultBranch, string? AppUrl = null, RepairMode RepairMode = RepairMode.DirectPush);

public sealed record RepositoryResponse(
    Guid Id,
    string Provider,
    string CloneUrl,
    string DefaultBranch,
    string? AppUrl,
    RepairMode RepairMode);

public sealed record IngestFailureRequest(
    Guid RepositoryId,
    int StatusCode,
    string Method,
    string Path,
    string? RequestPayload = null,
    string? ResponsePayload = null,
    string? ExceptionMessage = null,
    string? StackTrace = null);

public sealed record IngestResponse(Guid FailureEventId, Guid TicketId, string Category, string Kind, string Status);

public sealed record TicketResponse(
    Guid Id,
    Guid FailureEventId,
    Guid RepositoryId,
    string Category,
    string Kind,
    string Status,
    string? Analysis,
    string? PatchSummary,
    string? CommitSha,
    string? PullRequestUrl,
    DateTimeOffset UpdatedAt);

public sealed record AiKeyRequest(AiModelProvider Provider, string Model, string Key);

public sealed record AiKeyResponse(AiModelProvider Provider, string Model, string? KeyMask, DateTimeOffset UpdatedAt);

public sealed record CreateWebhookRequest(string Url, List<WebhookEvent>? Events = null);

/// <summary>The signing secret is returned only once, in this creation response.</summary>
public sealed record CreateWebhookResponse(Guid Id, string Url, string Secret, List<WebhookEvent> Events, DateTimeOffset CreatedAt);

public sealed record WebhookResponse(Guid Id, string Url, List<WebhookEvent> Events, bool Active, DateTimeOffset CreatedAt);