using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Agent.Repair;
using DevSup.Api;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class MemberNotificationTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public MemberNotificationTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Ingest_NotifiesOperatorMembers_NotObservers()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "mnf-owner@test.dev", "MNF Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/mnf-owner.git");

        var operatorToken = await Helpers.LoginAndGetTokenAsync(_client, "mnf-operator@test.dev", "MNF Operator");
        var observerToken = await Helpers.LoginAndGetTokenAsync(_client, "mnf-observer@test.dev", "MNF Observer");
        await AddMemberAsync(repoId, ownerToken, "mnf-operator@test.dev", "operator");
        await AddMemberAsync(repoId, ownerToken, "mnf-observer@test.dev", "observer");

        await Helpers.IngestAsync(_client, ownerToken, repoId, 500, "GET", "/api/orders");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.True(await db.EmailMessages.AsNoTracking().AnyAsync(m =>
            m.To == "mnf-owner@test.dev" && m.Subject.Contains("failure detected")));
        Assert.True(await db.EmailMessages.AsNoTracking().AnyAsync(m =>
            m.To == "mnf-operator@test.dev" && m.Subject.Contains("failure detected")));
        Assert.False(await db.EmailMessages.AsNoTracking().AnyAsync(m => m.To == "mnf-observer@test.dev"));
    }

    [Fact]
    public async Task RepairFixPushed_NotifiesOperatorMembers()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "mnf-repair-owner@test.dev", "MNF Repair Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/healme.git");

        await LinkGitHubTokenAsync("mnf-repair-owner@test.dev", "gho_test_access_token");
        var operatorToken = await Helpers.LoginAndGetTokenAsync(_client, "mnf-repair-op@test.dev", "MNF Repair Op");
        await AddMemberAsync(repoId, ownerToken, "mnf-repair-op@test.dev", "operator");

        var ingest = await Helpers.IngestAsync(
            _client, ownerToken, repoId,
            statusCode: 500, method: "GET", path: "/api/orders",
            exceptionMessage: "NullReferenceException: Object reference not set to an instance of an object.");
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        await processor.ProcessPendingAsync(10, CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.True(await db.EmailMessages.AsNoTracking().AnyAsync(m =>
            m.To == "mnf-repair-op@test.dev" && m.Subject.Contains("fix pushed")));
    }

    [Fact]
    public async Task Replay_NotifiesOperatorMembers()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "mnf-replay-owner@test.dev", "MNF Replay Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/mnf-replay.git");

        await Helpers.LoginAndGetTokenAsync(_client, "mnf-replay-op@test.dev", "MNF Replay Op");
        await AddMemberAsync(repoId, ownerToken, "mnf-replay-op@test.dev", "operator");

        var ingest = await Helpers.IngestAsync(_client, ownerToken, repoId, 500, "GET", "/api/replay");
        var ingestBody = await ingest.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.NotNull(ingestBody);
        await ClearEmailBoxAsync();

        var replay = new HttpRequestMessage(HttpMethod.Post, $"/api/failures/{ingestBody!.FailureEventId}/replay");
        replay.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        Assert.Equal(HttpStatusCode.Accepted, (await _client.SendAsync(replay)).StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.True(await db.EmailMessages.AsNoTracking().AnyAsync(m =>
            m.To == "mnf-replay-op@test.dev" && m.Subject.Contains("replaying")));
    }

    private async Task ClearEmailBoxAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        db.EmailMessages.RemoveRange(db.EmailMessages);
        await db.SaveChangesAsync();
    }

    private async Task LinkGitHubTokenAsync(string email, string tokenValue)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Email == email);
        var protector = scope.ServiceProvider.GetRequiredService<IKeyProtector>();
        db.OAuthTokens.Add(new OAuthToken
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Provider = GitProvider.GitHub,
            EncryptedAccessToken = protector.Protect(tokenValue),
            Scope = "repo",
            LinkedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task AddMemberAsync(Guid repoId, string token, string email, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{repoId}/members")
        {
            Content = JsonContent.Create(new { email, role })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}