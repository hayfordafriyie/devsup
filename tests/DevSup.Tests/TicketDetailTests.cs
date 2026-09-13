using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class TicketDetailTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public TicketDetailTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task GetDetail_ReturnsFullTicketData()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "detail-get@test.dev", "Detail Get");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/detail-get.git");
        var (ticketId, _) = await SeedTicketAsync(repoId, "GET", "/api/orders", 404, "Not found");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tickets/" + ticketId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<TicketDetailResponse>(Helpers.ApiJson);
        Assert.NotNull(detail);
        Assert.Equal(ticketId, detail.Id);
        Assert.Equal("GET", detail.Method);
        Assert.Equal("/api/orders", detail.Path);
        Assert.Equal(404, detail.StatusCode);
        Assert.Equal("Not found", detail.ExceptionMessage);
        Assert.Equal(TicketStatus.New.ToString(), detail.Status);
    }

    [Fact]
    public async Task GetDetail_Returns404_ForUnownedTicket()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "detail-own@test.dev", "Owner");
        var ownerRepo = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/detail-own.git");
        var (ticketId, _) = await SeedTicketAsync(ownerRepo, "GET", "/api/own", 500, "err");

        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "detail-other@test.dev", "Other");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tickets/" + ticketId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Close_Reopen_RoundTrip()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "detail-cr@test.dev", "CR");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/detail-cr.git");
        var (ticketId, _) = await SeedTicketAsync(repoId, "POST", "/api/cr", 500, "err");

        var close = new HttpRequestMessage(HttpMethod.Post, "/api/tickets/" + ticketId + "/close");
        close.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var closeResp = await _client.SendAsync(close);
        Assert.Equal(HttpStatusCode.OK, closeResp.StatusCode);
        var closeBody = await closeResp.Content.ReadFromJsonAsync<TicketCloseReopenResponse>(Helpers.ApiJson);
        Assert.Equal("closed", closeBody!.Status);

        var reopen = new HttpRequestMessage(HttpMethod.Post, "/api/tickets/" + ticketId + "/reopen");
        reopen.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var reopenResp = await _client.SendAsync(reopen);
        Assert.Equal(HttpStatusCode.OK, reopenResp.StatusCode);
        var reopenBody = await reopenResp.Content.ReadFromJsonAsync<TicketCloseReopenResponse>(Helpers.ApiJson);
        Assert.Equal("new", reopenBody!.Status);
    }

    [Fact]
    public async Task Close_AlreadyClosed_Returns409()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "detail-closed@test.dev", "CloseAgain");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/detail-closed.git");
        var (ticketId, _) = await SeedTicketAsync(repoId, "GET", "/api/closed", 500, "err");
        await CloseAsync(ticketId, token);

        var close = new HttpRequestMessage(HttpMethod.Post, "/api/tickets/" + ticketId + "/close");
        close.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(close)).StatusCode);
    }

    [Fact]
    public async Task Reopen_NotClosed_Returns409()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "detail-reopen@test.dev", "ReopenFail");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/detail-reopen.git");
        var (ticketId, _) = await SeedTicketAsync(repoId, "GET", "/api/reopen", 500, "err");

        var reopen = new HttpRequestMessage(HttpMethod.Post, "/api/tickets/" + ticketId + "/reopen");
        reopen.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(reopen)).StatusCode);
    }

    private async Task<(Guid ticketId, Guid failureId)> SeedTicketAsync(Guid repoId, string method, string path, int status, string? exception)
    {
        var now = DateTimeOffset.UtcNow;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var failure = new FailureEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = repoId,
            StatusCode = status,
            Method = method,
            Path = path,
            ExceptionMessage = exception,
            OccurredAt = now
        };
        db.FailureEvents.Add(failure);
        var ticket = new RepairTicket
        {
            Id = Guid.NewGuid(),
            FailureEventId = failure.Id,
            RepositoryId = repoId,
            Category = FailureCategory.CodeError,
            Kind = ErrorKind.NullReference,
            Status = TicketStatus.New,
            UpdatedAt = now
        };
        db.RepairTickets.Add(ticket);
        await db.SaveChangesAsync();
        return (ticket.Id, failure.Id);
    }

    private async Task CloseAsync(Guid ticketId, string token)
    {
        var close = new HttpRequestMessage(HttpMethod.Post, "/api/tickets/" + ticketId + "/close");
        close.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await _client.SendAsync(close);
    }
}

public sealed record TicketCloseReopenResponse(Guid TicketId, string Status);
