namespace DevSup.Agent.Repair;

using System.Text.Json;
using System.Text.Json.Serialization;
using DevSup.Core;
using DevSup.Core.Models;

/// <summary>
/// Template-driven repair provider. Reads <c>.devsup/repairs.json</c> from the
/// checked-out repository and, when an entry matches the failing route or error
/// kind, applies an exact, verified string replacement in the target file.
/// This is deliberately conservative: a patch is produced only when the
/// fragment is found verbatim, so the generated diff is always syntactically
/// intact. Anything else is escalated to <see cref="TicketStatus.NeedsHumanReview"/>.
/// </summary>
public sealed class HeuristicRepairProvider : IRepairProvider
{
    public const string RepairsFile = ".devsup/repairs.json";

    public RepairProposal Repair(FailureEvent failure, ErrorKind kind, string repositoryRoot)
    {
        var repairsFile = Path.Combine(repositoryRoot, RepairsFile);
        if (!File.Exists(repairsFile))
        {
            return new RepairProposal(
                HasPatch: false, null, null,
                Analysis: "No .devsup/repairs.json template found in the repository, so no automatic repair could be matched. A human should inspect the failure.",
                Summary: null, kind);
        }

        RepairEntry[] entries;
        try
        {
            entries = JsonSerializer.Deserialize<RepairsFileSchema>(
                File.ReadAllText(repairsFile),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
                })?.Repairs ?? [];
        }
        catch (JsonException ex)
        {
            return new RepairProposal(
                HasPatch: false, null, null,
                Analysis: $".devsup/repairs.json could not be parsed ({ex.Message}); no automatic repair applied.",
                Summary: null, kind);
        }

        var matching = entries.FirstOrDefault(e =>
            (e.Path is not null && string.Equals(e.Path, failure.Path, StringComparison.OrdinalIgnoreCase))
            || (e.Kind is not null && string.Equals(e.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase)));

        if (matching is null || matching.File is null || matching.Fragment is null || matching.Replacement is null)
        {
            return new RepairProposal(
                HasPatch: false, null, null,
                Analysis: $"No repair template matched routing path '{failure.Path}' or error kind '{kind}'. Escalated for human review.",
                Summary: null, kind);
        }

        // Path traversal guard: the resolved file must stay inside the working directory.
        var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, matching.File));
        var root = Path.GetFullPath(repositoryRoot);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new RepairProposal(
                HasPatch: false, null, null,
                Analysis: $"Repair template references a path outside the repository ('{matching.File}'); rejected for security.",
                Summary: null, kind);
        }

        if (!File.Exists(fullPath))
        {
            return new RepairProposal(
                HasPatch: false, null, null,
                Analysis: $"Repair template targets '{matching.File}' but that file does not exist in the repository.",
                Summary: null, kind);
        }

        var content = File.ReadAllText(fullPath);
        var count = CountOccurrences(content, matching.Fragment);
        if (count == 0)
        {
            return new RepairProposal(
                HasPatch: false, null, null,
                Analysis: $"Repair template targets '{matching.File}' but the expected fragment was not found, so no patch was produced. Escalated for human review.",
                Summary: null, kind);
        }
        if (count > 1)
        {
            return new RepairProposal(
                HasPatch: false, null, null,
                Analysis: $"Repair template fragment in '{matching.File}' appears {count} times; refusing an ambiguous replacement. Escalated for human review.",
                Summary: null, kind);
        }

        var repaired = content.Replace(matching.Fragment, matching.Replacement, StringComparison.Ordinal);
        var summary = string.IsNullOrWhiteSpace(matching.Summary)
            ? $"{failure.Method} {failure.Path}: apply repair template ({kind})"
            : matching.Summary;

        return new RepairProposal(
            HasPatch: true, matching.File, repaired,
            Analysis: $"Matched repair template for route '{failure.Path}' ({kind}). Applied a single verified replacement in '{matching.File}'.",
            Summary: summary, kind);
    }

    private static int CountOccurrences(string text, string fragment) =>
        fragment.Length == 0 ? 0 : text.Split(fragment, StringSplitOptions.None).Length - 1;

    private sealed class RepairsFileSchema
    {
        public RepairEntry[] Repairs { get; init; } = [];
    }

    private sealed class RepairEntry
    {
        public string? Path { get; init; }
        public string? Kind { get; init; }
        public string? File { get; init; }
        public string? Fragment { get; init; }
        public string? Replacement { get; init; }
        public string? Summary { get; init; }
    }
}