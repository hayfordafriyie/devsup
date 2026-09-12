namespace DevSup.Core;

public enum GitProvider
{
    GitHub,
    GitLab
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
    FixPushed,
    FixVerified,
    Closed,
    SkippedNotCodeError
}