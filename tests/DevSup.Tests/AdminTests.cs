using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevSup.Tests;

public sealed class AdminTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public AdminTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task AdminOverview_And_Users_AreAdminOnly()
    {
        var adminToken = await Helpers.LoginAndGetTokenAsync(_client, "admin@devsup.test", "Fleet Admin");
        await PromoteToAdminAsync("admin@devsup.test");

        var memberToken = await Helpers.LoginAndGetTokenAsync(_client, "member@devsup.test", "Regular Member");
        var memberId = await GetUserIdAsync("member@devsup.test");
        await Helpers.CreateRepositoryAsync(_client, memberToken, "https://github.com/acme/member.git");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/overview");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        var forbidden = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/overview");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var overview = await response.Content.ReadFromJsonAsync<AdminOverviewResponse>(Helpers.ApiJson);
        Assert.NotNull(overview);
        Assert.Equal(2, overview.TotalUsers);
        Assert.True(overview.TotalRepositories >= 1);

        request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var usersResponse = await _client.SendAsync(request);
        usersResponse.EnsureSuccessStatusCode();
        var users = await usersResponse.Content.ReadFromJsonAsync<List<AdminUserResponse>>(Helpers.ApiJson);
        Assert.NotNull(users);
        var admin = Assert.Single(users!, u => u.Email == "admin@devsup.test");
        Assert.True(admin.IsAdmin);
        var member = Assert.Single(users, u => u.Email == "member@devsup.test");
        Assert.False(member.IsAdmin);
        Assert.True(member.RepositoryCount >= 1);
    }

    [Fact]
    public async Task DeactivatedUser_CannotLogIn_AndActivateRestoresAccess()
    {
        var adminToken = await Helpers.LoginAndGetTokenAsync(_client, "boss@devsup.test", "Boss");
        await PromoteToAdminAsync("boss@devsup.test");

        await Helpers.LoginAndGetTokenAsync(_client, "minion@devsup.test", "Minion");
        var minionId = await GetUserIdAsync("minion@devsup.test");

        var deactivate = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/users/{minionId}/deactivate");
        deactivate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(deactivate)).StatusCode);

        var login = new StringContent("{\"email\":\"minion@devsup.test\",\"password\":\"password123\"}", System.Text.Encoding.UTF8, "application/json");
        var blocked = await _client.PostAsync("/api/users/login", login);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        var activate = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/users/{minionId}/activate");
        activate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(activate)).StatusCode);

        var loginAgain = new StringContent("{\"email\":\"minion@devsup.test\",\"password\":\"password123\"}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/users/login", loginAgain)).StatusCode);
    }

    [Fact]
    public async Task DeleteAdminTarget_MissingUser_IsNotFound()
    {
        var adminToken = await Helpers.LoginAndGetTokenAsync(_client, "admin2@devsup.test", "Admin Two");
        await PromoteToAdminAsync("admin2@devsup.test");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/users/{Guid.NewGuid()}/deactivate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(request)).StatusCode);
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