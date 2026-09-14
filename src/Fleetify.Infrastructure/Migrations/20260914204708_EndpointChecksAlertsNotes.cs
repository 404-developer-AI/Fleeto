using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetify.Infrastructure.Migrations
{
    /// <summary>
    /// Per-endpoint check management (endpoint-only checks, overrides, extra template links), check run requests, alert
    /// hold, notes, the public IP of an endpoint, and foreign keys from client-specific templates and policies to their client.
    /// Rules and triggers for these tables are in migration EndpointChecksRules.
    /// </summary>
    public partial class EndpointChecksAlertsNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicIpAddress",
                table: "Endpoints",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublicIpSeenAt",
                table: "Endpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResetAt",
                table: "CheckStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "MonitoringTemplateId",
                table: "CheckDefinitions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "EndpointId",
                table: "CheckDefinitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HeldAt",
                table: "Alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HeldByUserId",
                table: "Alerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HeldUntil",
                table: "Alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CheckRunRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reset = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResetAppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckRunRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckRunRequests_CheckDefinitions_CheckDefinitionId",
                        column: x => x.CheckDefinitionId,
                        principalTable: "CheckDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CheckRunRequests_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EndpointCheckOverrides",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "integer", nullable: true),
                    FailuresBeforeAlert = table.Column<int>(type: "integer", nullable: true),
                    OverrideThresholds = table.Column<bool>(type: "boolean", nullable: false),
                    WarningThreshold = table.Column<double>(type: "double precision", nullable: true),
                    CriticalThreshold = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointCheckOverrides", x => new { x.EndpointId, x.CheckDefinitionId });
                    table.CheckConstraint("CK_EndpointCheckOverrides_Failures", "\"FailuresBeforeAlert\" IS NULL OR \"FailuresBeforeAlert\" BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_EndpointCheckOverrides_Interval", "\"IntervalSeconds\" IS NULL OR \"IntervalSeconds\" BETWEEN 10 AND 2678400");
                    table.ForeignKey(
                        name: "FK_EndpointCheckOverrides_CheckDefinitions_CheckDefinitionId",
                        column: x => x.CheckDefinitionId,
                        principalTable: "CheckDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EndpointCheckOverrides_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EndpointMonitoringTemplates",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    MonitoringTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointMonitoringTemplates", x => new { x.EndpointId, x.MonitoringTemplateId });
                    table.ForeignKey(
                        name: "FK_EndpointMonitoringTemplates_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EndpointMonitoringTemplates_MonitoringTemplates_MonitoringT~",
                        column: x => x.MonitoringTemplateId,
                        principalTable: "MonitoringTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notes", x => x.Id);
                    table.CheckConstraint("CK_Notes_Body", "char_length(\"Body\") BETWEEN 1 AND 20000");
                    table.ForeignKey(
                        name: "FK_Notes_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckDefinitions_EndpointId_ClientId",
                table: "CheckDefinitions",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CheckDefinitions_EndpointClient",
                table: "CheckDefinitions",
                sql: "\"EndpointId\" IS NULL OR \"ClientId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CheckDefinitions_Owner",
                table: "CheckDefinitions",
                sql: "num_nonnulls(\"MonitoringTemplateId\", \"EndpointId\") = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Held",
                table: "Alerts",
                column: "HeldUntil",
                filter: "\"HeldUntil\" IS NOT NULL AND \"State\" <> 'Resolved'");

            migrationBuilder.CreateIndex(
                name: "IX_CheckRunRequests_CheckDefinitionId",
                table: "CheckRunRequests",
                column: "CheckDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckRunRequests_EndpointId_ClientId",
                table: "CheckRunRequests",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckRunRequests_EndpointId_RequestedAt",
                table: "CheckRunRequests",
                columns: new[] { "EndpointId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckRunRequests_Pending",
                table: "CheckRunRequests",
                column: "ExpiresAt",
                filter: "\"DeliveredAt\" IS NULL AND \"Outcome\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CheckRunRequests_RequestedAt",
                table: "CheckRunRequests",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CheckRunRequests_RequestedByUserId_RequestedAt",
                table: "CheckRunRequests",
                columns: new[] { "RequestedByUserId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointCheckOverrides_CheckDefinitionId",
                table: "EndpointCheckOverrides",
                column: "CheckDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointCheckOverrides_EndpointId_ClientId",
                table: "EndpointCheckOverrides",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointMonitoringTemplates_EndpointId_ClientId",
                table: "EndpointMonitoringTemplates",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointMonitoringTemplates_MonitoringTemplateId",
                table: "EndpointMonitoringTemplates",
                column: "MonitoringTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_EndpointId_ClientId",
                table: "Notes",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_Notes_EndpointId_CreatedAt_Id",
                table: "Notes",
                columns: new[] { "EndpointId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.AddForeignKey(
                name: "FK_CheckDefinitions_Endpoints_EndpointId_ClientId",
                table: "CheckDefinitions",
                columns: new[] { "EndpointId", "ClientId" },
                principalTable: "Endpoints",
                principalColumns: new[] { "Id", "ClientId" },
                onDelete: ReferentialAction.Cascade);

            // Client-specific templates and policies of clients deleted before these foreign keys existed are unreachable.
            migrationBuilder.Sql("""
                DELETE FROM "MonitoringTemplates" t WHERE t."ClientId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Clients" c WHERE c."Id" = t."ClientId");
                DELETE FROM "Policies" p WHERE p."ClientId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Clients" c WHERE c."Id" = p."ClientId");
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_MonitoringTemplates_Clients_ClientId",
                table: "MonitoringTemplates",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Policies_Clients_ClientId",
                table: "Policies",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CheckDefinitions_Endpoints_EndpointId_ClientId",
                table: "CheckDefinitions");

            migrationBuilder.DropForeignKey(
                name: "FK_MonitoringTemplates_Clients_ClientId",
                table: "MonitoringTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_Policies_Clients_ClientId",
                table: "Policies");

            migrationBuilder.DropTable(
                name: "CheckRunRequests");

            migrationBuilder.DropTable(
                name: "EndpointCheckOverrides");

            migrationBuilder.DropTable(
                name: "EndpointMonitoringTemplates");

            migrationBuilder.DropTable(
                name: "Notes");

            migrationBuilder.DropIndex(
                name: "IX_CheckDefinitions_EndpointId_ClientId",
                table: "CheckDefinitions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CheckDefinitions_EndpointClient",
                table: "CheckDefinitions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CheckDefinitions_Owner",
                table: "CheckDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_Alerts_Held",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "PublicIpAddress",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "PublicIpSeenAt",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "ResetAt",
                table: "CheckStates");

            migrationBuilder.DropColumn(
                name: "EndpointId",
                table: "CheckDefinitions");

            migrationBuilder.DropColumn(
                name: "HeldAt",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "HeldByUserId",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "HeldUntil",
                table: "Alerts");

            migrationBuilder.AlterColumn<Guid>(
                name: "MonitoringTemplateId",
                table: "CheckDefinitions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
