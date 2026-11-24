using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// The task history of the public API (0.9.0, GET /api/v1/task-results): newest due first per client, per endpoint and over every client,
    /// paged on (DueAt, RunId, EndpointId). Expand only: the previous release does not use them.
    /// </summary>
    public partial class TaskHistoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRunTargets_ClientHistory",
                table: "AutomationTaskRunTargets",
                columns: new[] { "ClientId", "DueAt", "RunId", "EndpointId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRunTargets_EndpointHistory",
                table: "AutomationTaskRunTargets",
                columns: new[] { "EndpointId", "DueAt", "RunId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRunTargets_History",
                table: "AutomationTaskRunTargets",
                columns: new[] { "DueAt", "RunId", "EndpointId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AutomationTaskRunTargets_ClientHistory",
                table: "AutomationTaskRunTargets");

            migrationBuilder.DropIndex(
                name: "IX_AutomationTaskRunTargets_EndpointHistory",
                table: "AutomationTaskRunTargets");

            migrationBuilder.DropIndex(
                name: "IX_AutomationTaskRunTargets_History",
                table: "AutomationTaskRunTargets");
        }
    }
}
