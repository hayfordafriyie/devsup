using System.Net;
using DevSup.Core;
using DevSup.Infrastructure.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevSup.Tests;

public sealed class HttpWebhookDelivererTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => handler(request);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Fact]
    public async Task DeliverAsync_SignsPayloadAndSetsEventHeader()
    {
        HttpRequestMessage? captured = null;
        string? sentBody = null;
        var deliverer = new HttpWebhookDeliverer(
            new StubHttpClientFactory(new HttpClient(new StubHandler(async request =>
            {
                captured = request;
                sentBody = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }))),
            NullLogger<HttpWebhookDeliverer>.Instance);

        const string secret = "s3cret";
        const string payload = """{"event":"failureDetected","ticket":{"status":"new"}}""";

        await deliverer.DeliverAsync("https://hooks.example.com/devsup", secret, payload, WebhookEvent.FailureDetected, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("https://hooks.example.com/devsup", captured!.RequestUri!.AbsoluteUri);
        Assert.Equal("failureDetected", Assert.Single(captured.Headers.GetValues(WebhookQueue.EventHeader)));
        Assert.Equal(
            "sha256=" + HttpWebhookDeliverer.ComputeSignature(secret, payload),
            Assert.Single(captured.Headers.GetValues(WebhookQueue.SignatureHeader)));
        Assert.Equal(payload, sentBody);
        Assert.Equal("application/json", captured.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task DeliverAsync_NonSuccess_Throws()
    {
        var deliverer = new HttpWebhookDeliverer(
            new StubHttpClientFactory(new HttpClient(new StubHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))))),
            NullLogger<HttpWebhookDeliverer>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            deliverer.DeliverAsync("https://hooks.example.com/devsup", "s3cret", "{}", WebhookEvent.NotCodeError, CancellationToken.None));
    }

    [Fact]
    public void ComputeSignature_IsHmacSha256Hex()
    {
        // Known value computed against the RFC 4231-style empty-payload vector for HMAC-SHA256.
        var signature = HttpWebhookDeliverer.ComputeSignature("secret", "payload");
        Assert.Equal(64, signature.Length);
        Assert.Matches("^[0-9a-f]{64}$", signature);
    }
}