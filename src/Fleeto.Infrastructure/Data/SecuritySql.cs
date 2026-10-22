namespace Fleeto.Infrastructure.Data;

/// <summary>
/// Rules of the security monitor that live in the database (0.7.0, migration SecurityMonitor): web and the gateway write only
/// their own signals, web writes only manual blocks (automatic ones are the workers'), every change of the blocked or allowed
/// addresses wakes web and the gateway, and the signal table is a TimescaleDB hypertable with a retention policy where the
/// extension is installed (the workers delete per retention either way).
/// </summary>
public static class SecuritySql
{
    public const string Up = """
        DO $$
        BEGIN
          IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') THEN
            PERFORM create_hypertable('"SecuritySignals"', by_range('Minute', INTERVAL '1 day'), migrate_data => true);
            -- A ceiling: the workers delete signals older than 90 days before this drops them.
            PERFORM add_retention_policy('"SecuritySignals"', INTERVAL '91 days');
          END IF;
        END $$;

        -- The function of the performance monitor checks the "Component" column of any table against the role.
        CREATE TRIGGER "TR_SecuritySignals_Component" BEFORE INSERT ON "SecuritySignals" FOR EACH ROW EXECUTE FUNCTION fleeto_perf_component();

        CREATE OR REPLACE FUNCTION fleeto_ip_block_source() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF current_user = 'fleeto_web' AND NEW."Source" <> 'Manual' THEN
            RAISE EXCEPTION 'Web writes only manual blocks' USING ERRCODE = 'insufficient_privilege';
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER "TR_IpBlocks_Source" BEFORE INSERT OR UPDATE ON "IpBlocks" FOR EACH ROW EXECUTE FUNCTION fleeto_ip_block_source();

        CREATE OR REPLACE FUNCTION fleeto_notify_ip_blocks() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          PERFORM pg_notify('fleeto_ip_blocks', '');
          RETURN NULL;
        END $$;
        CREATE TRIGGER "TR_IpBlocks_Notify" AFTER INSERT OR UPDATE OR DELETE ON "IpBlocks"
          FOR EACH STATEMENT EXECUTE FUNCTION fleeto_notify_ip_blocks();
        CREATE TRIGGER "TR_IpAllows_Notify" AFTER INSERT OR UPDATE OR DELETE ON "IpAllows"
          FOR EACH STATEMENT EXECUTE FUNCTION fleeto_notify_ip_blocks();
        """;

    /// <summary>
    /// Blocklists (step 3, migration SecurityFeeds): the catalog rows with their defaults, a notification on
    /// <c>fleeto_ip_blocks</c> when a list is switched or replaced so web and the gateway load it again, and one on
    /// <c>fleeto_ip_feeds</c> when an admin asks for a refresh so the workers fetch it at once.
    /// </summary>
    public const string FeedsUp = """
        INSERT INTO "IpFeeds" ("Key", "Enabled", "EntryCount", "Version") VALUES
          ('spamhaus-drop', true, 0, 0), ('feodo-tracker', true, 0, 0), ('firehol-level1', true, 0, 0),
          ('blocklist-de', false, 0, 0), ('crowdsec', false, 0, 0)
        ON CONFLICT ("Key") DO NOTHING;

        CREATE OR REPLACE FUNCTION fleeto_notify_ip_feeds() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF NEW."Enabled" IS DISTINCT FROM OLD."Enabled" OR NEW."Version" IS DISTINCT FROM OLD."Version" THEN
            PERFORM pg_notify('fleeto_ip_blocks', '');
          END IF;
          IF NEW."RefreshRequestedAt" IS NOT NULL AND NEW."RefreshRequestedAt" IS DISTINCT FROM OLD."RefreshRequestedAt" THEN
            PERFORM pg_notify('fleeto_ip_feeds', NEW."Key");
          END IF;
          RETURN NULL;
        END $$;
        CREATE TRIGGER "TR_IpFeeds_Notify" AFTER UPDATE ON "IpFeeds" FOR EACH ROW EXECUTE FUNCTION fleeto_notify_ip_feeds();
        """;

    public const string FeedsDown = """
        DROP TRIGGER IF EXISTS "TR_IpFeeds_Notify" ON "IpFeeds";
        DROP FUNCTION IF EXISTS fleeto_notify_ip_feeds();
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS "TR_IpAllows_Notify" ON "IpAllows";
        DROP TRIGGER IF EXISTS "TR_IpBlocks_Notify" ON "IpBlocks";
        DROP FUNCTION IF EXISTS fleeto_notify_ip_blocks();
        DROP TRIGGER IF EXISTS "TR_IpBlocks_Source" ON "IpBlocks";
        DROP FUNCTION IF EXISTS fleeto_ip_block_source();
        DROP TRIGGER IF EXISTS "TR_SecuritySignals_Component" ON "SecuritySignals";
        """;
}
