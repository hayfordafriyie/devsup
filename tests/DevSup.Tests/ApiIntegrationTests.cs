using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevSup.Api;
using DevSup.Api.Auth;
using DevSup.Infrastructure.Email;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Security;
using DevSup.Core;
using DevSup.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DevSup.Tests;

public sealed class FakeEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    public bool ThrowOnSend { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (ThrowOnSend)
        {
            throw new InvalidOperationException("SMTP unavailable (fake)");
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class FakeGitHubGateway(GitHubAuthSettings settings) : IGitHubGateway
{
    public string BuildAuthorizeUrl(string state)
        => Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            settings.AuthorizeUrl,
            new Dictionary<string, string?>
            {
                ["client_id"] = settings.ClientId,
                ["redirect_uri"] = settings.RedirectUri,
                ["scope"] = settings.Scope,
                ["state"] = state,
                ["allow_signup"] = "true"
            });

    public Task<GitHubTokenResult> ExchangeCodeAsync(string code, CancellationToken ct)
        => Task.FromResult(new GitHubTokenResult("gho_fake_access_token", "repo user:email"));

    public Task<GitHubProfile> GetProfileAsync(string accessToken, CancellationToken ct)
        => Task.FromResult(new GitHubProfile("octocat", "Octo Cat", "octocat@example.com"));
}

public sealed class DevSupApiFactory : WebApplicationFactory<Program>
{
    private readonly InMemoryDatabaseRoot _databaseRoot = new();

    public DevSupApiFactory()
    {
        ClientOptions.AllowAutoRedirect = false;
    }

    public FakeEmailSender EmailSender { get; } = new();

    public GitHubAuthSettings GitHubAuth { get; } = new()
    {
        ClientId = "test-client-id",
        ClientSecret = "test-client-secret",
        RedirectUri = "/api/auth/github/callback"
    };

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:ClientId"] = "test-client-id",
                ["GitHub:ClientSecret"] = "test-client-secret",
                ["Emailing:IntervalSeconds"] = "3600"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<DevSupDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<DevSupDbContext>>();
            services.RemoveAll<DevSupDbContext>();
            services.RemoveAll<IEmailSender>();
            services.RemoveAll<IGitHubGateway>();
            services.RemoveAll<GitHubAuthSettings>();

            services.AddDbContext<DevSupDbContext>(options =>
                options.UseInMemoryDatabase("devsup-tests", _databaseRoot));
            services.AddSingleton<IEmailSender>(EmailSender);
            services.AddSingleton<IGitHubGateway>(new FakeGitHubGateway(GitHubAuth));
            services.AddSingleton(GitHubAuth);
        });
    }
}

public sealed class ApiIntegrationTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public ApiIntegrationTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Register_ThenLogin_IssuesJwt()
    {
        var register = await RegisterUserAsync("ops@example.com", "Ops Admin");

        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await LoginAsync("ops@example.com", "password123");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var payload = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload);
        Assert.NotEmpty(payload.Token);
        Assert.True(payload.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.Equal("ops@example.com", payload.User.Email);

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", payload.Token);

        var repos = await _client.GetAsync("/api/repositories");
        Assert.Equal(HttpStatusCode.OK, repos.StatusCode);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsConflict()
    {
        await RegisterUserAsync("dup@example.com", "First");
        var second = await RegisterUserAsync("dup@example.com", "Second");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Register_WeakPassword_ReturnsBadRequest()
    {
        var response = await PostAsync("/api/users/register", new { email = "weak@example.com", displayName = "Weak", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        await RegisterUserAsync("wrong@example.com", "Wrong Pass");

        var response = await LoginAsync("wrong@example.com", "not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateRepository_AndConnect_IsListed()
    {
        var token = await LoginAndGetTokenAsync("repos@example.com", "Repo Owner");

        var create = await CreateRepositoryAsync(token, "https://github.com/acme/widgets.git");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var list = await _client.GetAsync("/api/repositories");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var body = await list.Content.ReadFromJsonAsync<List<RepositoryResponse>>();
        Assert.NotNull(body);
        Assert.Contains(body, r => r.CloneUrl == "https://github.com/acme/widgets.git");
    }

    [Fact]
    public async Task Ingest_CodeError_CreatesNewTicket_AndEmailsOwner()
    {
        var token = await LoginAndGetTokenAsync("ingest-code@example.com", "Ingest Code");
        var repositoryId = await CreateRepositoryIdAsync(token, "https://github.com/acme/ingest-code.git");

        var response = await IngestAsync(token, repositoryId,
            statusCode: 500,
            method: "POST",
            path: "/api/orders",
            exceptionMessage: "NullReferenceException: Object reference not set to an instance of an object.");

Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ingest = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.NotNull(ingest);
        Assert.Equal("CodeError", ingest.Category);
        Assert.Equal("New", ingest.Status);

        await using var scope = _factory.Services.CreateAsyncScope();
        var owner = await scope.ServiceProvider.GetRequiredService<DevSupDbContext>()
            .EmailMessages.FirstOrDefaultAsync(m => m.To == "ingest-code@example.com");
        Assert.NotNull(owner);
        Assert.False(owner.Sent);
    }

    [Fact]
    public async Task Ingest_NotCodeError_SkipsRepair()
    {
        var token = await LoginAndGetTokenAsync("ingest-notcode@example.com", "Ingest NotCode");
        var repositoryId = await CreateRepositoryIdAsync(token, "https://github.com/acme/ingest-notcode.git");

        var response = await IngestAsync(token, repositoryId,
            statusCode: 401,
            method: "GET",
            path: "/api/secret",
            exceptionMessage: "Unauthorized: invalid access token for external API");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ingest = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.NotNull(ingest);
        Assert.Equal("NotCodeError", ingest.Category);
        Assert.Equal("Credentials", ingest.Kind);
        Assert.Equal("SkippedNotCodeError", ingest.Status);
    }

    [Fact]
    public async Task Ingest_OnRepositoryOfAnotherUser_ReturnsNotFound()
    {
        var tokenA = await LoginAndGetTokenAsync("owner-a@example.com", "Owner A");
        var tokenB = await LoginAndGetTokenAsync("owner-b@example.com", "Owner B");
        var repositoryId = await CreateRepositoryIdAsync(tokenA, "https://github.com/acme/shared.git");

        var response = await IngestAsync(tokenB, repositoryId, statusCode: 500, method: "GET", path: "/api/x");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_WithoutToken_ReturnsUnauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await PostAsync("/api/ingest", new { repositoryId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tickets_ListRepairTicketsForUser()
    {
        var token = await LoginAndGetTokenAsync("tickets@example.com", "Ticket User");
        var repositoryId = await CreateRepositoryIdAsync(token, "https://github.com/acme/tickets.git");
        await IngestAsync(token, repositoryId, statusCode: 500, method: "GET", path: "/api/quote",
            exceptionMessage: "TimeoutException: the operation timed out after 30000ms");

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/tickets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tickets = await response.Content.ReadFromJsonAsync<List<TicketResponse>>();
        Assert.NotNull(tickets);
        Assert.Contains(tickets, t => t.RepositoryId == repositoryId && t.Category == "CodeError");
    }

    private Task<HttpResponseMessage> RegisterUserAsync(string email, string displayName)
        => PostAsync("/api/users/register", new { email, displayName, password = "password123" });

    private Task<HttpResponseMessage> LoginAsync(string email, string password)
        => PostAsync("/api/users/login", new { email, password });

    private async Task<string> LoginAndGetTokenAsync(string email, string displayName)
    {
        await RegisterUserAsync(email, displayName);
        var login = await LoginAsync(email, "password123");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var payload = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload);
        return payload.Token;
    }

    private async Task<HttpResponseMessage> CreateRepositoryAsync(string token, string cloneUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/repositories")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { provider = "github", cloneUrl, defaultBranch = "main" }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return await _client.SendAsync(request);
    }

    private async Task<Guid> CreateRepositoryIdAsync(string token, string cloneUrl)
    {
        var create = await CreateRepositoryAsync(token, cloneUrl);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var response = await create.Content.ReadFromJsonAsync<RepositoryResponse>();
        Assert.NotNull(response);
        return response.Id;
    }

    private Task<HttpResponseMessage> IngestAsync(
        string token, Guid repositoryId, int statusCode, string method, string path, string? exceptionMessage = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { repositoryId, statusCode, method, path, exceptionMessage }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body)
        => _client.PostAsJsonAsync(path, body);
}