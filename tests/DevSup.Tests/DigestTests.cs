using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Digest;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class DigestTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public DigestTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Digest_EnqueuesSummary_ForActiveUsersWithActivityOnly()
    {
        var busy = await Helpers.LoginAndGetTokenAsync(_client, "digest-busy@test.dev", "Busy One");
        var quiet = await Helpers.LoginAndGetTokenAsync(_client, "digest-quiet@test.dev", "Quiet One");
        var dormant = await Helpers.LoginAndGetTokenAsync(_client, "digest-dormant@test.dev", "Dormant One");

        var busyRepo = await Helpers.CreateRepositoryAsync(_client, busy, "https://github.com/acme/digest-busy.git");
        await Helpers.CreateRepositoryAsync(_client, quiet, "https://github.com/acme/digest-quiet.git");

        var busyId = await UserIdAsync("digest-busy@test.dev");
        var dormantId = await UserIdAsync("digest-dormant@test.dev");

        await SeedActivityAsync(busyRepo, now: DateTimeOffset.UtcNow, openTickets: 2, fixedShas: 1);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
            var dormantUser = await db.Users.SingleAsync(u => u.Id == dormantId);
            db.Entry(dormantUser).Property(u => u.Active).CurrentValue = false;
            await db.SaveChangesAsync();
        }

        var generated = await RunDigestAsync(intervalHours: 24);
        Assert.Equal(1, generated);

        await using var scope2 = _factory.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var email = await db2.EmailMessages.AsNoTracking().SingleAsync();
        Assert.Equal("digest-busy@test.dev", email.To);
        Assert.Contains("2 open ticket(s)", email.Subject);
        Assert.Contains("Open tickets", email.HtmlBody);
        Assert.Contains("Busy One", email.HtmlBody);
    }

    [Fact]
    public async Task Digest_WindowCountsOnlyRecentActivity()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "digest-window@test.dev", "Window One");
        var repo = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/digest-window.git");

        await SeedActivityAsync(repo, now: DateTimeOffset.UtcNow.AddHours(-30), openTickets: 1, fixedShas: 0);
        await SeedActivityAsync(repo, now: DateTimeOffset.UtcNow.AddHours(-2), openTickets: 0, fixedShas: 1);

        var generated = await RunDigestAsync(intervalHours: 24);
        Assert.Equal(1, generated);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var email = await db.EmailMessages.AsNoTracking().SingleAsync();
        // Only the in-window failure is counted; the 30h-old one is ignored.
        Assert.Contains("1 failure(s)", email.Subject);
        Assert.Contains("1 fix(es)", email.Subject);
    }

    [Fact]
    public async Task Digest_SkipsUserWhoOptedOut()
    {
        var doomed = await Helpers.LoginAndGetTokenAsync(_client, "digest-out@test.dev", "Opt Out");
        var repo = await Helpers.CreateRepositoryAsync(_client, doomed, "https://github.com/acme/digest-out.git");
        await SeedActivityAsync(repo, now: DateTimeOffset.UtcNow, openTickets: 1, fixedShas: 0);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == "digest-out@test.dev");
            db.Entry(user).Property(u => u.DigestEnabled).CurrentValue = false;
            await db.SaveChangesAsync();
        }

        var generated = await RunDigestAsync(intervalHours: 24);
        Assert.Equal(0, generated);

        await using var scope2 = _factory.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.False(await db2.EmailMessages.AsNoTracking().AnyAsync());
    }

    private async Task<int> RunDigestAsync(int intervalHours)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = new DigestProcessor(
            scope.ServiceProvider.GetRequiredService<DevSupDbContext>(),
            new DigestOptions { IntervalHours = intervalHours, MaxOpenTickets = 5 });
        return await processor.RunAsync(CancellationToken.None);
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Email == email)).Id;
    }

    private async Task SeedActivityAsync(Guid repositoryId, DateTimeOffset now, int openTickets, int fixedShas)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        for (var i = 0; i < fixedShas; i++)
        {
            var failure = new FailureEvent
            {
                Id = Guid.NewGuid(),
                RepositoryId = repositoryId,
                StatusCode = 500,
                Method = "GET",
                Path = $"/api/fixed-{now.Ticks}-{i}",
                OccurredAt = now
            };
            db.FailureEvents.Add(failure);
            db.RepairTickets.Add(new RepairTicket
            {
                Id = Guid.NewGuid(),
                FailureEventId = failure.Id,
                RepositoryId = repositoryId,
                Category = FailureCategory.CodeError,
                Kind = ErrorKind.NullReference,
                Status = TicketStatus.FixPushed,
                CommitSha = Guid.NewGuid().ToString("N")[..7],
                UpdatedAt = now
            });
        }

        for (var i = 0; i < openTickets; i++)
        {
            var failure = new FailureEvent
            {
                Id = Guid.NewGuid(),
                RepositoryId = repositoryId,
                StatusCode = 500,
                Method = "POST",
                Path = $"/api/open-{now.Ticks}-{i}",
                OccurredAt = now
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
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync();
    }
}