using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using Xunit;

namespace DevSup.Tests;

public sealed class RepositoryActivityTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public RepositoryActivityTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Activity_ListsLifecycleAndMemberEvents()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "act-owner@test.dev", "Act Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/act-owner.git");
        await Helpers.LoginAndGetTokenAsync(_client, "act-member@test.dev", "Act Member");
        await AddMemberAsync(repoId, token, "act-member@test.dev", "operator");
        await PostAsync($"/api/repositories/{repoId}/pause", token);

        var items = await (await GetAsync($"/api/repositories/{repoId}/activity", token)).Content.ReadFromJsonAsync<List<RepositoryActivityItem>>(Helpers.ApiJson);
        Assert.NotNull(items);
        Assert.Contains(items!, i => i.Action == "repository.connect");
        Assert.Contains(items!, i => i.Action == "repository.share");
        Assert.Contains(items!, i => i.Action == "repository.pause");
        Assert.All(items!, i => Assert.Equal("act-owner@test.dev", i.ActorEmail));
        Assert.Equal(items!.OrderByDescending(i => i.Timestamp).Select(i => i.Id), items.Select(i => i.Id));
    }

    [Fact]
    public async Task Activity_IncludesTicketTriage()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "act-ticket@test.dev", "Act Ticket");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/act-ticket.git");
        var ingest = await Helpers.IngestAsync(_client, token, repoId, 500, "GET", "/api/boom");
        var body = await ingest.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/api/tickets/{body!.TicketId}/close", token)).StatusCode);

        var items = await (await GetAsync($"/api/repositories/{repoId}/activity", token)).Content.ReadFromJsonAsync<List<RepositoryActivityItem>>(Helpers.ApiJson);
        Assert.Contains(items!, i => i.Action == "ticket.close");
    }

    [Fact]
    public async Task Activity_MemberCanView()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "act-v-owner@test.dev", "Act V Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/act-v.git");
        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "act-v-member@test.dev", "Act V Member");
        await AddMemberAsync(repoId, ownerToken, "act-v-member@test.dev", "observer");

        var response = await GetAsync($"/api/repositories/{repoId}/activity", memberToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<RepositoryActivityItem>>(Helpers.ApiJson);
        Assert.Contains(items!, i => i.Action == "repository.share");
    }

    [Fact]
    public async Task Activity_NonMember_Returns404()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "act-404-owner@test.dev", "Act 404 Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/act-404.git");
        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "act-404-other@test.dev", "Act 404 Other");

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/api/repositories/{repoId}/activity", otherToken)).StatusCode);
    }

    [Fact]
    public async Task Activity_DoesNotLeakOtherRepositories()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "act-iso@test.dev", "Act Iso");
        var repoA = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/act-a.git");
        var repoB = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/act-b.git");
        await Helpers.LoginAndGetTokenAsync(_client, "act-iso-member@test.dev", "Act Iso Member");
        await AddMemberAsync(repoB, token, "act-iso-member@test.dev", "operator");
        await PostAsync($"/api/repositories/{repoA}/pause", token);

        var items = await (await GetAsync($"/api/repositories/{repoA}/activity", token)).Content.ReadFromJsonAsync<List<RepositoryActivityItem>>(Helpers.ApiJson);
        Assert.Contains(items!, i => i.Action == "repository.pause");
        Assert.DoesNotContain(items!, i => i.Action == "repository.share");
    }

    [Fact]
    public async Task ActivityExport_ReturnsCsv()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "act-export@test.dev", "Act Export");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/act-export.git");
        await PostAsync($"/api/repositories/{repoId}/pause", token);

        var response = await GetAsync($"/api/repositories/{repoId}/activity/export", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("timestampUtc,action,actorEmail,before,after", csv);
        Assert.Contains("repository.connect", csv);
        Assert.Contains("repository.pause", csv);
        Assert.Contains("act-export@test.dev", csv);
    }

    [Fact]
    public async Task ActivityExport_NonMember_Returns404()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "act-exp-owner@test.dev", "Act Exp Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/act-exp.git");
        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "act-exp-other@test.dev", "Act Exp Other");

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/api/repositories/{repoId}/activity/export", otherToken)).StatusCode);
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