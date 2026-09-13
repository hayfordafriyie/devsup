using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class RoleEnforcementTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public RoleEnforcementTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Observer_CannotCloseTicket_Returns403()
    {
        var (repoId, ownerToken, ticketId) = await SeedSharedTicketAsync("role-obs-close", "observer");
        var observerToken = await Helpers.LoginAndGetTokenAsync(_client, "role-obs-close-member@test.dev", "Observer");

        var response = await PostAsync($"/api/tickets/{ticketId}/close", observerToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Observer_CannotReopenTicket_Returns403()
    {
        var (repoId, ownerToken, ticketId) = await SeedSharedTicketAsync("role-obs-reopen", "observer");
        var observerToken = await Helpers.LoginAndGetTokenAsync(_client, "role-obs-reopen-member@test.dev", "Observer");

        await PostAsync($"/api/tickets/{ticketId}/close", ownerToken).WaitAsync(TimeSpan.FromSeconds(10));
        var response = await PostAsync($"/api/tickets/{ticketId}/reopen", observerToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Observer_CannotRedispatchTicket_Returns403()
    {
        var (repoId, ownerToken, ticketId) = await SeedSharedTicketAsync("role-obs-rd", "observer");
        var observerToken = await Helpers.LoginAndGetTokenAsync(_client, "role-obs-rd-member@test.dev", "Observer");

        var response = await PostAsync($"/api/tickets/{ticketId}/redispatch", observerToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Operator_CanTriageSharedTicket()
    {
        var (repoId, ownerToken, ticketId) = await SeedSharedTicketAsync("role-op", "operator");
        var operatorToken = await Helpers.LoginAndGetTokenAsync(_client, "role-op-member@test.dev", "Operator");

        var close = await PostAsync($"/api/tickets/{ticketId}/close", operatorToken);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);

        var reopen = await PostAsync($"/api/tickets/{ticketId}/reopen", operatorToken);
        Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
    }

    [Fact]
    public async Task TicketDetail_ReportsCanTriage()
    {
        var (repoId, ownerToken, ticketId) = await SeedSharedTicketAsync("role-flag", "operator");
        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "role-flag-member@test.dev", "Operator");

        var ownerDetail = await (await GetAsync($"/api/tickets/{ticketId}", ownerToken)).Content.ReadFromJsonAsync<TicketDetailResponse>(Helpers.ApiJson);
        Assert.True(ownerDetail!.CanTriage);

        var operatorDetail = await (await GetAsync($"/api/tickets/{ticketId}", memberToken)).Content.ReadFromJsonAsync<TicketDetailResponse>(Helpers.ApiJson);
        Assert.True(operatorDetail!.CanTriage);

        await Helpers.LoginAndGetTokenAsync(_client, "role-obs-flag-member@test.dev", "Observer");
        await AddMemberAsync(repoId, ownerToken, "role-obs-flag-member@test.dev", "observer");
        var observerToken = await Helpers.LoginAndGetTokenAsync(_client, "role-obs-flag-member@test.dev", "Observer");
        var observerDetail = await (await GetAsync($"/api/tickets/{ticketId}", observerToken)).Content.ReadFromJsonAsync<TicketDetailResponse>(Helpers.ApiJson);
        Assert.False(observerDetail!.CanTriage);
    }

    private async Task<(Guid RepoId, string OwnerToken, Guid TicketId)> SeedSharedTicketAsync(string prefix, string memberRole)
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, $"{prefix}-owner@test.dev", "Role Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, $"https://github.com/acme/{prefix}-owner.git");
        var ingest = await Helpers.IngestAsync(_client, ownerToken, repoId, 500, "GET", "/api/boom");
        var ingestBody = await ingest.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.NotNull(ingestBody);

        if (memberRole != "_nobody")
        {
            var memberEmail = $"{prefix}-member@test.dev";
            await Helpers.LoginAndGetTokenAsync(_client, memberEmail, "Member");
            await AddMemberAsync(repoId, ownerToken, memberEmail, memberRole);
        }

        return (repoId, ownerToken, ingestBody!.TicketId);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
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