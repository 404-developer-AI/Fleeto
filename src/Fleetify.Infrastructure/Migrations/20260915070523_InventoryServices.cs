using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetify.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InventoryServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ServicesJson",
                table: "InventorySnapshots",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServicesJson",
                table: "InventorySnapshots");
        }
    }
}
