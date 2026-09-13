namespace DevSup.Agent.PullRequests;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevSup.Core;
using Microsoft.Extensions.Logging;

/// <summary>
/// REST client that opens pull/merge requests on GitHub or GitLab. The repository
/// slug and host are derived from the https clone URL, and the caller's provider
/// token is attached to the request only (never persisted or logged).
/// </summary>
public sealed class PullRequestGateway(
    IHttpClientFactory httpClientFactory,
    ILogger<PullRequestGateway> logger) : IPullRequestGateway
{
    public async Task<string> OpenAsync(
        GitProvider provider,
        string cloneUrl,
        string sourceBranch,
        string targetBranch,
        string title,
        string body,
        string accessToken,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Pull requests require an https clone URL.");
        }

        var slug = uri.AbsolutePath.TrimStart('/');
        if (slug.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            slug = slug[..^4];
        }

        using var client = httpClientFactory.CreateClient("devsup-provider");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.Timeout = TimeSpan.FromSeconds(15);

        using var response = provider == GitProvider.GitLab
            ? await PostMergeRequestAsync(client, uri, slug, sourceBranch, targetBranch, title, body, ct)
            : await PostPullRequestAsync(client, uri, slug, sourceBranch, targetBranch, title, body, ct);

        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("PR open failed ({Status}) for {Provider} {Slug}", response.StatusCode, provider, slug);
            throw new InvalidOperationException($"Provider rejected the pull request ({response.StatusCode}): {Truncate(content, 500)}");
        }

        using var doc = JsonDocument.Parse(content);
        var urlProperty = provider == GitProvider.GitLab ? "web_url" : "html_url";
        var url = doc.RootElement.TryGetProperty(urlProperty, out var node) ? node.GetString() : null;
        return url ?? string.Empty;
    }

    private static Task<HttpResponseMessage> PostPullRequestAsync(
        HttpClient client, Uri uri, string slug, string source, string target, string title, string body, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["title"] = title,
            ["head"] = source,
            ["base"] = target,
            ["body"] = body
        };
        return client.PostAsJsonAsync($"https://{uri.Host}/repos/{slug}/pulls", payload, ct);
    }

    private static Task<HttpResponseMessage> PostMergeRequestAsync(
        HttpClient client, Uri uri, string slug, string source, string target, string title, string body, CancellationToken ct)
    {
        // GitLab projects are addressed by URL-encoded path_with_namespace, e.g. group%2Fproject.
        var projectId = Uri.EscapeDataString(slug);
        var payload = new JsonObject
        {
            ["source_branch"] = source,
            ["target_branch"] = target,
            ["title"] = title,
            ["description"] = body,
            ["remove_source_branch"] = true
        };
        return client.PostAsJsonAsync($"https://{uri.Host}/api/v4/projects/{projectId}/merge_requests", payload, ct);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}