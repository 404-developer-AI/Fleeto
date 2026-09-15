using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentUpdatesAndWatchdog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UpdateRing",
                table: "Policies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<DateTime>(
                name: "WatchdogLastSeenAt",
                table: "Endpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WatchdogOnline",
                table: "Endpoints",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WatchdogVersion",
                table: "Endpoints",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "AgentCertificates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Agent");

            migrationBuilder.CreateTable(
                name: "AgentReleases",
                columns: table => new
                {
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ManifestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InstalledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    PausedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PausedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PausedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReleasedToAllAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleasedToAllByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleasedToAllByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentReleases", x => x.Version);
                });

            migrationBuilder.CreateTable(
                name: "EndpointComponentStates",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstalledVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ServiceState = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ServiceDetail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ServiceStateAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdateVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdateState = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UpdateDetail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UpdateAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointComponentStates", x => new { x.EndpointId, x.Component });
                    table.ForeignKey(
                        name: "FK_EndpointComponentStates_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReleases_Current",
                table: "AgentReleases",
                column: "IsCurrent",
                unique: true,
                filter: "\"IsCurrent\"");

            // Watchdog certificates (0.2.1) may only be requested by the gateway, for a live agent session.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION fleeto_signing_request_origin() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Kind" IN ('AgentEnrollment', 'AgentRenewal', 'GatewayCertificate', 'AgentRecovery', 'WatchdogCertificate') THEN
                    IF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer') THEN
                      RAISE EXCEPTION '% requests may only be created by the gateway', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF NEW."Kind" = 'AgentConfig' THEN
                    IF current_user IN ('fleeto_web', 'fleeto_gateway') THEN
                      RAISE EXCEPTION 'AgentConfig requests may only be created by the workers or the signer' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF NEW."Kind" = 'Job' THEN
                    IF current_user IN ('fleeto_gateway', 'fleeto_workers', 'fleeto_signer') THEN
                      RAISE EXCEPTION 'Job requests may only be created by web' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer', 'fleeto_gateway') THEN
                    RAISE EXCEPTION 'Unknown signing request kind %', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  RETURN NEW;
                END $$;
                """);


            migrationBuilder.CreateIndex(
                name: "IX_EndpointComponentStates_EndpointId_ClientId",
                table: "EndpointComponentStates",
                columns: new[] { "EndpointId", "ClientId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentReleases");

            migrationBuilder.DropTable(
                name: "EndpointComponentStates");

            migrationBuilder.DropColumn(
                name: "UpdateRing",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "WatchdogLastSeenAt",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "WatchdogOnline",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "WatchdogVersion",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "AgentCertificates");
        }
    }
}
