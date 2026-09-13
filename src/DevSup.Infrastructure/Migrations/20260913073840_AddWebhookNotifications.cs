using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevSup.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebhookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Event = table.Column<int>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    Sent = table.Column<bool>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    EncryptedSecret = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    EventMask = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_endpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_Sent_CreatedAt",
                table: "webhook_deliveries",
                columns: new[] { "Sent", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_endpoints_UserId_Url",
                table: "webhook_endpoints",
                columns: new[] { "UserId", "Url" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "webhook_endpoints");
        }
    }
}
