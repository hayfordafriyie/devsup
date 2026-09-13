namespace DevSup.Agent.Repair;

using DevSup.Agent.Ai;
using DevSup.Core;
using DevSup.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// AI-driven repair: resolves the user's bound model endpoint, asks the generator
/// for a surgical patch, and funnels the answer through the same verified-replace
/// gate as template repairs. A model reply is never trusted past the gate.
/// </summary>
public sealed class AiRepairProvider(
    IAiPatchGenerator generator,
    AiEndpointResolver endpointResolver,
    ILogger<AiRepairProvider> logger)
{
    public async Task<RepairProposal> GenerateAsync(
        FailureEvent failure,
        ErrorKind kind,
        string repositoryRoot,
        AiModelKeyBinding binding,
        string apiKey,
        CancellationToken ct)
    {
        var endpoint = endpointResolver.Resolve(binding.Provider, binding.Model);
        var prompt = BuildPrompt(failure, kind, repositoryRoot);

        var suggestion = await generator.GenerateAsync(
            new AiGenerationRequest(failure, kind, prompt), endpoint, apiKey, ct);

        if (suggestion is null)
        {
            return RepairApplicator.NoPatch(kind,
                $"AI provider '{binding.Provider}' returned no usable patch for '{failure.Method} {failure.Path}'.");
        }

        var proposal = RepairApplicator.Apply(
            failure, kind, repositoryRoot,
            suggestion.File, suggestion.Fragment, suggestion.Replacement, suggestion.Summary);

        logger.LogInformation("AI repair for ticket on {Path}: patch={HasPatch} via {Provider}/{Model}",
            failure.Path, proposal.HasPatch, binding.Provider, binding.Model);

        return proposal;
    }

    private static string BuildPrompt(FailureEvent failure, ErrorKind kind, string repositoryRoot)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Production API failure detected:");
        sb.AppendLine($"- Endpoint: {failure.Method} {failure.Path} (HTTP {failure.StatusCode})");
        sb.AppendLine($"- Kind: {kind}");
        if (!string.IsNullOrWhiteSpace(failure.ExceptionMessage))
        {
            sb.AppendLine($"- Exception: {Truncate(failure.ExceptionMessage, 2000)}");
        }
        if (!string.IsNullOrWhiteSpace(failure.StackTrace))
        {
            sb.AppendLine("- Stack trace:");
            sb.AppendLine(Truncate(failure.StackTrace, 8000));
        }

        var codeContext = FindCrashingFile(repositoryRoot, failure.StackTrace);
        if (codeContext is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"The failing file lives in the repository at '{codeContext.Value.Relative}', contents:");
            sb.AppendLine("```");
            sb.AppendLine(Truncate(codeContext.Value.Content, 8192));
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    /// <summary>Best-effort: grabs the file named by a stack-trace frame if it exists in the check-out.</summary>
    private static (string Relative, string Content)? FindCrashingFile(string repositoryRoot, string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return null;
        }

        foreach (var token in stackTrace.Split([' ', ':', '(', ')', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var name = Path.GetFileName(token);
            var match = Directory.EnumerateFiles(repositoryRoot, name, SearchOption.AllDirectories).FirstOrDefault();
            if (match is null)
            {
                continue;
            }
            try
            {
                var content = File.ReadAllText(match);
                if (content.Length > 16_384)
                {
                    continue;
                }
                var relative = Path.GetRelativePath(repositoryRoot, match);
                return (relative, content);
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}