using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Patch state from the patch management product (0.4.0 step 2), additive. One row per endpoint with the counts of
    /// missing updates and whether the product still patches it, plus the missing updates themselves for endpoints that
    /// are not compliant. Both are current state, replaced on every sync and deleted with their endpoint; the product
    /// keeps the history. The integration row records when the last sync ran.
    /// </summary>
    public partial class PatchState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PatchSyncedAt",
                table: "Integrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EndpointMissingUpdates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalUpdateId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Vendor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KbNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RebootNeeded = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointMissingUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EndpointMissingUpdates_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EndpointPatchStates",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalEndpointId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalTenantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Coverage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MissingCritical = table.Column<int>(type: "integer", nullable: false),
                    MissingOther = table.Column<int>(type: "integer", nullable: false),
                    RebootRequired = table.Column<bool>(type: "boolean", nullable: false),
                    ProductLastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProductAgentVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointPatchStates", x => x.EndpointId);
                    table.ForeignKey(
                        name: "FK_EndpointPatchStates_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointMissingUpdates_EndpointId_ClientId",
                table: "EndpointMissingUpdates",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointMissingUpdates_EndpointId_Severity",
                table: "EndpointMissingUpdates",
                columns: new[] { "EndpointId", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointPatchStates_ClientId_MissingCritical",
                table: "EndpointPatchStates",
                columns: new[] { "ClientId", "MissingCritical" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointPatchStates_EndpointId_ClientId",
                table: "EndpointPatchStates",
                columns: new[] { "EndpointId", "ClientId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EndpointMissingUpdates");

            migrationBuilder.DropTable(
                name: "EndpointPatchStates");

            migrationBuilder.DropColumn(
                name: "PatchSyncedAt",
                table: "Integrations");
        }
    }
}
