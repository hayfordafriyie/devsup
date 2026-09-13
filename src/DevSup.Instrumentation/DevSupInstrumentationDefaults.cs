namespace DevSup.Instrumentation;

/// <summary>
/// Versioning and header constants shared between the DevSup consumer middleware
/// and the platform ingest endpoint. Consumers report against the schema version
/// they speak; the platform rejects anything it cannot interpret with a
/// 426 Upgrade Required response rather than failing open on garbled events.
/// </summary>
public static class DevSupInstrumentationDefaults
{
    /// <summary>Current schema version negotiated on the wire for captured events.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Human-readable SDK version the middleware reports alongside captures.</summary>
    public const string SdkVersion = "0.6.0";

    /// <summary>Header carrying the negotiated schema version on ingest requests.</summary>
    public const string SchemaVersionHeaderName = "X-DevSup-Schema-Version";
}