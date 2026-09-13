using System.Net;
using System.Text.Json;
using DevSup.Instrumentation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DevSup.Tests;

/// <summary>
/// Exercises the DevSup consumer middleware end to end against a local TestHost:
/// a request that throws must produce a capture POST to the ingest endpoint with
/// (a) the configurable schema-version header, (b) SDK version metadata, and
/// (c) a sanitized request payload that has secret-shaped values redacted.
/// </summary>
public sealed class DevSupInstrumentationTests : IDisposable
{
    private readonly FakeCapturingHandler _capture;
    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly Guid _repositoryId = Guid.NewGuid();

    public DevSupInstrumentationTests()
    {
        _capture = new FakeCapturingHandler();
        _server = new TestServer(
            new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddHttpClient("devsup").ConfigurePrimaryHttpMessageHandler(() => _capture);
                })
                .Configure(app =>
                {
                    app.UseDevSup(new DevSupInstrumentationOptions
                    {
                        IngestEndpoint = new Uri("http://devsup.local/api/ingest"),
                        ApiToken = "test-token",
                        RepositoryId = _repositoryId
                    }).Run(async context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/boom"))
                        {
                            throw new InvalidOperationException("boom orchestration failure");
                        }

                        await context.Response.WriteAsync("fine");
                    });
                }));
        _client = _server.CreateClient();
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task CapturesInternalServerError_AndReportsSanitizedVersionedPayload()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/boom")
        {
            Content = new StringContent(
                "{\"user\":\"alice\",\"token\":\"sk-live-abcdef0123456789\",\"password\":\"hunter2\"}",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Authorization", "Bearer s3cr3t-header-token");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _client.SendAsync(request));
        Assert.Equal("boom orchestration failure", ex.Message);

        Assert.NotNull(_capture.CapturedMessage);

        var capture = _capture.CapturedMessage!;

        Assert.Equal("http://devsup.local/api/ingest", capture.RequestUri!.AbsoluteUri);
        Assert.Equal("1", capture.Headers.GetValues(DevSupInstrumentationDefaults.SchemaVersionHeaderName).Single());
        Assert.Equal("Bearer test-token", capture.Headers.GetValues("Authorization").Single());

        var body = await capture.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(_repositoryId, root.GetProperty("repositoryId").GetGuid());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(DevSupInstrumentationDefaults.SdkVersion, root.GetProperty("sdkVersion").GetString());

        var payload = root.GetProperty("requestPayload").GetString();
        Assert.DoesNotContain("sk-live", payload);
        Assert.DoesNotContain("hunter2", payload);
        Assert.DoesNotContain("s3cr3t-header-token", payload);
    }

    private sealed class FakeCapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedMessage { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedMessage = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }
}