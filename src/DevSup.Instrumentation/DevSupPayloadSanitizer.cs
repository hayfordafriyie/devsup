namespace DevSup.Instrumentation;

using System.Text.RegularExpressions;

/// <summary>
/// Pure, dependency-free payload sanitizer used by the DevSup middleware before a
/// captured failure payload leaves the consumer host. It removes high-entropy
/// credentials embedded in the text being reported (Bearer tokens, api keys,
/// generic <c>secret</c>/<c>password</c>/<c>token</c> assignments, AWS keys,
/// private-key blocks and JSON object members whose name signals a secret value)
/// without coupling to any ASP.NET type, so it is trivially unit testable.
/// </summary>
public static partial class DevSupPayloadSanitizer
{
    // Two-pass strategy: first remove whole secret-bearing lines (best-effort),
    // then blank secret-shaped JSON members so a redacted-but-parseable object is
    // reported to the collector.
    private const string Redacted = "REDACTED";

    /// <summary>Gutter in which the report body is kept after sanitization.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var firstPass = SecretLinePattern().Replace(text, string.Empty);
        var secondPass = SecretJsonPattern().Replace(firstPass, "$1" + "\"" + Redacted + "\"");
        return secondPass;
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
