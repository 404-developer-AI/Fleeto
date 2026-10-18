using Fleeto.Core.Entities;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Performance;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleeto.Workers.Performance;

/// <summary>
/// The database as the performance monitor sees it from the server side (0.7.0, PERFORMANCE.md §4.3): statement statistics and a
/// snapshot of the database every 15 minutes, the tables every hour (both settings), and more often while Diagnose runs (5 and 15
/// minutes by default). Counters are stored as PostgreSQL reports them, cumulative, and the page computes the difference between
/// the snapshots around a period. The snapshot also counts the sessions: web may not see other roles' sessions, the workers with
/// pg_monitor may. Catalog and statistics views only: nothing is reset, vacuumed or changed. Also read once at start, so a period
/// has a baseline as soon as possible.
/// </summary>
public sealed class PerfSnapshotService : WorkerLoop
{
    private const string DatabaseSql = """
        SELECT d.blks_hit AS "BlocksHit", d.blks_read AS "BlocksRead", d.temp_files AS "TempFiles", d.temp_bytes AS "TempBytes",
               d.deadlocks AS "Deadlocks", d.xact_commit AS "Commits", d.xact_rollback AS "Rollbacks", d.stats_reset AS "StatisticsResetAt",
               (SELECT count(*) FROM pg_stat_activity a WHERE a.backend_type = 'client backend' AND a.datname = current_database())::int AS "Connections",
               current_setting('max_connections')::int AS "MaxConnections",
               age(db.datfrozenxid)::bigint AS "WraparoundAge",
               pg_database_size(current_database()) AS "DatabaseBytes"
        FROM pg_stat_database d JOIN pg_database db ON db.oid = d.datid
        WHERE d.datname = current_database()
        """;

    private const string SessionsSql = """
        SELECT count(*) FILTER (WHERE a.state = 'active')::int AS "Active",
               count(*) FILTER (WHERE a.state = 'idle')::int AS "Idle",
               count(*) FILTER (WHERE a.state LIKE 'idle in transaction%')::int AS "IdleInTransaction",
               EXTRACT(EPOCH FROM max(now() - a.xact_start))::float8 AS "LongestTransaction",
               EXTRACT(EPOCH FROM max(now() - a.state_change) FILTER (WHERE a.state LIKE 'idle in transaction%'))::float8 AS "LongestIdleInTransaction",
               count(*) FILTER (WHERE a.wait_event_type = 'Lock')::int AS "LockWaiting",
               EXTRACT(EPOCH FROM max(now() - a.query_start) FILTER (WHERE a.wait_event_type = 'Lock'))::float8 AS "LongestLockWait"
        FROM pg_stat_activity a
        WHERE a.datname = current_database() AND a.backend_type = 'client backend' AND a.pid <> pg_backend_pid()
        """;

    private const string TablesSql = """
        SELECT c.relname AS "Table", pg_total_relation_size(c.oid) AS "Bytes", GREATEST(c.reltuples, 0)::bigint AS "Rows",
               COALESCE(s.seq_scan, 0) AS "SequentialScans", COALESCE(s.seq_tup_read, 0) AS "SequentialRowsRead",
               COALESCE(s.idx_scan, 0) AS "IndexScans", COALESCE(s.n_live_tup, 0) AS "LiveRows", COALESCE(s.n_dead_tup, 0) AS "DeadRows",
               GREATEST(s.last_vacuum, s.last_autovacuum) AS "LastVacuum", GREATEST(s.last_analyze, s.last_autoanalyze) AS "LastAnalyze",
               false AS "IsHypertable", false AS "Compressed", false AS "RetentionPolicy", 0 AS "Chunks"
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
        WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
        """;

    private const string TimescaleInstalledSql = """SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') AS "Value" """;

    // The rows of a hypertable are in its chunks, so its statistics are the sum of theirs.
    private const string HypertablesSql = """
        SELECT h.hypertable_name AS "Table",
               COALESCE(hypertable_size(format('%I.%I', h.hypertable_schema, h.hypertable_name)::regclass), 0) AS "Bytes",
               GREATEST(approximate_row_count(format('%I.%I', h.hypertable_schema, h.hypertable_name)::regclass), 0) AS "Rows",
               true AS "IsHypertable", h.num_chunks::int AS "Chunks", h.compression_enabled AS "Compressed",
               EXISTS (SELECT 1 FROM timescaledb_information.jobs j
                       WHERE j.hypertable_schema = h.hypertable_schema AND j.hypertable_name = h.hypertable_name
                         AND j.proc_name = 'policy_retention') AS "RetentionPolicy",
               COALESCE(st.seq_scan, 0) AS "SequentialScans", COALESCE(st.seq_tup_read, 0) AS "SequentialRowsRead",
               COALESCE(st.idx_scan, 0) AS "IndexScans", COALESCE(st.live, 0) AS "LiveRows", COALESCE(st.dead, 0) AS "DeadRows",
               st.last_vacuum AS "LastVacuum", st.last_analyze AS "LastAnalyze"
        FROM timescaledb_information.hypertables h
        LEFT JOIN LATERAL (
          SELECT sum(t.seq_scan)::bigint AS seq_scan, sum(t.seq_tup_read)::bigint AS seq_tup_read, sum(t.idx_scan)::bigint AS idx_scan,
                 sum(t.n_live_tup)::bigint AS live, sum(t.n_dead_tup)::bigint AS dead,
                 max(GREATEST(t.last_vacuum, t.last_autovacuum)) AS last_vacuum, max(GREATEST(t.last_analyze, t.last_autoanalyze)) AS last_analyze
          FROM timescaledb_information.chunks ch
          JOIN pg_stat_all_tables t ON t.schemaname = ch.chunk_schema AND t.relname = ch.chunk_name
          WHERE ch.hypertable_schema = h.hypertable_schema AND ch.hypertable_name = h.hypertable_name) st ON true
        WHERE h.hypertable_schema = 'public'
        """;

    private readonly IFleetoDbContextFactory _dbFactory;
    private DateTime _lastStatements = DateTime.MinValue;
    private DateTime _lastTables = DateTime.MinValue;

    public PerfSnapshotService(IFleetoDbContextFactory dbFactory, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<PerfSnapshotService> logger)
        : base("perf-snapshots", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = Perf.Recorder.Settings.Current;
        if (!settings.Collects(PerfCollector.Snapshots))
        {
            return false;
        }

        // The interval is judged against the last snapshot, so starting Diagnose makes the next one come sooner.
        var now = Time.GetUtcNow().UtcDateTime;
        if (now >= _lastStatements.AddMinutes(settings.StatementSnapshotMinutesAt(now)))
        {
            _lastStatements = now;
            await SnapshotDatabaseAsync(cancellationToken);
        }

        if (now >= _lastTables.AddMinutes(settings.TableSnapshotMinutesAt(now)))
        {
            _lastTables = now;
            await SnapshotTablesAsync(cancellationToken);
        }

        return false;
    }

    /// <summary>Stores the statement statistics and the database snapshot of now.</summary>
    public async Task SnapshotDatabaseAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var statements = await StatementStatisticsReader.ReadAsync(db, cancellationToken);
        var database = await db.Database.SqlQueryRaw<DatabaseRow>(DatabaseSql).SingleAsync(cancellationToken);
        SessionsRow? sessions = null;
        try
        {
            sessions = await db.Database.SqlQueryRaw<SessionsRow>(SessionsSql).SingleAsync(cancellationToken);
        }
        catch (PostgresException ex)
        {
            Logger.LogDebug(ex, "The sessions of the database could not be counted");
        }

        db.PerfDatabaseSnapshots.Add(new PerfDatabaseSnapshot
        {
            Time = now,
            BlocksHit = database.BlocksHit,
            BlocksRead = database.BlocksRead,
            TempFiles = database.TempFiles,
            TempBytes = database.TempBytes,
            Deadlocks = database.Deadlocks,
            Commits = database.Commits,
            Rollbacks = database.Rollbacks,
            Connections = database.Connections,
            MaxConnections = database.MaxConnections,
            WraparoundAge = database.WraparoundAge,
            DatabaseBytes = database.DatabaseBytes,
            StatisticsResetAt = database.StatisticsResetAt,
            StatementsProblem = statements.Problem is { } problem ? problem[..Math.Min(problem.Length, 500)] : null,
            ActiveSessions = sessions?.Active,
            IdleSessions = sessions?.Idle,
            IdleInTransactionSessions = sessions?.IdleInTransaction,
            LongestTransactionSeconds = sessions?.LongestTransaction,
            LongestIdleInTransactionSeconds = sessions?.LongestIdleInTransaction,
            LockWaitingSessions = sessions?.LockWaiting,
            LongestLockWaitSeconds = sessions?.LongestLockWait
        });
        db.PerfStatementSnapshots.AddRange(statements.Statements.Select(s => new PerfStatementSnapshot
        {
            Time = now,
            QueryId = s.QueryId,
            Query = s.Query,
            Calls = s.Calls,
            TotalMs = s.TotalMs,
            Rows = s.Rows,
            SharedBlocksHit = s.SharedBlocksHit,
            SharedBlocksRead = s.SharedBlocksRead,
            TempBlocksWritten = s.TempBlocksWritten
        }));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Stores a snapshot of every table, hypertables measured with their chunks.</summary>
    public async Task SnapshotTablesAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var tables = (await db.Database.SqlQueryRaw<TableRow>(TablesSql).ToListAsync(cancellationToken))
            .ToDictionary(t => t.Table, StringComparer.Ordinal);
        try
        {
            if (await db.Database.SqlQueryRaw<bool>(TimescaleInstalledSql).SingleAsync(cancellationToken))
            {
                foreach (var hypertable in await db.Database.SqlQueryRaw<TableRow>(HypertablesSql).ToListAsync(cancellationToken))
                {
                    tables[hypertable.Table] = hypertable;
                }
            }
        }
        catch (PostgresException ex)
        {
            Logger.LogWarning(ex, "The hypertables could not be measured; their own relations are reported instead");
        }

        db.PerfTableSnapshots.AddRange(tables.Values.Select(t => new PerfTableSnapshot
        {
            Time = now,
            Table = t.Table.Length <= 200 ? t.Table : t.Table[..200],
            Bytes = t.Bytes,
            Rows = t.Rows,
            SequentialScans = t.SequentialScans,
            SequentialRowsRead = t.SequentialRowsRead,
            IndexScans = t.IndexScans,
            LiveRows = t.LiveRows,
            DeadRows = t.DeadRows,
            LastVacuum = t.LastVacuum,
            LastAnalyze = t.LastAnalyze,
            IsHypertable = t.IsHypertable,
            Compressed = t.Compressed,
            RetentionPolicy = t.RetentionPolicy,
            Chunks = t.Chunks
        }));
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed class DatabaseRow
    {
        public long BlocksHit { get; set; }
        public long BlocksRead { get; set; }
        public long TempFiles { get; set; }
        public long TempBytes { get; set; }
        public long Deadlocks { get; set; }
        public long Commits { get; set; }
        public long Rollbacks { get; set; }
        public DateTime? StatisticsResetAt { get; set; }
        public int Connections { get; set; }
        public int MaxConnections { get; set; }
        public long WraparoundAge { get; set; }
        public long DatabaseBytes { get; set; }
    }

    private sealed class SessionsRow
    {
        public int Active { get; set; }
        public int Idle { get; set; }
        public int IdleInTransaction { get; set; }
        public double? LongestTransaction { get; set; }
        public double? LongestIdleInTransaction { get; set; }
        public int LockWaiting { get; set; }
        public double? LongestLockWait { get; set; }
    }

    private sealed class TableRow
    {
        public string Table { get; set; } = string.Empty;
        public long Bytes { get; set; }
        public long Rows { get; set; }
        public long SequentialScans { get; set; }
        public long SequentialRowsRead { get; set; }
        public long IndexScans { get; set; }
        public long LiveRows { get; set; }
        public long DeadRows { get; set; }
        public DateTime? LastVacuum { get; set; }
        public DateTime? LastAnalyze { get; set; }
        public bool IsHypertable { get; set; }
        public bool Compressed { get; set; }
        public bool RetentionPolicy { get; set; }
        public int Chunks { get; set; }
    }
}
