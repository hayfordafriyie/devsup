using System.Text.Json;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Retention;
using DevSup.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class RetentionTests
{
    [Fact]
    public async Task Purge_RemovesExpired_FailureTicketDeliveriesAndSentEmails_KeepsRecentAndUnsent()
    {
        var factory = new DevSupApiFactory();
        await using var _ = factory;
        using var client = factory.CreateClient();
        await Helpers.LoginAndGetTokenAsync(client, "rt-purge@example.com", "RT Purge");

        await using var seedScope = factory.Services.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync();

        var (oldFailure, oldTicket) = await SeedFailureAsync(db, owner, daysAgo: 400);
        var (newFailure, newTicket) = await SeedFailureAsync(db, owner, daysAgo: 2);

        db.EmailMessages.Add(new EmailMessage
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            To = owner.Email,
            Subject = "old sent",
            HtmlBody = "<p>old</p>",
            Sent = true,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-400)
        });
        db.EmailMessages.Add(new EmailMessage
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            To = owner.Email,
            Subject = "old unsent",
            HtmlBody = "<p>old</p>",
            Sent = false,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-400)
        });
        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            WebhookId = Guid.NewGuid(),
            Event = WebhookEvent.FailureDetected,
            Payload = "{}",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-400)
        });
        await db.SaveChangesAsync();

        await using var purgeScope = factory.Services.CreateAsyncScope();
        var cleaner = purgeScope.ServiceProvider.GetRequiredService<RetentionCleaner>();
        var purged = await cleaner.PurgeAsync(windowDays: 365, batchSize: 500, CancellationToken.None);

        Assert.True(purged >= 4);

        await using var scope = factory.Services.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();

        Assert.Null(await check.FailureEvents.FindAsync(oldFailure));
        Assert.Null(await check.RepairTickets.FindAsync(oldTicket));
        Assert.NotNull(await check.FailureEvents.FindAsync(newFailure));
        Assert.NotNull(await check.RepairTickets.FindAsync(newTicket));

        Assert.Null(await check.EmailMessages.FirstOrDefaultAsync(m => m.Subject == "old sent"));
        Assert.NotNull(await check.EmailMessages.FirstOrDefaultAsync(m => m.Subject == "old unsent"));
        Assert.Empty(await check.WebhookDeliveries.ToListAsync());
    }

    [Fact]
    public async Task Purge_DisabledWindow_DoesNothing()
    {
        var factory = new DevSupApiFactory();
        await using var _ = factory;
        using var client = factory.CreateClient();
        await Helpers.LoginAndGetTokenAsync(client, "rt-off@example.com", "RT Off");

        await using var seedScope = factory.Services.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync();
        var (oldFailure, _) = await SeedFailureAsync(db, owner, daysAgo: 400);
        await db.SaveChangesAsync();

        await using var purgeScope = factory.Services.CreateAsyncScope();
        var cleaner = purgeScope.ServiceProvider.GetRequiredService<RetentionCleaner>();
        var purged = await cleaner.PurgeAsync(windowDays: 0, batchSize: 500, CancellationToken.None);
        Assert.Equal(0, purged);

        await using var scope = factory.Services.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.NotNull(await check.FailureEvents.FindAsync(oldFailure));
    }

    [Fact]
    public async Task Purge_RespectsPerRepositoryKeepForever()
    {
        var factory = new DevSupApiFactory();
        await using var _ = factory;
        using var client = factory.CreateClient();
        await Helpers.LoginAndGetTokenAsync(client, "rt-forever@example.com", "RT Forever");

        await using var seedScope = factory.Services.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync();
        var (failure, _) = await SeedFailureAsync(db, owner, daysAgo: 400, retentionDays: 0);

        await using var purgeScope = factory.Services.CreateAsyncScope();
        var cleaner = purgeScope.ServiceProvider.GetRequiredService<RetentionCleaner>();
        Assert.Equal(0, await cleaner.PurgeAsync(windowDays: 365, batchSize: 500, CancellationToken.None));

        await using var scope = factory.Services.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.NotNull(await check.FailureEvents.FindAsync(failure));
    }

    [Fact]
    public async Task Purge_PerRepositoryShorterWindowPurgesSooner()
    {
        var factory = new DevSupApiFactory();
        await using var _ = factory;
        using var client = factory.CreateClient();
        await Helpers.LoginAndGetTokenAsync(client, "rt-short@example.com", "RT Short");

        await using var seedScope = factory.Services.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync();
        var (failure, _) = await SeedFailureAsync(db, owner, daysAgo: 40, retentionDays: 30);

        await using var purgeScope = factory.Services.CreateAsyncScope();
        var cleaner = purgeScope.ServiceProvider.GetRequiredService<RetentionCleaner>();
        Assert.True(await cleaner.PurgeAsync(windowDays: 365, batchSize: 500, CancellationToken.None) >= 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Null(await check.FailureEvents.FindAsync(failure));
    }

    [Fact]
    public async Task Purge_PerRepositoryLongerWindowKeepsLonger()
    {
        var factory = new DevSupApiFactory();
        await using var _ = factory;
        using var client = factory.CreateClient();
        await Helpers.LoginAndGetTokenAsync(client, "rt-long@example.com", "RT Long");

        await using var seedScope = factory.Services.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var owner = await db.Users.SingleAsync();
        var (failure, _) = await SeedFailureAsync(db, owner, daysAgo: 400, retentionDays: 730);

        await using var purgeScope = factory.Services.CreateAsyncScope();
        var cleaner = purgeScope.ServiceProvider.GetRequiredService<RetentionCleaner>();
        await cleaner.PurgeAsync(windowDays: 365, batchSize: 500, CancellationToken.None);

        await using var scope = factory.Services.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.NotNull(await check.FailureEvents.FindAsync(failure));
    }

    private static async Task<(Guid FailureId, Guid TicketId)> SeedFailureAsync(
        DevSupDbContext db, User owner, int daysAgo, int? retentionDays = null)
    {
        var repository = new ConnectedRepository
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            Provider = GitProvider.GitHub,
            CloneUrl = $"https://github.com/acme/retention-{Guid.NewGuid():N}.git",
            DefaultBranch = "main",
            RetentionDays = retentionDays,
            ConnectedAt = DateTimeOffset.UtcNow
        };
        db.ConnectedRepositories.Add(repository);

        var failure = new FailureEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            StatusCode = 500,
            Method = "GET",
            Path = "/api/retention",
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-daysAgo)
        };
        db.FailureEvents.Add(failure);

        var ticket = new RepairTicket
        {
            Id = Guid.NewGuid(),
            FailureEventId = failure.Id,
            RepositoryId = repository.Id,
            Category = FailureCategory.CodeError,
            Kind = ErrorKind.NullReference,
            Status = TicketStatus.FixPushed,
            UpdatedAt = failure.OccurredAt
        };
        db.RepairTickets.Add(ticket);
        await db.SaveChangesAsync();

        return (failure.Id, ticket.Id);
    }
}