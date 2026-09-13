using DevSup.Instrumentation;

namespace DevSup.Tests;

/// <summary>
/// Pure unit tests for the two-pass payload sanitizer: secret-bearing whole
/// lines are dropped and secret-shaped JSON members are blanked to REDACTED.
/// </summary>
public sealed class DevSupPayloadSanitizerTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc123.def456", "abc123")]
    [InlineData("authorization = bearer tok-1234", "tok")]
    [InlineData("X-Api-Key: sk_live_supersecret", "sk_live")]
    [InlineData("api_key = \"some-secret-key\"", "some-secret-key")]
    [InlineData("password: hunter2", "hunter2")]
    [InlineData("client_secret='c-s-e-c-r-e-t'", "c-s-e-c-r-e-t")]
    [InlineData("access_token=ghp_deadbeef", "ghp_deadbeef")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END PRIVATE KEY-----", "MIIEowIBAAKCAQEA")]
    public void Redact_RemovesWholeSecretBearingLine(string input, string secret)
    {
        var result = DevSupPayloadSanitizer.Redact(input);
        Assert.DoesNotContain(secret, result);
    }

    [Theory]
    [InlineData("{\"token\":\"sk-123\",\"name\":\"alice\"}", "sk-123")]
    [InlineData("{\"api_key\":\"AKIAIOSFODNN7EXAMPLE\",\"region\":\"eu\"}", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("{\"client_secret\":\"topsecret\",\"kid\":\"k\"}", "topsecret")]
    [InlineData("{\"password\":\"hunter2\"}", "hunter2")]
    public void Redact_BlanksSecretShapedJsonMembers(string input, string secret)
    {
        var result = DevSupPayloadSanitizer.Redact(input);
        Assert.DoesNotContain(secret, result);
        Assert.Contains("\"REDACTED\"", result);
    }

    [Fact]
    public void Redact_KeepsNonSecretContentIntact()
    {
        const string text = "GET /api/orders => 500. User alice. CorrelationId 42.";
        Assert.Equal(text, DevSupPayloadSanitizer.Redact(text));
    }

    [Fact]
    public void Redact_Null_ReturnsEmpty() => Assert.Equal(string.Empty, DevSupPayloadSanitizer.Redact(null));

    [Fact]
    public void Redact_EmptyOrWhitespace_ReturnsOriginal()
    {
        Assert.Equal(string.Empty, DevSupPayloadSanitizer.Redact(string.Empty));
        Assert.Equal("  ", DevSupPayloadSanitizer.Redact("  "));
    }
}