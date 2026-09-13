namespace DevSup.Core;

using System.Text.RegularExpressions;

/// <summary>
/// Pure, dependency-free payload sanitizer guaranteeing that high-entropy
/// credentials embedded in failure data (Bearer tokens, api keys, generic
/// <c>secret</c>/<c>password</c>/<c>token</c> assignments, AWS/private-key
/// blocks and JSON object members whose name signals a secret value) are
/// redacted both on the consumer host (middleware) and at the ingest boundary
/// (platform) — defense in depth with a single implementation.
/// </summary>
public static partial class PayloadSanitizer
{
    // Two-pass strategy: first remove whole secret-bearing lines (best-effort),
    // then blank secret-shaped JSON members so a redacted-but-parseable object is
    // reported to the collector.
    private const string Redacted = "REDACTED";

    /// <summary>Wired ingest schema this platform understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Header consumers use to report the schema version they speak.</summary>
    public const string SchemaVersionHeaderName = "X-DevSup-Schema-Version";

    /// <summary>Gutter in which the report body is kept after sanitization.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var firstPass = SecretLinePattern().Replace(text, string.Empty);
        return SecretJsonPattern().Replace(firstPass, "$1" + "\"" + Redacted + "\"");
    }

    // Whole-line removal: bearer header, token/secret/api-key assignment, private key block.
    [GeneratedRegex(
        "(?im)(^|\\n)" +
        "\\s*" +
        "(?:" +
        "authorization\\s*[:=]\\s*bearer\\s+[a-z0-9._~+/=-]+" +
        "|x-api-key\\s*[:=]\\s*\\S+" +
        "|(?:api[_-]?key|secret|pass(?:word|phrase)?|token|access[_-]?token|client[_-]?secret|private[_-]?key)\\s*[:=]\\s*(?:\"([^\"]*)\"|'([^']*)'|(?![=/?&;#])\\S+)" +
        "|-----BEGIN[^-]*PRIVATE KEY-----.*?-----END[^-]*PRIVATE KEY-----" +
        ")" +
        "[^\\n]*",
        RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex SecretLinePattern();

    // JSON member whose key names a secret: keep the key, blank the value.
    [GeneratedRegex(
        "(\\\"(?:api[_-]?key|secret|pass(?:word|phrase)?|token|access[_-]?token|client[_-]?secret|private[_-]?key|authorization|x-api-key)\\\"\\s*:\\s*)\\\"(?:[^\\\\\\\"]|\\\\[\\s\\S])*\\\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SecretJsonPattern();
}