using Fleeto.Core.Entities;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Infrastructure.Performance;

/// <summary>Interactions or requests in one bucket of the timeline.</summary>
public sealed record PerfTimelineFlow(long Count, long Errors, double? P50, double? P95, double? P99);

/// <summary>One bucket of the timeline of the page.</summary>
public sealed record PerfTimelinePoint(DateTime Time, PerfTimelineFlow Interactions, PerfTimelineFlow Requests);

/// <summary>One flow in one bucket: how often, how long in total, the failures and the percentiles.</summary>
public sealed record PerfFlowPoint(DateTime Time, PerfTimelineFlow Flow, double Sum);

/// <summary>The runtime of one component in one bucket: averages and peaks of its minutes.</summary>
public sealed record PerfRuntimePoint(DateTime Time, string Component, double? CpuAverage, double? CpuMax, long WorkingSetMax, double GcPauseMs,
    int QueueMax, int PoolBusyMax, int ConnectionsMax, long Exceptions, bool Diagnose);

/// <summary>The VPS in one bucket.</summary>
public sealed record PerfHostPoint(DateTime Time, double? CpuAverage, double? CpuMax, double? StealAverage, double? IowaitAverage, double? Load1Max,
    long? MemoryAvailableMin, double? DiskLatencyAverage);

/// <summary>One key of a kind in one bucket: how often, its total value and its failures. For loops, message types and connections.</summary>
public sealed record PerfKeyPoint(DateTime Time, string Key, long Count, double Sum, long Errors);

/// <summary>
/// Builds the dataset of a period from what the components wrote (0.7.0, PERFORMANCE.md §6, spec §7). Periods up to 6 hours read
/// minute rows; longer periods read hour rows up to the last hour the workers rolled up, and minutes after it. Sums are made in
/// the database, histograms added up bucket by bucket there too, so a long period reads a few thousand rows, not every minute.
/// Database statistics are the difference between the snapshot before the period and the last one in it; without a snapshot
/// before the period that is said, never guessed.
/// </summary>
public static class PerformanceReader
{
    /// <summary>Periods up to this long are read from minute rows.</summary>
    public static readonly TimeSpan MinuteResolution = TimeSpan.FromHours(6);

    /// <summary>The overlap of loops and pages is judged per minute, for periods up to this long.</summary>
    public static readonly TimeSpan OverlapWindow = TimeSpan.FromHours(24);

    public const string RollupWatermark = "perf-rollup";

    private const string MinuteSeriesSql = """
        WITH per_minute AS (
          SELECT "Component", "Kind", "Key", "SubKey", "Minute", sum("Count")::bigint AS c, sum("Errors")::bigint AS e, sum("Sum") AS s,
                 max("Max") AS mx, sum("Bytes")::bigint AS b, sum("DbCount")::bigint AS dc, sum("DbMs") AS dm, sum("ExternalMs") AS em
          FROM "PerfMinutes" WHERE "Minute" >= @from AND "Minute" < @to
          GROUP BY 1, 2, 3, 4, 5)
        SELECT "Component", "Kind", "Key", "SubKey", sum(c)::bigint AS "Count", sum(e)::bigint AS "Errors", sum(s) AS "Sum", max(mx) AS "Max",
               sum(b)::bigint AS "Bytes", sum(dc)::bigint AS "DbCount", sum(dm) AS "DbMs", sum(em) AS "ExternalMs", max(c)::bigint AS "PeakCount"
        FROM per_minute GROUP BY 1, 2, 3, 4
        """;

    private const string MinuteHistogramSql = """
        SELECT m."Component", m."Kind", m."Key", m."SubKey", h.i::int AS "Bucket", sum(h.v)::bigint AS "Count"
        FROM "PerfMinutes" m CROSS JOIN LATERAL unnest(m."Histogram") WITH ORDINALITY AS h(v, i)
        WHERE m."Minute" >= @from AND m."Minute" < @to AND h.v <> 0
        GROUP BY 1, 2, 3, 4, 5
        """;

    private const string HourSeriesSql = """
        SELECT "Component", "Kind", "Key", "SubKey", sum("Count")::bigint AS "Count", sum("Errors")::bigint AS "Errors", sum("Sum") AS "Sum",
               max("Max") AS "Max", sum("Bytes")::bigint AS "Bytes", sum("DbCount")::bigint AS "DbCount", sum("DbMs") AS "DbMs",
               sum("ExternalMs") AS "ExternalMs", max("PeakCount")::bigint AS "PeakCount"
        FROM "PerfHours" WHERE "Hour" >= @from AND "Hour" < @to
        GROUP BY 1, 2, 3, 4
        """;

    private const string HourHistogramSql = """
        SELECT m."Component", m."Kind", m."Key", m."SubKey", h.i::int AS "Bucket", sum(h.v)::bigint AS "Count"
        FROM "PerfHours" m CROSS JOIN LATERAL unnest(m."Histogram") WITH ORDINALITY AS h(v, i)
        WHERE m."Hour" >= @from AND m."Hour" < @to AND h.v <> 0
        GROUP BY 1, 2, 3, 4, 5
        """;

    private const string OverlapHistogramSql = """
        SELECT m."Minute", h.i::int AS "Bucket", sum(h.v)::bigint AS "Count"
        FROM "PerfMinutes" m CROSS JOIN LATERAL unnest(m."Histogram") WITH ORDINALITY AS h(v, i)
        WHERE m."Kind" = 'Interaction' AND m."Key" <> @dashboard AND m."Minute" >= @from AND m."Minute" < @to AND h.v <> 0
        GROUP BY 1, 2
        """;

    private const string OverlapLoopsSql = """
        SELECT DISTINCT "Minute", "Key" FROM "PerfMinutes"
        WHERE "Kind" = 'WorkerRun' AND "Minute" >= @from AND "Minute" < @to AND "Count" > 0
        """;

    private const string RuntimeSql = """
        SELECT "Component", count(DISTINCT "Minute")::int AS "Minutes",
               (array_agg("Version" ORDER BY "Minute" DESC))[1] AS "Version",
               avg("CpuPercent") AS "CpuAverage", max("CpuPercentMax") AS "CpuMax", max("WorkingSetBytes") AS "WorkingSet",
               max("HeapBytes") AS "Heap", sum("GcPauseMs") AS "GcPauseMs", sum("AllocatedBytes")::bigint AS "Allocated",
               max("ThreadPoolThreads") AS "Threads", max("ThreadPoolQueue") AS "Queue", sum("LockContentions")::bigint AS "Contentions",
               sum("Exceptions")::bigint AS "Exceptions", max("Connections") AS "Connections", max("DbPoolBusy") AS "PoolBusy",
               max("DbPoolIdle") AS "PoolIdle", max("DbPoolMax") AS "PoolMax", sum("DbPoolTimeouts")::bigint AS "PoolTimeouts",
               sum("OverheadMs") AS "OverheadMs", max("Cores") AS "Cores",
               count(DISTINCT "Minute") FILTER (WHERE "Level" = 'Diagnose')::int AS "DiagnoseMinutes"
        FROM "PerfRuntimeMinutes" WHERE "Minute" >= @from AND "Minute" < @to
        GROUP BY "Component"
        """;

    private const string DiagnoseMinutesSql = """
        SELECT count(DISTINCT "Minute")::int AS "Value" FROM "PerfRuntimeMinutes"
        WHERE "Level" = 'Diagnose' AND "Minute" >= @from AND "Minute" < @to
        """;

    private const string FingerprintsSql = """
        SELECT "Hash", "Sql", "Callers" FROM "PerfFingerprints" WHERE "Hash" = ANY(@hashes)
        """;

    // Catalog estimates for sizes, never a size function per file (spec §5: seconds on a large schema). Readable by every role.
    private const string IndexesSql = """
        SELECT t.relname AS "Table", i.relname AS "Name", i.relpages::bigint * current_setting('block_size')::bigint AS "Bytes",
               COALESCE(s.idx_scan, 0) AS "Scans", x.indisunique AS "Unique", x.indisprimary AS "Primary",
               COALESCE((SELECT string_agg(a.attname, ', ' ORDER BY k.n) FROM unnest(x.indkey) WITH ORDINALITY AS k(attnum, n)
                         JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum), '') AS "Columns",
               x.indkey::text || '|' || COALESCE(pg_get_expr(x.indexprs, x.indrelid), '') || '|' || COALESCE(pg_get_expr(x.indpred, x.indrelid), '')
                 || '|' || i.relam::text AS "Shape"
        FROM pg_index x
        JOIN pg_class i ON i.oid = x.indexrelid
        JOIN pg_class t ON t.oid = x.indrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        LEFT JOIN pg_stat_user_indexes s ON s.indexrelid = x.indexrelid
        WHERE n.nspname = 'public'
        """;

    private const string HostSql = """
        SELECT count(*)::int AS "Minutes", max("Cores") AS "Cores", avg("CpuPercent") AS "CpuAverage",
               percentile_cont(0.95) WITHIN GROUP (ORDER BY "CpuPercent") AS "CpuP95",
               avg("StealPercent") AS "StealAverage", max("StealPercent") AS "StealMax", avg("IowaitPercent") AS "IowaitAverage",
               max("Load1") AS "Load1Max", max("MemoryTotalBytes") AS "MemoryTotal", min("MemoryAvailableBytes") AS "MemoryAvailableMin",
               max("SwapTotalBytes") AS "SwapTotal", min("SwapFreeBytes") AS "SwapFreeMin", avg("DiskLatencyMs") AS "DiskLatency",
               max("DiskUtilizationPercent") AS "DiskUtilization", (array_agg("DiskTotalBytes" ORDER BY "Minute" DESC))[1] AS "DiskTotal",
               (array_agg("DiskFreeBytes" ORDER BY "Minute" DESC))[1] AS "DiskFree", avg("ThrottledPercent") AS "Throttled"
        FROM "PerfHostMinutes" WHERE "Minute" >= @from AND "Minute" < @to
        """;

    public static async Task<PerformanceDataset> ReadAsync(FleetoDbContext db, DateTime from, DateTime to, PerformanceSettings settings,
        CancellationToken cancellationToken)
    {
        using var scope = PerfContext.Begin(PerfKeys.DashboardSource);
        var series = await ReadSeriesAsync(db, from, to, cancellationToken);
        var runtime = await ReadRuntimeAsync(db, from, to, cancellationToken);
        var host = await ReadHostAsync(db, from, to, cancellationToken);
        var (statements, statementsProblem, database) = await ReadDatabaseAsync(db, from, to, cancellationToken);
        var tables = await ReadTablesAsync(db, from, to, cancellationToken);
        var minutes = to - from <= OverlapWindow ? await ReadOverlapAsync(db, from, to, cancellationToken) : [];
        var markers = await db.PerfMarkers.AsNoTracking().Where(m => m.Time >= from && m.Time < to).OrderBy(m => m.Time)
            .Select(m => new PerfMarkerView(m.Time, m.Kind.ToString(), m.Label)).ToListAsync(cancellationToken);
        var diagnoseMinutes = await db.Database.SqlQueryRaw<int>(DiagnoseMinutesSql, Range(from, to)).SingleAsync(cancellationToken);
        var events = await ReadEventsAsync(db, from, to, cancellationToken);
        var fingerprints = await ReadFingerprintsAsync(db, series, events, cancellationToken);
        var indexes = await ReadIndexesAsync(db, cancellationToken);

        var missing = new List<string>();
        foreach (var info in PerfCategories.All.Where(c => !c.Measured))
        {
            missing.Add($"{info.Label}: not measured in this version. The browser side (Web Vitals, network time) comes with 0.7.0 step 3.");
        }

        var periodMinutes = (int)Math.Round((to - from).TotalMinutes);
        if (diagnoseMinutes == 0)
        {
            missing.Add("Diagnose did not run in this period: no statement fingerprints, no N+1 detection and no calling method per query. " +
                        "Start it at the top of the page, use Fleeto for its duration, then export.");
        }
        else if (diagnoseMinutes < periodMinutes)
        {
            missing.Add($"Diagnose ran {diagnoseMinutes} of the {periodMinutes} minutes: statements, N+1 suspects and callers cover only those minutes.");
        }

        if (diagnoseMinutes > 0)
        {
            missing.Add("A caller is unknown for a statement sent after its method awaited something else first; the page, loop or message that sent it is still named.");
        }

        if (indexes.Count == 0)
        {
            missing.Add("The indexes could not be read, so unused and duplicate indexes are not reported.");
        }
        if (!settings.IsOn)
        {
            missing.Add("The level is Off: nothing new is measured until an admin sets it to Basic.");
        }

        foreach (var collector in settings.DisabledCollectors)
        {
            var label = PerfCollectors.All.FirstOrDefault(c => c.Collector == collector)?.Label ?? collector.ToString();
            missing.Add($"The collector {label} is switched off.");
        }

        if (host is null)
        {
            missing.Add("No VPS figures in the period: the workers read /proc on Linux only; elsewhere cores and disk are all there is.");
        }
        else if (host.StealAverage is null)
        {
            missing.Add("The VPS was measured without CPU detail (steal, iowait): not on Linux, or only one reading in the period.");
        }

        if (statementsProblem is not null)
        {
            missing.Add($"Statement statistics: {statementsProblem}");
        }

        if (database is { Baseline: false })
        {
            missing.Add("No database snapshot before the start of the period: cache hit ratio, temporary files and table scans are unknown for it.");
        }
        else if (database is null)
        {
            missing.Add("No database snapshot in the period: the workers read one every 15 minutes, and at start.");
        }

        if (tables.Count > 0 && !tables.Any(t => t.IsHypertable))
        {
            missing.Add("TimescaleDB is not installed in this database (local development): retention runs as deletes, and hypertable rules are skipped.");
        }

        if (to - from > OverlapWindow)
        {
            missing.Add("Periods longer than 24 hours are not judged for loops that overlap with pages; choose 24 hours or less for that.");
        }

        return new PerformanceDataset(from, to, settings, series, runtime, host, statements, statementsProblem, database, tables, minutes, markers, missing)
        {
            Fingerprints = fingerprints,
            Events = events,
            Indexes = indexes,
            DiagnoseMinutes = diagnoseMinutes
        };
    }

    /// <summary>The slowest events of the period, with their heaviest statements where Diagnose kept them.</summary>
    private static async Task<IReadOnlyList<PerfEventView>> ReadEventsAsync(FleetoDbContext db, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var rows = await db.PerfEvents.AsNoTracking().Where(e => e.Time >= from && e.Time < to).OrderByDescending(e => e.DurationMs).Take(MaxEvents)
            .ToListAsync(cancellationToken);
        return rows.Select(e => new PerfEventView(e.Time, e.Component, e.Kind, e.Key, e.SubKey, e.DurationMs, e.DbCount, e.DbMs, e.ExternalMs, e.Failed,
            ParseStatements(e.Statements))).ToList();
    }

    public const int MaxEvents = 100;

    /// <summary>The stored form of <see cref="PerfRecorder.HeaviestStatements"/> back into statements; what does not parse is left out.</summary>
    public static IReadOnlyList<PerfEventStatement> ParseStatements(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var statements = new List<PerfEventStatement>();
        foreach (var part in stored.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 3 && int.TryParse(fields[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) &&
                double.TryParse(fields[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
            {
                statements.Add(new PerfEventStatement(fields[0], count, ms));
            }
        }

        return statements;
    }

    /// <summary>The text and callers of every statement the period refers to, merged over the components that saw it.</summary>
    private static async Task<IReadOnlyDictionary<string, PerfFingerprintView>> ReadFingerprintsAsync(FleetoDbContext db, IReadOnlyList<PerfSeries> series,
        IReadOnlyList<PerfEventView> events, CancellationToken cancellationToken)
    {
        var hashes = series.Where(s => s.Kind == PerfKind.Statement).Select(s => s.Key)
            .Concat(series.Where(s => s.Kind == PerfKind.NPlusOne).Select(s => s.SubKey))
            .Concat(events.SelectMany(e => e.Statements).Select(s => s.Hash))
            .Where(h => h.Length > 0 && h != PerfKeys.Other)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (hashes.Length == 0)
        {
            return new Dictionary<string, PerfFingerprintView>();
        }

        var rows = await db.Database.SqlQueryRaw<FingerprintRow>(FingerprintsSql, new NpgsqlParameter("hashes", hashes)).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.Hash, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => new PerfFingerprintView(g.Key, g.First().Sql, g.SelectMany(r => r.Callers).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Take(PerfFingerprint.MaxCallers).ToList()), StringComparer.Ordinal);
    }

    /// <summary>
    /// The indexes of the tables of Fleeto, read live from the catalog, with the one each duplicates: same table, same columns in
    /// the same order, same expressions, condition and method. A unique or primary index is kept as the original. Empty when the
    /// catalog cannot be read.
    /// </summary>
    public static async Task<IReadOnlyList<PerfIndexView>> ReadIndexesAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        List<IndexRow> rows;
        try
        {
            rows = await db.Database.SqlQueryRaw<IndexRow>(IndexesSql).ToListAsync(cancellationToken);
        }
        catch (PostgresException)
        {
            return [];
        }

        var originals = rows.GroupBy(r => (r.Table, r.Shape))
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Primary).ThenByDescending(r => r.Unique).ThenBy(r => r.Name, StringComparer.Ordinal).First().Name);
        return rows.Select(r => new PerfIndexView(r.Table, r.Name, r.Bytes, r.Scans, r.Unique, r.Primary, r.Columns,
                originals.TryGetValue((r.Table, r.Shape), out var original) && original != r.Name ? original : null))
            .OrderByDescending(i => i.Bytes)
            .ToList();
    }

    private static async Task<IReadOnlyList<PerfSeries>> ReadSeriesAsync(FleetoDbContext db, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var parts = new List<(List<SeriesRow> Rows, List<BucketRow> Buckets)>();
        if (to - from <= MinuteResolution)
        {
            parts.Add(await ReadPartAsync(db, MinuteSeriesSql, MinuteHistogramSql, from, to, cancellationToken));
        }
        else
        {
            // Hours up to the rollup, minutes after it.
            var watermark = await db.WorkerWatermarks.AsNoTracking().Where(w => w.Name == RollupWatermark).Select(w => (long?)w.Value)
                .FirstOrDefaultAsync(cancellationToken);
            var rolledUntil = watermark is { } ticks ? new DateTime(ticks, DateTimeKind.Utc).AddHours(1) : from;
            var split = rolledUntil < from ? from : rolledUntil > to ? to : rolledUntil;
            var hourFrom = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc);
            if (split > from)
            {
                parts.Add(await ReadPartAsync(db, HourSeriesSql, HourHistogramSql, hourFrom, split, cancellationToken));
            }

            if (split < to)
            {
                parts.Add(await ReadPartAsync(db, MinuteSeriesSql, MinuteHistogramSql, split, to, cancellationToken));
            }
        }

        var merged = new Dictionary<(string, string, string, string), Accumulated>();
        foreach (var (rows, buckets) in parts)
        {
            foreach (var row in rows)
            {
                var key = (row.Component, row.Kind, row.Key, row.SubKey);
                if (!merged.TryGetValue(key, out var total))
                {
                    merged[key] = total = new Accumulated();
                }

                total.Count += row.Count;
                total.Errors += row.Errors;
                total.Sum += row.Sum;
                total.Max = Math.Max(total.Max, row.Max);
                total.Bytes += row.Bytes;
                total.DbCount += row.DbCount;
                total.DbMs += row.DbMs;
                total.ExternalMs += row.ExternalMs;
                total.PeakCount = Math.Max(total.PeakCount, row.PeakCount);
            }

            foreach (var bucket in buckets)
            {
                if (merged.TryGetValue((bucket.Component, bucket.Kind, bucket.Key, bucket.SubKey), out var total) &&
                    bucket.Bucket is >= 1 and var index && index <= PerfHistogram.BucketCount)
                {
                    total.Histogram[index - 1] += (int)Math.Min(int.MaxValue, bucket.Count);
                }
            }
        }

        return merged
            .Where(pair => Enum.TryParse<PerfKind>(pair.Key.Item2, out _))
            .Select(pair => new PerfSeries(pair.Key.Item1, Enum.Parse<PerfKind>(pair.Key.Item2), pair.Key.Item3, pair.Key.Item4, pair.Value.Count,
                pair.Value.Errors, pair.Value.Sum, pair.Value.Max, PerfHistogram.Trim(pair.Value.Histogram), pair.Value.Bytes, pair.Value.DbCount,
                pair.Value.DbMs, pair.Value.ExternalMs, pair.Value.PeakCount))
            .ToList();
    }

    private static async Task<(List<SeriesRow>, List<BucketRow>)> ReadPartAsync(FleetoDbContext db, string seriesSql, string histogramSql, DateTime from,
        DateTime to, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<SeriesRow>(seriesSql, Range(from, to)).ToListAsync(cancellationToken);
        var buckets = await db.Database.SqlQueryRaw<BucketRow>(histogramSql, Range(from, to)).ToListAsync(cancellationToken);
        return (rows, buckets);
    }

    private static async Task<IReadOnlyList<PerfRuntimeSummary>> ReadRuntimeAsync(FleetoDbContext db, DateTime from, DateTime to,
        CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<RuntimeRow>(RuntimeSql, Range(from, to)).ToListAsync(cancellationToken);
        return rows.Select(r => new PerfRuntimeSummary(r.Component, r.Minutes, r.Version, r.CpuAverage, r.CpuMax, r.WorkingSet, r.Heap,
                r.Minutes <= 0 ? 0 : r.GcPauseMs / (r.Minutes * 60_000d) * 100, r.Allocated, r.Threads, r.Queue, r.Contentions, r.Exceptions,
                r.Connections, r.PoolBusy, r.PoolIdle, r.PoolMax, r.PoolTimeouts, r.OverheadMs, r.Cores) { DiagnoseMinutes = r.DiagnoseMinutes })
            .OrderBy(r => r.Component, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<PerfHostSummary?> ReadHostAsync(FleetoDbContext db, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var row = await db.Database.SqlQueryRaw<HostRow>(HostSql, Range(from, to)).SingleAsync(cancellationToken);
        return row.Minutes == 0
            ? null
            : new PerfHostSummary(row.Minutes, row.Cores, row.CpuAverage, row.CpuP95, row.StealAverage, row.StealMax, row.IowaitAverage, row.Load1Max,
                row.MemoryTotal, row.MemoryAvailableMin, row.SwapTotal, row.SwapFreeMin, row.DiskLatency, row.DiskUtilization, row.DiskTotal,
                row.DiskFree, row.Throttled);
    }

    private static async Task<(IReadOnlyList<PerfStatementDelta>, string?, PerfDatabaseSummary?)> ReadDatabaseAsync(FleetoDbContext db,
        DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var last = await db.PerfDatabaseSnapshots.AsNoTracking().Where(s => s.Time < to).OrderByDescending(s => s.Time).FirstOrDefaultAsync(cancellationToken);
        if (last is null || last.Time < from.AddHours(-2))
        {
            return ([], last?.StatementsProblem, null);
        }

        var baseline = await db.PerfDatabaseSnapshots.AsNoTracking().Where(s => s.Time <= from).OrderByDescending(s => s.Time)
            .FirstOrDefaultAsync(cancellationToken);
        var first = baseline ?? await db.PerfDatabaseSnapshots.AsNoTracking().Where(s => s.Time >= from).OrderBy(s => s.Time)
            .FirstAsync(cancellationToken);

        PerfDatabaseSummary summary;
        if (baseline is not null && baseline.Time < last.Time)
        {
            var reset = last.StatisticsResetAt != baseline.StatisticsResetAt;
            var hit = reset ? last.BlocksHit : last.BlocksHit - baseline.BlocksHit;
            var read = reset ? last.BlocksRead : last.BlocksRead - baseline.BlocksRead;
            summary = new PerfDatabaseSummary(true, hit + read > 0 ? hit / (double)(hit + read) : null,
                reset ? last.TempFiles : last.TempFiles - baseline.TempFiles, reset ? last.TempBytes : last.TempBytes - baseline.TempBytes,
                reset ? last.Deadlocks : last.Deadlocks - baseline.Deadlocks, last.Connections, last.MaxConnections, last.WraparoundAge, last.DatabaseBytes);
        }
        else
        {
            summary = new PerfDatabaseSummary(false, null, null, null, null, last.Connections, last.MaxConnections, last.WraparoundAge, last.DatabaseBytes);
        }

        if (last.ActiveSessions is { } active)
        {
            summary = summary with
            {
                Sessions = new PerfSessionsSummary(active, last.IdleSessions ?? 0, last.IdleInTransactionSessions ?? 0, last.LongestTransactionSeconds,
                    last.LongestIdleInTransactionSeconds, last.LockWaitingSessions ?? 0, last.LongestLockWaitSeconds)
            };
        }

        var statements = await StatementDeltasAsync(db, first.Time, last.Time, cancellationToken);
        return (statements, last.StatementsProblem, summary);
    }

    /// <summary>
    /// Per statement the difference between its first snapshot in the period (or the one before it) and the last. A statement whose
    /// counters went down was reset in between and counts from zero; one seen only once has no difference and is left out.
    /// </summary>
    private static async Task<IReadOnlyList<PerfStatementDelta>> StatementDeltasAsync(FleetoDbContext db, DateTime baseTime, DateTime lastTime,
        CancellationToken cancellationToken)
    {
        if (lastTime <= baseTime)
        {
            return [];
        }

        var last = await db.PerfStatementSnapshots.AsNoTracking().Where(s => s.Time == lastTime).ToListAsync(cancellationToken);
        var ids = last.Select(s => s.QueryId).ToList();
        var earliest = await db.PerfStatementSnapshots.AsNoTracking()
            .Where(s => s.Time >= baseTime && s.Time < lastTime && ids.Contains(s.QueryId))
            .GroupBy(s => s.QueryId)
            .Select(g => new { QueryId = g.Key, Time = g.Min(s => s.Time) })
            .ToListAsync(cancellationToken);
        var times = earliest.Select(e => e.Time).Distinct().ToList();
        var candidates = await db.PerfStatementSnapshots.AsNoTracking().Where(s => times.Contains(s.Time) && ids.Contains(s.QueryId))
            .ToListAsync(cancellationToken);
        var baseRows = earliest.Join(candidates, e => (e.QueryId, e.Time), c => (c.QueryId, c.Time), (_, c) => c)
            .GroupBy(c => c.QueryId).ToDictionary(g => g.Key, g => g.First());

        var deltas = new List<PerfStatementDelta>();
        foreach (var now in last)
        {
            if (!baseRows.TryGetValue(now.QueryId, out var before))
            {
                continue;
            }

            var reset = now.Calls < before.Calls;
            var calls = reset ? now.Calls : now.Calls - before.Calls;
            if (calls <= 0)
            {
                continue;
            }

            deltas.Add(new PerfStatementDelta(now.QueryId, now.Query, calls, reset ? now.TotalMs : now.TotalMs - before.TotalMs,
                reset ? now.Rows : now.Rows - before.Rows, reset ? now.SharedBlocksRead : now.SharedBlocksRead - before.SharedBlocksRead,
                reset ? now.TempBlocksWritten : now.TempBlocksWritten - before.TempBlocksWritten));
        }

        return deltas.OrderByDescending(d => d.TotalMs).ToList();
    }

    private static async Task<IReadOnlyList<PerfTableSummary>> ReadTablesAsync(FleetoDbContext db, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var lastTime = await db.PerfTableSnapshots.AsNoTracking().Where(t => t.Time < to).MaxAsync(t => (DateTime?)t.Time, cancellationToken);
        if (lastTime is null)
        {
            return [];
        }

        var baseTime = await db.PerfTableSnapshots.AsNoTracking().Where(t => t.Time <= from).MaxAsync(t => (DateTime?)t.Time, cancellationToken);
        var last = await db.PerfTableSnapshots.AsNoTracking().Where(t => t.Time == lastTime).ToListAsync(cancellationToken);
        var baseline = baseTime is { } b && b < lastTime
            ? (await db.PerfTableSnapshots.AsNoTracking().Where(t => t.Time == b).ToListAsync(cancellationToken)).ToDictionary(t => t.Table, StringComparer.Ordinal)
            : null;

        return last.Select(t =>
            {
                PerfTableSnapshot? before = null;
                var known = baseline is not null && baseline.TryGetValue(t.Table, out before) && t.SequentialScans >= before!.SequentialScans;
                return new PerfTableSummary(t.Table, t.Bytes, known ? t.Bytes - before!.Bytes : null, t.Rows,
                    known ? t.SequentialScans - before!.SequentialScans : null, known ? t.SequentialRowsRead - before!.SequentialRowsRead : null,
                    known ? t.IndexScans - before!.IndexScans : null, t.LiveRows, t.DeadRows, t.LastVacuum, t.LastAnalyze, t.IsHypertable, t.Compressed,
                    t.RetentionPolicy, t.Chunks);
            })
            .OrderByDescending(t => t.Bytes)
            .ToList();
    }

    private static async Task<IReadOnlyList<PerfOverlapMinute>> ReadOverlapAsync(FleetoDbContext db, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var buckets = await db.Database.SqlQueryRaw<OverlapBucketRow>(OverlapHistogramSql,
            [new NpgsqlParameter("from", from), new NpgsqlParameter("to", to), new NpgsqlParameter("dashboard", PerformanceRules.DashboardRoute)])
            .ToListAsync(cancellationToken);
        var loops = await db.Database.SqlQueryRaw<OverlapLoopRow>(OverlapLoopsSql, Range(from, to)).ToListAsync(cancellationToken);
        var loopsByMinute = loops.GroupBy(l => l.Minute).ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(l => l.Key).ToHashSet(StringComparer.Ordinal));
        return buckets.GroupBy(b => b.Minute)
            .Select(g =>
            {
                var histogram = new int[PerfHistogram.BucketCount];
                foreach (var bucket in g.Where(b => b.Bucket >= 1 && b.Bucket <= PerfHistogram.BucketCount))
                {
                    histogram[bucket.Bucket - 1] += (int)Math.Min(int.MaxValue, bucket.Count);
                }

                return new PerfOverlapMinute(g.Key, PerfHistogram.Trim(histogram), PerfHistogram.Total(histogram),
                    loopsByMinute.GetValueOrDefault(g.Key) ?? []);
            })
            .OrderBy(m => m.Minute)
            .ToList();
    }

    private const string TimelineSql = """
        SELECT date_bin(@bucket, m."Minute", @from) AS "Time", m."Kind", sum(m."Count")::bigint AS "Count", sum(m."Errors")::bigint AS "Errors"
        FROM "PerfMinutes" m
        WHERE m."Kind" IN ('Interaction', 'Http') AND m."Key" <> @dashboard AND m."Minute" >= @from AND m."Minute" < @to
        GROUP BY 1, 2
        """;

    private const string TimelineHistogramSql = """
        SELECT date_bin(@bucket, m."Minute", @from) AS "Time", m."Kind", h.i::int AS "Bucket", sum(h.v)::bigint AS "Count"
        FROM "PerfMinutes" m CROSS JOIN LATERAL unnest(m."Histogram") WITH ORDINALITY AS h(v, i)
        WHERE m."Kind" IN ('Interaction', 'Http') AND m."Key" <> @dashboard AND m."Minute" >= @from AND m."Minute" < @to AND h.v <> 0
        GROUP BY 1, 2, 3
        """;

    private const string HourTimelineSql = """
        SELECT date_bin(@bucket, m."Hour", @from) AS "Time", m."Kind", sum(m."Count")::bigint AS "Count", sum(m."Errors")::bigint AS "Errors"
        FROM "PerfHours" m
        WHERE m."Kind" IN ('Interaction', 'Http') AND m."Key" <> @dashboard AND m."Hour" >= @from AND m."Hour" < @to
        GROUP BY 1, 2
        """;

    private const string HourTimelineHistogramSql = """
        SELECT date_bin(@bucket, m."Hour", @from) AS "Time", m."Kind", h.i::int AS "Bucket", sum(h.v)::bigint AS "Count"
        FROM "PerfHours" m CROSS JOIN LATERAL unnest(m."Histogram") WITH ORDINALITY AS h(v, i)
        WHERE m."Kind" IN ('Interaction', 'Http') AND m."Key" <> @dashboard AND m."Hour" >= @from AND m."Hour" < @to AND h.v <> 0
        GROUP BY 1, 2, 3
        """;

    /// <summary>
    /// Page interactions and requests per bucket of the period: how many, how many failed, and their percentiles. Minute rows for
    /// periods up to 24 hours, hour rows beyond. The monitor's own page is left out.
    /// </summary>
    public static async Task<IReadOnlyList<PerfTimelinePoint>> ReadTimelineAsync(FleetoDbContext db, DateTime from, DateTime to, TimeSpan bucket,
        CancellationToken cancellationToken)
    {
        using var scope = PerfContext.Begin(PerfKeys.DashboardSource);
        var hours = to - from > OverlapWindow;
        object[] Parameters() =>
        [
            new NpgsqlParameter("bucket", bucket), new NpgsqlParameter("from", from), new NpgsqlParameter("to", to),
            new NpgsqlParameter("dashboard", PerformanceRules.DashboardRoute)
        ];
        var counts = await db.Database.SqlQueryRaw<TimelineRow>(hours ? HourTimelineSql : TimelineSql, Parameters()).ToListAsync(cancellationToken);
        var buckets = await db.Database.SqlQueryRaw<TimelineBucketRow>(hours ? HourTimelineHistogramSql : TimelineHistogramSql, Parameters())
            .ToListAsync(cancellationToken);

        var points = new List<PerfTimelinePoint>();
        for (var time = from; time < to; time += bucket)
        {
            PerfTimelineFlow Flow(string kind)
            {
                var histogram = new int[PerfHistogram.BucketCount];
                foreach (var b in buckets.Where(b => b.Time == time && b.Kind == kind && b.Bucket >= 1 && b.Bucket <= PerfHistogram.BucketCount))
                {
                    histogram[b.Bucket - 1] += (int)Math.Min(int.MaxValue, b.Count);
                }

                var row = counts.FirstOrDefault(c => c.Time == time && c.Kind == kind);
                return new PerfTimelineFlow(row?.Count ?? 0, row?.Errors ?? 0, PerfHistogram.Percentile(histogram, 0.5),
                    PerfHistogram.Percentile(histogram, 0.95), PerfHistogram.Percentile(histogram, 0.99));
            }

            points.Add(new PerfTimelinePoint(time, Flow(nameof(PerfKind.Interaction)), Flow(nameof(PerfKind.Http))));
        }

        return points;
    }

    private const string FlowTimelineSql = """
        SELECT date_bin(@bucket, m."Minute", @from) AS "Time", sum(m."Count")::bigint AS "Count", sum(m."Errors")::bigint AS "Errors", sum(m."Sum") AS "Sum"
        FROM "PerfMinutes" m
        WHERE m."Kind" = @kind AND m."Key" = @key AND (@subKey = '' OR m."SubKey" = @subKey) AND m."Minute" >= @from AND m."Minute" < @to
        GROUP BY 1
        """;

    private const string FlowTimelineHistogramSql = """
        SELECT date_bin(@bucket, m."Minute", @from) AS "Time", '' AS "Kind", h.i::int AS "Bucket", sum(h.v)::bigint AS "Count"
        FROM "PerfMinutes" m CROSS JOIN LATERAL unnest(m."Histogram") WITH ORDINALITY AS h(v, i)
        WHERE m."Kind" = @kind AND m."Key" = @key AND (@subKey = '' OR m."SubKey" = @subKey) AND m."Minute" >= @from AND m."Minute" < @to AND h.v <> 0
        GROUP BY 1, 3
        """;

    private const string RuntimeTimelineSql = """
        SELECT date_bin(@bucket, "Minute", @from) AS "Time", "Component", avg("CpuPercent") AS "CpuAverage", max("CpuPercentMax") AS "CpuMax",
               max("WorkingSetBytes") AS "WorkingSetMax", sum("GcPauseMs") AS "GcPauseMs", max("ThreadPoolQueue") AS "QueueMax",
               max("DbPoolBusy") AS "PoolBusyMax", max("Connections") AS "ConnectionsMax", sum("Exceptions")::bigint AS "Exceptions",
               bool_or("Level" = 'Diagnose') AS "Diagnose"
        FROM "PerfRuntimeMinutes" WHERE "Minute" >= @from AND "Minute" < @to
        GROUP BY 1, 2
        """;

    private const string HostTimelineSql = """
        SELECT date_bin(@bucket, "Minute", @from) AS "Time", avg("CpuPercent") AS "CpuAverage", max("CpuPercent") AS "CpuMax",
               avg("StealPercent") AS "StealAverage", avg("IowaitPercent") AS "IowaitAverage", max("Load1") AS "Load1Max",
               min("MemoryAvailableBytes") AS "MemoryAvailableMin", avg("DiskLatencyMs") AS "DiskLatencyAverage"
        FROM "PerfHostMinutes" WHERE "Minute" >= @from AND "Minute" < @to
        GROUP BY 1
        """;

    private const string KeyTimelineSql = """
        SELECT date_bin(@bucket, "Minute", @from) AS "Time", "Key", sum("Count")::bigint AS "Count", sum("Sum") AS "Sum", sum("Errors")::bigint AS "Errors"
        FROM "PerfMinutes" WHERE "Kind" = @kind AND "Minute" >= @from AND "Minute" < @to
        GROUP BY 1, 2
        """;

    private const string KeyTimelineHourSql = """
        SELECT date_bin(@bucket, "Hour", @from) AS "Time", "Key", sum("Count")::bigint AS "Count", sum("Sum") AS "Sum", sum("Errors")::bigint AS "Errors"
        FROM "PerfHours" WHERE "Kind" = @kind AND "Hour" >= @from AND "Hour" < @to
        GROUP BY 1, 2
        """;

    /// <summary>
    /// One flow over the period per bucket (a page, a route, a loop): count, failures, total and percentiles. From minute rows, so
    /// for the periods the minutes are kept; empty buckets are included.
    /// </summary>
    public static async Task<IReadOnlyList<PerfFlowPoint>> ReadFlowTimelineAsync(FleetoDbContext db, DateTime from, DateTime to, TimeSpan bucket, PerfKind kind,
        string key, string subKey, CancellationToken cancellationToken)
    {
        using var scope = PerfContext.Begin(PerfKeys.DashboardSource);
        object[] Parameters() =>
        [
            new NpgsqlParameter("bucket", bucket), new NpgsqlParameter("from", from), new NpgsqlParameter("to", to),
            new NpgsqlParameter("kind", kind.ToString()), new NpgsqlParameter("key", key), new NpgsqlParameter("subKey", subKey)
        ];
        var counts = await db.Database.SqlQueryRaw<FlowRow>(FlowTimelineSql, Parameters()).ToListAsync(cancellationToken);
        var buckets = await db.Database.SqlQueryRaw<TimelineBucketRow>(FlowTimelineHistogramSql, Parameters()).ToListAsync(cancellationToken);
        var points = new List<PerfFlowPoint>();
        for (var time = from; time < to; time += bucket)
        {
            var histogram = new int[PerfHistogram.BucketCount];
            foreach (var b in buckets.Where(b => b.Time == time && b.Bucket >= 1 && b.Bucket <= PerfHistogram.BucketCount))
            {
                histogram[b.Bucket - 1] += (int)Math.Min(int.MaxValue, b.Count);
            }

            var row = counts.FirstOrDefault(c => c.Time == time);
            points.Add(new PerfFlowPoint(time, new PerfTimelineFlow(row?.Count ?? 0, row?.Errors ?? 0, PerfHistogram.Percentile(histogram, 0.5),
                PerfHistogram.Percentile(histogram, 0.95), PerfHistogram.Percentile(histogram, 0.99)), row?.Sum ?? 0));
        }

        return points;
    }

    /// <summary>The runtime of every component per bucket.</summary>
    public static async Task<IReadOnlyList<PerfRuntimePoint>> ReadRuntimeTimelineAsync(FleetoDbContext db, DateTime from, DateTime to, TimeSpan bucket,
        CancellationToken cancellationToken)
    {
        using var scope = PerfContext.Begin(PerfKeys.DashboardSource);
        var rows = await db.Database.SqlQueryRaw<RuntimePointRow>(RuntimeTimelineSql, BucketRange(from, to, bucket)).ToListAsync(cancellationToken);
        return rows.Select(r => new PerfRuntimePoint(r.Time, r.Component, r.CpuAverage, r.CpuMax, r.WorkingSetMax, r.GcPauseMs, r.QueueMax, r.PoolBusyMax,
            r.ConnectionsMax, r.Exceptions, r.Diagnose)).OrderBy(r => r.Time).ToList();
    }

    /// <summary>The VPS per bucket.</summary>
    public static async Task<IReadOnlyList<PerfHostPoint>> ReadHostTimelineAsync(FleetoDbContext db, DateTime from, DateTime to, TimeSpan bucket,
        CancellationToken cancellationToken)
    {
        using var scope = PerfContext.Begin(PerfKeys.DashboardSource);
        var rows = await db.Database.SqlQueryRaw<HostPointRow>(HostTimelineSql, BucketRange(from, to, bucket)).ToListAsync(cancellationToken);
        return rows.Select(r => new PerfHostPoint(r.Time, r.CpuAverage, r.CpuMax, r.StealAverage, r.IowaitAverage, r.Load1Max, r.MemoryAvailableMin,
            r.DiskLatencyAverage)).OrderBy(r => r.Time).ToList();
    }

    /// <summary>Every key of a kind per bucket: the runs of each loop, the messages of each type, the connections by outcome.</summary>
    public static async Task<IReadOnlyList<PerfKeyPoint>> ReadKeyTimelineAsync(FleetoDbContext db, DateTime from, DateTime to, TimeSpan bucket, PerfKind kind,
        CancellationToken cancellationToken)
    {
        using var scope = PerfContext.Begin(PerfKeys.DashboardSource);
        object[] parameters =
        [
            new NpgsqlParameter("bucket", bucket), new NpgsqlParameter("from", from), new NpgsqlParameter("to", to), new NpgsqlParameter("kind", kind.ToString())
        ];
        var rows = await db.Database.SqlQueryRaw<KeyPointRow>(to - from > OverlapWindow ? KeyTimelineHourSql : KeyTimelineSql, parameters)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new PerfKeyPoint(r.Time, r.Key, r.Count, r.Sum, r.Errors)).OrderBy(r => r.Time).ToList();
    }

    private static object[] BucketRange(DateTime from, DateTime to, TimeSpan bucket) =>
        [new NpgsqlParameter("bucket", bucket), new NpgsqlParameter("from", from), new NpgsqlParameter("to", to)];

    private static object[] Range(DateTime from, DateTime to) => [new NpgsqlParameter("from", from), new NpgsqlParameter("to", to)];

    private sealed class FlowRow
    {
        public DateTime Time { get; set; }
        public long Count { get; set; }
        public long Errors { get; set; }
        public double Sum { get; set; }
    }

    private sealed class RuntimePointRow
    {
        public DateTime Time { get; set; }
        public string Component { get; set; } = string.Empty;
        public double? CpuAverage { get; set; }
        public double? CpuMax { get; set; }
        public long WorkingSetMax { get; set; }
        public double GcPauseMs { get; set; }
        public int QueueMax { get; set; }
        public int PoolBusyMax { get; set; }
        public int ConnectionsMax { get; set; }
        public long Exceptions { get; set; }
        public bool Diagnose { get; set; }
    }

    private sealed class HostPointRow
    {
        public DateTime Time { get; set; }
        public double? CpuAverage { get; set; }
        public double? CpuMax { get; set; }
        public double? StealAverage { get; set; }
        public double? IowaitAverage { get; set; }
        public double? Load1Max { get; set; }
        public long? MemoryAvailableMin { get; set; }
        public double? DiskLatencyAverage { get; set; }
    }

    private sealed class KeyPointRow
    {
        public DateTime Time { get; set; }
        public string Key { get; set; } = string.Empty;
        public long Count { get; set; }
        public double Sum { get; set; }
        public long Errors { get; set; }
    }

    private sealed class FingerprintRow
    {
        public string Hash { get; set; } = string.Empty;
        public string Sql { get; set; } = string.Empty;
        public string[] Callers { get; set; } = [];
    }

    private sealed class IndexRow
    {
        public string Table { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long Bytes { get; set; }
        public long Scans { get; set; }
        public bool Unique { get; set; }
        public bool Primary { get; set; }
        public string Columns { get; set; } = string.Empty;
        public string Shape { get; set; } = string.Empty;
    }

    private sealed class TimelineRow
    {
        public DateTime Time { get; set; }
        public string Kind { get; set; } = string.Empty;
        public long Count { get; set; }
        public long Errors { get; set; }
    }

    private sealed class TimelineBucketRow
    {
        public DateTime Time { get; set; }
        public string Kind { get; set; } = string.Empty;
        public int Bucket { get; set; }
        public long Count { get; set; }
    }

    private sealed class Accumulated
    {
        public long Count;
        public long Errors;
        public double Sum;
        public double Max;
        public long Bytes;
        public long DbCount;
        public double DbMs;
        public double ExternalMs;
        public long PeakCount;
        public readonly int[] Histogram = new int[PerfHistogram.BucketCount];
    }

    private sealed class SeriesRow
    {
        public string Component { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string SubKey { get; set; } = string.Empty;
        public long Count { get; set; }
        public long Errors { get; set; }
        public double Sum { get; set; }
        public double Max { get; set; }
        public long Bytes { get; set; }
        public long DbCount { get; set; }
        public double DbMs { get; set; }
        public double ExternalMs { get; set; }
        public long PeakCount { get; set; }
    }

    private sealed class BucketRow
    {
        public string Component { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string SubKey { get; set; } = string.Empty;
        public int Bucket { get; set; }
        public long Count { get; set; }
    }

    private sealed class OverlapBucketRow
    {
        public DateTime Minute { get; set; }
        public int Bucket { get; set; }
        public long Count { get; set; }
    }

    private sealed class OverlapLoopRow
    {
        public DateTime Minute { get; set; }
        public string Key { get; set; } = string.Empty;
    }

    private sealed class RuntimeRow
    {
        public string Component { get; set; } = string.Empty;
        public int Minutes { get; set; }
        public string? Version { get; set; }
        public double? CpuAverage { get; set; }
        public double? CpuMax { get; set; }
        public long WorkingSet { get; set; }
        public long Heap { get; set; }
        public double GcPauseMs { get; set; }
        public long Allocated { get; set; }
        public int Threads { get; set; }
        public int Queue { get; set; }
        public long Contentions { get; set; }
        public long Exceptions { get; set; }
        public int Connections { get; set; }
        public int PoolBusy { get; set; }
        public int PoolIdle { get; set; }
        public int PoolMax { get; set; }
        public long PoolTimeouts { get; set; }
        public double OverheadMs { get; set; }
        public int Cores { get; set; }
        public int DiagnoseMinutes { get; set; }
    }

    private sealed class HostRow
    {
        public int Minutes { get; set; }
        public int? Cores { get; set; }
        public double? CpuAverage { get; set; }
        public double? CpuP95 { get; set; }
        public double? StealAverage { get; set; }
        public double? StealMax { get; set; }
        public double? IowaitAverage { get; set; }
        public double? Load1Max { get; set; }
        public long? MemoryTotal { get; set; }
        public long? MemoryAvailableMin { get; set; }
        public long? SwapTotal { get; set; }
        public long? SwapFreeMin { get; set; }
        public double? DiskLatency { get; set; }
        public double? DiskUtilization { get; set; }
        public long? DiskTotal { get; set; }
        public long? DiskFree { get; set; }
        public double? Throttled { get; set; }
    }
}
