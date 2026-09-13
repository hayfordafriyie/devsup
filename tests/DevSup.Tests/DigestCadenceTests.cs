using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Digest;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class DigestCadenceTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public DigestCadenceTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Weekly_User_SkippedUntilWeekElapses()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "cadence-weekly-wait@test.dev", "Weekly Wait");
        var repo = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/cadence-weekly-wait.git");
        await SeedActivityAsync(repo, DateTimeOffset.UtcNow, openTickets: 1, fixedShas: 0);
        await SetCadenceAsync("cadence-weekly-wait@test.dev", DigestFrequency.Weekly, DateTimeOffset.UtcNow.AddHours(-2));

        var generated = await RunDigestAsync();
        Assert.Equal(0, generated);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.False(await db.EmailMessages.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task Weekly_User_SentAfterWeek()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "cadence-weekly-due@test.dev", "Weekly Due");
        var repo = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/cadence-weekly-due.git");
        await SeedActivityAsync(repo, DateTimeOffset.UtcNow, openTickets: 2, fixedShas: 0);
        await SetCadenceAsync("cadence-weekly-due@test.dev", DigestFrequency.Weekly, DateTimeOffset.UtcNow.AddDays(-8));

        var generated = await RunDigestAsync();
        Assert.Equal(1, generated);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var email = await db.EmailMessages.AsNoTracking().SingleAsync();
        Assert.Equal("cadence-weekly-due@test.dev", email.To);
        Assert.Contains("2 open ticket(s)", email.Subject);
        Assert.Contains("168", email.HtmlBody);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "cadence-weekly-due@test.dev");
        Assert.NotNull(user.LastDigestSentAt);
        Assert.True(user.LastDigestSentAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Daily_User_SkippedAfterRecentSend()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "cadence-daily-wait@test.dev", "Daily Wait");
        var repo = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/cadence-daily-wait.git");
        await SeedActivityAsync(repo, DateTimeOffset.UtcNow, openTickets: 1, fixedShas: 0);
        await SetCadenceAsync("cadence-daily-wait@test.dev", DigestFrequency.Daily, DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(0, await RunDigestAsync());
    }

    [Fact]
    public async Task Weekly_WindowCoversSevenDays()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "cadence-weekly-window@test.dev", "Weekly Window");
        var repo = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/cadence-weekly-window.git");
        // Fix pushed three days ago: outside a 24h window, inside a 168h window.
        await SeedActivityAsync(repo, DateTimeOffset.UtcNow.AddDays(-3), openTickets: 0, fixedShas: 1);
        await SetCadenceAsync("cadence-weekly-window@test.dev", DigestFrequency.Weekly, DateTimeOffset.UtcNow.AddDays(-8));

        Assert.Equal(1, await RunDigestAsync());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var email = await db.EmailMessages.AsNoTracking().SingleAsync();
        Assert.Contains("1 fix(es)", email.Subject);
    }

    private async Task<int> RunDigestAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = new DigestProcessor(
            scope.ServiceProvider.GetRequiredService<DevSupDbContext>(),
            new DigestOptions { IntervalHours = 24, MaxOpenTickets = 5 });
        return await processor.RunAsync(CancellationToken.None);
    }

    private async Task SetCadenceAsync(string email, DigestFrequency frequency, DateTimeOffset? lastSent)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        db.Entry(user).Property(u => u.DigestFrequency).CurrentValue = frequency;
        db.Entry(user).Property(u => u.LastDigestSentAt).CurrentValue = lastSent;
        await db.SaveChangesAsync();
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
                Path = $"/api/fixed-{i}",
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
                Path = $"/api/open-{i}",
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