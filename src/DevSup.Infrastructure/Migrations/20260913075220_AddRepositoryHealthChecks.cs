using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevSup.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryHealthChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AppHealthCheckedAt",
                table: "connected_repositories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppHealthLastError",
                table: "connected_repositories",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AppHealthy",
                table: "connected_repositories",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppHealthCheckedAt",
                table: "connected_repositories");

            migrationBuilder.DropColumn(
                name: "AppHealthLastError",
                table: "connected_repositories");

            migrationBuilder.DropColumn(
                name: "AppHealthy",
                table: "connected_repositories");
        }
    }
}
