using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevSup.Api;
using DevSup.Core;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class WebhookRotationTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public WebhookRotationTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Rotate_ReturnsFreshSecret_AndAuditsTheAction()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "rot-owner@test.dev", "Rot Owner");
        var created = await CreateWebhookAsync(token, "https://hooks.example.com/rot");
        var old = await created.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);
        Assert.NotNull(old);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{old!.Id}/rotate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = await response.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);
        Assert.NotNull(rotated);
        Assert.NotEqual(old.Secret, rotated!.Secret);
        Assert.Equal(old.Id, rotated.Id);

        await PromoteToAdminAsync("rot-owner@test.dev");
        var audit = new HttpRequestMessage(HttpMethod.Get, "/api/admin/audit?action=webhook.rotate");
        audit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var auditResponse = await _client.SendAsync(audit);
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        var entries = await auditResponse.Content.ReadFromJsonAsync<List<AuditEntryResponse>>(Helpers.ApiJson);
        var rotate = Assert.Single(entries!);
        Assert.Equal(old.Id.ToString(), rotate.EntityId);
    }

    [Fact]
    public async Task RotatedSecret_IsUsedForSubsequentDeliveries()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "rot-sweep@test.dev", "Rot Sweep");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/rot-sweep.git");
        var created = await CreateWebhookAsync(token, "https://hooks.example.com/rot-sweep");
        var webhook = await created.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);

        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "GET", path: "/api/before-rotation");
        await ProcessPendingAsync();
        var first = _factory.WebhookDeliverer.Delivered.Single(d => d.Url.EndsWith("rot-sweep"));
        Assert.Equal(webhook!.Secret, first.Secret);

        var rotate = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhook.Id}/rotate");
        rotate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var rotated = await (await _client.SendAsync(rotate))
            .Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);

        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "POST", path: "/api/after-rotation");
        await ProcessPendingAsync();

        var second = _factory.WebhookDeliverer.Delivered.Last(d => d.Url.EndsWith("rot-sweep"));
        Assert.Equal(rotated!.Secret, second.Secret);
        Assert.NotEqual(webhook.Secret, second.Secret);
    }

    [Fact]
    public async Task Rotate_IsNotFound_ForForeignOrMissingWebhook()
    {
        await Helpers.LoginAndGetTokenAsync(_client, "rot-owner2@test.dev", "Rot Owner2");
        var stranger = await Helpers.LoginAndGetTokenAsync(_client, "rot-stranger@test.dev", "Rot Stranger");
        var created = await CreateWebhookAsync(stranger, "https://hooks.example.com/other");
        var webhook = await created.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhook!.Id}/rotate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await Helpers.LoginAndGetTokenAsync(_client, "rot-owner2-2@test.dev", "Rot Owner2B"));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(request)).StatusCode);

        request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{Guid.NewGuid()}/rotate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(request)).StatusCode);
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

    private async Task PromoteToAdminAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        db.Entry(user).Property(u => u.IsAdmin).CurrentValue = true;
        await db.SaveChangesAsync();
    }
}