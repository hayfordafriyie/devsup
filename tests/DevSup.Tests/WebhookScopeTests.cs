using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevSup.Api;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class WebhookScopeTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public WebhookScopeTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task ScopedWebhook_OnlyReceivesItsRepositoryEvents()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "scope-owner@test.dev", "Scope Owner");
        var repoA = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/scope-a.git");
        var repoB = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/scope-b.git");
        await CreateWebhookAsync(token, "https://hooks.example.com/scoped", repoA);

        await Helpers.IngestAsync(_client, token, repoA, 500, "GET", "/api/a");
        await Helpers.IngestAsync(_client, token, repoB, 500, "GET", "/api/b");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var deliveries = await db.WebhookDeliveries.AsNoTracking().ToListAsync();
        var delivery = Assert.Single(deliveries);
        Assert.Contains(repoA.ToString(), delivery.Payload);
        Assert.DoesNotContain(repoB.ToString(), delivery.Payload);
    }

    [Fact]
    public async Task UnscopedWebhook_ReceivesAllRepositories()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "scope-all@test.dev", "Scope All");
        var repoA = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/scope-all-a.git");
        var repoB = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/scope-all-b.git");
        await CreateWebhookAsync(token, "https://hooks.example.com/all");

        await Helpers.IngestAsync(_client, token, repoA, 500, "GET", "/api/a");
        await Helpers.IngestAsync(_client, token, repoB, 500, "GET", "/api/b");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(2, await db.WebhookDeliveries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CreateWebhook_RejectsForeignScopeRepository()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "scope-rej-owner@test.dev", "Scope Rej Owner");
        await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/scope-rej-owner.git");

        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "scope-rej-other@test.dev", "Scope Rej Other");
        var foreignRepo = await Helpers.CreateRepositoryAsync(_client, otherToken, "https://github.com/acme/scope-rej-other.git");

        var response = await CreateWebhookRawAsync(ownerToken, "https://hooks.example.com/rej", foreignRepo);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateWebhook_PersistsScopeInList()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "scope-list@test.dev", "Scope List");
        var repoA = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/scope-list-a.git");
        var created = await CreateWebhookAsync(token, "https://hooks.example.com/list", repoA);
        var payload = await created.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);
        Assert.Equal(new[] { repoA }, payload!.RepositoryIds);

        var listed = await (await GetAsync("/api/webhooks", token)).Content.ReadFromJsonAsync<List<WebhookResponse>>(Helpers.ApiJson);
        Assert.Equal(new[] { repoA }, listed!.Single().RepositoryIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Matches_NullOrEmptyScope_MatchesEverything(string? scope)
    {
        Assert.True(WebhookScope.Matches(scope, Guid.NewGuid()));
        Assert.True(WebhookScope.Matches(scope, null));
    }

    [Fact]
    public void Matches_ScopedEndpoint_OnlyMatchesListedRepository()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var scope = a.ToString();

        Assert.True(WebhookScope.Matches(scope, a));
        Assert.False(WebhookScope.Matches(scope, b));
        Assert.False(WebhookScope.Matches(scope, null));
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> CreateWebhookAsync(string token, string url, params Guid[] repositoryIds)
        => CreateWebhookRawAsync(token, url, repositoryIds);

    private async Task<HttpResponseMessage> CreateWebhookRawAsync(string token, string url, params Guid[] repositoryIds)
    {
        var body = repositoryIds.Length == 0
            ? JsonSerializer.Serialize(new { url })
            : JsonSerializer.Serialize(new { url, repositoryIds });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
}