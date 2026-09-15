namespace Fleeto.Infrastructure.Data;

/// <summary>
/// Rules that live in the database rather than in code, applied by migration DatabaseRules:
/// append-only audit, ClientId consistency for nullable ClientIds, immutable ClientIds, notification triggers and
/// the TimescaleDB hypertable (only when the extension is installed; local development runs without it).
/// </summary>
public static class DatabaseRulesSql
{
    public const string Up = """
        -- Append-only audit trail: no update, delete or truncate, whoever the caller is.
        CREATE OR REPLACE FUNCTION fleeto_audit_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          RAISE EXCEPTION 'AuditEntries is append-only' USING ERRCODE = 'insufficient_privilege';
        END $$;
        CREATE TRIGGER "TR_AuditEntries_AppendOnly" BEFORE UPDATE OR DELETE ON "AuditEntries"
          FOR EACH ROW EXECUTE FUNCTION fleeto_audit_append_only();
        CREATE TRIGGER "TR_AuditEntries_NoTruncate" BEFORE TRUNCATE ON "AuditEntries"
          FOR EACH STATEMENT EXECUTE FUNCTION fleeto_audit_append_only();

        -- ClientId is immutable on every table that carries it as the tenant boundary.
        CREATE OR REPLACE FUNCTION fleeto_client_id_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF NEW."ClientId" IS DISTINCT FROM OLD."ClientId" THEN
            RAISE EXCEPTION 'ClientId of % cannot change', TG_TABLE_NAME USING ERRCODE = 'integrity_constraint_violation';
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER "TR_Sites_ClientIdImmutable" BEFORE UPDATE OF "ClientId" ON "Sites" FOR EACH ROW EXECUTE FUNCTION fleeto_client_id_immutable();
        CREATE TRIGGER "TR_Endpoints_ClientIdImmutable" BEFORE UPDATE OF "ClientId" ON "Endpoints" FOR EACH ROW EXECUTE FUNCTION fleeto_client_id_immutable();
        CREATE TRIGGER "TR_Policies_ClientIdImmutable" BEFORE UPDATE OF "ClientId" ON "Policies" FOR EACH ROW EXECUTE FUNCTION fleeto_client_id_immutable();
        CREATE TRIGGER "TR_MonitoringTemplates_ClientIdImmutable" BEFORE UPDATE OF "ClientId" ON "MonitoringTemplates" FOR EACH ROW EXECUTE FUNCTION fleeto_client_id_immutable();

        -- Nullable ClientId: a check definition carries exactly the ClientId of its template, null included.
        -- A composite foreign key would not check rows with a null column, hence a trigger.
        CREATE OR REPLACE FUNCTION fleeto_check_definition_client() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE parent_client uuid;
        BEGIN
          SELECT "ClientId" INTO parent_client FROM "MonitoringTemplates" WHERE "Id" = NEW."MonitoringTemplateId";
          IF NEW."ClientId" IS DISTINCT FROM parent_client THEN
            RAISE EXCEPTION 'CheckDefinitions.ClientId must equal the ClientId of its monitoring template' USING ERRCODE = 'integrity_constraint_violation';
          END IF;
          RETURN NEW;
        END $$;
        CREATE CONSTRAINT TRIGGER "TR_CheckDefinitions_Client" AFTER INSERT OR UPDATE ON "CheckDefinitions"
          FOR EACH ROW EXECUTE FUNCTION fleeto_check_definition_client();

        -- A site can link a global template or policy, or one of its own client; never another client's.
        CREATE OR REPLACE FUNCTION fleeto_site_template_client() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE template_client uuid;
        BEGIN
          SELECT "ClientId" INTO template_client FROM "MonitoringTemplates" WHERE "Id" = NEW."MonitoringTemplateId";
          IF template_client IS NOT NULL AND template_client <> NEW."ClientId" THEN
            RAISE EXCEPTION 'A site cannot link a monitoring template of another client' USING ERRCODE = 'integrity_constraint_violation';
          END IF;
          RETURN NEW;
        END $$;
        CREATE CONSTRAINT TRIGGER "TR_SiteMonitoringTemplates_Client" AFTER INSERT OR UPDATE ON "SiteMonitoringTemplates"
          FOR EACH ROW EXECUTE FUNCTION fleeto_site_template_client();

        CREATE OR REPLACE FUNCTION fleeto_site_policy_client() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE policy_client uuid;
        BEGIN
          SELECT "ClientId" INTO policy_client FROM "Policies" WHERE "Id" = NEW."PolicyId";
          IF policy_client IS NOT NULL AND policy_client <> NEW."ClientId" THEN
            RAISE EXCEPTION 'A site cannot link a policy of another client' USING ERRCODE = 'integrity_constraint_violation';
          END IF;
          RETURN NEW;
        END $$;
        CREATE CONSTRAINT TRIGGER "TR_SitePolicies_Client" AFTER INSERT OR UPDATE ON "SitePolicies"
          FOR EACH ROW EXECUTE FUNCTION fleeto_site_policy_client();

        -- Client templates apply to any client, so they may only reference global templates and policies.
        CREATE OR REPLACE FUNCTION fleeto_client_template_global() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF TG_TABLE_NAME = 'ClientTemplateSites' THEN
            IF NEW."PolicyId" IS NOT NULL AND EXISTS (SELECT 1 FROM "Policies" WHERE "Id" = NEW."PolicyId" AND "ClientId" IS NOT NULL) THEN
              RAISE EXCEPTION 'A client template can only use global policies' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
          ELSE
            IF EXISTS (SELECT 1 FROM "MonitoringTemplates" WHERE "Id" = NEW."MonitoringTemplateId" AND "ClientId" IS NOT NULL) THEN
              RAISE EXCEPTION 'A client template can only use global monitoring templates' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
          END IF;
          RETURN NEW;
        END $$;
        CREATE CONSTRAINT TRIGGER "TR_ClientTemplateSites_Global" AFTER INSERT OR UPDATE ON "ClientTemplateSites"
          FOR EACH ROW EXECUTE FUNCTION fleeto_client_template_global();
        CREATE CONSTRAINT TRIGGER "TR_ClientTemplateSiteMonitoringTemplates_Global" AFTER INSERT OR UPDATE ON "ClientTemplateSiteMonitoringTemplates"
          FOR EACH ROW EXECUTE FUNCTION fleeto_client_template_global();

        -- Notifications raised by the database itself, so no writer can forget them. Payload: the row id.
        CREATE OR REPLACE FUNCTION fleeto_notify_id() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          PERFORM pg_notify(TG_ARGV[0], NEW."Id"::text);
          RETURN NULL;
        END $$;
        CREATE TRIGGER "TR_SigningRequests_Notify" AFTER INSERT ON "SigningRequests"
          FOR EACH ROW EXECUTE FUNCTION fleeto_notify_id('fleeto_signing_requests');
        CREATE TRIGGER "TR_SigningRequests_NotifyResult" AFTER UPDATE OF "State" ON "SigningRequests"
          FOR EACH ROW WHEN (NEW."State" <> 'Pending') EXECUTE FUNCTION fleeto_notify_id('fleeto_signing_results');
        CREATE TRIGGER "TR_ConfigChangeEvents_Notify" AFTER INSERT ON "ConfigChangeEvents"
          FOR EACH ROW EXECUTE FUNCTION fleeto_notify_id('fleeto_config_changes');
        CREATE TRIGGER "TR_EndpointEvents_Notify" AFTER INSERT ON "EndpointEvents"
          FOR EACH ROW EXECUTE FUNCTION fleeto_notify_id('fleeto_endpoint_events');
        CREATE TRIGGER "TR_OutboxEmails_Notify" AFTER INSERT ON "OutboxEmails"
          FOR EACH ROW EXECUTE FUNCTION fleeto_notify_id('fleeto_outbox_emails');

        -- TimescaleDB: hypertable, compression and retention for check results, when the extension is installed
        -- (by install.sh as superuser). Without it the workers enforce retention with plain deletes.
        DO $$
        BEGIN
          IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') THEN
            PERFORM create_hypertable('"CheckResults"', by_range('Time', INTERVAL '1 day'), migrate_data => true);
            ALTER TABLE "CheckResults" SET (timescaledb.compress, timescaledb.compress_segmentby = '"EndpointId"', timescaledb.compress_orderby = '"Time" DESC');
            PERFORM add_compression_policy('"CheckResults"', INTERVAL '3 days');
            PERFORM add_retention_policy('"CheckResults"', INTERVAL '30 days');
          END IF;
        END $$;
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS "TR_OutboxEmails_Notify" ON "OutboxEmails";
        DROP TRIGGER IF EXISTS "TR_EndpointEvents_Notify" ON "EndpointEvents";
        DROP TRIGGER IF EXISTS "TR_ConfigChangeEvents_Notify" ON "ConfigChangeEvents";
        DROP TRIGGER IF EXISTS "TR_SigningRequests_NotifyResult" ON "SigningRequests";
        DROP TRIGGER IF EXISTS "TR_SigningRequests_Notify" ON "SigningRequests";
        DROP FUNCTION IF EXISTS fleeto_notify_id();
        DROP TRIGGER IF EXISTS "TR_ClientTemplateSiteMonitoringTemplates_Global" ON "ClientTemplateSiteMonitoringTemplates";
        DROP TRIGGER IF EXISTS "TR_ClientTemplateSites_Global" ON "ClientTemplateSites";
        DROP FUNCTION IF EXISTS fleeto_client_template_global();
        DROP TRIGGER IF EXISTS "TR_SitePolicies_Client" ON "SitePolicies";
        DROP FUNCTION IF EXISTS fleeto_site_policy_client();
        DROP TRIGGER IF EXISTS "TR_SiteMonitoringTemplates_Client" ON "SiteMonitoringTemplates";
        DROP FUNCTION IF EXISTS fleeto_site_template_client();
        DROP TRIGGER IF EXISTS "TR_CheckDefinitions_Client" ON "CheckDefinitions";
        DROP FUNCTION IF EXISTS fleeto_check_definition_client();
        DROP TRIGGER IF EXISTS "TR_MonitoringTemplates_ClientIdImmutable" ON "MonitoringTemplates";
        DROP TRIGGER IF EXISTS "TR_Policies_ClientIdImmutable" ON "Policies";
        DROP TRIGGER IF EXISTS "TR_Endpoints_ClientIdImmutable" ON "Endpoints";
        DROP TRIGGER IF EXISTS "TR_Sites_ClientIdImmutable" ON "Sites";
        DROP FUNCTION IF EXISTS fleeto_client_id_immutable();
        DROP TRIGGER IF EXISTS "TR_AuditEntries_NoTruncate" ON "AuditEntries";
        DROP TRIGGER IF EXISTS "TR_AuditEntries_AppendOnly" ON "AuditEntries";
        DROP FUNCTION IF EXISTS fleeto_audit_append_only();
        """;
}
