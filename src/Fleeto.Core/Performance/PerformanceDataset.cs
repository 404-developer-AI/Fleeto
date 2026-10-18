namespace Fleeto.Core.Performance;

/// <summary>
/// One flow over the period: every minute row of one component, kind, key and sub key added up. Values are milliseconds unless
/// the kind says otherwise (<see cref="PerfKind"/>). <see cref="PeakCount"/> is the busiest minute.
/// </summary>
public sealed record PerfSeries(
    string Component,
    PerfKind Kind,
    string Key,
    string SubKey,
    long Count,
    long Errors,
    double Sum,
    double Max,
    IReadOnlyList<int> Histogram,
    long Bytes,
    long DbCount,
    double DbMs,
    double ExternalMs,
    long PeakCount)
{
    public double Mean => Count == 0 ? 0 : Sum / Count;
    public double? P50 => PerfHistogram.Percentile(Histogram, 0.50, Max);
    public double? P95 => PerfHistogram.Percentile(Histogram, 0.95, Max);
    public double? P99 => PerfHistogram.Percentile(Histogram, 0.99, Max);
    public double DbShare => Sum <= 0 ? 0 : Math.Min(1, DbMs / Sum);
    public double ExternalShare => Sum <= 0 ? 0 : Math.Min(1, ExternalMs / Sum);
    public double DbPerCall => Count == 0 ? 0 : (double)DbCount / Count;
    public double ErrorShare => Count == 0 ? 0 : (double)Errors / Count;
}

/// <summary>The runtime of one component over the period.</summary>
/// <param name="Connections">Peak open circuits (web) or connected agents and watchdogs (gateway).</param>
/// <param name="OverheadMs">Time the monitor itself spent: collectors and flushes.</param>
public sealed record PerfRuntimeSummary(
    string Component,
    int Minutes,
    string? Version,
    double? CpuPercentAverage,
    double? CpuPercentMax,
    long WorkingSetMaxBytes,
    long HeapMaxBytes,
    double GcPausePercent,
    long AllocatedBytes,
    int ThreadPoolThreadsMax,
    int ThreadPoolQueueMax,
    long LockContentions,
    long Exceptions,
    int ConnectionsMax,
    int DbPoolBusyMax,
    int DbPoolIdleMax,
    int DbPoolMax,
    long DbPoolTimeouts,
    double OverheadMs,
    int Cores)
{
    /// <summary>CPU of the monitor as a share of one core over the measured minutes.</summary>
    public double OverheadCpuPercent => Minutes <= 0 ? 0 : OverheadMs / (Minutes * 60_000d) * 100;
}

/// <summary>The VPS over the period. Shared by every instance on it.</summary>
public sealed record PerfHostSummary(
    int Minutes,
    int? Cores,
    double? CpuAverage,
    double? CpuP95,
    double? StealAverage,
    double? StealMax,
    double? IowaitAverage,
    double? Load1Max,
    long? MemoryTotalBytes,
    long? MemoryAvailableMinBytes,
    long? SwapTotalBytes,
    long? SwapFreeMinBytes,
    double? DiskLatencyAverageMs,
    double? DiskUtilizationMaxPercent,
    long? DiskTotalBytes,
    long? DiskFreeBytes,
    double? ThrottledPercent);

/// <summary>A statement from pg_stat_statements, as the difference between the snapshot before the period and the last one in it.</summary>
public sealed record PerfStatementDelta(long QueryId, string Query, long Calls, double TotalMs, long Rows, long SharedBlocksRead, long TempBlocksWritten)
{
    public double MeanMs => Calls == 0 ? 0 : TotalMs / Calls;
}

/// <summary>The database over the period. <see cref="Baseline"/> is false without a snapshot before the period: the deltas are then unknown.</summary>
public sealed record PerfDatabaseSummary(
    bool Baseline,
    double? CacheHitRatio,
    long? TempFiles,
    long? TempBytes,
    long? Deadlocks,
    int Connections,
    int MaxConnections,
    long WraparoundAge,
    long DatabaseBytes);

/// <summary>A table in the last snapshot of the period, with deltas against the snapshot before it (null without a baseline).</summary>
public sealed record PerfTableSummary(
    string Name,
    long Bytes,
    long? BytesGrowth,
    long Rows,
    long? SequentialScans,
    long? SequentialRowsRead,
    long? IndexScans,
    long LiveRows,
    long DeadRows,
    DateTime? LastVacuum,
    DateTime? LastAnalyze,
    bool IsHypertable,
    bool Compressed,
    bool RetentionPolicy,
    int Chunks);

/// <summary>One minute for the overlap rule: the page interactions in it and the worker loops that ran.</summary>
public sealed record PerfOverlapMinute(DateTime Minute, IReadOnlyList<int> InteractionHistogram, long Interactions, IReadOnlyCollection<string> Loops);

/// <summary>A deploy or manual marker.</summary>
public sealed record PerfMarkerView(DateTime Time, string Kind, string Label);

/// <summary>
/// Everything the rules judge, for one period (PERFORMANCE.md §7). Built from the database by the reader; the rules never touch it.
/// <paramref name="StatementsProblem"/> and <paramref name="Missing"/> say what could not be measured, so absence of data is never
/// mistaken for absence of problems.
/// </summary>
public sealed record PerformanceDataset(
    DateTime From,
    DateTime To,
    PerformanceSettings Settings,
    IReadOnlyList<PerfSeries> Series,
    IReadOnlyList<PerfRuntimeSummary> Runtime,
    PerfHostSummary? Host,
    IReadOnlyList<PerfStatementDelta> Statements,
    string? StatementsProblem,
    PerfDatabaseSummary? Database,
    IReadOnlyList<PerfTableSummary> Tables,
    IReadOnlyList<PerfOverlapMinute> Minutes,
    IReadOnlyList<PerfMarkerView> Markers,
    IReadOnlyList<string> Missing)
{
    public TimeSpan Length => To - From;

    public IEnumerable<PerfSeries> Of(PerfKind kind) => Series.Where(s => s.Kind == kind);

    public static PerformanceDataset Empty(DateTime from, DateTime to) =>
        new(from, to, PerformanceSettings.Default, [], [], null, [], null, null, [], [], [], []);
}
