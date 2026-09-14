using Fleetify.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetify.Infrastructure.Migrations
{
    /// <summary>
    /// Database rules for per-endpoint check management and check run requests (migration EndpointChecksAlertsNotes):
    /// client consistency of template checks, endpoint template links, overrides and run requests, and the notification
    /// trigger of run requests. Endpoint-only checks are kept consistent by their composite foreign key.
    /// </summary>
    [DbContext(typeof(FleetifyDbContext))]
    [Migration("20260914204800_EndpointChecksRules")]
    public partial class EndpointChecksRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- A template check carries exactly the ClientId of its template, null included. An endpoint-only check is
                -- covered by the composite foreign key to its endpoint.
                CREATE OR REPLACE FUNCTION fleetify_check_definition_client() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE parent_client uuid;
                BEGIN
                  IF NEW."MonitoringTemplateId" IS NOT NULL THEN
                    SELECT "ClientId" INTO parent_client FROM "MonitoringTemplates" WHERE "Id" = NEW."MonitoringTemplateId";
                    IF NEW."ClientId" IS DISTINCT FROM parent_client THEN
                      RAISE EXCEPTION 'CheckDefinitions.ClientId must equal the ClientId of its monitoring template' USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                  END IF;
                  RETURN NEW;
                END $$;

                -- An endpoint can link a global template or one of its own client; never another client's.
                CREATE OR REPLACE FUNCTION fleetify_endpoint_template_client() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE template_client uuid;
                BEGIN
                  SELECT "ClientId" INTO template_client FROM "MonitoringTemplates" WHERE "Id" = NEW."MonitoringTemplateId";
                  IF template_client IS NOT NULL AND template_client <> NEW."ClientId" THEN
                    RAISE EXCEPTION 'An endpoint cannot link a monitoring template of another client' USING ERRCODE = 'integrity_constraint_violation';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE CONSTRAINT TRIGGER "TR_EndpointMonitoringTemplates_Client" AFTER INSERT OR UPDATE OF "ClientId", "MonitoringTemplateId"
                  ON "EndpointMonitoringTemplates" FOR EACH ROW EXECUTE FUNCTION fleetify_endpoint_template_client();

                -- Overrides and run requests may only name a check of the same client (or a global one). Overrides only adjust
                -- template checks; a run request for an endpoint-only check must be for that endpoint.
                CREATE OR REPLACE FUNCTION fleetify_endpoint_check_client() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE definition_client uuid; definition_endpoint uuid; found_definition boolean;
                BEGIN
                  SELECT true, "ClientId", "EndpointId" INTO found_definition, definition_client, definition_endpoint
                  FROM "CheckDefinitions" WHERE "Id" = NEW."CheckDefinitionId";
                  IF found_definition IS NULL THEN
                    RETURN NEW; -- the foreign key reports the missing check
                  END IF;
                  IF definition_client IS NOT NULL AND definition_client <> NEW."ClientId" THEN
                    RAISE EXCEPTION '% cannot reference a check of another client', TG_TABLE_NAME USING ERRCODE = 'integrity_constraint_violation';
                  END IF;
                  IF TG_TABLE_NAME = 'EndpointCheckOverrides' AND definition_endpoint IS NOT NULL THEN
                    RAISE EXCEPTION 'Overrides only apply to checks of a monitoring template' USING ERRCODE = 'integrity_constraint_violation';
                  END IF;
                  IF definition_endpoint IS NOT NULL AND definition_endpoint <> NEW."EndpointId" THEN
                    RAISE EXCEPTION '% cannot reference a check of another endpoint', TG_TABLE_NAME USING ERRCODE = 'integrity_constraint_violation';
                  END IF;
                  RETURN NEW;
                END $$;
                -- Only when a referencing column changes: the gateway marks requests delivered and the workers mark resets
                -- applied, and those roles need no access to the tables the check reads.
                CREATE CONSTRAINT TRIGGER "TR_EndpointCheckOverrides_Client" AFTER INSERT OR UPDATE OF "ClientId", "EndpointId", "CheckDefinitionId"
                  ON "EndpointCheckOverrides" FOR EACH ROW EXECUTE FUNCTION fleetify_endpoint_check_client();
                CREATE CONSTRAINT TRIGGER "TR_CheckRunRequests_Client" AFTER INSERT OR UPDATE OF "ClientId", "EndpointId", "CheckDefinitionId"
                  ON "CheckRunRequests" FOR EACH ROW EXECUTE FUNCTION fleetify_endpoint_check_client();

                -- A new request wakes the workers (reset) and the gateway (plain run); an applied reset wakes the gateway.
                CREATE TRIGGER "TR_CheckRunRequests_Notify" AFTER INSERT ON "CheckRunRequests"
                  FOR EACH ROW EXECUTE FUNCTION fleetify_notify_id('fleetify_check_run_requests');
                CREATE TRIGGER "TR_CheckRunRequests_NotifyReset" AFTER UPDATE OF "ResetAppliedAt" ON "CheckRunRequests"
                  FOR EACH ROW WHEN (OLD."ResetAppliedAt" IS NULL AND NEW."ResetAppliedAt" IS NOT NULL)
                  EXECUTE FUNCTION fleetify_notify_id('fleetify_check_run_requests');

                -- Checks can now also be deleted from an endpoint, so the reason no longer names the template.
                CREATE OR REPLACE FUNCTION fleetify_resolve_orphaned_check_alert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Kind" = 'Check' AND NEW."CheckDefinitionId" IS NULL AND OLD."CheckDefinitionId" IS NOT NULL
                     AND NEW."State" <> 'Resolved' THEN
                    NEW."State" := 'Resolved';
                    NEW."ResolvedAt" := now();
                    NEW."ResolvedReason" := 'The check was deleted';
                    NEW."UpdatedAt" := now();
                  END IF;
                  RETURN NEW;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_CheckRunRequests_NotifyReset" ON "CheckRunRequests";
                DROP TRIGGER IF EXISTS "TR_CheckRunRequests_Notify" ON "CheckRunRequests";
                DROP TRIGGER IF EXISTS "TR_CheckRunRequests_Client" ON "CheckRunRequests";
                DROP TRIGGER IF EXISTS "TR_EndpointCheckOverrides_Client" ON "EndpointCheckOverrides";
                DROP FUNCTION IF EXISTS fleetify_endpoint_check_client();
                DROP TRIGGER IF EXISTS "TR_EndpointMonitoringTemplates_Client" ON "EndpointMonitoringTemplates";
                DROP FUNCTION IF EXISTS fleetify_endpoint_template_client();
                """);
        }
    }
}
