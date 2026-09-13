using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevSup.Agent.Ai;
using DevSup.Agent.Repair;
using DevSup.Api;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class WebhookNotificationTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public WebhookNotificationTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Create_ReturnsSecretOnce_ListOmitsIt_DeleteRemoves()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-crud@example.com", "WH Crud");
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var created = await CreateWebhookAsync(token, "https://hooks.example.com/devsup");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var payload = await created.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);
        Assert.NotNull(payload);
        Assert.NotEmpty(payload.Secret);
        Assert.Equal(5, payload.Events.Count);

        var listResponse = await _client.GetAsync("/api/webhooks");
        var listedJson = await listResponse.Content.ReadAsStringAsync();
        var listed = JsonSerializer.Deserialize<List<WebhookResponse>>(listedJson, Helpers.ApiJson);
        var single = Assert.Single(listed!);
        Assert.Equal("https://hooks.example.com/devsup", single.Url);
        Assert.DoesNotContain("secret", listedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, single.Events.Count);

        var deleteResponse = await _client.DeleteAsync($"/api/webhooks/{single.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var afterDelete = await _client.GetAsync("/api/webhooks");
        var remaining = await afterDelete.Content.ReadFromJsonAsync<List<WebhookResponse>>(Helpers.ApiJson);
        Assert.NotNull(remaining);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Create_InvalidUrl_ReturnsBadRequest()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-badurl@example.com", "WH BadUrl");
        const string body = """{"url":"not-a-url"}""";
        var response = await SendWithAuthAsync(HttpMethod.Post, "/api/webhooks", token, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateUrl_ReturnsConflict()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-dup@example.com", "WH Dup");
        await CreateWebhookAsync(token, "https://hooks.example.com/devsup");

        var response = await SendWithAuthAsync(HttpMethod.Post, "/api/webhooks", token,
            """{"url":"https://hooks.example.com/devsup"}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_EnqueuesFailureDetected_ForMatchingWebhook()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-ingest@example.com", "WH Ingest");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/wh-ingest.git");
        await CreateWebhookAsync(token, "https://hooks.example.com/devsup");

        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "POST", path: "/api/orders",
            exceptionMessage: "NullReferenceException: order is null");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var delivery = await db.WebhookDeliveries.SingleAsync();

        Assert.False(delivery.Sent);
        Assert.Equal(WebhookEvent.FailureDetected, delivery.Event);

        var payload = JsonNode.Parse(delivery.Payload);
        Assert.Equal("POST", payload!["failure"]!["method"]!.GetValue<string>());
        Assert.Equal("/api/orders", payload["failure"]!["path"]!.GetValue<string>());
        Assert.Equal(500, payload["failure"]!["statusCode"]!.GetValue<int>());
        Assert.Equal("new", payload["ticket"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ingest_RespectsEventFilter()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-filter@example.com", "WH Filter");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/wh-filter.git");
        await CreateWebhookAsync(token, "https://hooks.example.com/devsup", new[] { "fixPushed" });

        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "GET", path: "/api/quote");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(0, await db.WebhookDeliveries.CountAsync());
    }

    [Fact]
    public async Task Sweep_DeliversPayloadAndMarksSent()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-sweep@example.com", "WH Sweep");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/wh-sweep.git");
        var created = await CreateWebhookAsync(token, "https://hooks.example.com/devsup");
        var webhook = await created.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);

        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "POST", path: "/api/payments");

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<WebhookOutboxProcessor>();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();

        var delivered = await processor.ProcessPendingAsync(10, CancellationToken.None);

        Assert.Equal(1, delivered);
        var row = Assert.Single(_factory.WebhookDeliverer.Delivered);
        Assert.Equal("https://hooks.example.com/devsup", row.Url);
        Assert.Equal(webhook!.Secret, row.Secret);
        Assert.Equal(WebhookEvent.FailureDetected, row.Event);

        var delivery = await db.WebhookDeliveries.SingleAsync();
        Assert.True(delivery.Sent);
        Assert.NotNull(delivery.SentAt);
        Assert.Equal(1, delivery.Attempts);
        Assert.Null(delivery.LastError);
    }

    [Fact]
    public async Task Sweep_FailedDelivery_RetriesThenSucceeds()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-retry@example.com", "WH Retry");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/wh-retry.git");
        await CreateWebhookAsync(token, "https://hooks.example.com/devsup");

        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "GET", path: "/api/quote");

        _factory.WebhookDeliverer.ThrowOnDeliver = true;

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<WebhookOutboxProcessor>();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();

        await processor.ProcessPendingAsync(10, CancellationToken.None);

        var failed = await db.WebhookDeliveries.AsNoTracking().SingleAsync();
        Assert.False(failed.Sent);
        Assert.Equal(1, failed.Attempts);
        Assert.Contains("webhook target unavailable", failed.LastError);
        Assert.Empty(_factory.WebhookDeliverer.Delivered);

        _factory.WebhookDeliverer.ThrowOnDeliver = false;
        await processor.ProcessPendingAsync(10, CancellationToken.None);

        var delivered = await db.WebhookDeliveries.AsNoTracking().SingleAsync();
        Assert.True(delivered.Sent);
        Assert.Equal(2, delivered.Attempts);
        Assert.Null(delivered.LastError);
        Assert.Single(_factory.WebhookDeliverer.Delivered);
    }

    [Fact]
    public async Task Sweep_DeletedEndpoint_DropsRowInsteadOfRetrying()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-drop@example.com", "WH Drop");
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/wh-drop.git");
        var created = await CreateWebhookAsync(token, "https://hooks.example.com/devsup");
        var webhook = await created.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);

        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "GET", path: "/api/quote");

        await _client.DeleteAsync($"/api/webhooks/{webhook!.Id}");

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<WebhookOutboxProcessor>();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();

        await processor.ProcessPendingAsync(10, CancellationToken.None);

        var delivery = await db.WebhookDeliveries.AsNoTracking().SingleAsync();
        Assert.True(delivery.Sent);
        Assert.Contains("webhook endpoint removed", delivery.LastError);
        Assert.Empty(_factory.WebhookDeliverer.Delivered);
    }

    [Fact]
    public async Task Repair_EnqueuesFixPendingReview_WhenPullRequestOpened()
    {
        _factory.AiGenerator.Result = new AiPatchSuggestion(
            "src/Handlers/OrderHandler.cs",
            "var order = repository.GetOrder(orderId);",
            "var order = repository.GetOrder(orderId);\nif (order is null)\n{\n    return Results.NotFound(new { error = \"order not found\" });\n}",
            "AI: guard missing order lookups against null");

        var token = await Helpers.LoginAndGetTokenAsync(_client, "wh-pr@example.com", "WH PR");
        var repositoryId = await CreatePullRequestRepositoryAsync(token, "https://github.com/acme/healme.git");
        await CreateWebhookAsync(token, "https://hooks.example.com/devsup");
        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "GET", path: "/api/orders",
            exceptionMessage: "NullReferenceException: Object reference not set to an instance of an object.");

        await using var setupScope = _factory.Services.CreateAsyncScope();
        var db = setupScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Email == "wh-pr@example.com");
        var protector = setupScope.ServiceProvider.GetRequiredService<DevSup.Infrastructure.Security.IKeyProtector>();
        db.OAuthTokens.Add(new OAuthToken
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Provider = GitProvider.GitHub,
            EncryptedAccessToken = protector.Protect("gho_wh_pr_token"),
            Scope = "repo",
            LinkedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        await processor.ProcessPendingAsync(10, CancellationToken.None);

        var deliveries = await db.WebhookDeliveries.OrderBy(d => d.CreatedAt).ToListAsync();
        Assert.Contains(deliveries, d => d.Event == WebhookEvent.FailureDetected);
        var pr = Assert.Single(deliveries, d => d.Event == WebhookEvent.FixPendingReview);
        Assert.Contains(FakePullRequestGateway.FakeUrl, pr.Payload);
        Assert.Contains("\"status\":\"fixPendingReview\"", pr.Payload);
    }

    private async Task<HttpResponseMessage> CreateWebhookAsync(string token, string url, string[]? events = null)
    {
        var body = events is null
            ? JsonSerializer.Serialize(new { url })
            : JsonSerializer.Serialize(new { url, events });
        return await SendWithAuthAsync(HttpMethod.Post, "/api/webhooks", token, body);
    }

    private async Task<Guid> CreatePullRequestRepositoryAsync(string token, string cloneUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/repositories")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { provider = "github", cloneUrl, defaultBranch = "main", repairMode = "pullRequest" }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var repository = await response.Content.ReadFromJsonAsync<RepositoryResponse>(Helpers.ApiJson);
        Assert.NotNull(repository);
        return repository.Id;
    }

    private async Task<HttpResponseMessage> SendWithAuthAsync(HttpMethod method, string path, string token, string body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
}