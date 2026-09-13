using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevSup.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRepairMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RepairMode",
                table: "connected_repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepairMode",
                table: "connected_repositories");
        }
    }
}
