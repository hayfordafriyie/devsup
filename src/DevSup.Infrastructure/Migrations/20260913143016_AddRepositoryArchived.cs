using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevSup.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryArchived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Archived",
                table: "connected_repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "connected_repositories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Archived",
                table: "connected_repositories");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "connected_repositories");
        }
    }
}
