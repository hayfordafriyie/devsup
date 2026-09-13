using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Verification;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class VerificationTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public VerificationTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Verification_FlipsToFixVerified_WhenProbeHealthy()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "verify-ok@test.dev", "Verifier");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/verify-ok.git");
        await SetAppUrlAsync(repoId, "https://app.acme.example/health");

        var ticketId = await SeedFixPushedAsync(repoId, minutesAgo: 5, "GET", "/api/ok");

        var verified = await RunVerificationAsync();
        Assert.Equal(1, verified);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var ticket = await db.RepairTickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.FixVerified, ticket.Status);

        var email = await db.EmailMessages.AsNoTracking().SingleAsync(e => e.To == "verify-ok@test.dev");
        Assert.Contains("fix verified", email.Subject);
        Assert.Contains("/api/ok", email.Subject);
    }

    [Fact]
    public async Task Verification_SkipsUnhealthyProbe()
    {
        _factory.Prober.Result = _factory.Prober.Unhealthy;
        var token = await Helpers.LoginAndGetTokenAsync(_client, "verify-ng@test.dev", "Verifier Ng");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/verify-ng.git");
        var ticketId = await SeedFixPushedAsync(repoId, minutesAgo: 5, "POST", "/api/ng");

        var verified = await RunVerificationAsync();
        Assert.Equal(0, verified);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var ticket = await db.RepairTickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.FixPushed, ticket.Status);
        Assert.False(await db.EmailMessages.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task Verification_SkipsTicketWithinProbeDelay()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "verify-fresh@test.dev", "Fresh One");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/verify-fresh.git");
        await SeedFixPushedAsync(repoId, minutesAgo: 1, "GET", "/api/fresh");

        var verified = await RunVerificationAsync();
        Assert.Equal(0, verified);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.False(await db.EmailMessages.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task Verification_SkipsWhenNoAppUrl()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "verify-nourl@test.dev", "NoUrl");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/verify-nourl.git");
        var ticketId = await SeedFixPushedAsync(repoId, minutesAgo: 5, "GET", "/api/nourl");
        await ClearAppUrlAsync(repoId);

        var verified = await RunVerificationAsync();
        Assert.Equal(0, verified);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var ticket = await db.RepairTickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.FixPushed, ticket.Status);
        Assert.False(await db.EmailMessages.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task Verification_SkipsOutsideWindow()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "verify-old@test.dev", "Old One");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/verify-old.git");
        var ticketId = await SeedFixPushedAsync(repoId, minutesAgo: 35, "GET", "/api/old");

        var verified = await RunVerificationAsync();
        Assert.Equal(0, verified);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var ticket = await db.RepairTickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.FixPushed, ticket.Status);
        Assert.False(await db.EmailMessages.AsNoTracking().AnyAsync());
    }

    private async Task<Guid> SeedFixPushedAsync(Guid repositoryId, int minutesAgo, string method, string path)
    {
        var now = DateTimeOffset.UtcNow;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var failure = new FailureEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            StatusCode = 500,
            Method = method,
            Path = path,
            OccurredAt = now.AddMinutes(-minutesAgo)
        };
        db.FailureEvents.Add(failure);
        var ticket = new RepairTicket
        {
            Id = Guid.NewGuid(),
            FailureEventId = failure.Id,
            RepositoryId = repositoryId,
            Category = FailureCategory.CodeError,
            Kind = ErrorKind.NullReference,
            Status = TicketStatus.FixPushed,
            CommitSha = Guid.NewGuid().ToString("N")[..7],
            UpdatedAt = now.AddMinutes(-minutesAgo)
        };
        db.RepairTickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket.Id;
    }

    private async Task SetAppUrlAsync(Guid repositoryId, string appUrl)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repo = await db.ConnectedRepositories.FindAsync(repositoryId);
        Assert.NotNull(repo);
        db.Entry(repo).Property(r => r.AppUrl).CurrentValue = appUrl;
        await db.SaveChangesAsync();
    }

    private async Task ClearAppUrlAsync(Guid repositoryId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var repo = await db.ConnectedRepositories.FindAsync(repositoryId);
        Assert.NotNull(repo);
        db.Entry(repo).Property(r => r.AppUrl).CurrentValue = null;
        await db.SaveChangesAsync();
    }

    private async Task<int> RunVerificationAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = new VerificationProcessor(
            scope.ServiceProvider.GetRequiredService<DevSupDbContext>(),
            _factory.Prober,
            new VerificationOptions { ProbeDelayMinutes = 2, WindowMinutes = 30 });
        return await processor.RunAsync(CancellationToken.None);
    }
}