using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetify.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AlertAndCursorIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Alerts_EndpointId_Kind_CheckDefinitionId_Target",
                table: "Alerts");

            migrationBuilder.CreateIndex(
                name: "IX_CheckResults_EndpointId_Id",
                table: "CheckResults",
                columns: new[] { "EndpointId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_EndpointId_Kind_CheckDefinitionId_Target",
                table: "Alerts",
                columns: new[] { "EndpointId", "Kind", "CheckDefinitionId", "Target" },
                unique: true,
                filter: "\"State\" <> 'Resolved' AND NOT (\"Kind\" = 'Check' AND \"CheckDefinitionId\" IS NULL)")
                .Annotation("Npgsql:NullsDistinct", false);

            // When a check definition is deleted, the foreign key sets CheckDefinitionId to null on its alerts; resolve
            // the open ones in the same statement so they never linger as orphaned open alerts.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION fleetify_resolve_orphaned_check_alert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Kind" = 'Check' AND NEW."CheckDefinitionId" IS NULL AND OLD."CheckDefinitionId" IS NOT NULL
                     AND NEW."State" <> 'Resolved' THEN
                    NEW."State" := 'Resolved';
                    NEW."ResolvedAt" := now();
                    NEW."ResolvedReason" := 'The check was removed from its monitoring template';
                    NEW."UpdatedAt" := now();
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER "TR_Alerts_ResolveOrphanedCheck" BEFORE UPDATE OF "CheckDefinitionId" ON "Alerts"
                  FOR EACH ROW EXECUTE FUNCTION fleetify_resolve_orphaned_check_alert();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_Alerts_ResolveOrphanedCheck" ON "Alerts";
                DROP FUNCTION IF EXISTS fleetify_resolve_orphaned_check_alert();
                """);

            migrationBuilder.DropIndex(
                name: "IX_CheckResults_EndpointId_Id",
                table: "CheckResults");

            migrationBuilder.DropIndex(
                name: "IX_Alerts_EndpointId_Kind_CheckDefinitionId_Target",
                table: "Alerts");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_EndpointId_Kind_CheckDefinitionId_Target",
                table: "Alerts",
                columns: new[] { "EndpointId", "Kind", "CheckDefinitionId", "Target" },
                unique: true,
                filter: "\"State\" <> 'Resolved'")
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
