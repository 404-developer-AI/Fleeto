using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Workers.Performance;

/// <summary>A statement as pg_stat_statements counts it since its last reset, folded per query id and normalized again.</summary>
public sealed record StatementRow(long QueryId, string Query, long Calls, double TotalMs, long Rows, long SharedBlocksHit, long SharedBlocksRead,
    long TempBlocksWritten);

/// <summary>
/// The statements of one read, or why there are none, and when pg_stat_statements was last reset: every count is since then. Null
/// when the extension does not say (before PostgreSQL 14) or was not read.
/// </summary>
public sealed record StatementRead(IReadOnlyList<StatementRow> Statements, string? Problem, DateTime? ResetAt = null);

/// <summary>
/// Reads pg_stat_statements for instance health (0.6.0) and the performance monitor (0.7.0), so both see the same statements: the
/// 50 that cost most in total and the 20 slowest per call, of this database only, folded per query id. Read-only; the statistics
/// are never reset from Fleeto. The text is PostgreSQL's normalized form, normalized again so no literal survives.
/// </summary>
public static class StatementStatisticsReader
{
    public const string NotInstalled = "pg_stat_statements is not installed in this database. install.sh switches it on with the next update.";

    private const string InstalledSql = """SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements') AS "Value" """;

    private const string ResetSql = """SELECT stats_reset AS "Value" FROM pg_stat_statements_info""";

    private const string StatementsSql = """
        WITH s AS (
          SELECT queryid, min(query) AS query, sum(calls)::bigint AS calls, sum(total_exec_time) AS total, sum(rows)::bigint AS rows,
                 sum(shared_blks_hit)::bigint AS hit, sum(shared_blks_read)::bigint AS read, sum(temp_blks_written)::bigint AS temp
          FROM pg_stat_statements
          WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database()) AND queryid IS NOT NULL
          GROUP BY queryid)
        SELECT queryid AS "QueryId", query AS "Query", calls AS "Calls", total AS "TotalMs", rows AS "Rows", hit AS "Hit", read AS "Read", temp AS "Temp"
        FROM (SELECT * FROM s ORDER BY total DESC LIMIT 50) top_total
        UNION
        SELECT queryid, query, calls, total, rows, hit, read, temp
        FROM (SELECT * FROM s WHERE calls > 0 ORDER BY total / calls DESC LIMIT 20) top_mean
        """;

    public static async Task<StatementRead> ReadAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            if (!await db.Database.SqlQueryRaw<bool>(InstalledSql).SingleAsync(cancellationToken))
            {
                return new StatementRead([], NotInstalled);
            }

            var rows = await db.Database.SqlQueryRaw<Row>(StatementsSql).ToListAsync(cancellationToken);
            var resetAt = await ReadResetAsync(db, cancellationToken);
            return new StatementRead(rows
                .Select(r => new StatementRow(r.QueryId, SqlFingerprint.Normalize(r.Query), r.Calls, r.TotalMs, r.Rows, r.Hit, r.Read, r.Temp))
                .ToList(), null, resetAt);
        }
        catch (PostgresException ex)
        {
            return new StatementRead([], $"Query statistics could not be read: {ex.MessageText}");
        }
    }

    // The statements stand without it: a database that cannot say when they were reset loses only that line of the diagnostics.
    private static async Task<DateTime?> ReadResetAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            return await db.Database.SqlQueryRaw<DateTime?>(ResetSql).SingleOrDefaultAsync(cancellationToken);
        }
        catch (PostgresException)
        {
            return null;
        }
    }

    private sealed class Row
    {
        public long QueryId { get; set; }
        public string Query { get; set; } = string.Empty;
        public long Calls { get; set; }
        public double TotalMs { get; set; }
        public long Rows { get; set; }
        public long Hit { get; set; }
        public long Read { get; set; }
        public long Temp { get; set; }
    }
}
