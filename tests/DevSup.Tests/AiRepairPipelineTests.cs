using System.Net;
using System.Net.Http.Json;
using DevSup.Agent.Ai;
using DevSup.Agent.Repair;
using DevSup.Api;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class AiRepairPipelineTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public AiRepairPipelineTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static readonly AiPatchSuggestion GoodSuggestion = new(
        "src/Handlers/OrderHandler.cs",
        "var order = repository.GetOrder(orderId);",
        "var order = repository.GetOrder(orderId);\nif (order is null)\n{\n    return Results.NotFound(new { error = \"order not found\" });\n}",
        "AI: guard missing order lookups against null");

    private async Task<(Guid RepositoryId, string Token)> SeedOwnerAsync(string email, string cloneUrl)
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, email, "AI Owner");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, cloneUrl);

        await using var setupScope = _factory.Services.CreateAsyncScope();
        var db = setupScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Email == email);
        var protector = setupScope.ServiceProvider.GetRequiredService<IKeyProtector>();
        db.OAuthTokens.Add(new OAuthToken
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Provider = GitProvider.GitHub,
            EncryptedAccessToken = protector.Protect("gho_ai_test_token"),
            Scope = "repo",
            LinkedAt = DateTimeOffset.UtcNow
        });
        db.AiModelKeyBindings.Add(new AiModelKeyBinding
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Provider = AiModelProvider.OpenAi,
            Model = "gpt-4o-mini",
            EncryptedApiKey = protector.Protect("sk-ai-test-key-1234567"),
            KeyMask = "••••4547",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        return (repositoryId, token);
    }

    private async Task<Guid> IngestAsync(Guid repositoryId, string token)
    {
        var response = await Helpers.IngestAsync(
            _client, token, repositoryId,
            statusCode: 500, method: "GET", path: "/api/orders",
            exceptionMessage: "NullReferenceException: Object reference not set to an instance of an object.");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ingest = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.NotNull(ingest);
        return ingest!.TicketId;
    }

    private async Task<TicketStatus> ReadStatusAsync(Guid ticketId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<DevSupDbContext>()
            .RepairTickets.SingleAsync(t => t.Id == ticketId)).Status;
    }

    [Fact]
    public async Task RepairLoop_WithAiKey_UsesModelSuggestionAndPushes()
    {
        _factory.AiGenerator.Result = GoodSuggestion;
        var (repositoryId, token) = await SeedOwnerAsync("ai-heal@example.com", "https://github.com/acme/healme.git");
        var ticketId = await IngestAsync(repositoryId, token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        await processor.ProcessPendingAsync(10, CancellationToken.None);

        Assert.Equal(1, _factory.AiGenerator.CallCount);
        Assert.Equal(TicketStatus.FixPushed, await ReadStatusAsync(ticketId));

        await using var checkScope = _factory.Services.CreateAsyncScope();
        var db = checkScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var ticket = await db.RepairTickets.SingleAsync(t => t.Id == ticketId);
        Assert.Equal("AI: guard missing order lookups against null", ticket.PatchSummary);
        Assert.Equal(FakeGitAdapter.FakeSha, ticket.CommitSha);
    }

    [Fact]
    public async Task RepairLoop_WhenAiHasNothing_FallsBackToTemplateRepair()
    {
        _factory.AiGenerator.Result = null;
        var (repositoryId, token) = await SeedOwnerAsync("ai-fallback@example.com", "https://github.com/acme/healme.git");
        var ticketId = await IngestAsync(repositoryId, token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        await processor.ProcessPendingAsync(10, CancellationToken.None);

        Assert.Equal(1, _factory.AiGenerator.CallCount);
        Assert.Equal(TicketStatus.FixPushed, await ReadStatusAsync(ticketId));

        await using var checkScope = _factory.Services.CreateAsyncScope();
        var ticket = await checkScope.ServiceProvider.GetRequiredService<DevSupDbContext>()
            .RepairTickets.SingleAsync(t => t.Id == ticketId);
        Assert.Equal("Guard missing order lookups against null", ticket.PatchSummary);
    }

    [Fact]
    public async Task RepairLoop_NoAiKey_UsesTemplateDirectly()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "ai-nokey@example.com", "AI NoKey");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/healme.git");
        var ticketId = await IngestAsync(repositoryId, token);

        await using var setupScope = _factory.Services.CreateAsyncScope();
        var db = setupScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Email == "ai-nokey@example.com");
        var protector = setupScope.ServiceProvider.GetRequiredService<IKeyProtector>();
        db.OAuthTokens.Add(new OAuthToken
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Provider = GitProvider.GitHub,
            EncryptedAccessToken = protector.Protect("gho_ai_test_token"),
            Scope = "repo",
            LinkedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        await processor.ProcessPendingAsync(10, CancellationToken.None);

        Assert.Equal(0, _factory.AiGenerator.CallCount);
        Assert.Equal(TicketStatus.FixPushed, await ReadStatusAsync(ticketId));
    }
}