using DevSup.Api;
using System.Net;
using System.Net.Http.Json;
using DevSup.Core;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class GitHubOAuthTests : IAsyncLifetime
{
    private const string GitHubEmail = "octocat@example.com";

    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public GitHubOAuthTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Login_RedirectsToAuthorizeWithState()
    {
        var login = await _client.GetAsync("/api/auth/github/login");

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var location = login.Headers.Location;
        Assert.NotNull(location);
        Assert.Equal("github.com", location.Host);
        Assert.NotNull(Helpers.GetQueryParam(location, "state"));
        Assert.Equal("test-client-id", Helpers.GetQueryParam(location, "client_id"));
    }

    [Fact]
    public async Task Callback_CreatesUserLinksTokenAndIssuesJwt()
    {
        var state = await StartLoginAsync();

        var callback = await _client.GetAsync($"/api/auth/github/callback?code=abc123&state={state}");

        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        var login = await callback.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        Assert.NotEmpty(login.Token);
        Assert.Equal(GitHubEmail, login.User.Email);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var protector = _factory.Services.GetRequiredService<IKeyProtector>();

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == GitHubEmail);
        var token = await db.OAuthTokens.AsNoTracking().SingleAsync(o => o.UserId == user.Id);
        Assert.Equal(GitProvider.GitHub, token.Provider);
        Assert.StartsWith("aesgcm:", token.EncryptedAccessToken);
        Assert.Equal("gho_fake_access_token", protector.Unprotect(token.EncryptedAccessToken));
    }

    [Fact]
    public async Task Callback_IsIdempotent_ReusesExistingUserAndToken()
    {
        var state1 = await StartLoginAsync();
        await _client.GetAsync($"/api/auth/github/callback?code=abc123&state={state1}");

        var state2 = await StartLoginAsync();
        var callback2 = await _client.GetAsync($"/api/auth/github/callback?code=def456&state={state2}");

        Assert.Equal(HttpStatusCode.OK, callback2.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();

        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == GitHubEmail));
        Assert.Equal(1, await db.OAuthTokens.CountAsync());
    }

    [Fact]
    public async Task Callback_WithMismatchedState_ReturnsBadRequest()
    {
        var state = await StartLoginAsync();

        var callback = await _client.GetAsync($"/api/auth/github/callback?code=abc123&state=not-the-stored-state");

        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.NotEqual("not-the-stored-state", state);
    }

    [Fact]
    public async Task Callback_WithoutPriorLogin_ReturnsBadRequest()
    {
        var callback = await _client.GetAsync("/api/auth/github/callback?code=abc123&state=whatever");

        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
    }

    private async Task<string> StartLoginAsync()
    {
        var login = await _client.GetAsync("/api/auth/github/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var location = login.Headers.Location
            ?? throw new InvalidOperationException("Login did not redirect.");
        return Helpers.GetQueryParam(location, "state")
            ?? throw new InvalidOperationException("Login did not include state.");
    }
}