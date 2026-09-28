using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StorageScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StorageScanIntervalHours",
                table: "Policies",
                type: "integer",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.CreateTable(
                name: "StorageScanRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageScanRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageScanRequests_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StorageScans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Volume = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Filesystem = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    FreeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AgentStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Complete = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FileCount = table.Column<long>(type: "bigint", nullable: false),
                    FolderCount = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    FoldersJson = table.Column<string>(type: "jsonb", nullable: false),
                    FilesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageScans_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Policies_StorageScanIntervalHours",
                table: "Policies",
                sql: "\"StorageScanIntervalHours\" IN (0, 6, 12, 24, 72, 168)");

            migrationBuilder.CreateIndex(
                name: "IX_StorageScanRequests_EndpointId_ClientId",
                table: "StorageScanRequests",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageScanRequests_EndpointId_RequestedAt",
                table: "StorageScanRequests",
                columns: new[] { "EndpointId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageScanRequests_Pending",
                table: "StorageScanRequests",
                column: "ExpiresAt",
                filter: "\"DeliveredAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StorageScanRequests_RequestedAt",
                table: "StorageScanRequests",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StorageScans_EndpointId_AgentScanId",
                table: "StorageScans",
                columns: new[] { "EndpointId", "AgentScanId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageScans_EndpointId_ClientId",
                table: "StorageScans",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageScans_EndpointId_Volume_ReceivedAt",
                table: "StorageScans",
                columns: new[] { "EndpointId", "Volume", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageScans_ReceivedAt",
                table: "StorageScans",
                column: "ReceivedAt");

            // A new scan request wakes the gateway, which delivers it to a live agent.
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_StorageScanRequests_Notify" AFTER INSERT ON "StorageScanRequests"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_notify_id('fleeto_storage_scan_requests');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "TR_StorageScanRequests_Notify" ON "StorageScanRequests";""");

            migrationBuilder.DropTable(
                name: "StorageScanRequests");

            migrationBuilder.DropTable(
                name: "StorageScans");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Policies_StorageScanIntervalHours",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "StorageScanIntervalHours",
                table: "Policies");
        }
    }
}
