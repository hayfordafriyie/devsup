using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevSup.Api;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class FailureHistoryTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public FailureHistoryTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task History_ListsOwnFailures_WithTicketJoinAndPagination()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "fh-list@test.dev", "FH List");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/fh-list.git");

        await IngestAsync(token, repositoryId, "GET", "/api/orders");
        await IngestAsync(token, repositoryId, "POST", "/api/users");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/failures?page=1&pageSize=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<FailureHistoryPage>(Helpers.ApiJson);
        Assert.NotNull(page);
        Assert.Equal(2, page!.Total);
        Assert.Single(page.Items);

        var newest = page.Items[0];
        Assert.Equal(repositoryId, newest.RepositoryId);
        Assert.NotNull(newest.TicketId);
        Assert.Equal("New", newest.TicketStatus);
        Assert.Equal("500", newest.StatusCode.ToString());
    }

    [Fact]
    public async Task History_AppliesDateRepositoryAndStatusFilters()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "fh-filter@test.dev", "FH Filter");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/fh-filter.git");

        var now = DateTime.UtcNow;
        await SeedFailureAsync(repositoryId, now.AddDays(-2), "GET", "/api/old", addTicket: true);
        await SeedFailureAsync(repositoryId, now.AddDays(-10), "POST", "/api/ancient", addTicket: false);

        var fromFilter = await GetAsync(token, $"/api/failures?from={now.AddDays(-7).ToString("O")}");
        var fromPage = fromFilter!;
        Assert.Equal(1, fromPage.Total);
        Assert.Equal("/api/old", fromPage.Items[0].Path);

        var toFilter = await GetAsync(token, $"/api/failures?to={now.AddDays(-7).ToString("O")}");
        Assert.Equal(1, toFilter!.Total);
        Assert.Equal("/api/ancient", toFilter.Items[0].Path);

        var statusFilter = await GetAsync(token, "/api/failures?status=new");
        Assert.Equal(1, statusFilter!.Total);
        Assert.Equal("New", statusFilter.Items[0].TicketStatus);
    }

    [Fact]
    public async Task Export_ReturnsCsvWithHeaderAndRows()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "fh-export@test.dev", "FH Export");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/fh-export.git");
        await IngestAsync(token, repositoryId, "GET", "/api/export");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/failures/export");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/csv", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("devsup-failures-", response.Content.Headers.ContentDisposition!.FileName);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("occurredAtUtc,repositoryId,method,path,statusCode", body);
        Assert.Contains("GET,/api/export,500", body);
    }

    [Fact]
    public async Task History_IsIsolatedToYourRepositories()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "fh-owner@test.dev", "FH Owner");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/fh-owner.git");
        await IngestAsync(token, repositoryId, "GET", "/api/private");

        var stranger = await Helpers.LoginAndGetTokenAsync(_client, "fh-stranger@test.dev", "FH Stranger");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/failures");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        var response = await _client.SendAsync(request);
        var page = await response.Content.ReadFromJsonAsync<FailureHistoryPage>(Helpers.ApiJson);
        Assert.NotNull(page);
        Assert.Equal(0, page!.Total);
        Assert.Empty(page.Items);
    }

    private static async Task<JsonNode> IngestAsync(HttpClient client, string token, Guid repositoryId, string method, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                repositoryId,
                statusCode = 500,
                method,
                path,
                exception = "Object reference not set"
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonNode>();
    }

    private async Task<JsonNode> IngestAsync(string token, Guid repositoryId, string method, string path)
        => await IngestAsync(_client, token, repositoryId, method, path);

    private async Task<FailureHistoryPage?> GetAsync(string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<FailureHistoryPage>(Helpers.ApiJson);
    }

    private async Task SeedFailureAsync(Guid repositoryId, DateTimeOffset occurredAt, string method, string path, bool addTicket)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var failure = new FailureEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            StatusCode = 500,
            Method = method,
            Path = path,
            OccurredAt = occurredAt
        };
        db.FailureEvents.Add(failure);

        if (addTicket)
        {
            db.RepairTickets.Add(new RepairTicket
            {
                Id = Guid.NewGuid(),
                FailureEventId = failure.Id,
                RepositoryId = repositoryId,
                Category = Core.FailureCategory.CodeError,
                Kind = Core.ErrorKind.NullReference,
                Status = Core.TicketStatus.New,
                UpdatedAt = occurredAt
            });
        }

        await db.SaveChangesAsync();
    }
}