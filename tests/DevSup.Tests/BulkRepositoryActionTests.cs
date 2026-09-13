using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class BulkRepositoryActionTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public BulkRepositoryActionTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Bulk_Pause_ChangesOwnedRepositories()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "bulk-pause@test.dev", "Bulk Pause");
        var a = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/bulk-a.git");
        var b = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/bulk-b.git");

        var response = await BulkAsync(token, "pause", a, b);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BulkRepositoryActionResponse>(Helpers.ApiJson);
        Assert.Equal("pause", body!.Action);
        Assert.All(body.Results, r => Assert.Equal("ok", r.Status));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(2, await db.ConnectedRepositories.AsNoTracking().CountAsync(r => r.Paused));
        Assert.Equal(2, await db.AuditEntries.AsNoTracking().CountAsync(x => x.Action == "repository.pause"));
    }

    [Fact]
    public async Task Bulk_AlreadyInState_ReportsUnchanged()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "bulk-unchanged@test.dev", "Bulk Unchanged");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/bulk-unchanged.git");
        await BulkAsync(token, "pause", repoId);

        var body = await (await BulkAsync(token, "pause", repoId)).Content.ReadFromJsonAsync<BulkRepositoryActionResponse>(Helpers.ApiJson);
        var item = Assert.Single(body!.Results);
        Assert.Equal("unchanged", item.Status);
    }

    [Fact]
    public async Task Bulk_FlagsUnownedAndUnknownRepositories()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "bulk-owner@test.dev", "Bulk Owner");
        var owned = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/bulk-owned.git");

        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "bulk-other@test.dev", "Bulk Other");
        var foreign = await Helpers.CreateRepositoryAsync(_client, otherToken, "https://github.com/acme/bulk-foreign.git");
        var missing = Guid.NewGuid();

        var body = await (await BulkAsync(ownerToken, "pause", owned, foreign, missing)).Content.ReadFromJsonAsync<BulkRepositoryActionResponse>(Helpers.ApiJson);
        Assert.Equal("ok", body!.Results.Single(r => r.RepositoryId == owned).Status);
        Assert.Equal("forbidden", body.Results.Single(r => r.RepositoryId == foreign).Status);
        Assert.Equal("notFound", body.Results.Single(r => r.RepositoryId == missing).Status);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.False((await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == foreign)).Paused);
    }

    [Fact]
    public async Task Bulk_Archive_HidesRepositoriesAndUnarchiveRestores()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "bulk-archive@test.dev", "Bulk Archive");
        var a = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/bulk-arch-a.git");
        var b = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/bulk-arch-b.git");

        await BulkAsync(token, "archive", a, b);
        var active = await (await GetAsync("/api/repositories", token)).Content.ReadFromJsonAsync<List<RepositoryResponse>>(Helpers.ApiJson);
        Assert.Empty(active!);
        var archived = await (await GetAsync("/api/repositories?archived=true", token)).Content.ReadFromJsonAsync<List<RepositoryResponse>>(Helpers.ApiJson);
        Assert.Equal(2, archived!.Count);

        await BulkAsync(token, "unarchive", a, b);
        var restored = await (await GetAsync("/api/repositories", token)).Content.ReadFromJsonAsync<List<RepositoryResponse>>(Helpers.ApiJson);
        Assert.Equal(2, restored!.Count);
    }

    [Fact]
    public async Task Bulk_InvalidAction_Returns400()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "bulk-invalid@test.dev", "Bulk Invalid");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/bulk-invalid.git");
        Assert.Equal(HttpStatusCode.BadRequest, (await BulkAsync(token, "explode", repoId)).StatusCode);
    }

    [Fact]
    public async Task Bulk_NoIds_Returns400()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "bulk-empty@test.dev", "Bulk Empty");
        var response = await PostJsonAsync("/api/repositories/bulk", token, new { action = "pause", repositoryIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> BulkAsync(string token, string action, params Guid[] ids) =>
        PostJsonAsync("/api/repositories/bulk", token, new { action, repositoryIds = ids });

    private async Task<HttpResponseMessage> PostJsonAsync(string path, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
}