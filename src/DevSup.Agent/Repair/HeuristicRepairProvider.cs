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
            return RepairApplicator.NoPatch(kind, "No .devsup/repairs.json template found in the repository, so no automatic repair could be matched. A human should inspect the failure.");
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
            return RepairApplicator.NoPatch(kind, $".devsup/repairs.json could not be parsed ({ex.Message}); no automatic repair applied.");
        }

        var matching = entries.FirstOrDefault(e =>
            (e.Path is not null && string.Equals(e.Path, failure.Path, StringComparison.OrdinalIgnoreCase))
            || (e.Kind is not null && string.Equals(e.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase)));

        if (matching is null || matching.File is null || matching.Fragment is null || matching.Replacement is null)
        {
            return RepairApplicator.NoPatch(kind, $"No repair template matched routing path '{failure.Path}' or error kind '{kind}'. Escalated for human review.");
        }

        return RepairApplicator.Apply(failure, kind, repositoryRoot, matching.File, matching.Fragment, matching.Replacement, matching.Summary);
    }

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