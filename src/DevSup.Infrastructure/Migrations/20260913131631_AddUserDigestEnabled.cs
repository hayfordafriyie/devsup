using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevSup.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDigestEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DigestEnabled",
                table: "users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigestEnabled",
                table: "users");
        }
    }
}
