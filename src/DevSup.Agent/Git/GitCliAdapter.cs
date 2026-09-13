namespace DevSup.Agent.Git;

using Microsoft.Extensions.Logging;

/// <summary>
/// Git operations used by the repair pipeline. Abstracted so the pipeline is
/// testable without a real network clone and push.
/// </summary>
public interface IGitAdapter
{
    /// <summary>Clones <paramref name="cloneUrl"/> into a fresh temp working directory and returns its path.</summary>
    Task<string> CloneAsync(string cloneUrl, string branch, string accessToken, CancellationToken ct);

    /// <summary>
    /// Stages all changes, commits with <paramref name="message"/> and pushes to the
    /// remote branch the repository was cloned from. Returns the resulting commit SHA.
    /// </summary>
    Task<string> CommitAndPushAsync(string workingDirectory, string message, string user, string email, CancellationToken ct);

    /// <summary>Removes a previously cloned working directory (best-effort).</summary>
    Task CleanupAsync(string workingDirectory, CancellationToken ct);
}

/// <summary>
/// Real implementation backed by the git CLI. Only https:// clone URLs are
/// supported; the user's access token is embedded as an x-access-token credential
/// in the URL and is never written to the filesystem or logs.
/// </summary>
public sealed class GitCliAdapter : IGitAdapter
{
    private readonly ILogger<GitCliAdapter> _logger;

    public GitCliAdapter(ILogger<GitCliAdapter> logger) => _logger = logger;

    public async Task<string> CloneAsync(string cloneUrl, string branch, string accessToken, CancellationToken ct)
    {
        if (!Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Repair pushes require an https clone URL so the owner's token can be attached.");
        }

        var target = Path.Combine(Path.GetTempPath(), "devsup-workspaces", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);

        var authUrl = $"{uri.Scheme}://x-access-token:{accessToken}@{uri.Host}" +
                      (uri.Port > 0 ? $":{uri.Port}" : "") + uri.AbsolutePath;
        var sanitizedUrl = $"{uri.Scheme}://***@{uri.Host}{uri.AbsolutePath}";

        try
        {
            await RunAsync(target, ct, "clone", "--depth", "1", "--branch", branch, authUrl, ".").ConfigureAwait(false);
            return target;
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(target);
            _logger.LogError(ex, "Clone failed for {Url}", sanitizedUrl);
            throw;
        }
    }

    public async Task<string> CommitAndPushAsync(string workingDirectory, string message, string user, string email, CancellationToken ct)
    {
        await RunAsync(workingDirectory, ct, "config", "user.name", user).ConfigureAwait(false);
        await RunAsync(workingDirectory, ct, "config", "user.email", email).ConfigureAwait(false);
        await RunAsync(workingDirectory, ct, "add", "-A").ConfigureAwait(false);
        await RunAsync(workingDirectory, ct, "commit", "-m", message, "--allow-empty").ConfigureAwait(false);
        var sha = (await RunAsync(workingDirectory, ct, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        await RunAsync(workingDirectory, ct, "push", "origin", "HEAD").ConfigureAwait(false);
        return sha;
    }

    public Task CleanupAsync(string workingDirectory, CancellationToken ct)
    {
        TryDeleteDirectory(workingDirectory);
        return Task.CompletedTask;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort: the workspace sits under the temp dir anyway.
        }
    }

    private static async Task<string> RunAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start git process.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args[0]} failed ({process.ExitCode}): {error.Trim()} {output.Trim()}");
        }
        return output;
    }
}