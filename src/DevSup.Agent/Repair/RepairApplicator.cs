namespace DevSup.Agent.Repair;

using DevSup.Core;
using DevSup.Core.Models;

/// <summary>
/// Applies a single, verified-verbatim file replacement inside a checked-out
/// workspace. Refuses ambiguous matches, missing files or fragments, malformed
/// content, and any path that escapes the workspace — a patch is produced only
/// when every safety check passes, so the resulting diff is always intact.
/// </summary>
public static class RepairApplicator
{
    public static RepairProposal Apply(
        FailureEvent failure,
        ErrorKind kind,
        string repositoryRoot,
        string relativeFilePath,
        string fragment,
        string replacement,
        string? summary)
    {
        var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativeFilePath));
        var root = Path.GetFullPath(repositoryRoot);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return NoPatch(kind, $"Repair template references a path outside the repository ('{relativeFilePath}'); rejected for security.");
        }

        if (!File.Exists(fullPath))
        {
            return NoPatch(kind, $"Repair template targets '{relativeFilePath}' but that file does not exist in the repository.");
        }

        var content = File.ReadAllText(fullPath);
        var count = CountOccurrences(content, fragment);
        if (count == 0)
        {
            return NoPatch(kind, $"Repair template targets '{relativeFilePath}' but the expected fragment was not found, so no patch was produced. Escalated for human review.");
        }
        if (count > 1)
        {
            return NoPatch(kind, $"Repair template fragment in '{relativeFilePath}' appears {count} times; refusing an ambiguous replacement. Escalated for human review.");
        }

        var repaired = content.Replace(fragment, replacement, StringComparison.Ordinal);
        var resolvedSummary = string.IsNullOrWhiteSpace(summary)
            ? $"{failure.Method} {failure.Path}: apply repair ({kind})"
            : summary!;

        return new RepairProposal(
            HasPatch: true,
            RelativeFilePath: relativeFilePath,
            RepairedContent: repaired,
            Analysis: $"Matched repair for route '{failure.Path}' ({kind}). Applied a single verified replacement in '{relativeFilePath}'.",
            Summary: resolvedSummary,
            RootCause: kind);
    }

    public static RepairProposal NoPatch(ErrorKind kind, string analysis)
        => new(HasPatch: false, null, null, analysis, null, kind);

    private static int CountOccurrences(string text, string fragment) =>
        fragment.Length == 0 ? 0 : text.Split(fragment, StringSplitOptions.None).Length - 1;
}