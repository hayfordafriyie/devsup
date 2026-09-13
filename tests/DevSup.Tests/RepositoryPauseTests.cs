using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Infrastructure.HealthChecks;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class RepositoryPauseTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public RepositoryPauseTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Pause_ThenOverviewShowsPaused()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "pause-overview@test.dev", "Pause Overview");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/pause-overview.git");

        var pause = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/pause");
        pause.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(pause);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var overview = await (await GetAsync("/api/overview", token)).Content.ReadFromJsonAsync<OverviewResponse>(Helpers.ApiJson);
        var row = overview!.Repositories.Single(r => r.Id == repoId);
        Assert.True(row.Paused);
        Assert.NotNull(row.PausedAt);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(1, await db.AuditEntries.AsNoTracking().CountAsync(a => a.Action == "repository.pause"));
    }

    [Fact]
    public async Task Pause_RejectsIngest_With409()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "pause-ingest@test.dev", "Pause Ingest");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/pause-ingest.git");
        await PauseAsync(repoId, token);

        var ingest = await Helpers.IngestAsync(_client, token, repoId, 500, "GET", "/api/paused");
        Assert.Equal(HttpStatusCode.Conflict, ingest.StatusCode);
    }

    [Fact]
    public async Task Unpause_AllowsIngestAgain()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "pause-resume@test.dev", "Pause Resume");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/pause-resume.git");
        await PauseAsync(repoId, token);

        var unpause = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/unpause");
        unpause.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(unpause)).StatusCode);

        var ingest = await Helpers.IngestAsync(_client, token, repoId, 500, "GET", "/api/resumed");
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repoRow = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repoId);
        Assert.False(repoRow.Paused);
        Assert.Null(repoRow.PausedAt);
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "repository.unpause"));
    }

    [Fact]
    public async Task DoublePause_Returns409()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "pause-twice@test.dev", "Pause Twice");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/pause-twice.git");
        await PauseAsync(repoId, token);

        var pause = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/pause");
        pause.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(pause)).StatusCode);
    }

    [Fact]
    public async Task Unpause_WhenNotPaused_Returns409()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "pause-unpause@test.dev", "Pause Unpause");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/pause-unpause.git");

        var unpause = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/unpause");
        unpause.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(unpause)).StatusCode);
    }

    [Fact]
    public async Task Pause_OtherUsersRepo_Returns404()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "pause-own@test.dev", "Pause Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/pause-own.git");

        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "pause-other@test.dev", "Pause Other");
        var pause = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/pause");
        pause.Headers.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(pause)).StatusCode);
    }

    [Fact]
    public async Task HealthChecker_SkipsPausedRepository()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "pause-hc@test.dev", "Pause Health");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/pause-hc.git");
        await SetAppUrlAsync(repoId, "https://app.pause-hc.example/health");
        await PauseAsync(repoId, token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<AppHealthChecker>();
        await checker.ProbeDueAsync(50, TimeSpan.FromSeconds(5), CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repoRow = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repoId);
        Assert.Null(repoRow.AppHealthCheckedAt);
        Assert.Null(repoRow.AppHealthy);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task PauseAsync(Guid repoId, string token)
    {
        var pause = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/pause");
        pause.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(pause)).StatusCode);
    }

    private async Task SetAppUrlAsync(Guid repositoryId, string appUrl)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repo = await db.ConnectedRepositories.FindAsync(repositoryId);
        Assert.NotNull(repo);
        db.Entry(repo).Property(r => r.AppUrl).CurrentValue = appUrl;
        await db.SaveChangesAsync();
    }
}