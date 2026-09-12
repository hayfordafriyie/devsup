using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevSup.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailOutboxRetriesAndOAuthTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "email_messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "email_messages",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SentAt",
                table: "email_messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "oauth_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    EncryptedAccessToken = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LinkedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_tokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_oauth_tokens_UserId_Provider",
                table: "oauth_tokens",
                columns: new[] { "UserId", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oauth_tokens");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "email_messages");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "email_messages");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "email_messages");
        }
    }
}
