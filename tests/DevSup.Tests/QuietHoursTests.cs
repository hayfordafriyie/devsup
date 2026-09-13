using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Infrastructure.Email;
using DevSup.Infrastructure.Notifications;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class QuietHoursTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public QuietHoursTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task QuietHoursEndUtc_ComputesWindow_ForWraparound()
    {
        var (token, repoId) = await SeedRepoWithQuietHoursAsync("qh-policy@test.dev", start: 22, end: 7);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var userId = (await db.Users.AsNoTracking().SingleAsync(u => u.Email == "qh-policy@test.dev")).Id;

        // 23:00 is inside the 22→7 window and ends tomorrow at 07:00.
        var late = new DateTimeOffset(2026, 1, 15, 23, 0, 0, TimeSpan.Zero);
        var lateEnd = NotificationPreferencePolicy.QuietHoursEndUtc(db, userId, repoId, late);
        Assert.Equal(new DateTimeOffset(2026, 1, 16, 7, 0, 0, TimeSpan.Zero), lateEnd);

        // 03:00 is inside the window and ends today at 07:00.
        var early = new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 7, 0, 0, TimeSpan.Zero),
            NotificationPreferencePolicy.QuietHoursEndUtc(db, userId, repoId, early));

        // Midday is outside the window.
        var midday = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(NotificationPreferencePolicy.QuietHoursEndUtc(db, userId, repoId, midday));
    }

    [Fact]
    public async Task QuietHours_HoldIncidentEmail_UntilWindowEnds()
    {
        var (token, repoId) = await SeedRepoWithQuietHoursAsync("qh-active@test.dev", start: 22, end: 7);

        var now = DateTimeOffset.UtcNow;
        // A window that always contains "now": previous hour through two hours ahead.
        await PutQuietHoursAsync(token, repoId, (now.Hour + 23) % 24, (now.Hour + 2) % 24);

        await Helpers.IngestAsync(_client, token, repoId, 500, "GET", "/api/qh");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
            var email = await db.EmailMessages.AsNoTracking().SingleAsync();
            Assert.NotNull(email.NotBefore);
        }

        await ProcessEmailAsync();
        Assert.Empty(_factory.EmailSender.Sent);

        // Release the hold (as if the window passed) and retry.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
            var email = await db.EmailMessages.SingleAsync();
            db.Entry(email).Property(e => e.NotBefore).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await ProcessEmailAsync();
        Assert.Single(_factory.EmailSender.Sent);
    }

    [Fact]
    public async Task NoQuietHours_EmailIsNotHeld()
    {
        var (token, repoId) = await SeedRepoWithQuietHoursAsync("qh-none@test.dev", start: null, end: null);
        await Helpers.IngestAsync(_client, token, repoId, 500, "GET", "/api/qh");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var email = await db.EmailMessages.AsNoTracking().SingleAsync();
        Assert.Null(email.NotBefore);
    }

    private async Task<(string Token, Guid RepoId)> SeedRepoWithQuietHoursAsync(string email, int? start, int? end)
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, email, "QH");
        var repoId = await Helpers.CreateRepositoryAsync(_client, token, $"https://github.com/acme/{email.Split('@')[0]}.git");
        if (start is not null)
        {
            await PutQuietHoursAsync(token, repoId, start, end);
        }
        return (token, repoId);
    }

    private async Task PutQuietHoursAsync(string token, Guid repoId, int? start, int? end)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/notification-preferences")
        {
            Content = JsonContent.Create(new { repositoryId = repoId, quietHoursStart = start, quietHoursEnd = end })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    private Task ProcessEmailAsync()
    {
        return Task.Run(async () =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<EmailOutboxProcessor>();
            await processor.ProcessPendingAsync(10, CancellationToken.None);
        });
    }
}