using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PatchScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Engine",
                table: "EndpointPatchStates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Action1");

            migrationBuilder.AddColumn<DateTime>(
                name: "ScanFailingSince",
                table: "EndpointPatchStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScanProblem",
                table: "EndpointPatchStates",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScanTool",
                table: "EndpointPatchStates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Advisory",
                table: "EndpointMissingUpdates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeenAt",
                table: "EndpointMissingUpdates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Held",
                table: "EndpointMissingUpdates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "EndpointMissingUpdates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Removal",
                table: "EndpointMissingUpdates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PatchScanRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchScanRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatchScanRequests_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatchScans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AgentStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Tool = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RestartRequired = table.Column<bool>(type: "boolean", nullable: false),
                    UpdateCount = table.Column<int>(type: "integer", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdatesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchScans", x => x.Id);
                    table.CheckConstraint("CK_PatchScans_Tool", "\"Tool\" IN ('apt', 'dnf', 'windows_update')");
                    table.CheckConstraint("CK_PatchScans_UpdateCount", "\"UpdateCount\" BETWEEN 0 AND 2000");
                    table.ForeignKey(
                        name: "FK_PatchScans_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatchScanRequests_EndpointId_ClientId",
                table: "PatchScanRequests",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatchScanRequests_EndpointId_RequestedAt",
                table: "PatchScanRequests",
                columns: new[] { "EndpointId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatchScanRequests_Pending",
                table: "PatchScanRequests",
                column: "ExpiresAt",
                filter: "\"DeliveredAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatchScanRequests_RequestedAt",
                table: "PatchScanRequests",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PatchScans_EndpointId_AgentScanId",
                table: "PatchScans",
                columns: new[] { "EndpointId", "AgentScanId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatchScans_EndpointId_ClientId",
                table: "PatchScans",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatchScans_EndpointId_ReceivedAt",
                table: "PatchScans",
                columns: new[] { "EndpointId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatchScans_Unprocessed",
                table: "PatchScans",
                column: "ReceivedAt",
                filter: "\"ProcessedAt\" IS NULL");

            // A new scan request wakes the gateway, which delivers it to a live agent (0.9.0).
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_PatchScanRequests_Notify" AFTER INSERT ON "PatchScanRequests"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_notify_id('fleeto_patch_scan_requests');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "TR_PatchScanRequests_Notify" ON "PatchScanRequests";""");

            migrationBuilder.DropTable(
                name: "PatchScanRequests");

            migrationBuilder.DropTable(
                name: "PatchScans");

            migrationBuilder.DropColumn(
                name: "Engine",
                table: "EndpointPatchStates");

            migrationBuilder.DropColumn(
                name: "ScanFailingSince",
                table: "EndpointPatchStates");

            migrationBuilder.DropColumn(
                name: "ScanProblem",
                table: "EndpointPatchStates");

            migrationBuilder.DropColumn(
                name: "ScanTool",
                table: "EndpointPatchStates");

            migrationBuilder.DropColumn(
                name: "Advisory",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "FirstSeenAt",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "Held",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "Removal",
                table: "EndpointMissingUpdates");
        }
    }
}
