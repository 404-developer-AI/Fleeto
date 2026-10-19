using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TurnOffWindowsUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TurnOffWindowsUpdate",
                table: "PatchPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WindowsUpdate",
                table: "InventorySnapshots",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TurnOffWindowsUpdate",
                table: "PatchPolicies");

            migrationBuilder.DropColumn(
                name: "WindowsUpdate",
                table: "InventorySnapshots");
        }
    }
}
