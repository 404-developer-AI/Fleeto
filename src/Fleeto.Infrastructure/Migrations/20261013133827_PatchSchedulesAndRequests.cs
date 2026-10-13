using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PatchSchedulesAndRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalAutomationId",
                table: "PatchDeployments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "FollowUntil",
                table: "PatchDeployments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PatchPolicyId",
                table: "PatchDeployments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRunAt",
                table: "IntegrationAutomations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReadAt",
                table: "IntegrationAutomations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AutomationId",
                table: "EndpointPatchStates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduledPolicyId",
                table: "EndpointPatchStates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledSince",
                table: "EndpointPatchStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PatchRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExternalTenantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalEndpointId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Failure = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatchRequests_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeployments_ClientId_ExternalDeploymentId",
                table: "PatchDeployments",
                columns: new[] { "ClientId", "ExternalDeploymentId" },
                unique: true,
                filter: "\"ExternalAutomationId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointPatchStates_AutomationId",
                table: "EndpointPatchStates",
                column: "AutomationId",
                filter: "\"AutomationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatchRequests_EndpointId_ClientId",
                table: "PatchRequests",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatchRequests_EndpointId_RequestedAt",
                table: "PatchRequests",
                columns: new[] { "EndpointId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatchRequests_Pending",
                table: "PatchRequests",
                column: "RequestedAt",
                filter: "\"CompletedAt\" IS NULL");

            // A new request wakes the workers, which are the only ones that can reach the patch management product.
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_PatchRequests_Notify" AFTER INSERT ON "PatchRequests"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_notify_id('fleeto_patch_requests');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "TR_PatchRequests_Notify" ON "PatchRequests";""");

            migrationBuilder.DropTable(
                name: "PatchRequests");

            migrationBuilder.DropIndex(
                name: "IX_PatchDeployments_ClientId_ExternalDeploymentId",
                table: "PatchDeployments");

            migrationBuilder.DropIndex(
                name: "IX_EndpointPatchStates_AutomationId",
                table: "EndpointPatchStates");

            migrationBuilder.DropColumn(
                name: "ExternalAutomationId",
                table: "PatchDeployments");

            migrationBuilder.DropColumn(
                name: "FollowUntil",
                table: "PatchDeployments");

            migrationBuilder.DropColumn(
                name: "PatchPolicyId",
                table: "PatchDeployments");

            migrationBuilder.DropColumn(
                name: "NextRunAt",
                table: "IntegrationAutomations");

            migrationBuilder.DropColumn(
                name: "ReadAt",
                table: "IntegrationAutomations");

            migrationBuilder.DropColumn(
                name: "AutomationId",
                table: "EndpointPatchStates");

            migrationBuilder.DropColumn(
                name: "ScheduledPolicyId",
                table: "EndpointPatchStates");

            migrationBuilder.DropColumn(
                name: "ScheduledSince",
                table: "EndpointPatchStates");
        }
    }
}
