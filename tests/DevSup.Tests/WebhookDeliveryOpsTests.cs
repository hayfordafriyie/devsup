using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevSup.Api;
using DevSup.Core;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class WebhookDeliveryOpsTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public WebhookDeliveryOpsTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Ping_EnqueuesAndDeliversSignedPayload()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "ops-ping@test.dev", "Ops Ping");
        var webhook = await CreateWebhookAsync(token, "https://hooks.example.com/ops-ping");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhook}/test");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var delivery = await response.Content.ReadFromJsonAsync<WebhookDeliveryResponse>(Helpers.ApiJson);
        Assert.NotNull(delivery);
        Assert.Equal("Ping", delivery!.Event);

        await ProcessPendingAsync();
        var sent = _factory.WebhookDeliverer.Delivered.Single(d => d.Url == "https://hooks.example.com/ops-ping");
        Assert.Equal(WebhookEvent.Ping, sent.Event);
        var node = JsonNode.Parse(sent.Payload)!;
        Assert.Equal("devsup.ping", node["event"]!.GetValue<string>());
        Assert.Equal(webhook, Guid.Parse(node["webhookId"]!.GetValue<string>()));

        var unknown = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{Guid.NewGuid()}/test");
        unknown.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(unknown)).StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(1, await db.AuditEntries.AsNoTracking().CountAsync(a => a.Action == "webhook.test"));
    }

    [Fact]
    public async Task Retry_RequeuesFailedDelivery_OwnershipAndConflict()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "ops-retry@test.dev", "Ops Retry");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/ops-retry.git");
        var webhook = await CreateWebhookAsync(token, "https://hooks.example.com/ops-retry");

        _factory.WebhookDeliverer.ThrowOnDeliver = true;
        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "GET", path: "/api/flaky", exceptionMessage: "boom");
        await ProcessPendingAsync();
        var failed = _factory.WebhookDeliverer.Delivered.Count;

        Guid deliveryId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
            var row = await db.WebhookDeliveries.AsNoTracking().SingleAsync();
            deliveryId = row.Id;
            Assert.False(row.Sent);
            Assert.Equal(1, row.Attempts);
            Assert.NotNull(row.LastError);
        }

        var stranger = await Helpers.LoginAndGetTokenAsync(_client, "ops-x@test.dev", "Ops X");
        var foreign = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhook}/deliveries/{deliveryId}/retry");
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(foreign)).StatusCode);

        var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhook}/deliveries/{deliveryId}/retry");
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var retryResponse = await _client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        var reset = await retryResponse.Content.ReadFromJsonAsync<WebhookDeliveryResponse>(Helpers.ApiJson);
        Assert.NotNull(reset);
        Assert.Equal(0, reset!.Attempts);
        Assert.Null(reset.LastError);

        _factory.WebhookDeliverer.ThrowOnDeliver = false;
        await ProcessPendingAsync();
        var nowSent = _factory.WebhookDeliverer.Delivered.Count;
        Assert.Equal(failed + 1, nowSent);

        await using var scope2 = _factory.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var sentRow = await db2.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.Id == deliveryId);
        Assert.True(sentRow.Sent);

        var again = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhook}/deliveries/{deliveryId}/retry");
        again.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task RetryAll_RequeuesFailedDeliveries_OwnershipScoped()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "ops-retryall@test.dev", "Ops RetryAll");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/ops-retryall.git");
        var webhook = await CreateWebhookAsync(token, "https://hooks.example.com/ops-retryall");

        _factory.WebhookDeliverer.ThrowOnDeliver = true;
        await Helpers.IngestAsync(_client, token, repositoryId, 500, "GET", "/api/one", exceptionMessage: "boom");
        await Helpers.IngestAsync(_client, token, repositoryId, 500, "GET", "/api/two", exceptionMessage: "boom");
        await ProcessPendingAsync();

        var stranger = await Helpers.LoginAndGetTokenAsync(_client, "ops-retryall-x@test.dev", "Ops RetryAll X");
        var foreign = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhook}/deliveries/retry-all");
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(foreign)).StatusCode);

        var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhook}/deliveries/retry-all");
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WebhookRetryAllResponse>(Helpers.ApiJson);
        Assert.Equal(2, body!.Requeued);

        _factory.WebhookDeliverer.ThrowOnDeliver = false;
        await ProcessPendingAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(2, await db.WebhookDeliveries.AsNoTracking().CountAsync(d => d.Sent));
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "webhook.retryAll"));
    }

    private async Task<Guid> CreateWebhookAsync(string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { url, channel = "http" }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);
        return created!.Id;
    }

    private Task ProcessPendingAsync()
    {
        return Task.Run(async () =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<WebhookOutboxProcessor>();
            await processor.ProcessPendingAsync(10, CancellationToken.None);
        });
    }
}