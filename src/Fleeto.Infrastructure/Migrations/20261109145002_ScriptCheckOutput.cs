using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScriptCheckOutput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProblemCount",
                table: "CheckResultsHourly",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProblemCount",
                table: "CheckResultsDaily",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorOutput",
                table: "CheckResults",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Output",
                table: "CheckResults",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutputBytes",
                table: "CheckResults",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputLimitBytes",
                table: "CheckResults",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CheckResults_ScriptOutput",
                table: "CheckResults",
                columns: new[] { "EndpointId", "CheckDefinitionId", "Time" },
                filter: "\"Output\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CheckResults_ScriptOutput",
                table: "CheckResults");

            migrationBuilder.DropColumn(
                name: "ProblemCount",
                table: "CheckResultsHourly");

            migrationBuilder.DropColumn(
                name: "ProblemCount",
                table: "CheckResultsDaily");

            migrationBuilder.DropColumn(
                name: "ErrorOutput",
                table: "CheckResults");

            migrationBuilder.DropColumn(
                name: "Output",
                table: "CheckResults");

            migrationBuilder.DropColumn(
                name: "OutputBytes",
                table: "CheckResults");

            migrationBuilder.DropColumn(
                name: "OutputLimitBytes",
                table: "CheckResults");
        }
    }
}
