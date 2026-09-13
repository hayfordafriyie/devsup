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

public sealed class WebhookDeliveryLogTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public WebhookDeliveryLogTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task DeliveryLog_ListsPastAttempts_MostRecentFirst()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "dl-owner@test.dev", "DL Owner");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/dl.git");
        var created = await CreateWebhookAsync(token, "https://hooks.example.com/dl");
        var webhook = await created.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);

        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "GET", path: "/api/orders");
        await ProcessPendingAsync();
        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "POST", path: "/api/users");
        await ProcessPendingAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/webhooks/{webhook!.Id}/deliveries");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<WebhookDeliveryPage>(Helpers.ApiJson);
        Assert.NotNull(page);
        Assert.Equal(2, page!.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, d => Assert.Equal(WebhookEvent.FailureDetected.ToString(), d.Event));
        Assert.All(page.Items, d => Assert.True(d.Sent));
        Assert.All(page.Items, d => Assert.Equal(1, d.Attempts));
        Assert.All(page.Items, d => Assert.Null(d.LastError));
        Assert.True(page.Items[0].CreatedAt >= page.Items[1].CreatedAt);

        var oldest = page.Items[1];
        Assert.Equal("/api/orders", (JsonNodePayload(oldest.Id)));
    }

    [Fact]
    public async Task DeliveryLog_IsPaged_AndScopedToYourEndpoint()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "dl-page@test.dev", "DL Page");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/dl-page.git");
        var created = await CreateWebhookAsync(token, "https://hooks.example.com/dl-page");
        var webhook = await created.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);

        for (var i = 0; i < 3; i++)
        {
            await Helpers.IngestAsync(_client, token, repositoryId,
                statusCode: 500, method: "GET", path: $"/api/item-{i}");
        }
        await ProcessPendingAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/webhooks/{webhook!.Id}/deliveries?page=1&pageSize=2");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        var page = await response.Content.ReadFromJsonAsync<WebhookDeliveryPage>(Helpers.ApiJson);
        Assert.NotNull(page);
        Assert.Equal(3, page!.Total);
        Assert.Equal(2, page.Items.Count);

        var stranger = await Helpers.LoginAndGetTokenAsync(_client, "dl-stranger@test.dev", "DL Stranger");
        var foreign = new HttpRequestMessage(HttpMethod.Get, $"/api/webhooks/{webhook.Id}/deliveries");
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(foreign)).StatusCode);
    }

    private string JsonNodePayload(Guid deliveryId)
    {
        using var scope2 = _factory.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var delivery = db2.WebhookDeliveries.AsNoTracking().Single(d => d.Id == deliveryId);
        var node = JsonNode.Parse(delivery.Payload)!;
        var failure = node["failure"]!;
        return failure["path"]!.GetValue<string>();
    }

    private async Task<HttpResponseMessage> CreateWebhookAsync(string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { url, channel = "http" }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
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