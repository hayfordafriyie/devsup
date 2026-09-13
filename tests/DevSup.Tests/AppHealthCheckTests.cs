using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevSup.Api;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.HealthChecks;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class AppHealthCheckTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public AppHealthCheckTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task UnhealthyApp_RecordsFailureTicketAndNotifies()
    {
        _factory.Prober.Result = _factory.Prober.Unhealthy;
        var (token, repositoryId) = await SeedRepositoryWithAppUrlAsync("hc-fail@example.com");
        await RegisterWebhookAsync(token, "https://hooks.example.com/devsup");

        await using var scope = _factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<AppHealthChecker>();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();

        var newFailures = await checker.ProbeDueAsync(10, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(1, newFailures);

        var repository = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repositoryId);
        Assert.False(repository.AppHealthy);
        Assert.NotNull(repository.AppHealthCheckedAt);
        Assert.Equal("HTTP 500", repository.AppHealthLastError);

        var failure = await db.FailureEvents.AsNoTracking().SingleAsync();
        Assert.Equal("GET", failure.Method);

        var ticket = await db.RepairTickets.AsNoTracking().SingleAsync();
        Assert.Equal(TicketStatus.New, ticket.Status);

        var email = await db.EmailMessages.AsNoTracking().SingleAsync();
        Assert.Contains("health check failed", email.Subject);

        var delivery = await db.WebhookDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(WebhookEvent.FailureDetected, delivery.Event);
        var payload = JsonNode.Parse(delivery.Payload);
        Assert.Equal("appHealthCheck", payload!["source"]!.GetValue<string>());
        Assert.Equal("GET", payload["failure"]!["method"]!.GetValue<string>());

        Assert.Empty(_factory.WebhookDeliverer.Delivered); // outbox populated; sweep has not run yet
    }

    [Fact]
    public async Task ConsecutiveFailures_OnlyRecordOneTicket()
    {
        _factory.Prober.Result = _factory.Prober.Unhealthy;
        var (_, repositoryId) = await SeedRepositoryWithAppUrlAsync("hc-repeat@example.com");

        await using var scope = _factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<AppHealthChecker>();

        var first = await checker.ProbeDueAsync(10, TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = await checker.ProbeDueAsync(10, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);

        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(1, await db.FailureEvents.CountAsync());
        Assert.Equal(1, await db.RepairTickets.CountAsync());

        var repository = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repositoryId);
        Assert.False(repository.AppHealthy);
    }

    [Fact]
    public async Task Recovery_AfterFailure_DoesNotDuplicateFailure()
    {
        _factory.Prober.Result = _factory.Prober.Unhealthy;
        var (_, repositoryId) = await SeedRepositoryWithAppUrlAsync("hc-recover@example.com");

        await using var scope = _factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<AppHealthChecker>();

        Assert.Equal(1, await checker.ProbeDueAsync(10, TimeSpan.FromSeconds(5), CancellationToken.None));

        _factory.Prober.Result = _factory.Prober.Healthy;
        var probes = await checker.ProbeDueAsync(10, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(0, probes);

        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(1, await db.FailureEvents.CountAsync());
        var repository = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repositoryId);
        Assert.True(repository.AppHealthy);
        Assert.Null(repository.AppHealthLastError);
    }

    [Fact]
    public async Task HealthyApp_FirstCheck_RecordsNothing()
    {
        _factory.Prober.Result = _factory.Prober.Healthy;
        var (_, repositoryId) = await SeedRepositoryWithAppUrlAsync("hc-ok@example.com");

        await using var scope = _factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<AppHealthChecker>();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();

        var newFailures = await checker.ProbeDueAsync(10, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, newFailures);
        Assert.Equal(0, await db.FailureEvents.CountAsync());
        Assert.Equal(0, await db.RepairTickets.CountAsync());

        var repository = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repositoryId);
        Assert.True(repository.AppHealthy);
    }

    [Fact]
    public async Task Repositories_WithoutAppUrl_AreSkipped()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "hc-skip@example.com", "HC Skip");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/hc-skip.git");

        await using var scope = _factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<AppHealthChecker>();

        var newFailures = await checker.ProbeDueAsync(10, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(0, newFailures);

        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repository = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == repositoryId);
        Assert.Null(repository.AppHealthCheckedAt);
    }

    private async Task<(string Token, Guid RepositoryId)> SeedRepositoryWithAppUrlAsync(string email)
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, email, "HC Owner");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/repositories")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    provider = "github",
                    cloneUrl = "https://github.com/acme/hc-app.git",
                    defaultBranch = "main",
                    appUrl = "https://app.acme.example.com/health"
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var repository = await response.Content.ReadFromJsonAsync<RepositoryResponse>(Helpers.ApiJson);
        Assert.NotNull(repository);
        return (token, repository.Id);
    }

    private async Task<HttpResponseMessage> RegisterWebhookAsync(string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { url }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
}