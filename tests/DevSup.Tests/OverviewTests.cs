using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevSup.Api;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class OverviewTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public OverviewTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Overview_AggregatesReposAndTicketCounts_AcrossRepositories()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "ov-agg@example.com", "OV Agg");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/ov-agg.git");

        await using var seedScope = _factory.Services.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Email == "ov-agg@example.com");

        var repository = await db.ConnectedRepositories.SingleAsync(r => r.Id == repositoryId);
        var healthyRepo = AddRepository(owner, "https://github.com/acme/ov-healthy.git", healthy: true);
        var unhealthyRepo = AddRepository(owner, "https://github.com/acme/ov-down.git", healthy: false);
        await db.AddRangeAsync(healthyRepo, unhealthyRepo);
        await AddTicketAsync(db, repository, TicketStatus.New);
        await AddTicketAsync(db, repository, TicketStatus.FixPendingReview);
        await AddTicketAsync(db, healthyRepo, TicketStatus.FixPushed);
        await AddTicketAsync(db, unhealthyRepo, TicketStatus.NeedsHumanReview);
        await db.SaveChangesAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/overview");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var overview = await response.Content.ReadFromJsonAsync<OverviewResponse>(Helpers.ApiJson);
        Assert.NotNull(overview);

        Assert.Equal(3, overview.RepositoryCount);
        Assert.Equal(1, overview.HealthyRepos);
        Assert.Equal(1, overview.UnhealthyRepos);
        Assert.Equal(1, overview.UncheckedRepos);
        Assert.Equal(3, overview.Repositories.Count);

        Assert.Equal(1, overview.Tickets.New);
        Assert.Equal(0, overview.Tickets.InProgress);
        Assert.Equal(1, overview.Tickets.PendingReview);
        Assert.Equal(1, overview.Tickets.Fixed);
        Assert.Equal(1, overview.Tickets.NeedsHumanReview);
        Assert.Equal(4, overview.Tickets.Total);
    }

    [Fact]
    public async Task Overview_IsIsolatedPerUser()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "ov-alone@example.com", "OV Alone");
        await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/ov-alone.git");

        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "ov-other@example.com", "OV Other");
        var otherRepo = await Helpers.CreateRepositoryAsync(_client, otherToken, "https://github.com/acme/ov-other.git");

        await using var seedScope = _factory.Services.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var otherOwner = await db.Users.SingleAsync(u => u.Email == "ov-other@example.com");
        var otherRepository = await db.ConnectedRepositories.SingleAsync(u => u.Id == otherRepo);
        db.Entry(otherRepository).Property(r => r.AppHealthy).CurrentValue = false;
        db.Entry(otherRepository).Property(r => r.AppHealthCheckedAt).CurrentValue = DateTimeOffset.UtcNow;
        db.Entry(otherRepository).Property(r => r.AppHealthLastError).CurrentValue = "HTTP 500";
        await AddTicketAsync(db, otherRepository, TicketStatus.New);
        await db.SaveChangesAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/overview");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var overview = await response.Content.ReadFromJsonAsync<OverviewResponse>(Helpers.ApiJson);
        Assert.NotNull(overview);
        Assert.Equal(1, overview.RepositoryCount);
        Assert.Equal(1, overview.UncheckedRepos);
        Assert.Equal(0, overview.Tickets.Total);
    }

    [Fact]
    public async Task Tickets_FilterByRepositoryId()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "ov-filter@example.com", "OV Filter");
        var first = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/ov-first.git");
        var second = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/ov-second.git");

        await using var seedScope = _factory.Services.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Email == "ov-filter@example.com");
        var repoFirst = await db.ConnectedRepositories.SingleAsync(r => r.Id == first);
        var repoSecond = await db.ConnectedRepositories.SingleAsync(r => r.Id == second);
        await AddTicketAsync(db, repoFirst, TicketStatus.New);
        await AddTicketAsync(db, repoSecond, TicketStatus.FixPushed);
        await db.SaveChangesAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tickets?repositoryId={first}");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var tickets = await response.Content.ReadFromJsonAsync<List<TicketResponse>>(Helpers.ApiJson);
        var single = Assert.Single(tickets!);
        Assert.Equal(first, single.RepositoryId);
    }

    private ConnectedRepository AddRepository(User owner, string cloneUrl, bool healthy)
    {
        var now = DateTimeOffset.UtcNow;
        var appHost = cloneUrl.Split('/')[^1].Replace(".git", "");
        var repository = new ConnectedRepository
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            Provider = GitProvider.GitHub,
            CloneUrl = cloneUrl,
            DefaultBranch = "main",
            AppUrl = $"https://app.{appHost}.example.com",
            RepairMode = RepairMode.DirectPush,
            AppHealthy = healthy,
            AppHealthCheckedAt = now,
            AppHealthLastError = healthy ? null : "HTTP 500",
            ConnectedAt = now
        };
        return repository;
    }

    private static async Task AddTicketAsync(
        DevSup.Infrastructure.Persistence.DevSupDbContext db,
        ConnectedRepository repository,
        TicketStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var failure = new FailureEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            StatusCode = 500,
            Method = "GET",
            Path = "/api/orders",
            OccurredAt = now
        };
        db.FailureEvents.Add(failure);
        db.RepairTickets.Add(new RepairTicket
        {
            Id = Guid.NewGuid(),
            FailureEventId = failure.Id,
            RepositoryId = repository.Id,
            Category = FailureCategory.CodeError,
            Kind = ErrorKind.NullReference,
            Status = status,
            UpdatedAt = now
        });
        await Task.CompletedTask;
    }
}