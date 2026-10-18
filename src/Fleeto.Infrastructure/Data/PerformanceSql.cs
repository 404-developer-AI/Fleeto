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

    /// <summary>
    /// Diagnose (0.7.0 step 2, migration PerformanceDiagnose): fingerprints are written and updated by each component for itself,
    /// and web may run ANALYZE on one existing table on an admin's click (spec §2.8) through a function that can do nothing else:
    /// it runs as its owner, the migrator that owns the tables, takes a table name rather than SQL, and accepts only an ordinary
    /// or partitioned table of the public schema. A generic plan needs its <c>$1</c> left unbound, which the extended protocol of
    /// Npgsql cannot do, so web asks for it through a second function that runs with web's own rights (SECURITY INVOKER) and
    /// does nothing but EXPLAIN without ANALYZE, which never runs the statement. EXECUTE on both is revoked from PUBLIC here and
    /// granted to web by <see cref="DatabaseGrants.FunctionGrants"/>.
    /// </summary>
    public const string DiagnoseUp = """
        CREATE TRIGGER "TR_PerfFingerprints_Component" BEFORE INSERT OR UPDATE ON "PerfFingerprints"
          FOR EACH ROW EXECUTE FUNCTION fleeto_perf_component();

        CREATE OR REPLACE FUNCTION fleeto_perf_analyze(table_name text) RETURNS void
          LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp AS $$
        DECLARE
          target regclass;
        BEGIN
          SELECT c.oid::regclass INTO target
          FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
          WHERE n.nspname = 'public' AND c.relname = table_name AND c.relkind IN ('r', 'p');
          IF target IS NULL THEN
            RAISE EXCEPTION 'No table of Fleeto is called %', table_name USING ERRCODE = 'undefined_table';
          END IF;
          EXECUTE format('ANALYZE %s', target);
        END $$;

        REVOKE ALL ON FUNCTION fleeto_perf_analyze(text) FROM PUBLIC;

        CREATE OR REPLACE FUNCTION fleeto_perf_explain(statement text) RETURNS text
          LANGUAGE plpgsql SECURITY INVOKER SET search_path = public, pg_temp AS $$
        DECLARE
          plan text;
        BEGIN
          IF position(';' IN statement) > 0 THEN
            RAISE EXCEPTION 'Only a single statement can be planned' USING ERRCODE = 'syntax_error';
          END IF;
          EXECUTE 'EXPLAIN (GENERIC_PLAN, FORMAT JSON) ' || statement INTO plan;
          RETURN plan;
        END $$;

        REVOKE ALL ON FUNCTION fleeto_perf_explain(text) FROM PUBLIC;
        """;

    public const string DiagnoseDown = """
        DROP FUNCTION IF EXISTS fleeto_perf_explain(text);
        DROP FUNCTION IF EXISTS fleeto_perf_analyze(text);
        DROP TRIGGER IF EXISTS "TR_PerfFingerprints_Component" ON "PerfFingerprints";
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS "TR_PerfEvents_Component" ON "PerfEvents";
        DROP TRIGGER IF EXISTS "TR_PerfRuntimeMinutes_Component" ON "PerfRuntimeMinutes";
        DROP TRIGGER IF EXISTS "TR_PerfMinutes_Component" ON "PerfMinutes";
        DROP FUNCTION IF EXISTS fleeto_perf_component();
        """;
}
