using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Comments on alerts and the history of maintenance mode (0.6.0, decided 2026-10-16). Acknowledging, holding and resolving an
    /// alert by hand needs a comment, kept in <c>AlertActivities</c>. Every period of maintenance mode on a client, site or endpoint
    /// is kept in <c>MaintenanceRecords</c>, written by a trigger on the maintenance columns, so every writer (also the build before
    /// this one) leaves a record. Expand only: two new tables and a trigger; maintenance that runs now gets its record.
    /// </summary>
    public partial class AlertActivitiesAndMaintenanceRecords : Migration
    {
        /// <summary>
        /// Keeps <c>MaintenanceRecords</c> in step with the maintenance columns of clients, sites and endpoints: a new start closes the
        /// previous period (at its end time when that passed) and opens one; ending it by hand (the columns cleared) closes it now; a
        /// changed end time or reason updates the open period. The time is the row's <c>UpdatedAt</c>, the clock of the writer, or now
        /// when the writer left it as it was.
        /// </summary>
        internal const string RecordFunctionSql = """
            CREATE OR REPLACE FUNCTION fleeto_maintenance_record() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE
              client_id uuid;
              site_id uuid;
              endpoint_id uuid;
              at timestamptz := CASE WHEN NEW."UpdatedAt" IS DISTINCT FROM OLD."UpdatedAt" THEN NEW."UpdatedAt" ELSE now() END;
            BEGIN
              IF TG_TABLE_NAME = 'Clients' THEN
                client_id := NEW."Id";
              ELSIF TG_TABLE_NAME = 'Sites' THEN
                client_id := NEW."ClientId";
                site_id := NEW."Id";
              ELSE
                client_id := NEW."ClientId";
                endpoint_id := NEW."Id";
              END IF;

              IF NEW."MaintenanceStartedAt" IS NOT DISTINCT FROM OLD."MaintenanceStartedAt" THEN
                IF NEW."MaintenanceStartedAt" IS NOT NULL
                   AND (NEW."MaintenanceEndsAt", NEW."MaintenanceReason") IS DISTINCT FROM (OLD."MaintenanceEndsAt", OLD."MaintenanceReason") THEN
                  UPDATE "MaintenanceRecords" SET "EndsAt" = NEW."MaintenanceEndsAt", "Reason" = NEW."MaintenanceReason"
                  WHERE "ClientId" = client_id AND "SiteId" IS NOT DISTINCT FROM site_id AND "EndpointId" IS NOT DISTINCT FROM endpoint_id
                    AND "StartedAt" = NEW."MaintenanceStartedAt" AND "EndedAt" IS NULL;
                END IF;
                RETURN NEW;
              END IF;

              IF OLD."MaintenanceStartedAt" IS NOT NULL THEN
                UPDATE "MaintenanceRecords" SET "EndedAt" = LEAST(at, COALESCE(OLD."MaintenanceEndsAt", at))
                WHERE "ClientId" = client_id AND "SiteId" IS NOT DISTINCT FROM site_id AND "EndpointId" IS NOT DISTINCT FROM endpoint_id
                  AND "StartedAt" = OLD."MaintenanceStartedAt" AND "EndedAt" IS NULL;
              END IF;

              IF NEW."MaintenanceStartedAt" IS NOT NULL THEN
                INSERT INTO "MaintenanceRecords" ("ClientId", "SiteId", "EndpointId", "StartedAt", "EndsAt", "StartedByName", "Reason")
                VALUES (client_id, site_id, endpoint_id, NEW."MaintenanceStartedAt", NEW."MaintenanceEndsAt", NEW."MaintenanceStartedByName",
                        NEW."MaintenanceReason");
              END IF;
              RETURN NEW;
            END $$;
            """;

        internal const string TriggersSql = """
            CREATE TRIGGER "TR_Clients_MaintenanceRecord" AFTER UPDATE OF "MaintenanceStartedAt", "MaintenanceEndsAt", "MaintenanceReason"
              ON "Clients" FOR EACH ROW EXECUTE FUNCTION fleeto_maintenance_record();
            CREATE TRIGGER "TR_Sites_MaintenanceRecord" AFTER UPDATE OF "MaintenanceStartedAt", "MaintenanceEndsAt", "MaintenanceReason"
              ON "Sites" FOR EACH ROW EXECUTE FUNCTION fleeto_maintenance_record();
            CREATE TRIGGER "TR_Endpoints_MaintenanceRecord" AFTER UPDATE OF "MaintenanceStartedAt", "MaintenanceEndsAt", "MaintenanceReason"
              ON "Endpoints" FOR EACH ROW EXECUTE FUNCTION fleeto_maintenance_record();
            """;

        /// <summary>Maintenance that is set now gets its record; what ended before this release left none.</summary>
        internal const string BackfillSql = """
            INSERT INTO "MaintenanceRecords" ("ClientId", "SiteId", "EndpointId", "StartedAt", "EndsAt", "StartedByName", "Reason")
            SELECT "Id", NULL::uuid, NULL::uuid, "MaintenanceStartedAt", "MaintenanceEndsAt", "MaintenanceStartedByName", "MaintenanceReason"
              FROM "Clients" WHERE "MaintenanceStartedAt" IS NOT NULL
            UNION ALL
            SELECT "ClientId", "Id", NULL::uuid, "MaintenanceStartedAt", "MaintenanceEndsAt", "MaintenanceStartedByName", "MaintenanceReason"
              FROM "Sites" WHERE "MaintenanceStartedAt" IS NOT NULL
            UNION ALL
            SELECT "ClientId", NULL::uuid, "Id", "MaintenanceStartedAt", "MaintenanceEndsAt", "MaintenanceStartedByName", "MaintenanceReason"
              FROM "Endpoints" WHERE "MaintenanceStartedAt" IS NOT NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertActivities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HeldUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertActivities_Alerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "Alerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlertActivities_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceRecords_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaintenanceRecords_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaintenanceRecords_Sites_SiteId_ClientId",
                        columns: x => new { x.SiteId, x.ClientId },
                        principalTable: "Sites",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertActivities_AlertId_Id",
                table: "AlertActivities",
                columns: new[] { "AlertId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertActivities_EndpointId_ClientId",
                table: "AlertActivities",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRecords_ClientId_StartedAt",
                table: "MaintenanceRecords",
                columns: new[] { "ClientId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRecords_EndpointId_ClientId",
                table: "MaintenanceRecords",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRecords_EndpointId_StartedAt",
                table: "MaintenanceRecords",
                columns: new[] { "EndpointId", "StartedAt" },
                filter: "\"EndpointId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRecords_SiteId_ClientId",
                table: "MaintenanceRecords",
                columns: new[] { "SiteId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRecords_SiteId_StartedAt",
                table: "MaintenanceRecords",
                columns: new[] { "SiteId", "StartedAt" },
                filter: "\"SiteId\" IS NOT NULL");

            migrationBuilder.Sql(RecordFunctionSql);
            migrationBuilder.Sql(TriggersSql);
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_Clients_MaintenanceRecord" ON "Clients";
                DROP TRIGGER IF EXISTS "TR_Sites_MaintenanceRecord" ON "Sites";
                DROP TRIGGER IF EXISTS "TR_Endpoints_MaintenanceRecord" ON "Endpoints";
                DROP FUNCTION IF EXISTS fleeto_maintenance_record();
                """);

            migrationBuilder.DropTable(
                name: "AlertActivities");

            migrationBuilder.DropTable(
                name: "MaintenanceRecords");
        }
    }
}
