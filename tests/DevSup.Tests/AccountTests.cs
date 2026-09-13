using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DevSup.Api;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class AccountTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public AccountTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task GetProfile_ReturnsAccount_WithFlags()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "acct-get@test.dev", "Acct Get");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var account = await response.Content.ReadFromJsonAsync<AccountResponse>(Helpers.ApiJson);
        Assert.NotNull(account);
        Assert.Equal("acct-get@test.dev", account.Email);
        Assert.False(account.IsAdmin);
        Assert.True(account.Active);
    }

    [Fact]
    public async Task UpdateProfile_Audits_AndReflectsChange()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "acct-upd@test.dev", "Old Name");
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/account")
        {
            Content = JsonContent.Create(new { displayName = "New Name" })
        };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(put);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var account = await response.Content.ReadFromJsonAsync<AccountResponse>(Helpers.ApiJson);
        Assert.NotNull(account);
        Assert.Equal("New Name", account.DisplayName);

        var get = new HttpRequestMessage(HttpMethod.Get, "/api/account");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var fetched = await (await _client.SendAsync(get)).Content.ReadFromJsonAsync<AccountResponse>(Helpers.ApiJson);
        Assert.Equal("New Name", fetched!.DisplayName);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var audit = await db.AuditEntries.AsNoTracking()
            .SingleAsync(a => a.Action == "account.profileUpdate");
        Assert.Equal("acct-upd@test.dev", audit.ActorEmail);
        Assert.Equal("Old Name", audit.Before);
        Assert.Equal("New Name", audit.After);

        var blank = new HttpRequestMessage(HttpMethod.Put, "/api/account")
        {
            Content = JsonContent.Create(new { displayName = "  " })
        };
        blank.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(blank)).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_VerifiesCurrent_AndFlipsCredentials()
    {
        var token = await Helpers.LoginAndGetTokenAsync(_client, "acct-pw@test.dev", "Acct Pw");

        var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/account/password")
        {
            Content = JsonContent.Create(new { currentPassword = "not-the-password", newPassword = "brandnewpass1" })
        };
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(wrong)).StatusCode);

        var shortNew = new HttpRequestMessage(HttpMethod.Post, "/api/account/password")
        {
            Content = JsonContent.Create(new { currentPassword = "password123", newPassword = "short" })
        };
        shortNew.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(shortNew)).StatusCode);

        var change = new HttpRequestMessage(HttpMethod.Post, "/api/account/password")
        {
            Content = JsonContent.Create(new { currentPassword = "password123", newPassword = "brandnewpass1" })
        };
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(change)).StatusCode);

        var oldLogin = await _client.PostAsync("/api/users/login", JsonContent.Create(
            new { email = "acct-pw@test.dev", password = "password123" }));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await _client.PostAsync("/api/users/login", JsonContent.Create(
            new { email = "acct-pw@test.dev", password = "brandnewpass1" }));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(1, await db.AuditEntries.AsNoTracking().CountAsync(a => a.Action == "account.passwordChange"));
    }
}