using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevSup.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiModelKeyMaskUpdatedAtUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KeyMask",
                table: "ai_model_key_bindings",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "ai_model_key_bindings",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_key_bindings_UserId_Provider_Model",
                table: "ai_model_key_bindings",
                columns: new[] { "UserId", "Provider", "Model" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ai_model_key_bindings_UserId_Provider_Model",
                table: "ai_model_key_bindings");

            migrationBuilder.DropColumn(
                name: "KeyMask",
                table: "ai_model_key_bindings");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ai_model_key_bindings");
        }
    }
}
