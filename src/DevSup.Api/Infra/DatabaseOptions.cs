using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DevSup.Api.Infra;

/// <summary>
/// Picks the EF Core provider from configuration (<c>Database:Provider</c>). SQLite is
/// the default (and the migration-authoring target); <c>postgresql</c> opts into Npgsql
/// for the same shared migration set.
/// </summary>
public static class DatabaseOptions
{
    public static DbContextOptionsBuilder UseProvider(DbContextOptionsBuilder builder, string? provider, string connectionString)
    {
        return provider?.Trim().ToLowerInvariant() switch
        {
            null or "" or "sqlite" => builder.UseSqlite(connectionString),
            "postgresql" or "postgres" or "npgsql" => builder.UseNpgsql(connectionString),
            var other => throw new InvalidOperationException(
                $"Unknown Database:Provider '{other}'. Supported values: sqlite, postgresql.")
        };
    }
}