using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class AdminFleetTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public AdminFleetTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task AdminRepositories_IsAdminOnly()
    {
        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "fleet-member@test.dev", "Fleet Member");
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/api/admin/repositories", memberToken)).StatusCode);

        var adminToken = await Helpers.LoginAndGetTokenAsync(_client, "fleet-admin@test.dev", "Fleet Admin");
        await PromoteToAdminAsync("fleet-admin@test.dev");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/admin/repositories", adminToken)).StatusCode);
    }

    [Fact]
    public async Task AdminRepositories_ReturnsCrossTenantRowsWithStats()
    {
        var adminToken = await Helpers.LoginAndGetTokenAsync(_client, "fleet-admin2@test.dev", "Fleet Admin 2");
        await PromoteToAdminAsync("fleet-admin2@test.dev");

        var aliceToken = await Helpers.LoginAndGetTokenAsync(_client, "fleet-alice@test.dev", "Alice");
        var aliceRepo = await Helpers.CreateRepositoryAsync(_client, aliceToken, "https://github.com/acme/fleet-alice.git");
        await Helpers.IngestAsync(_client, aliceToken, aliceRepo, 500, "GET", "/api/boom");

        var bobToken = await Helpers.LoginAndGetTokenAsync(_client, "fleet-bob@test.dev", "Bob");
        await Helpers.CreateRepositoryAsync(_client, bobToken, "https://github.com/acme/fleet-bob.git");

        var page = await (await GetAsync("/api/admin/repositories", adminToken)).Content.ReadFromJsonAsync<AdminRepositoryPage>(Helpers.ApiJson);
        Assert.NotNull(page);
        Assert.True(page!.Total >= 2);
        var aliceRow = Assert.Single(page.Items, r => r.Id == aliceRepo);
        Assert.Equal("fleet-alice@test.dev", aliceRow.OwnerEmail);
        Assert.Equal(1, aliceRow.OpenTickets);
        Assert.Equal(1, aliceRow.TotalFailures);
        Assert.Contains(page.Items, r => r.OwnerEmail == "fleet-bob@test.dev" && r.OpenTickets == 0);
    }

    [Fact]
    public async Task AdminRepositories_FiltersByHealthAndOwner()
    {
        var adminToken = await Helpers.LoginAndGetTokenAsync(_client, "fleet-admin3@test.dev", "Fleet Admin 3");
        await PromoteToAdminAsync("fleet-admin3@test.dev");

        var token = await Helpers.LoginAndGetTokenAsync(_client, "fleet-health@test.dev", "Health");
        var healthyRepo = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/fleet-healthy.git");
        var unhealthyRepo = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/fleet-unhealthy.git");
        await SetHealthAsync(healthyRepo, true);
        await SetHealthAsync(unhealthyRepo, false);

        var unhealthy = await (await GetAsync("/api/admin/repositories?health=unhealthy", adminToken)).Content.ReadFromJsonAsync<AdminRepositoryPage>(Helpers.ApiJson);
        Assert.Contains(unhealthy!.Items, r => r.Id == unhealthyRepo);
        Assert.DoesNotContain(unhealthy.Items, r => r.Id == healthyRepo);

        var byOwner = await (await GetAsync("/api/admin/repositories?owner=fleet-health@test.dev", adminToken)).Content.ReadFromJsonAsync<AdminRepositoryPage>(Helpers.ApiJson);
        Assert.Equal(2, byOwner!.Total);
        Assert.All(byOwner.Items, r => Assert.Equal("fleet-health@test.dev", r.OwnerEmail));
    }

    [Fact]
    public async Task AdminOverview_IncludesHealthBreakdown()
    {
        var adminToken = await Helpers.LoginAndGetTokenAsync(_client, "fleet-admin4@test.dev", "Fleet Admin 4");
        await PromoteToAdminAsync("fleet-admin4@test.dev");

        var token = await Helpers.LoginAndGetTokenAsync(_client, "fleet-breakdown@test.dev", "Breakdown");
        var healthyRepo = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/fleet-bd-healthy.git");
        var uncheckedRepo = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/fleet-bd-unchecked.git");
        await SetHealthAsync(healthyRepo, true);

        var overview = await (await GetAsync("/api/admin/overview", adminToken)).Content.ReadFromJsonAsync<AdminOverviewResponse>(Helpers.ApiJson);
        Assert.True(overview!.HealthyRepos >= 1);
        Assert.True(overview.UncheckedRepos >= 1);
        Assert.True(overview.UnhealthyRepos >= 0);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task PromoteToAdminAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        db.Entry(user).Property(u => u.IsAdmin).CurrentValue = true;
        await db.SaveChangesAsync();
    }

    private async Task SetHealthAsync(Guid repositoryId, bool? healthy)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repo = await db.ConnectedRepositories.SingleAsync(r => r.Id == repositoryId);
        db.Entry(repo).Property(r => r.AppHealthy).CurrentValue = healthy;
        await db.SaveChangesAsync();
    }
}