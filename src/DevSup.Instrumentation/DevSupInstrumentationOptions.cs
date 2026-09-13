namespace DevSup.Instrumentation;

public sealed class DevSupInstrumentationOptions
{
    /// <summary>DevSup ingest endpoint URL, e.g. https://devsup.io/ingest.</summary>
    public required Uri IngestEndpoint { get; set; }

    /// <summary>Per-project API token issued by the DevSup platform.</summary>
    public required string ApiToken { get; set; }

    /// <summary>Repository id this app belongs to (from the platform).</summary>
    public required Guid RepositoryId { get; set; }

    /// <summary>
    /// Schema version the instrumented application speaks. Must be &lt;= the version
    /// accepted by the ingest endpoint; DevSup returns 426 Upgrade Required when a
    /// consumer reports against an unsupported (too-new) schema.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Maximum request/response payload size captured per failure (chars).</summary>
    public int MaxCapturedPayloadLength { get; set; } = 4_096;

    /// <summary>Header names to strip from captured request metadata.</summary>
    public HashSet<string> SensitiveHeaderNames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "X-Api-Key",
        "Cookie",
        "Set-Cookie"
    };

    /// <summary>Failure status codes considered worth reporting.</summary>
    public int[] FailureStatusCodes { get; set; } = [400, 401, 403, 404, 405, 408, 409, 422, 429, 500, 502, 503, 504];
}