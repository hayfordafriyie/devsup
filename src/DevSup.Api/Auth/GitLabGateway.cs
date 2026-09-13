namespace DevSup.Api.Auth;

public sealed class GitLabAuthSettings
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = "/api/auth/gitlab/callback";
    public string Scope { get; init; } = "read_user api";
    public string AuthorizeUrl { get; init; } = "https://gitlab.com/oauth/authorize";
    public string TokenUrl { get; init; } = "https://gitlab.com/oauth/token";
    public string UserApiUrl { get; init; } = "https://gitlab.com/api/v4/user";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public interface IGitLabGateway
{
    string BuildAuthorizeUrl(string state);

    Task<GitLabTokenResult> ExchangeCodeAsync(string code, CancellationToken ct);

    Task<GitLabProfile> GetProfileAsync(string accessToken, CancellationToken ct);
}

public sealed record GitLabTokenResult(string AccessToken, string? Scope);

public sealed record GitLabProfile(string Login, string? Name, string? Email);

/// <summary>
/// Thin HTTP client for the GitLab OAuth v3 dance: authorization-code flow then a
/// single GET to /api/v4/user to resolve the linked account's email.
/// </summary>
public sealed class GitLabGateway(HttpClient http, GitLabAuthSettings settings) : IGitLabGateway
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
                ["response_type"] = "code"
            });

    public async Task<GitLabTokenResult> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = settings.RedirectUri
            })
        };

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<GitLabTokenPayload>(ct)
            ?? throw new InvalidOperationException("GitLab token exchange returned an empty body.");

        if (string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new InvalidOperationException($"GitLab token exchange failed: {payload.ErrorDescription ?? payload.Error ?? "unknown"}");
        }

        return new GitLabTokenResult(payload.AccessToken, payload.Scope);
    }

    public async Task<GitLabProfile> GetProfileAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.UserApiUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var profile = await response.Content.ReadFromJsonAsync<GitLabUserPayload>(ct)
            ?? throw new InvalidOperationException("GitLab profile response was empty.");

        return new GitLabProfile(profile.Username, profile.Name, profile.Email);
    }

    private sealed record GitLabTokenPayload(string? AccessToken, string? Scope, string? Error, string? ErrorDescription);
    private sealed record GitLabUserPayload(string Username, string? Name, string? Email);
}