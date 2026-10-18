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

    /// <summary>Of <see cref="Minutes"/>, those that ran in Diagnose, which may cost more (spec §8, monitor rule).</summary>
    public int DiagnoseMinutes { get; init; }
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
    long DatabaseBytes)
{
    /// <summary>The sessions at the last snapshot, or null when the snapshot did not read them (before 0.7.0 step 2).</summary>
    public PerfSessionsSummary? Sessions { get; init; }
}

/// <summary>
/// The sessions of the database at one snapshot, read by the workers (web may not see other roles' sessions). Ages in seconds.
/// </summary>
public sealed record PerfSessionsSummary(
    int Active,
    int Idle,
    int IdleInTransaction,
    double? LongestTransactionSeconds,
    double? LongestIdleInTransactionSeconds,
    int LockWaiting,
    double? LongestLockWaitSeconds);

/// <summary>A statement as the components measured it in Diagnose: its normalized text and the methods that sent it.</summary>
public sealed record PerfFingerprintView(string Hash, string Sql, IReadOnlyList<string> Callers);

/// <summary>One of the heaviest statements of a slow event: its fingerprint, how often it ran in the event and its time.</summary>
public sealed record PerfEventStatement(string Hash, int Count, double Ms);

/// <summary>A slow interaction, request, run or message kept on its own, with its split; in Diagnose with its heaviest statements.</summary>
public sealed record PerfEventView(
    DateTime Time,
    string Component,
    PerfKind Kind,
    string Key,
    string SubKey,
    double DurationMs,
    int DbCount,
    double DbMs,
    double ExternalMs,
    bool Failed,
    IReadOnlyList<PerfEventStatement> Statements);

/// <summary>An index of a table with its size and how often it was used since the statistics were reset.</summary>
/// <param name="DuplicateOf">Another index of the same table on the same columns in the same order, or null.</param>
public sealed record PerfIndexView(string Table, string Name, long Bytes, long Scans, bool Unique, bool Primary, string Columns, string? DuplicateOf);

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

/// <summary>Page interactions in minutes a loop ran against minutes it did not, and whether there was enough to judge.</summary>
public sealed record PerfOverlap(
    string Loop,
    int MinutesWith,
    int MinutesWithout,
    long InteractionsWith,
    long InteractionsWithout,
    double? P95With,
    double? P95Without,
    double? MeanWith,
    double? MeanWithout,
    bool Judged);

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

    /// <summary>Statements measured in Diagnose by fingerprint: their text and callers. Empty when Diagnose did not run.</summary>
    public IReadOnlyDictionary<string, PerfFingerprintView> Fingerprints { get; init; } = new Dictionary<string, PerfFingerprintView>();

    /// <summary>The slowest events of the period, slowest first.</summary>
    public IReadOnlyList<PerfEventView> Events { get; init; } = [];

    /// <summary>The indexes of the tables, read live when the dataset was built; empty when they could not be read.</summary>
    public IReadOnlyList<PerfIndexView> Indexes { get; init; } = [];

    /// <summary>Minutes of the period in which a component ran in Diagnose.</summary>
    public int DiagnoseMinutes { get; init; }

    /// <summary>The share of the period that ran in Diagnose, from 0 to 1.</summary>
    public double DiagnoseShare => Length.TotalMinutes <= 0 ? 0 : Math.Min(1, DiagnoseMinutes / Length.TotalMinutes);

    /// <summary>The fingerprint of a statement, or null when it was not stored.</summary>
    public PerfFingerprintView? Fingerprint(string hash) => Fingerprints.GetValueOrDefault(hash);

    public IEnumerable<PerfSeries> Of(PerfKind kind) => Series.Where(s => s.Kind == kind);

    public static PerformanceDataset Empty(DateTime from, DateTime to) =>
        new(from, to, PerformanceSettings.Default, [], [], null, [], null, null, [], [], [], []);
}
