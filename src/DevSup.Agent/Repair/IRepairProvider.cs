namespace DevSup.Agent.Repair;

using DevSup.Core;
using DevSup.Core.Models;

/// <summary>
/// Outcome of a repair investigation: either a concrete matched auto-repair
/// (full replacement content for one file) or a recommendation for a human.
/// </summary>
public sealed record RepairProposal(
    bool HasPatch,
    string? RelativeFilePath,
    string? RepairedContent,
    string Analysis,
    string? Summary,
    ErrorKind RootCause);

public interface IRepairProvider
{
    /// <summary>
    /// Investigate a failure (with the classifier's <paramref name="kind"/>) against an
    /// already-checked-out repository working directory and, when a safe repair is
    /// available, produce it.
    /// </summary>
    RepairProposal Repair(FailureEvent failure, ErrorKind kind, string repositoryRoot);
}