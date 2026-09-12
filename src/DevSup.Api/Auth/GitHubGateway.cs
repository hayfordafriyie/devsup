namespace DevSup.Api.Auth;

public sealed class GitHubAuthSettings
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = "/api/auth/github/callback";
    public string Scope { get; init; } = "repo user:email";
    public string AuthorizeUrl { get; init; } = "https://github.com/login/oauth/authorize";
    public string TokenUrl { get; init; } = "https://github.com/login/oauth/access_token";
    public string UserApiUrl { get; init; } = "https://api.github.com/user";
    public string EmailsApiUrl { get; init; } = "https://api.github.com/user/emails";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public interface IGitHubGateway
{
    string BuildAuthorizeUrl(string state);

    Task<GitHubTokenResult> ExchangeCodeAsync(string code, CancellationToken ct);

    Task<GitHubProfile> GetProfileAsync(string accessToken, CancellationToken ct);
}

public sealed record GitHubTokenResult(string AccessToken, string? Scope);

public sealed record GitHubProfile(string Login, string? Name, string? Email);

/// <summary>
/// Thin HTTP client for the GitHub OAuth dance plus the /user and /user/emails
/// endpoints used to resolve the linked account's email.
/// </summary>
public sealed class GitHubGateway(HttpClient http, GitHubAuthSettings settings) : IGitHubGateway
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

    public async Task<GitHubTokenResult> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = settings.RedirectUri
            })
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<GitHubTokenPayload>(ct)
            ?? throw new InvalidOperationException("GitHub token exchange returned an empty body.");

        if (string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new InvalidOperationException($"GitHub token exchange failed: {payload.ErrorDescription ?? payload.Error ?? "unknown"}");
        }

        return new GitHubTokenResult(payload.AccessToken, payload.Scope);
    }

    public async Task<GitHubProfile> GetProfileAsync(string accessToken, CancellationToken ct)
    {
        using var head = new HttpRequestMessage(HttpMethod.Get, settings.UserApiUrl);
        ApplyAuth(head, accessToken);
        using var headResponse = await http.SendAsync(head, ct);
        headResponse.EnsureSuccessStatusCode();

        var profile = await headResponse.Content.ReadFromJsonAsync<GitHubUserPayload>(ct)
            ?? throw new InvalidOperationException("GitHub profile response was empty.");

        var email = profile.Email;

        if (string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(profile.EmailVisibility))
        {
            using var emails = new HttpRequestMessage(HttpMethod.Get, settings.EmailsApiUrl);
            ApplyAuth(emails, accessToken);
            using var emailsResponse = await http.SendAsync(emails, ct);
            if (emailsResponse.IsSuccessStatusCode)
            {
                var list = await emailsResponse.Content.ReadFromJsonAsync<List<GitHubEmailPayload>>(ct) ?? [];
                var primary = list.FirstOrDefault(e => e.Primary && e.Verified);
                email = primary?.Email ?? email;
            }
        }

        return new GitHubProfile(profile.Login, profile.Name, email);
    }

    private void ApplyAuth(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("DevSup/0.3 (+https://github.com/hayfordafriyie/devsup)");
    }

    private sealed record GitHubTokenPayload(string? AccessToken, string? Scope, string? Error, string? ErrorDescription);
    private sealed record GitHubUserPayload(string Login, string? Name, string? Email, string? EmailVisibility);
    private sealed record GitHubEmailPayload(string? Email, bool Primary, bool Verified);
}