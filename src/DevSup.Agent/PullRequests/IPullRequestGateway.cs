namespace DevSup.Agent.PullRequests;

using DevSup.Core;

/// <summary>
/// Opens a pull request (or merge request on GitLab) for a repair fix on a
/// feature branch. Implementations use the repository owner's linked provider
/// token, never the patched host's credentials.
/// </summary>
public interface IPullRequestGateway
{
    /// <summary>
    /// Opens a PR/MR from <paramref name="sourceBranch"/> into
    /// <paramref name="targetBranch"/>. Returns the web URL of the created request.
    /// </summary>
    Task<string> OpenAsync(
        GitProvider provider,
        string cloneUrl,
        string sourceBranch,
        string targetBranch,
        string title,
        string body,
        string accessToken,
        CancellationToken ct);
}