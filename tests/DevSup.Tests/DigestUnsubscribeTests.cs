using System.Net;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Digest;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class DigestUnsubscribeTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public DigestUnsubscribeTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Digest_IncludesUnsubscribeLinkAndToken()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "unsub-digest@test.dev", "Unsub Digest");
        var repo = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/unsub-digest.git");
        await SeedOpenTicketAsync(repo);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var processor = new DigestProcessor(
                scope.ServiceProvider.GetRequiredService<DevSupDbContext>(),
                new DigestOptions { IntervalHours = 24, MaxOpenTickets = 5, BaseUrl = "https://devsup.test" });
            Assert.Equal(1, await processor.RunAsync(CancellationToken.None));
        }

        await using var check = _factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var email = await db.EmailMessages.AsNoTracking().SingleAsync();
        Assert.Contains("https://devsup.test/api/digest/unsubscribe?token=", email.HtmlBody);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "unsub-digest@test.dev");
        Assert.False(string.IsNullOrEmpty(user.DigestUnsubscribeToken));
    }

    [Fact]
    public async Task Unsubscribe_DisablesDigestConsumesTokenAndAudits()
    {
        await Helpers.LoginAndGetTokenAsync(_client, "unsub-click@test.dev", "Unsub Click");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == "unsub-click@test.dev");
            db.Entry(user).Property(u => u.DigestUnsubscribeToken).CurrentValue = "tok-abc";
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/digest/unsubscribe?token=tok-abc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);

        // The link is one-shot: a second click no longer resolves.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/digest/unsubscribe?token=tok-abc")).StatusCode);

        await using var check = _factory.Services.CreateAsyncScope();
        var db2 = check.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var updated = await db2.Users.AsNoTracking().SingleAsync(u => u.Email == "unsub-click@test.dev");
        Assert.False(updated.DigestEnabled);
        Assert.Null(updated.DigestUnsubscribeToken);
        Assert.True(await db2.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "account.digestUnsubscribe"));
    }

    [Fact]
    public async Task Unsubscribe_InvalidToken_Returns404_AndMissingToken_Returns400()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/digest/unsubscribe?token=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/digest/unsubscribe")).StatusCode);
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
            Method = "GET",
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
}