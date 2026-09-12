using DevSup.Core;
using DevSup.Core.Models;

namespace DevSup.Core.Services;

/// <summary>
/// Decides whether a failure is caused by application code (fixable by the agent)
/// or by an external condition such as bad credentials, unsupported clients, rate
/// limiting, or downstream problems. Non-code failures are skipped and flagged,
/// not patched.
/// </summary>
public interface IFailureClassifier
{
    (FailureCategory Category, ErrorKind Kind) Classify(FailureEvent failure);
}

public sealed class FailureClassifier : IFailureClassifier
{
    public (FailureCategory Category, ErrorKind Kind) Classify(FailureEvent failure)
    {
        var haystack = BuildHaystack(failure);
        var status = failure.StatusCode;
        var method = failure.Method;
        var path = failure.Path;

        if (status is 400 or 401 or 403 or 407)
        {
            if (status == 401 && ContainsAny(haystack, "credential", "password", "unauthorized", "token"))
            {
                return (FailureCategory.NotCodeError, ErrorKind.Credentials);
            }

            if (status == 400 || ContainsAny(haystack, "validation", "bad request", "invalid request"))
            {
                return (FailureCategory.NotCodeError, ErrorKind.InvalidRequest);
            }

            if (status == 403 && ContainsAny(haystack, "forbidden", "permission", "policy"))
            {
                return (FailureCategory.NotCodeError, ErrorKind.Configuration);
            }
        }

        if (status == 407 || ContainsAny(haystack, "proxy authentication", "ntlm", "corporate proxy"))
        {
            return (FailureCategory.NotCodeError, ErrorKind.Configuration);
        }

        if (status == 429 || ContainsAny(haystack, "rate limit", "too many requests", "throttl"))
        {
            return (FailureCategory.NotCodeError, ErrorKind.RateLimited);
        }

        if (ContainsAny(haystack, "nullreferenceexception", "null reference"))
        {
            return (FailureCategory.CodeError, ErrorKind.NullReference);
        }

        if (ContainsAny(haystack, "timeout", "deadline exceeded"))
        {
            return (FailureCategory.CodeError, ErrorKind.Timeout);
        }

        if (ContainsAny(haystack, "sql", "database", "connection refused", "npsql", "mysql"))
        {
            return (FailureCategory.CodeError, ErrorKind.Database);
        }

        if (ContainsAny(haystack, "not found", "404", "dependency missing"))
        {
            return (FailureCategory.CodeError, ErrorKind.NotFound);
        }

        if (ContainsAny(haystack, "connection reset", "502", "503", "504", "upstream", "gateway"))
        {
            return (FailureCategory.NotCodeError, ErrorKind.DownstreamService);
        }

        return (FailureCategory.Unknown, ErrorKind.Unknown);
    }

    private static string BuildHaystack(FailureEvent failure)
    {
        return string.Join(
            " ",
            failure.ExceptionMessage ?? string.Empty,
            failure.StackTrace ?? string.Empty,
            failure.ResponsePayload ?? string.Empty,
            failure.RequestPayload ?? string.Empty,
            failure.Method,
            failure.Path).ToLowerInvariant();
    }

    private static bool ContainsAny(string haystack, params string[] needles)
        => needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));
}