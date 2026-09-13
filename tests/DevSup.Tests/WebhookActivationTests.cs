using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class WebhookActivationTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public WebhookActivationTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private static async Task<Guid> CreateWebhookAsync(HttpClient client, string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks")
        {
            Content = JsonContent.Create(new { url, channel = "http", name = "activation-test", events = Array.Empty<string>() })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WebhookResponse>(Helpers.ApiJson))!.Id;
    }

    [Fact]
    public async Task Deactivate_ThenActivate_RoundTrip()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-active@test.dev", "Wh Active");
        var webhookId = await CreateWebhookAsync(_client, token, "https://hooks.example.com/active");

        var deactivate = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhookId}/deactivate");
        deactivate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var deactResp = await _client.SendAsync(deactivate);
        Assert.Equal(HttpStatusCode.OK, deactResp.StatusCode);

        var list = await (await GetAsync("/api/webhooks", token)).Content.ReadFromJsonAsync<List<WebhookResponse>>(Helpers.ApiJson);
        var row = list!.Single(w => w.Id == webhookId);
        Assert.False(row.Active);

        var activate = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhookId}/activate");
        activate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(activate)).StatusCode);

        var relist = await (await GetAsync("/api/webhooks", token)).Content.ReadFromJsonAsync<List<WebhookResponse>>(Helpers.ApiJson);
        Assert.True(relist!.Single(w => w.Id == webhookId).Active);
    }

    [Fact]
    public async Task Deactivate_Twice_Returns409()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-pause@test.dev", "Wh Pause");
        var webhookId = await CreateWebhookAsync(_client, token, "https://hooks.example.com/pause");
        await ActivateAsync("deactivate", webhookId, token);

        var again = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhookId}/deactivate");
        again.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task Activate_WhenAlreadyActive_Returns409()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-resume@test.dev", "Wh Resume");
        var webhookId = await CreateWebhookAsync(_client, token, "https://hooks.example.com/resume");

        var activate = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhookId}/activate");
        activate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(activate)).StatusCode);
    }

    [Fact]
    public async Task Deactivate_OtherUsersWebhook_Returns404()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "wh-own@test.dev", "Wh Owner");
        var webhookId = await CreateWebhookAsync(_client, ownerToken, "https://hooks.example.com/own");

        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "wh-other@test.dev", "Wh Other");
        var deactivate = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhookId}/deactivate");
        deactivate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(deactivate)).StatusCode);
    }

    [Fact]
    public async Task Deactivate_IsAudited()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-audit@test.dev", "Wh Audit");
        var webhookId = await CreateWebhookAsync(_client, token, "https://hooks.example.com/audit");
        await ActivateAsync("deactivate", webhookId, token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "webhook.deactivate"));
    }

    private async Task ActivateAsync(string action, Guid webhookId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhookId}/{action}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
}