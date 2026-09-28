using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CheckResultProcesses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProcessesJson",
                table: "CheckResults",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CheckResults_ProcessLists",
                table: "CheckResults",
                columns: new[] { "EndpointId", "CheckDefinitionId", "Time" },
                filter: "\"ProcessesJson\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CheckResults_ProcessLists",
                table: "CheckResults");

            migrationBuilder.DropColumn(
                name: "ProcessesJson",
                table: "CheckResults");
        }
    }
}
