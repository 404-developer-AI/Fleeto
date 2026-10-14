using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CheckOperatingSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperatingSystem",
                table: "CheckDefinitions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "All");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperatingSystem",
                table: "CheckDefinitions");
        }
    }
}
