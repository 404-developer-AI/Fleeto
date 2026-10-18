namespace Fleeto.Infrastructure.Data;

/// <summary>
/// Rules of the performance monitor that live in the database (0.7.0, migration PerformanceMonitor): every component writes only
/// its own rows, and the minute table is a TimescaleDB hypertable with a retention policy where the extension is installed
/// (local development runs without it; the workers delete per the retention setting either way).
/// </summary>
public static class PerformanceSql
{
    public const string Up = """
        -- A component writes only rows of its own name: the role fleeto_web writes "web", and so on. Other roles (the migrator,
        -- a superuser in tests) are not limited.
        CREATE OR REPLACE FUNCTION fleeto_perf_component() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF current_user IN ('fleeto_web', 'fleeto_gateway', 'fleeto_signer', 'fleeto_workers')
             AND NEW."Component" IS DISTINCT FROM substring(current_user FROM 8) THEN
            RAISE EXCEPTION 'A component writes only its own performance rows' USING ERRCODE = 'insufficient_privilege';
          END IF;
          RETURN NEW;
        END $$;

        DO $$
        BEGIN
          IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') THEN
            PERFORM create_hypertable('"PerfMinutes"', by_range('Minute', INTERVAL '1 day'), migrate_data => true);
            -- A ceiling: the workers delete minutes older than the retention setting (at most 30 days) before this drops them.
            PERFORM add_retention_policy('"PerfMinutes"', INTERVAL '31 days');
          END IF;
        END $$;

        CREATE TRIGGER "TR_PerfMinutes_Component" BEFORE INSERT ON "PerfMinutes" FOR EACH ROW EXECUTE FUNCTION fleeto_perf_component();
        CREATE TRIGGER "TR_PerfRuntimeMinutes_Component" BEFORE INSERT ON "PerfRuntimeMinutes" FOR EACH ROW EXECUTE FUNCTION fleeto_perf_component();
        CREATE TRIGGER "TR_PerfEvents_Component" BEFORE INSERT ON "PerfEvents" FOR EACH ROW EXECUTE FUNCTION fleeto_perf_component();
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS "TR_PerfEvents_Component" ON "PerfEvents";
        DROP TRIGGER IF EXISTS "TR_PerfRuntimeMinutes_Component" ON "PerfRuntimeMinutes";
        DROP TRIGGER IF EXISTS "TR_PerfMinutes_Component" ON "PerfMinutes";
        DROP FUNCTION IF EXISTS fleeto_perf_component();
        """;
}
