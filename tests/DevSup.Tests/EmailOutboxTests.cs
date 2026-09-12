using DevSup.Api;
using System.Net;
using System.Net.Http.Json;
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

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Sweep_DeliversPendingAndMarksSent()
    {
        var token = await LoginAndGetTokenAsync("outbox-deliver@example.com", "Outbox Deliver");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/outbox-deliver.git");

        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "POST", path: "/api/payments",
            exceptionMessage: "TimeoutException: upstream payment gateway timed out");

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<EmailOutboxProcessor>();
        var batch = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();

        var delivered = await processor.ProcessPendingAsync(10, CancellationToken.None);

        Assert.Equal(1, delivered);
        Assert.Single(_factory.EmailSender.Sent);
        Assert.Equal("outbox-deliver@example.com", _factory.EmailSender.Sent[0].To);
        Assert.Contains("failure detected", _factory.EmailSender.Sent[0].Subject);

        var message = await batch.EmailMessages.SingleAsync();
        Assert.True(message.Sent);
        Assert.NotNull(message.SentAt);
        Assert.Equal(1, message.Attempts);
        Assert.Null(message.LastError);
    }

    [Fact]
    public async Task Sweep_FailedSend_RetriesThenSucceeds()
    {
        var token = await LoginAndGetTokenAsync("outbox-retry@example.com", "Outbox Retry");
        var repositoryId = await Helpers.CreateRepositoryAsync(_client, token, "https://github.com/acme/outbox-retry.git");
        await Helpers.IngestAsync(_client, token, repositoryId,
            statusCode: 500, method: "GET", path: "/api/quote",
            exceptionMessage: "NullReferenceException: quote is null");

        _factory.EmailSender.ThrowOnSend = true;

        await using var scope = _factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<EmailOutboxProcessor>();
        var batch = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();

        await processor.ProcessPendingAsync(10, CancellationToken.None);

        var failed = await batch.EmailMessages.AsNoTracking().SingleAsync();
        Assert.False(failed.Sent);
        Assert.Equal(1, failed.Attempts);
        Assert.Contains("SMTP unavailable", failed.LastError);
        Assert.Empty(_factory.EmailSender.Sent);

        _factory.EmailSender.ThrowOnSend = false;
        await processor.ProcessPendingAsync(10, CancellationToken.None);

        var delivered = await batch.EmailMessages.AsNoTracking().SingleAsync();
        Assert.True(delivered.Sent);
        Assert.Equal(2, delivered.Attempts);
        Assert.Null(delivered.LastError);
        Assert.Single(_factory.EmailSender.Sent);
    }

    private async Task<string> LoginAndGetTokenAsync(string email, string displayName)
    {
        await _client.PostAsJsonAsync("/api/users/register", new { email, displayName, password = "password123" });

        var login = await _client.PostAsJsonAsync("/api/users/login", new { email, password = "password123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var payload = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload);
        return payload.Token;
    }
}