using DevSup.Core;

namespace DevSup.Api;

public sealed record RegisterUserRequest(string Email, string DisplayName, string Password);

public sealed record LoginRequest(string Email, string Password);

public sealed record UserResponse(Guid Id, string Email, string DisplayName);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, UserResponse User);

public sealed record UpdateAccountRequest(string DisplayName);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record AccountResponse(Guid Id, string Email, string DisplayName, bool IsAdmin, bool Active, DateTimeOffset CreatedAt);

public sealed record CreateRepositoryRequest(GitProvider Provider, string CloneUrl, string DefaultBranch, string? AppUrl = null, RepairMode RepairMode = RepairMode.DirectPush);

public sealed record RepositoryResponse(
    Guid Id,
    string Provider,
    string CloneUrl,
    string DefaultBranch,
    string? AppUrl,
    RepairMode RepairMode,
    bool? AppHealthy = null,
    DateTimeOffset? AppHealthCheckedAt = null,
    string? AppHealthLastError = null);

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
    DateTimeOffset UpdatedAt,
    string? Method = null,
    string? Path = null,
    int? StatusCode = null);

public sealed record AiKeyRequest(AiModelProvider Provider, string Model, string Key);

public sealed record AiKeyResponse(AiModelProvider Provider, string Model, string? KeyMask, DateTimeOffset UpdatedAt);

public sealed record CreateWebhookRequest(string Url, List<WebhookEvent>? Events = null, string? Name = null, WebhookChannel Channel = WebhookChannel.Http);

/// <summary>The signing secret is returned only once, in this creation response.</summary>
public sealed record CreateWebhookResponse(Guid Id, string Url, string Secret, List<WebhookEvent> Events, DateTimeOffset CreatedAt, string? Name, WebhookChannel Channel);

public sealed record WebhookResponse(Guid Id, string Url, List<WebhookEvent> Events, bool Active, DateTimeOffset CreatedAt, string? Name, WebhookChannel Channel);

public sealed record RepositoryHealthRow(
    Guid Id,
    string CloneUrl,
    string? AppUrl,
    bool? AppHealthy,
    DateTimeOffset? AppHealthCheckedAt,
    string? AppHealthLastError);

public sealed record TicketSummary(
    int New,
    int InProgress,
    int PendingReview,
    int Fixed,
    int NeedsHumanReview,
    int Total);

/// <summary>Cross-repository aggregation used by dashboards.</summary>
public sealed record OverviewResponse(
    int RepositoryCount,
    int HealthyRepos,
    int UnhealthyRepos,
    int UncheckedRepos,
    IReadOnlyList<RepositoryHealthRow> Repositories,
    TicketSummary Tickets);

public sealed record AdminUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsAdmin,
    bool Active,
    DateTimeOffset CreatedAt,
    int RepositoryCount,
    int TicketCount);

public sealed record AdminOverviewResponse(
    int TotalUsers,
    int ActiveUsers,
    int TotalRepositories,
    int TotalFailures,
    int OpenTickets,
    int WebhookEndpoints);

public sealed record AuditEntryResponse(
    Guid Id,
    string ActorEmail,
    string Action,
    string EntityType,
    string? EntityId,
    string? Before,
    string? After,
    string? IpAddress,
    DateTimeOffset Timestamp);

public sealed record FailureHistoryRow(
    Guid Id,
    Guid RepositoryId,
    int StatusCode,
    string Method,
    string Path,
    string? ExceptionMessage,
    DateTimeOffset OccurredAt,
    Guid? TicketId,
    string? TicketStatus,
    string? Category,
    string? Kind,
    string? PatchSummary,
    string? CommitSha);

public sealed record FailureHistoryPage(
    IReadOnlyList<FailureHistoryRow> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record WebhookDeliveryResponse(
    Guid Id,
    string Event,
    bool Sent,
    DateTimeOffset? SentAt,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt);

public sealed record WebhookDeliveryPage(
    IReadOnlyList<WebhookDeliveryResponse> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record NotificationPreferenceRequest(
    Guid RepositoryId,
    bool? EmailEnabled = null,
    List<string>? MutedEvents = null);

public sealed record NotificationPreferenceResponse(
    Guid RepositoryId,
    string CloneUrl,
    bool EmailEnabled,
    List<string> MutedEvents,
    DateTimeOffset UpdatedAt);