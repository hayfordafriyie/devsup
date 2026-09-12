using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Core.Services;

namespace DevSup.Tests;

public class FailureClassifierTests
{
    private static FailureEvent Failure(
        int statusCode,
        string? message = null,
        string? response = null)
        => new()
        {
            Id = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            StatusCode = statusCode,
            Method = "GET",
            Path = "/api/orders",
            ExceptionMessage = message,
            ResponsePayload = response,
            OccurredAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public void Classify_401WithCredentialWords_IsNotCodeErrorCredentials()
    {
        var classifier = new FailureClassifier();
        var failure = Failure(401, "Unauthorized", "Your API key or password is invalid");

        var (category, kind) = classifier.Classify(failure);

        Assert.Equal(FailureCategory.NotCodeError, category);
        Assert.Equal(ErrorKind.Credentials, kind);
    }

    [Fact]
    public void Classify_429RateLimit_IsNotCodeErrorRateLimited()
    {
        var classifier = new FailureClassifier();
        var failure = Failure(429, "Rate limit exceeded", null);

        var (category, kind) = classifier.Classify(failure);

        Assert.Equal(FailureCategory.NotCodeError, category);
        Assert.Equal(ErrorKind.RateLimited, kind);
    }

    [Fact]
    public void Classify_NullReferenceException_IsCodeErrorNullReference()
    {
        var classifier = new FailureClassifier();
        var failure = Failure(500, "NullReferenceException: Object reference not set...", null);

        var (category, kind) = classifier.Classify(failure);

        Assert.Equal(FailureCategory.CodeError, category);
        Assert.Equal(ErrorKind.NullReference, kind);
    }

    [Fact]
    public void Classify_SqlException_IsCodeErrorDatabase()
    {
        var classifier = new FailureClassifier();
        var failure = Failure(500, "SqlException: connection refused", null);

        var (category, kind) = classifier.Classify(failure);

        Assert.Equal(FailureCategory.CodeError, category);
        Assert.Equal(ErrorKind.Database, kind);
    }

    [Fact]
    public void Classify_502Upstream_IsNotCodeErrorDownstreamService()
    {
        var classifier = new FailureClassifier();
        var failure = Failure(502, "Bad Gateway", null);

        var (category, kind) = classifier.Classify(failure);

        Assert.Equal(FailureCategory.NotCodeError, category);
        Assert.Equal(ErrorKind.DownstreamService, kind);
    }

    [Fact]
    public void Classify_UnknownError_IsUnknown()
    {
        var classifier = new FailureClassifier();
        var failure = Failure(500, "Something unusual happened", null);

        var (category, kind) = classifier.Classify(failure);

        Assert.Equal(FailureCategory.Unknown, category);
        Assert.Equal(ErrorKind.Unknown, kind);
    }
}