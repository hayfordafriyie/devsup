namespace DevSup.Infrastructure;

/// <summary>
/// Marker type for the Infrastructure project. EF Core context, git provider adapters
/// (GitHub/GitLab), AI model providers (BYO-key), email transport and payload
/// sanitization land here as the project evolves.
/// </summary>
public static class InfrastructureMarker
{
    public const string Name = "DevSup.Infrastructure";
}