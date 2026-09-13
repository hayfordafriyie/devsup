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