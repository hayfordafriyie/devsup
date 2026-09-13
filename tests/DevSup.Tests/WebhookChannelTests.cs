using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevSup.Api;
using DevSup.Core;
using DevSup.Infrastructure.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class WebhookChannelTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public WebhookChannelTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Create_WithSlackChannelAndName_IsReturnedAndListed()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "wc-slack@example.com", "WC Slack");

        var create = await CreateWebhookAsync(token, "https://hooks.slack.com/services/T000/B00/XXXX", "slack", "#incidents");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreateWebhookResponse>(Helpers.ApiJson);
        Assert.NotNull(created);
        Assert.Equal(WebhookChannel.Slack, created.Channel);
        Assert.Equal("#incidents", created.Name);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/webhooks");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var listResponse = await _client.SendAsync(request);
        listResponse.EnsureSuccessStatusCode();

        var webhooks = await listResponse.Content.ReadFromJsonAsync<List<WebhookResponse>>(Helpers.ApiJson);
        var webhook = Assert.Single(webhooks!);
        Assert.Equal(WebhookChannel.Slack, webhook.Channel);
        Assert.Equal("#incidents", webhook.Name);
    }

    [Fact]
    public async Task Sweep_FormatsSlackPayloadAsBlocks()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<WebhookOutboxProcessor>();

        var token = await Helpers.LoginAndGetTokenAsync(_client, "wc-sweep@example.com", "WC Sweep");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/wc-sweep.git");
        await CreateWebhookAsync(token, "https://hooks.slack.com/services/T000/B00/XXXX", "slack", null);

        var ingest = await IngestAsync(token, repositoryId);
        var failureId = ingest["failureEventId"]!.GetValue<Guid>();

        var delivered = await processor.ProcessPendingAsync(50, CancellationToken.None);
        Assert.Equal(1, delivered);

        var delivery = Assert.Single(_factory.WebhookDeliverer!.Delivered);
        Assert.Equal(WebhookEvent.FailureDetected, delivery.Event);
        var body = JsonNode.Parse(delivery.Payload)!;
        Assert.NotNull(body["blocks"]);
        Assert.Contains("/api/orders", body["text"]!.GetValue<string>());
    }

    [Fact]
    public void Formatter_Slack_PutsMessageInBlocks()
    {
        var basePayload = WebhookQueueSerializer();
        var formatted = WebhookPayloadFormatter.Format(WebhookChannel.Slack, basePayload);

        var body = JsonNode.Parse(formatted)!;
        Assert.NotNull(body["blocks"]);
        var text = body["text"]!.GetValue<string>();
        Assert.Contains("failureDetected", text);
        Assert.Contains("/api/orders", text);
    }

    [Fact]
    public void Formatter_Teams_BuildsMessageCard()
    {
        var formatted = WebhookPayloadFormatter.Format(WebhookChannel.Teams, WebhookQueueSerializer());

        var body = JsonNode.Parse(formatted)!;
        Assert.Equal("MessageCard", body["@type"]!.GetValue<string>());
        Assert.Equal("http://schema.org/extensions", body["@context"]!.GetValue<string>());
        var facts = body["sections"]![0]!["facts"]!.AsArray();
        var factNames = facts.Select(f => f!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Failure", factNames);
        Assert.Contains("Repository", factNames);
    }

    [Fact]
    public void Formatter_Http_IsPassedThroughUnchanged()
    {
        var payload = WebhookQueueSerializer();
        Assert.Equal(payload, WebhookPayloadFormatter.Format(WebhookChannel.Http, payload));
    }

    [Fact]
    public void Formatter_ResilientToWeirdPayloads()
    {
        var formatted = WebhookPayloadFormatter.Format(WebhookChannel.Slack, "not-json");
        Assert.Equal("not-json", formatted);
        var empty = JsonNode.Parse(WebhookPayloadFormatter.Format(WebhookChannel.Teams, "{}"))!;
        Assert.NotNull(empty["sections"]);
    }

    private string WebhookQueueSerializer() => JsonSerializer.Serialize(new
    {
        Event = WebhookEvent.FailureDetected,
        failure = new { method = "GET", path = "/api/orders", statusCode = 500, failureId = Guid.NewGuid() },
        repository = new { id = Guid.NewGuid(), cloneUrl = "https://github.com/acme/wc.git", defaultBranch = "main" },
        ticket = new { id = Guid.NewGuid(), status = TicketStatus.New, kind = ErrorKind.NullReference }
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    });

    private async Task<HttpResponseMessage> CreateWebhookAsync(string token, string url, string channel, string? name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { url, channel, name }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<JsonNode> IngestAsync(string token, Guid repositoryId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                repositoryId,
                statusCode = 500,
                method = "GET",
                path = "/api/orders"
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonNode>();
    }
}