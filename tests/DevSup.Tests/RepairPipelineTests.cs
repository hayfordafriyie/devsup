using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevSup.Agent.Git;
using DevSup.Agent.Repair;
using DevSup.Api;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DevSup.Tests;

/// <summary>
/// In-memory git adapter: materializes a fake checked-out repository for the
/// "healme" clone URL and records every commit/push so the pipeline can be
/// verified end-to-end with no network access.
/// </summary>
public sealed class FakeGitAdapter : IGitAdapter
{
    public const string FakeSha = "0123456789abcdef0123456789abcdef01234567";

    public int CloneCount { get; private set; }
    public List<(string Message, string Sha)> Pushes { get; } = [];
    public List<(string Branch, string Message, string Sha)> PushedBranches { get; } = [];

    private static readonly string OrderHandlerSource = """
        public static class OrderHandler
        {
            public static IResult Get(int orderId, IOrderRepository repository)
            {
                var order = repository.GetOrder(orderId);
                return Results.Ok(order);
            }
        }
        """;

    private static readonly string FakeRepairsJson = """
        {
          "repairs": [
            {
              "path": "/api/orders",
              "kind": "NullReference",
              "file": "src/Handlers/OrderHandler.cs",
              "fragment": "var order = repository.GetOrder(orderId);",
              "replacement": "var order = repository.GetOrder(orderId);\nif (order is null)\n{\n    return Results.NotFound(new { error = \"order not found\" });\n}",
              "summary": "Guard missing order lookups against null"
            }
          ]
        }
        """;

    public Task<string> CloneAsync(string cloneUrl, string branch, GitProvider provider, string accessToken, CancellationToken ct)
    {
        CloneCount++;
        var dir = Path.Combine(Path.GetTempPath(), "devsup-tests-ws", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        if (cloneUrl.Contains("healme", StringComparison.OrdinalIgnoreCase))
        {
            var repairsDir = Path.Combine(dir, ".devsup");
            Directory.CreateDirectory(repairsDir);
            File.WriteAllText(Path.Combine(repairsDir, "repairs.json"), FakeRepairsJson);

            var sourceDir = Path.Combine(dir, "src", "Handlers");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "OrderHandler.cs"), OrderHandlerSource);
        }

        return Task.FromResult(dir);
    }

    public Task<string> CommitAndPushAsync(string workingDirectory, string message, string user, string email, CancellationToken ct)
    {
        Pushes.Add((message, FakeSha));
        return Task.FromResult(FakeSha);
    }

    public Task<string> CommitAndPushToBranchAsync(string workingDirectory, string branch, string message, string user, string email, CancellationToken ct)
    {
        PushedBranches.Add((branch, message, FakeSha));
        return Task.FromResult(FakeSha);
    }

    public Task CleanupAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
        catch (Exception)
        {
            // best-effort
        }

        return Task.CompletedTask;
    }
}

public sealed class RepairPipelineTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public RepairPipelineTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(Guid UserId, Guid RepositoryId, string Token)> SeedOwnerWithLinkedRepositoryAsync(string email, string cloneUrl)
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, email, "Repo Owner");
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
            EncryptedAccessToken = protector.Protect("gho_test_access_token"),
            Scope = "repo",
            LinkedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        return (owner.Id, repositoryId, token);
    }

    private async Task<Guid> IngestNullReferenceAsync(Guid repositoryId, string token)
    {
        var response = await Helpers.IngestAsync(
            _client, token, repositoryId,
            statusCode: 500, method: "GET", path: "/api/orders",
            exceptionMessage: "NullReferenceException: Object reference not set to an instance of an object.");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ingest = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.NotNull(ingest);
        Assert.Equal("CodeError", ingest.Category);
        Assert.Equal("New", ingest.Status);
        return ingest.TicketId;
    }

    private async Task<(TicketStatus status, string? sha, string? summary)> ReadTicketAsync(Guid ticketId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var ticket = await scope.ServiceProvider.GetRequiredService<DevSupDbContext>()
            .RepairTickets.SingleAsync(t => t.Id == ticketId);
        return (ticket.Status, ticket.CommitSha, ticket.PatchSummary);
    }

    private async Task<int> RunProcessorAsync(Guid ticketId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        var handled = await processor.ProcessPendingAsync(10, CancellationToken.None);
        Assert.Equal(1, handled);
        var (status, _, _) = await ReadTicketAsync(ticketId);
        Assert.Equal(TicketStatus.FixPushed, status);
        return handled;
    }

    [Fact]
    public async Task RepairLoop_WithMatchingTemplate_ClonesPushesAndEmails()
    {
        var (_, repositoryId, token) = await SeedOwnerWithLinkedRepositoryAsync(
            "heal@example.com", "https://github.com/acme/healme.git");
        var ticketId = await IngestNullReferenceAsync(repositoryId, token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        var handled = await processor.ProcessPendingAsync(10, CancellationToken.None);
        Assert.Equal(1, handled);

        var (status, sha, summary) = await ReadTicketAsync(ticketId);
        Assert.Equal(TicketStatus.FixPushed, status);
        Assert.Equal(FakeGitAdapter.FakeSha, sha);
        Assert.Equal("Guard missing order lookups against null", summary);

        var git = _factory.Services.GetRequiredService<IGitAdapter>() as FakeGitAdapter;
        Assert.NotNull(git);
        Assert.Equal(1, git!.CloneCount);
        Assert.Single(git.Pushes);

        await using var checkScope = _factory.Services.CreateAsyncScope();
        var email = await checkScope.ServiceProvider.GetRequiredService<DevSupDbContext>()
            .EmailMessages.SingleOrDefaultAsync(m => m.To == "heal@example.com" && m.Subject.Contains("fix pushed"));
        Assert.NotNull(email);
        Assert.Contains(FakeGitAdapter.FakeSha, email!.HtmlBody);
    }

    [Fact]
    public async Task RepairLoop_SecondSweepDoesNotReplay()
    {
        var (_, repositoryId, token) = await SeedOwnerWithLinkedRepositoryAsync(
            "replay@example.com", "https://github.com/acme/healme.git");
        var ticketId = await IngestNullReferenceAsync(repositoryId, token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        await processor.ProcessPendingAsync(10, CancellationToken.None);
        var second = await processor.ProcessPendingAsync(10, CancellationToken.None);
        Assert.Equal(0, second);

        var git = _factory.Services.GetRequiredService<IGitAdapter>() as FakeGitAdapter;
        Assert.NotNull(git);
        Assert.Equal(1, git!.Pushes.Count);

        var (status, _, _) = await ReadTicketAsync(ticketId);
        Assert.Equal(TicketStatus.FixPushed, status);
    }

    [Fact]
    public async Task RepairLoop_NoMatchingTemplate_EscalatesForHumanReview()
    {
        var (_, repositoryId, token) = await SeedOwnerWithLinkedRepositoryAsync(
            "human@example.com", "https://github.com/acme/unhealable.git");
        var ticketId = await IngestNullReferenceAsync(repositoryId, token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        var handled = await processor.ProcessPendingAsync(10, CancellationToken.None);
        Assert.Equal(1, handled);

        var (status, sha, _) = await ReadTicketAsync(ticketId);
        Assert.Equal(TicketStatus.NeedsHumanReview, status);
        Assert.Null(sha);

        await using var checkScope = _factory.Services.CreateAsyncScope();
        var email = await checkScope.ServiceProvider.GetRequiredService<DevSupDbContext>()
            .EmailMessages.SingleOrDefaultAsync(m => m.To == "human@example.com" && m.Subject.Contains("needs your review"));
        Assert.NotNull(email);
        Assert.Contains("NeedsHumanReview", email!.HtmlBody);
    }

    [Fact]
    public async Task RepairLoop_NoLinkedToken_EscalatesWithoutPushing()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "notoken@example.com", "No Token");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/healme.git");
        var ticketId = await IngestNullReferenceAsync(repositoryId, token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
        var handled = await processor.ProcessPendingAsync(10, CancellationToken.None);
        Assert.Equal(1, handled);

        var (status, sha, _) = await ReadTicketAsync(ticketId);
        Assert.Equal(TicketStatus.NeedsHumanReview, status);
        Assert.Null(sha);

        var git = _factory.Services.GetRequiredService<IGitAdapter>() as FakeGitAdapter;
        Assert.NotNull(git);
        Assert.Equal(0, git!.Pushes.Count);
    }
}