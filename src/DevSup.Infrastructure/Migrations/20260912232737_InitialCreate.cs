using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevSup.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_model_key_bindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EncryptedApiKey = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_model_key_bindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "connected_repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    CloneUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DefaultBranch = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    AppUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connected_repositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "email_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    HtmlBody = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    Sent = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "failure_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    RequestPayload = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: true),
                    ResponsePayload = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: true),
                    ExceptionMessage = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    StackTrace = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_failure_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "repair_tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FailureEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Analysis = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    PatchSummary = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: true),
                    CommitSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repair_tickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connected_repositories_OwnerUserId_CloneUrl",
                table: "connected_repositories",
                columns: new[] { "OwnerUserId", "CloneUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_messages_Sent_CreatedAt",
                table: "email_messages",
                columns: new[] { "Sent", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_failure_events_RepositoryId_OccurredAt",
                table: "failure_events",
                columns: new[] { "RepositoryId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_repair_tickets_RepositoryId_Status",
                table: "repair_tickets",
                columns: new[] { "RepositoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_model_key_bindings");

            migrationBuilder.DropTable(
                name: "connected_repositories");

            migrationBuilder.DropTable(
                name: "email_messages");

            migrationBuilder.DropTable(
                name: "failure_events");

            migrationBuilder.DropTable(
                name: "repair_tickets");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
