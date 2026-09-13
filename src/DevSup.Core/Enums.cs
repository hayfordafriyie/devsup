namespace DevSup.Core;

public enum GitProvider
{
    GitHub,
    GitLab
}

/// <summary>
/// How the repair agent lands a fix into the connected repository.
/// </summary>
public enum RepairMode
{
    /// <summary>Commit and push the fix straight to the default branch.</summary>
    DirectPush,

    /// <summary>Open a pull request for human review and merge; the agent never pushes to the default branch.</summary>
    PullRequest
}

/// <summary>The provider a webhook endpoint delivers to; controls payload formatting.</summary>
public enum WebhookChannel
{
    /// <summary>Raw JSON body exactly as queued, signed with the endpoint secret.</summary>
    Http,

    /// <summary>Formatted as an incoming-webhook message (mrkdwn blocks).</summary>
    Slack,

    /// <summary>Formatted as an Office 365 / Teams MessageCard.</summary>
    Teams
}

/// <summary>Domain events broadcast to configured webhook endpoints.</summary>
public enum WebhookEvent
{
    FailureDetected,
    FixPushed,
    FixPendingReview,
    NeedsHumanReview,
    NotCodeError,

    /// <summary>Synthetic connectivity check sent by <c>POST /api/webhooks/{id}/test</c>.</summary>
    Ping
}

public enum AiModelProvider
{
    AnthropicClaude,
    GoogleGemini,
    DeepSeek,
    OpenAi,
    Ollama
}

public enum FailureCategory
{
    Unknown,
    CodeError,
    NotCodeError
}

public enum ErrorKind
{
    Unknown,
    NullReference,
    Timeout,
    Database,
    Configuration,
    Credentials,
    NotFound,
    RateLimited,
    InvalidRequest,
    DownstreamService,
    Other
}

public enum TicketStatus
{
    New,
    Triaged,
    Investigating,
    PatchProposed,

    /// <summary>Fix landed on a feature branch and is awaiting human review/merge via a pull request.</summary>
    FixPendingReview,
    FixPushed,
    FixVerified,
    Closed,
    NeedsHumanReview,
    SkippedNotCodeError
}

/// <summary>Role a member has on a shared repository: observe (read) or operate (read + triage).</summary>
public enum MemberRole
{
    Observer,
    Operator
}

/// <summary>How often a user receives the activity digest email.</summary>
public enum DigestFrequency
{
    Daily,
    Weekly
}