using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Core;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class OwnershipTransferTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public OwnershipTransferTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Transfer_ToOperatorMember_ReassignsOwnerAndDemotesPreviousOwner()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "tr-owner@test.dev", "TR Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/tr-owner.git");
        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "tr-member@test.dev", "TR Member");
        await AddMemberAsync(repoId, ownerToken, "tr-member@test.dev", "operator");

        var response = await PostJsonAsync($"/api/repositories/{repoId}/transfer", ownerToken, new { email = "tr-member@test.dev" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repo = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repoId);
        var newOwner = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "tr-member@test.dev");
        var oldOwner = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "tr-owner@test.dev");
        Assert.Equal(newOwner.Id, repo.OwnerUserId);

        var members = await db.RepositoryMembers.AsNoTracking().Where(m => m.RepositoryId == repoId).ToListAsync();
        Assert.DoesNotContain(members, m => m.UserId == newOwner.Id);
        var demoted = Assert.Single(members, m => m.UserId == oldOwner.Id);
        Assert.Equal(MemberRole.Operator, demoted.Role);
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "repository.transfer"));
        Assert.True(await db.EmailMessages.AsNoTracking().AnyAsync(m => m.To == "tr-member@test.dev" && m.Subject.Contains("now own")));

        var overview = await (await GetAsync("/api/overview", memberToken)).Content.ReadFromJsonAsync<OverviewResponse>(Helpers.ApiJson);
        Assert.Single(overview!.Repositories, r => r.Id == repoId);

        var oldOwnerOverview = await (await GetAsync("/api/overview", ownerToken)).Content.ReadFromJsonAsync<OverviewResponse>(Helpers.ApiJson);
        Assert.Single(oldOwnerOverview!.Repositories, r => r.Id == repoId);
    }

    [Fact]
    public async Task Transfer_ToObserverMember_Returns400()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "tr-obs-owner@test.dev", "TR Obs Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/tr-obs.git");
        await Helpers.LoginAndGetTokenAsync(_client, "tr-obs-member@test.dev", "TR Obs Member");
        await AddMemberAsync(repoId, ownerToken, "tr-obs-member@test.dev", "observer");

        var response = await PostJsonAsync($"/api/repositories/{repoId}/transfer", ownerToken, new { email = "tr-obs-member@test.dev" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_ToNonMember_Returns400()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "tr-non-owner@test.dev", "TR Non Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/tr-non.git");
        await Helpers.LoginAndGetTokenAsync(_client, "tr-non-member@test.dev", "TR Non Member");

        var response = await PostJsonAsync($"/api/repositories/{repoId}/transfer", ownerToken, new { email = "tr-non-member@test.dev" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_OtherUsersRepo_Returns404()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "tr-other-owner@test.dev", "TR Other Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/tr-other.git");
        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "tr-other@test.dev", "TR Other");
        await Helpers.LoginAndGetTokenAsync(_client, "tr-other-member@test.dev", "TR Other Member");

        var response = await PostJsonAsync($"/api/repositories/{repoId}/transfer", otherToken, new { email = "tr-other-member@test.dev" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_WhenTargetAlreadyHasSameRepo_Returns409()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "tr-dup-owner@test.dev", "TR Dup Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/tr-dup.git");
        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "tr-dup-member@test.dev", "TR Dup Member");
        await Helpers.CreateRepositoryAsync(_client, memberToken, "https://github.com/acme/tr-dup.git");
        await AddMemberAsync(repoId, ownerToken, "tr-dup-member@test.dev", "operator");

        var response = await PostJsonAsync($"/api/repositories/{repoId}/transfer", ownerToken, new { email = "tr-dup-member@test.dev" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Leave_RemovesMembershipAndAccess()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "leave-owner@test.dev", "Leave Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/leave-owner.git");
        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "leave-member@test.dev", "Leave Member");
        await AddMemberAsync(repoId, ownerToken, "leave-member@test.dev", "operator");

        var leave = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/leave");
        leave.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(leave)).StatusCode);

        var overview = await (await GetAsync("/api/overview", memberToken)).Content.ReadFromJsonAsync<OverviewResponse>(Helpers.ApiJson);
        Assert.Empty(overview!.Repositories);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var member = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "leave-member@test.dev");
        Assert.False(await db.RepositoryMembers.AsNoTracking().AnyAsync(m => m.RepositoryId == repoId && m.UserId == member.Id));
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "repository.leave"));
    }

    [Fact]
    public async Task Leave_Owner_Returns409()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "leave-self@test.dev", "Leave Self");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/leave-self.git");

        var leave = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/leave");
        leave.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(leave)).StatusCode);
    }

    [Fact]
    public async Task Leave_NonMember_Returns404()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "leave-non-owner@test.dev", "Leave Non Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/leave-non.git");
        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "leave-non@test.dev", "Leave Non");

        var leave = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/leave");
        leave.Headers.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(leave)).StatusCode);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string path, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task AddMemberAsync(Guid repoId, string token, string email, string role)
    {
        var response = await PostJsonAsync($"/api/repositories/{repoId}/members", token, new { email, role });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}