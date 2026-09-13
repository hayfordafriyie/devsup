using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Core.Models;
using DevSup.Infrastructure.Email;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class EmailOutboxTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public EmailOutboxTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Outbox_IsPaged_ScopedToUser_AndFilterable()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "em-owner@test.dev", "Em Owner");
        var ownerId = (await UserIdAsync("em-owner@test.dev"))!;

        var stranger = await Helpers.LoginAndGetTokenAsync(_client, "em-x@test.dev", "Em X");
        var strangerId = (await UserIdAsync("em-x@test.dev"))!;

        for (var i = 0; i < 3; i++)
        {
            await SeedEmailAsync(ownerId, $"owner-{i}@test.dev", $"OWNER {i}", sent: i % 2 == 0, createdAgoHours: i);
        }
        await SeedEmailAsync(strangerId, "stranger@test.dev", "STRANGER", sent: false, createdAgoHours: 0);

        var page = await GetPageAsync(token, "/api/emails?pageSize=2");
        Assert.Equal(3, page!.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("OWNER 0", page.Items[0].Subject);
        Assert.Equal("OWNER 1", page.Items[1].Subject);
        Assert.All(page.Items, i => Assert.StartsWith("OWNER", i.Subject));

        var sentFilter = await GetPageAsync(token, "/api/emails?sent=true");
        Assert.Equal(2, sentFilter!.Total);

        var pendingFilter = await GetPageAsync(token, "/api/emails?sent=false");
        Assert.Equal(1, pendingFilter!.Total);

        var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/emails");
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        var foreignPage = await (await _client.SendAsync(foreign)).Content.ReadFromJsonAsync<EmailPage>(Helpers.ApiJson);
        Assert.Equal(1, foreignPage!.Total);
    }

    [Fact]
    public async Task Retry_RequeuesFailedMessage_AndDelivers()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "em-retry@test.dev", "Em Retry");
        var emailId = await SeedEmailAsync((await UserIdAsync("em-retry@test.dev"))!,
            "em-retry@test.dev", "RETRY ME", sent: false, createdAgoHours: 0, attempts: 5, lastError: "SMTP timeout");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/emails/{emailId}/retry");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reset = await response.Content.ReadFromJsonAsync<EmailMessageResponse>(Helpers.ApiJson);
        Assert.NotNull(reset);
        Assert.Equal(0, reset!.Attempts);
        Assert.Null(reset.LastError);

        await ProcessPendingAsync();
        Assert.Contains(_factory.EmailSender.Sent, m => m.Id == emailId);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var row = await db.EmailMessages.AsNoTracking().SingleAsync(m => m.Id == emailId);
        Assert.True(row.Sent);
        Assert.Equal(1, await db.AuditEntries.AsNoTracking().CountAsync(a => a.Action == "email.retry"));
    }

    [Fact]
    public async Task Retry_SentMessage_Conflicts_AndOwnershipScoped()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "em-sent@test.dev", "Em Sent");
        var sentId = await SeedEmailAsync((await UserIdAsync("em-sent@test.dev"))!,
            "em-sent@test.dev", "ALREADY SENT", sent: true, createdAgoHours: 0);

        var again = new HttpRequestMessage(HttpMethod.Post, $"/api/emails/{sentId}/retry");
        again.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(again)).StatusCode);

        var stranger = await Helpers.LoginAndGetTokenAsync(_client, "em-x2@test.dev", "Em X2");
        var foreign = new HttpRequestMessage(HttpMethod.Post, $"/api/emails/{sentId}/retry");
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(foreign)).StatusCode);
    }

    private async Task<EmailPage?> GetPageAsync(string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<EmailPage>(Helpers.ApiJson);
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Email == email)).Id;
    }

    private async Task<Guid> SeedEmailAsync(
        Guid userId, string to, string subject, bool sent,
        int createdAgoHours, int attempts = 0, string? lastError = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var message = new EmailMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            To = to,
            Subject = subject,
            HtmlBody = "<p>body</p>",
            Sent = sent,
            SentAt = sent ? DateTimeOffset.UtcNow : null,
            Attempts = attempts,
            LastError = lastError,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-createdAgoHours)
        };
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    private Task ProcessPendingAsync()
    {
        return Task.Run(async () =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<EmailOutboxProcessor>();
            await processor.ProcessPendingAsync(10, CancellationToken.None);
        });
    }
}