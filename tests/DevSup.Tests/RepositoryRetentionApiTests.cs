using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class RepositoryRetentionApiTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public RepositoryRetentionApiTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task SetRetention_UpdatesAndAudits()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "ret-api@test.dev", "Ret Api");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/ret-api.git");

        Assert.Equal(HttpStatusCode.OK, (await PutRetentionAsync(repoId, token, 30)).StatusCode);

        var repos = await (await GetAsync("/api/repositories", token)).Content.ReadFromJsonAsync<List<RepositoryResponse>>(Helpers.ApiJson);
        Assert.Equal(30, repos!.Single(r => r.Id == repoId).RetentionDays);

        Assert.Equal(HttpStatusCode.OK, (await PutRetentionAsync(repoId, token, null)).StatusCode);
        repos = await (await GetAsync("/api/repositories", token)).Content.ReadFromJsonAsync<List<RepositoryResponse>>(Helpers.ApiJson);
        Assert.Null(repos!.Single(r => r.Id == repoId).RetentionDays);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(2, await db.AuditEntries.AsNoTracking().CountAsync(a => a.Action == "repository.retention"));
    }

    [Fact]
    public async Task SetRetention_Negative_Returns400()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "ret-neg@test.dev", "Ret Neg");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/ret-neg.git");

        Assert.Equal(HttpStatusCode.BadRequest, (await PutRetentionAsync(repoId, token, -1)).StatusCode);
    }

    [Fact]
    public async Task SetRetention_OtherUsersRepo_Returns404()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "ret-owner@test.dev", "Ret Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/ret-owner.git");
        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "ret-other@test.dev", "Ret Other");

        Assert.Equal(HttpStatusCode.NotFound, (await PutRetentionAsync(repoId, otherToken, 10)).StatusCode);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PutRetentionAsync(Guid repoId, string token, int? days)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/repositories/{repoId}/retention")
        {
            Content = JsonContent.Create(new { retentionDays = days })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
}