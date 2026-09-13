using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class RepositoryMemberTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public RepositoryMemberTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Share_MemberSeesRepoTicketsAndFailures()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "share-owner@test.dev", "Share Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/share-owner.git");
        await Helpers.IngestAsync(_client, ownerToken, repoId, 500, "GET", "/api/boom");

        var memberEmail = "share-member@test.dev";
        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, memberEmail, "Share Member");
        await AddMemberAsync(repoId, ownerToken, memberEmail, "operator");

        var overview = await (await GetAsync("/api/overview", memberToken)).Content.ReadFromJsonAsync<OverviewResponse>(Helpers.ApiJson);
        Assert.Single(overview!.Repositories, r => r.Id == repoId);
        Assert.Equal(1, overview.Tickets.Total);

        var tickets = await (await GetAsync("/api/tickets", memberToken)).Content.ReadFromJsonAsync<List<TicketResponse>>(Helpers.ApiJson);
        Assert.Single(tickets!);

        var failures = await (await GetAsync($"/api/failures?page=1&pageSize=20", memberToken)).Content.ReadFromJsonAsync<FailureHistoryPage>(Helpers.ApiJson);
        Assert.Equal(1, failures!.Total);
    }

    [Fact]
    public async Task Share_MemberSeesRepoInRepositoryList()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "list-owner@test.dev", "List Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/list-owner.git");

        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "list-member@test.dev", "List Member");
        await AddMemberAsync(repoId, ownerToken, "list-member@test.dev", "operator");

        var repos = await (await GetAsync("/api/repositories", memberToken)).Content.ReadFromJsonAsync<List<RepositoryResponse>>(Helpers.ApiJson);
        Assert.Single(repos!, r => r.Id == repoId);
    }

    [Fact]
    public async Task Unshare_RemovesAccess()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "unshare-owner@test.dev", "Unshare Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/unshare-owner.git");

        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "unshare-member@test.dev", "Unshare Member");
        await AddMemberAsync(repoId, ownerToken, "unshare-member@test.dev", "observer");

        var members = await (await GetAsync($"/api/repositories/{repoId}/members", ownerToken)).Content.ReadFromJsonAsync<List<RepositoryMemberResponse>>(Helpers.ApiJson);
        var memberId = members!.Single(m => m.Email == "unshare-member@test.dev").UserId;

        var unshare = new HttpRequestMessage(HttpMethod.Delete, $"/api/repositories/{repoId}/members/{memberId}");
        unshare.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(unshare)).StatusCode);

        var overview = await (await GetAsync("/api/overview", memberToken)).Content.ReadFromJsonAsync<OverviewResponse>(Helpers.ApiJson);
        Assert.Empty(overview!.Repositories);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.False(await db.RepositoryMembers.AsNoTracking().AnyAsync(m => m.RepositoryId == repoId && m.UserId == memberId));
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "repository.unshare"));
    }

    [Fact]
    public async Task Share_UnknownEmail_Returns404()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "share-unknown@test.dev", "Share Unknown");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/share-unknown.git");

        var response = await AddMemberRawAsync(repoId, token, "nobody@nowhere.dev", "operator");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Share_Yourself_Returns404()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "share-self@test.dev", "Share Self");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/share-self.git");

        var response = await AddMemberRawAsync(repoId, token, "share-self@test.dev", "operator");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Share_InvalidRole_Returns400()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "share-role@test.dev", "Share Role");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/share-role.git");
        await Helpers.LoginAndGetTokenAsync(_client, "share-role-member@test.dev", "Role Member");

        var response = await AddMemberRawAsync(repoId, token, "share-role-member@test.dev", "billing");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Share_OtherUsersRepo_Returns404()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "share-own2@test.dev", "Share Own2");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/share-own2.git");

        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "share-other@test.dev", "Share Other");
        await Helpers.LoginAndGetTokenAsync(_client, "share-other-member@test.dev", "Other Member");

        var response = await AddMemberRawAsync(repoId, otherToken, "share-other-member@test.dev", "operator");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListMembers_OtherUsersRepo_Returns404()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "list-own@test.dev", "List Own");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/list-own.git");

        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "list-other@test.dev", "List Other");
        var response = await GetAsync($"/api/repositories/{repoId}/members", otherToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Member_CannotIngestButOwnerCan()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "ingest-own@test.dev", "Ingest Own");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/ingest-own.git");

        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "ingest-member@test.dev", "Ingest Member");
        await AddMemberAsync(repoId, ownerToken, "ingest-member@test.dev", "operator");

        var ingest = await Helpers.IngestAsync(_client, memberToken, repoId, 500, "GET", "/sneaky");
        Assert.Equal(HttpStatusCode.NotFound, ingest.StatusCode);

        var ingestOwner = await Helpers.IngestAsync(_client, ownerToken, repoId, 500, "GET", "/legit");
        Assert.Equal(HttpStatusCode.Created, ingestOwner.StatusCode);
    }

    [Fact]
    public async Task Reshare_UpdatesRole()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "reshare-owner@test.dev", "Reshare Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/reshare-owner.git");
        await Helpers.LoginAndGetTokenAsync(_client, "reshare-member@test.dev", "Reshare Member");

        await AddMemberAsync(repoId, ownerToken, "reshare-member@test.dev", "observer");
        await AddMemberAsync(repoId, ownerToken, "reshare-member@test.dev", "operator");

        var members = await (await GetAsync($"/api/repositories/{repoId}/members", ownerToken)).Content.ReadFromJsonAsync<List<RepositoryMemberResponse>>(Helpers.ApiJson);
        Assert.Single(members!);
        Assert.Equal("Operator", members[0].Role);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(1, await db.RepositoryMembers.AsNoTracking().CountAsync(m => m.RepositoryId == repoId));
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "repository.share"));
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task AddMemberAsync(Guid repoId, string ownerToken, string email, string role)
    {
        var response = await AddMemberRawAsync(repoId, ownerToken, email, role);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> AddMemberRawAsync(Guid repoId, string token, string email, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/members")
        {
            Content = JsonContent.Create(new { email, role })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
}