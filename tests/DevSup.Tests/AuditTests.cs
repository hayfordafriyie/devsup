using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Core;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class AuditTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public AuditTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task SensitiveActionsAppearInAuditLog_AndAreAdminOnly()
    {
        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "audit-member@test.dev", "Audit Member");
        await Helpers.CreateRepositoryAsync(_client, memberToken, "https://github.com/acme/audit.git");
        var memberUserId = await GetUserIdAsync("audit-member@test.dev");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        await _client.PostAsJsonAsync("/api/webhooks", new
        {
            url = "https://hooks.example.com/audit",
            events = new[] { "failureDetected" }
        }).ConfigureAwait(false);
        await _client.PostAsJsonAsync("/api/ai-keys", new
        {
            provider = "anthropicClaude",
            model = "claude-sonnet-4",
            key = "sk-ant-audit-long-enough"
        }).ConfigureAwait(false);

        var adminToken = await Helpers.LoginAndGetTokenAsync(_client, "audit-admin@test.dev", "Audit Admin");
        await PromoteToAdminAsync("audit-admin@test.dev");

        // Admin deactivates the member (itself an audited action).
        var deactivate = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/users/{memberUserId}/deactivate");
        deactivate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        await _client.SendAsync(deactivate).ConfigureAwait(false);

        // Non-admin is denied.
        var denied = new HttpRequestMessage(HttpMethod.Get, "/api/admin/audit");
        denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);

        // Admin can read the whole trail.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/audit");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var response = await _client.SendAsync(request).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = await response.Content.ReadFromJsonAsync<List<AuditEntryResponse>>(Helpers.ApiJson).ConfigureAwait(false);
        Assert.NotNull(entries);

        Assert.Contains(entries, e => e.Action == "repository.connect" && e.EntityType == "ConnectedRepository");
        Assert.Contains(entries, e => e.Action == "webhook.create" && e.EntityType == "WebhookEndpoint");
        Assert.Contains(entries, e => e.Action == "aiKey.create" && e.EntityType == "AiModelKeyBinding");
        Assert.Contains(entries, e => e.Action == "user.deactivate" && e.ActorEmail == "audit-admin@test.dev");
        Assert.Contains(entries, e => e.Action == "user.login" && e.ActorEmail == "audit-member@test.dev");
        var deactivateEntry = Assert.Single(entries, e => e.Action == "user.deactivate");
        Assert.Equal("False", deactivateEntry.After);
        Assert.Equal("True", deactivateEntry.Before);
    }

    [Fact]
    public async Task AuditLog_SupportsActorAndActionFilters()
    {
        var adminToken = await Helpers.LoginAndGetTokenAsync(_client, "audit-filter@test.dev", "Audit Filter");
        await PromoteToAdminAsync("audit-filter@test.dev");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/audit?action=user.login&actor=audit-filter");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var response = await _client.SendAsync(request).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = await response.Content.ReadFromJsonAsync<List<AuditEntryResponse>>(Helpers.ApiJson).ConfigureAwait(false);

        Assert.NotEmpty(entries!);
        Assert.All(entries, e => Assert.Equal("user.login", e.Action));
        Assert.All(entries, e => Assert.Equal("audit-filter@test.dev", e.ActorEmail));
    }

    private async Task PromoteToAdminAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        db.Entry(user).Property(u => u.IsAdmin).CurrentValue = true;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> GetUserIdAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        return user.Id;
    }
}