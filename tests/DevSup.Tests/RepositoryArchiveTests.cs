using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Agent.Repair;
using DevSup.Api;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Digest;
using DevSup.Infrastructure.HealthChecks;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class RepositoryArchiveTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public RepositoryArchiveTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Archive_HidesRepositoryFromListOverviewTicketsAndFailures()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "arch-hide@test.dev", "Arch Hide");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/arch-hide.git");
        await Helpers.IngestAsync(_client, token, repoId, 500, "GET", "/api/boom");

        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/api/repositories/{repoId}/archive", token)).StatusCode);

        var active = await (await GetAsync("/api/repositories", token)).Content.ReadFromJsonAsync<List<RepositoryResponse>>(Helpers.ApiJson);
        Assert.Empty(active!);

        var archived = await (await GetAsync("/api/repositories?archived=true", token)).Content.ReadFromJsonAsync<List<RepositoryResponse>>(Helpers.ApiJson);
        Assert.Single(archived!, r => r.Id == repoId && r.Archived);

        var overview = await (await GetAsync("/api/overview", token)).Content.ReadFromJsonAsync<OverviewResponse>(Helpers.ApiJson);
        Assert.Empty(overview!.Repositories);
        Assert.Equal(0, overview.Tickets.Total);

        var tickets = await (await GetAsync("/api/tickets", token)).Content.ReadFromJsonAsync<List<TicketResponse>>(Helpers.ApiJson);
        Assert.Empty(tickets!);

        var failures = await (await GetAsync("/api/failures?page=1&pageSize=20", token)).Content.ReadFromJsonAsync<FailureHistoryPage>(Helpers.ApiJson);
        Assert.Equal(0, failures!.Total);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repo = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repoId);
        Assert.True(repo.Archived);
        Assert.NotNull(repo.ArchivedAt);
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "repository.archive"));
    }

    [Fact]
    public async Task Archive_RejectsIngest_With409()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "arch-ingest@test.dev", "Arch Ingest");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/arch-ingest.git");
        await PostAsync($"/api/repositories/{repoId}/archive", token);

        var ingest = await Helpers.IngestAsync(_client, token, repoId, 500, "GET", "/api/archived");
        Assert.Equal(HttpStatusCode.Conflict, ingest.StatusCode);
    }

    [Fact]
    public async Task Unarchive_RestoresVisibility()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "arch-restore@test.dev", "Arch Restore");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/arch-restore.git");
        await Helpers.IngestAsync(_client, token, repoId, 500, "GET", "/api/boom");
        await PostAsync($"/api/repositories/{repoId}/archive", token);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/api/repositories/{repoId}/unarchive", token)).StatusCode);

        var active = await (await GetAsync("/api/repositories", token)).Content.ReadFromJsonAsync<List<RepositoryResponse>>(Helpers.ApiJson);
        Assert.Single(active!, r => r.Id == repoId);

        var overview = await (await GetAsync("/api/overview", token)).Content.ReadFromJsonAsync<OverviewResponse>(Helpers.ApiJson);
        Assert.Single(overview!.Repositories);
        Assert.Equal(1, overview.Tickets.Total);

        var archived = await (await GetAsync("/api/repositories?archived=true", token)).Content.ReadFromJsonAsync<List<RepositoryResponse>>(Helpers.ApiJson);
        Assert.Empty(archived!);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repo = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repoId);
        Assert.False(repo.Archived);
        Assert.Null(repo.ArchivedAt);
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "repository.unarchive"));
    }

    [Fact]
    public async Task Archive_OtherUsersRepo_Returns404()
    {
        var ownerToken = await Helpers.LoginAndGetTokenAsync(_client, "arch-owner@test.dev", "Arch Owner");
        var repoId = await Helpers.CreateRepositoryAsync(_client, ownerToken, "https://github.com/acme/arch-owner.git");
        var otherToken = await Helpers.LoginAndGetTokenAsync(_client, "arch-other@test.dev", "Arch Other");

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync($"/api/repositories/{repoId}/archive", otherToken)).StatusCode);
    }

    [Fact]
    public async Task Archive_AlreadyArchived_Returns409()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "arch-twice@test.dev", "Arch Twice");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/arch-twice.git");
        await PostAsync($"/api/repositories/{repoId}/archive", token);

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync($"/api/repositories/{repoId}/archive", token)).StatusCode);
    }

    [Fact]
    public async Task Unarchive_NotArchived_Returns409()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "arch-un@test.dev", "Arch Un");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/arch-un.git");

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync($"/api/repositories/{repoId}/unarchive", token)).StatusCode);
    }

    [Fact]
    public async Task HealthChecker_SkipsArchivedRepository()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "arch-hc@test.dev", "Arch Health");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/arch-hc.git");
        await SetAppUrlAsync(repoId, "https://app.arch-hc.example/health");
        await PostAsync($"/api/repositories/{repoId}/archive", token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<AppHealthChecker>();
        await checker.ProbeDueAsync(50, TimeSpan.FromSeconds(5), CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repo = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repoId);
        Assert.Null(repo.AppHealthCheckedAt);
        Assert.Null(repo.AppHealthy);
    }

    [Fact]
    public async Task RepairProcessor_SkipsArchivedRepository()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "arch-repair@test.dev", "Arch Repair");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/arch-repair.git");
        var ingest = await Helpers.IngestAsync(
            _client, token, repoId, 500, "GET", "/api/orders",
            exceptionMessage: "NullReferenceException: Object reference not set to an instance of an object.");
        var body = await ingest.Content.ReadFromJsonAsync<IngestResponse>();
        await PostAsync($"/api/repositories/{repoId}/archive", token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        var handled = await processor.ProcessPendingAsync(10, CancellationToken.None);
        Assert.Equal(0, handled);

        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var ticket = await db.RepairTickets.AsNoTracking().SingleAsync(t => t.Id == body!.TicketId);
        Assert.Equal(TicketStatus.New, ticket.Status);
    }

    [Fact]
    public async Task Digest_SkipsArchivedRepository()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "arch-digest@test.dev", "Arch Digest");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/arch-digest.git");
        await SeedOpenTicketAsync(repoId);
        await PostAsync($"/api/repositories/{repoId}/archive", token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = new DigestProcessor(
            scope.ServiceProvider.GetRequiredService<DevSupDbContext>(),
            new DigestOptions { IntervalHours = 24, MaxOpenTickets = 5 });
        var generated = await processor.RunAsync(CancellationToken.None);
        Assert.Equal(0, generated);

        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.False(await db.EmailMessages.AsNoTracking().AnyAsync());
    }

    private async Task SeedOpenTicketAsync(Guid repositoryId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var failure = new FailureEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            StatusCode = 500,
            Method = "POST",
            Path = "/api/open",
            OccurredAt = DateTimeOffset.UtcNow
        };
        db.FailureEvents.Add(failure);
        db.RepairTickets.Add(new RepairTicket
        {
            Id = Guid.NewGuid(),
            FailureEventId = failure.Id,
            RepositoryId = repositoryId,
            Category = FailureCategory.CodeError,
            Kind = ErrorKind.NullReference,
            Status = TicketStatus.New,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SetAppUrlAsync(Guid repositoryId, string appUrl)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repo = await db.ConnectedRepositories.FindAsync(repositoryId);
        Assert.NotNull(repo);
        db.Entry(repo).Property(r => r.AppUrl).CurrentValue = appUrl;
        await db.SaveChangesAsync();
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
}