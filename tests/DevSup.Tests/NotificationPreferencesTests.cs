using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevSup.Api;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class NotificationPreferencesTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public NotificationPreferencesTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task EmailsOff_FailureFanOut_StillDeliversWebhooks()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "pref-off@test.dev", "Pref Off");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/pref-off.git");
        await CreateWebhookAsync(token, "https://hooks.example.com/pref-off");

        await IngestAsync(token, repositoryId, "/api/first");
        Assert.Equal(1, await EmailCountAsync("/api/first"));

        var update = await PutPreferenceAsync(token, repositoryId, emailEnabled: false);
        Assert.False(update.EmailEnabled);

        await IngestAsync(token, repositoryId, "/api/second");
        Assert.Equal(0, await EmailCountAsync("/api/second"));

        // Webhooks are independent of email preferences and still fan out.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var deliveries = await db.WebhookDeliveries.AsNoTracking().CountAsync();
        Assert.Equal(2, deliveries);
    }

    [Fact]
    public async Task EventMute_IsPerEvent()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "pref-event@test.dev", "Pref Event");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/pref-event.git");
        await CreateWebhookAsync(token, "https://hooks.example.com/pref-event");

        await PutPreferenceAsync(token, repositoryId, mutedEvents: new[] { "failureDetected" });

        // A code failure maps to FailureDetected: email muted.
        await IngestAsync(token, repositoryId, "/api/code", statusCode: 500, exception: "NullReferenceException");
        Assert.Equal(0, await EmailCountAsync("/api/code"));

        // A 401 maps to NotCodeError: still emailed ("not a code error").
        await IngestAsync(token, repositoryId, "/api/creds", statusCode: 401, exception: "Unauthorized: invalid access token for external API");
        Assert.Equal(1, await EmailCountAsync("not a code error"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var deliveries = await db.WebhookDeliveries.AsNoTracking().CountAsync();
        Assert.Equal(2, deliveries);
    }

    [Fact]
    public async Task Preferences_ListAndOwnership()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "pref-list@test.dev", "Pref List");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/pref-list.git");

        await PutPreferenceAsync(token, repositoryId, emailEnabled: false, mutedEvents: new[] { "NeedsHumanReview", "FixPushed" });

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/notification-preferences");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        var rows = await response.Content.ReadFromJsonAsync<List<NotificationPreferenceResponse>>(Helpers.ApiJson);
        Assert.NotNull(rows);
        var row = Assert.Single(rows!);
        Assert.False(row.EmailEnabled);
        Assert.Contains("NeedsHumanReview", row.MutedEvents);
        Assert.Contains("FixPushed", row.MutedEvents);
        Assert.Contains("pref-list.git", row.CloneUrl);

        // Someone else cannot change your preference.
        var stranger = await Helpers.LoginAndGetTokenAsync(_client, "pref-x@test.dev", "Pref X");
        var foreign = new HttpRequestMessage(HttpMethod.Put, "/api/notification-preferences")
        {
            Content = JsonContent.Create(new { repositoryId, emailEnabled = true })
        };
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(foreign)).StatusCode);

        // Unknown repository id is 404.
        var missing = new HttpRequestMessage(HttpMethod.Put, "/api/notification-preferences")
        {
            Content = JsonContent.Create(new { repositoryId = Guid.NewGuid(), emailEnabled = true })
        };
        missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(missing)).StatusCode);
    }

    private async Task<NotificationPreferenceResponse> PutPreferenceAsync(string token, Guid repositoryId, bool? emailEnabled = null, string[]? mutedEvents = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/notification-preferences")
        {
            Content = JsonContent.Create(new { repositoryId, emailEnabled, mutedEvents })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<NotificationPreferenceResponse>(Helpers.ApiJson);
        Assert.NotNull(body);
        return body!;
    }

    private async Task IngestAsync(string token, Guid repositoryId, string path, int statusCode = 500, string? exception = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                repositoryId,
                statusCode,
                method = "GET",
                path,
                exceptionMessage = exception
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(request)).EnsureSuccessStatusCode();
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

    private async Task<int> EmailCountAsync(string partialSubject)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        return await db.EmailMessages.AsNoTracking().CountAsync(m => m.Subject.Contains(partialSubject));
    }
}