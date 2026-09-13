using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevSup.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuietHoursAndEmailNotBefore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuietHoursEnd",
                table: "notification_preferences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuietHoursStart",
                table: "notification_preferences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotBefore",
                table: "email_messages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuietHoursEnd",
                table: "notification_preferences");

            migrationBuilder.DropColumn(
                name: "QuietHoursStart",
                table: "notification_preferences");

            migrationBuilder.DropColumn(
                name: "NotBefore",
                table: "email_messages");
        }
    }
}
