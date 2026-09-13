using DevSup.Api.Infra;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevSup.Tests;

public sealed class DatabaseOptionsTests
{
    [Fact]
    public void DefaultsToSqlite()
    {
        var options = DatabaseOptions.UseProvider(
            new DbContextOptionsBuilder<DevSupDbContext>(), null, "Data Source=devsup.db");

        Assert.Contains(options.Options.Extensions, e => e.GetType().Name == "SqliteOptionsExtension");
    }

    [Fact]
    public void Sqlite_IsAcceptedExplicitly()
    {
        var options = DatabaseOptions.UseProvider(
            new DbContextOptionsBuilder<DevSupDbContext>(), "SQLITE", "Data Source=devsup.db");

        Assert.Contains(options.Options.Extensions, e => e.GetType().Name == "SqliteOptionsExtension");
    }

    [Fact]
    public void Postgresql_SelectsNpgsql()
    {
        var options = DatabaseOptions.UseProvider(
            new DbContextOptionsBuilder<DevSupDbContext>(),
            "postgresql",
            "Host=localhost;Database=devsup;Username=devsup;Password=devsup");

        Assert.Contains(options.Options.Extensions, e => e.GetType().Name == "NpgsqlOptionsExtension");
    }

    [Fact]
    public void UnknownProvider_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseOptions.UseProvider(
                new DbContextOptionsBuilder<DevSupDbContext>(), "oracle", "Data Source=x"));

        Assert.Contains("oracle", exception.Message);
    }
}