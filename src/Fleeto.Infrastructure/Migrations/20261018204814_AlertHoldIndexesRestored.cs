using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AlertHoldIndexesRestored : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Alerts_Held",
                table: "Alerts");

            migrationBuilder.DropIndex(
                name: "IX_Alerts_HeldSource",
                table: "Alerts");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Held",
                table: "Alerts",
                column: "HeldUntil",
                filter: "\"HeldUntil\" IS NOT NULL AND \"State\" <> 'Resolved'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Alerts_Held",
                table: "Alerts");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Held",
                table: "Alerts",
                column: "HeldUntil",
                filter: "\"HeldUntil\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_HeldSource",
                table: "Alerts",
                columns: new[] { "EndpointId", "Kind" },
                filter: "\"HeldUntil\" IS NOT NULL");
        }
    }
}
