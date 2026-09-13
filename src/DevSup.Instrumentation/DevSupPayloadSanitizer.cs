namespace DevSup.Instrumentation;

/// <summary>
/// Consumer-side entry point for failure-payload sanitization. Delegates to the
/// single shared <see cref="Core.PayloadSanitizer"/> implementation so the
/// middleware and the platform ingest boundary never drift apart.
/// </summary>
public static partial class DevSupPayloadSanitizer
{
    /// <summary>Removes secret-bearing lines and blanks secret-shaped JSON members.</summary>
    public static string Redact(string? text) => Core.PayloadSanitizer.Redact(text);
}