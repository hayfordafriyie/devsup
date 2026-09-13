using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevSup.Api;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class EventReplayTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public EventReplayTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Replay_ReenqueuesEmailAndWebhook_ForPastFailure()
    {
        var factory = new DevSupApiFactory();
        await using var _ = factory;
        using var client = factory.CreateClient();

        var token = await Helpers.LoginAndGetTokenAsync(client, "rp-replay@example.com", "RP Replay");
        var repositoryId = await Helpers.CreateRepositoryAsync(client, token, "https://github.com/acme/rp-replay.git");
        await RegisterWebhookAsync(client, token, "https://hooks.example.com/rp-replay", "http");

        var ingest = await IngestAsync(client, token, repositoryId);
        var failureId = ingest["failureEventId"].GetValue<Guid>();
        var ticketId = ingest["ticketId"].GetValue<Guid>();

        await using var seedScope = factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var before = await seedDb.EmailMessages.AsNoTracking().CountAsync();
        Assert.Equal(1, before);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/failures/{failureId}/replay");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var replayResult = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(1, replayResult["emailsQueued"].GetValue<int>());
        Assert.Equal(1, replayResult["webhooksQueued"].GetValue<int>());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var emails = await db.EmailMessages.AsNoTracking().Where(m => m.Subject.Contains("replaying")).ToListAsync();
        var replayedEmail = Assert.Single(emails);
        Assert.Contains("GET /api/orders", replayedEmail.Subject);

        var deliveries = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.Event == WebhookEvent.FailureDetected)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, deliveries.Count);
        var replayDelivery = deliveries[1];
        var payload = JsonNode.Parse(replayDelivery.Payload)!;
        Assert.Equal(failureId, payload["failure"]!["failureId"]!.GetValue<Guid>());
        Assert.Equal(ticketId, payload["ticket"]!["id"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task Replay_IsolatedToYourOwnFailures()
    {
        var factory = new DevSupApiFactory();
        await using var _ = factory;
        using var client = factory.CreateClient();

        var token = await Helpers.LoginAndGetTokenAsync(client, "rp-owner@example.com", "RP Owner");
        var repositoryId = await Helpers.CreateRepositoryAsync(client, token, "https://github.com/acme/rp-owner.git");
        await RegisterWebhookAsync(client, token, "https://hooks.example.com/rp-owner", "http");
        var ingest = await IngestAsync(client, token, repositoryId);
        var failureId = ingest["failureEventId"].GetValue<Guid>();

        var stranger = await Helpers.LoginAndGetTokenAsync(client, "rp-stranger@example.com", "RP Stranger");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/failures/{failureId}/replay");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Redispatch_ResetsNeedsReviewTicketToNew()
    {
        var factory = new DevSupApiFactory();
        await using var _ = factory;
        using var client = factory.CreateClient();

        var token = await Helpers.LoginAndGetTokenAsync(client, "rp-redispatch@example.com", "RP Redispatch");
        var repositoryId = await Helpers.CreateRepositoryAsync(client, token, "https://github.com/acme/rp-healme.git");

        var ingest = await IngestAsync(client, token, repositoryId);
        var ticketId = ingest["ticketId"].GetValue<Guid>();

        await using var seedScope = factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var ticket = await seedDb.RepairTickets.SingleAsync(t => t.Id == ticketId);
        seedDb.Entry(ticket).Property(t => t.Status).CurrentValue = TicketStatus.NeedsHumanReview;
        await seedDb.SaveChangesAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tickets/{ticketId}/redispatch");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var updated = await db.RepairTickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.New, updated.Status);
    }

    [Fact]
    public async Task Redispatch_RejectsFixedOrClosedTickets()
    {
        var factory = new DevSupApiFactory();
        await using var _ = factory;
        using var client = factory.CreateClient();

        var token = await Helpers.LoginAndGetTokenAsync(client, "rp-fixed@example.com", "RP Fixed");
        var repositoryId = await Helpers.CreateRepositoryAsync(client, token, "https://github.com/acme/rp-fixed.git");

        var ingest = await IngestAsync(client, token, repositoryId);
        var ticketId = ingest["ticketId"].GetValue<Guid>();

        await using var seedScope = factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var ticket = await seedDb.RepairTickets.SingleAsync(t => t.Id == ticketId);
        seedDb.Entry(ticket).Property(t => t.Status).CurrentValue = TicketStatus.Closed;
        await seedDb.SaveChangesAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tickets/{ticketId}/redispatch");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var updated = await db.RepairTickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.Closed, updated.Status);
    }

    private static async Task RegisterWebhookAsync(HttpClient client, string token, string url, string channel)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { url, channel }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonNode> IngestAsync(HttpClient client, string token, Guid repositoryId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                repositoryId,
                statusCode = 500,
                method = "GET",
                path = "/api/orders",
                exception = "Object reference not set"
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonNode>();
    }
}