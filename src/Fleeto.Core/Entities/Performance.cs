using Fleeto.Core.Performance;

namespace Fleeto.Core.Entities;

// The performance monitor (0.7.0, PERFORMANCE.md §6). It measures Fleeto, never the endpoints: no table here belongs to a client,
// and no column holds a value of the data Fleeto manages. Keys are templates, type names and fixed names.

/// <summary>
/// One flow of one component in one minute, written once per minute by the component itself in one batch. A TimescaleDB
/// hypertable where the extension is installed. The value columns are milliseconds unless <see cref="Kind"/> says otherwise.
/// </summary>
public class PerfMinute
{
    public long Id { get; set; }
    public DateTime Minute { get; set; }

    /// <summary>web, gateway, workers or signer. A database trigger lets each role write only its own rows.</summary>
    public string Component { get; set; } = string.Empty;

    public PerfKind Kind { get; set; }
    public string Key { get; set; } = string.Empty;
    public string SubKey { get; set; } = string.Empty;
    public int Count { get; set; }
    public int Errors { get; set; }
    public double Sum { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }

    /// <summary>Counts per bucket of <see cref="PerfHistogram"/>, trailing zeros left out; empty for kinds without durations.</summary>
    public int[] Histogram { get; set; } = [];

    public long Bytes { get; set; }
    public int DbCount { get; set; }
    public double DbMs { get; set; }
    public double ExternalMs { get; set; }
}

/// <summary>The minutes of one hour added up by the workers, kept longer than the minutes. Rebuilt per hour, so a rollup is idempotent.</summary>
public class PerfHour
{
    public long Id { get; set; }
    public DateTime Hour { get; set; }
    public string Component { get; set; } = string.Empty;
    public PerfKind Kind { get; set; }
    public string Key { get; set; } = string.Empty;
    public string SubKey { get; set; } = string.Empty;
    public long Count { get; set; }
    public long Errors { get; set; }
    public double Sum { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public int[] Histogram { get; set; } = [];
    public long Bytes { get; set; }
    public long DbCount { get; set; }
    public double DbMs { get; set; }
    public double ExternalMs { get; set; }

    /// <summary>The busiest minute of the hour.</summary>
    public int PeakCount { get; set; }
}

/// <summary>The runtime of one component in one minute, with its version and level, so periods can be compared by version.</summary>
public class PerfRuntimeMinute
{
    public long Id { get; set; }
    public DateTime Minute { get; set; }
    public string Component { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public PerfLevel Level { get; set; }

    /// <summary>CPU of the process as a share of the whole machine (all cores), average and peak of the 10-second samples.</summary>
    public double CpuPercent { get; set; }

    public double CpuPercentMax { get; set; }
    public int Cores { get; set; }
    public long WorkingSetBytes { get; set; }
    public long HeapBytes { get; set; }
    public double GcPauseMs { get; set; }
    public long AllocatedBytes { get; set; }
    public int ThreadPoolThreads { get; set; }
    public int ThreadPoolQueue { get; set; }
    public long LockContentions { get; set; }
    public int Exceptions { get; set; }

    /// <summary>Peak open circuits (web) or connected agents and watchdogs (gateway); 0 elsewhere.</summary>
    public int Connections { get; set; }

    public int DbPoolBusy { get; set; }
    public int DbPoolIdle { get; set; }
    public int DbPoolMax { get; set; }
    public int DbPoolTimeouts { get; set; }

    /// <summary>Time the monitor spent in this process: recording, sampling and flushing.</summary>
    public double OverheadMs { get; set; }
}

/// <summary>The VPS in one minute, read by the workers from /proc. Shared by every instance on the VPS. Null where unknown.</summary>
public class PerfHostMinute
{
    public DateTime Minute { get; set; }
    public int? Cores { get; set; }
    public double? CpuPercent { get; set; }
    public double? StealPercent { get; set; }
    public double? IowaitPercent { get; set; }
    public double? Load1 { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public long? MemoryAvailableBytes { get; set; }
    public long? SwapTotalBytes { get; set; }
    public long? SwapFreeBytes { get; set; }
    public double? DiskLatencyMs { get; set; }
    public double? DiskUtilizationPercent { get; set; }
    public long? DiskTotalBytes { get; set; }
    public long? DiskFreeBytes { get; set; }
    public double? ThrottledPercent { get; set; }
}

/// <summary>A slow interaction, request or worker run kept on its own, with its split. No ids, no values.</summary>
public class PerfEvent
{
    public long Id { get; set; }
    public DateTime Time { get; set; }
    public string Component { get; set; } = string.Empty;
    public PerfKind Kind { get; set; }
    public string Key { get; set; } = string.Empty;
    public string SubKey { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public int DbCount { get; set; }
    public double DbMs { get; set; }
    public double ExternalMs { get; set; }
    public bool Failed { get; set; }

    /// <summary>
    /// In Diagnose, the heaviest statements of the event: up to three of <c>fingerprint count milliseconds</c>, separated by
    /// <c>;</c>. Null outside Diagnose.
    /// </summary>
    public string? Statements { get; set; }
}

/// <summary>
/// A statement one component measured in Diagnose (0.7.0 step 2): its fingerprint, its normalized text (every literal a
/// <c>?</c>) and the methods that sent it. Written once a minute by the component itself; a trigger keeps every role to its own rows.
/// </summary>
public class PerfFingerprint
{
    public string Component { get; set; } = string.Empty;

    /// <summary><see cref="Performance.SqlFingerprint.Hash"/> of <see cref="Sql"/>.</summary>
    public string Hash { get; set; } = string.Empty;

    public string Sql { get; set; } = string.Empty;

    /// <summary>Up to <see cref="MaxCallers"/> methods, as <c>Namespace.Type.Method</c>; never a value.</summary>
    public string[] Callers { get; set; } = [];

    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    public const int MaxCallers = 5;
}

/// <summary>
/// A statement from pg_stat_statements as the workers read it: cumulative since the statistics were reset, so the page computes
/// differences between snapshots. The text is PostgreSQL's normalized form (parameters as <c>$1</c>), normalized again.
/// </summary>
public class PerfStatementSnapshot
{
    public long Id { get; set; }
    public DateTime Time { get; set; }
    public long QueryId { get; set; }
    public string Query { get; set; } = string.Empty;
    public long Calls { get; set; }
    public double TotalMs { get; set; }
    public long Rows { get; set; }
    public long SharedBlocksHit { get; set; }
    public long SharedBlocksRead { get; set; }
    public long TempBlocksWritten { get; set; }
}

/// <summary>The database as a whole at one time. Counters are cumulative since their reset.</summary>
public class PerfDatabaseSnapshot
{
    public DateTime Time { get; set; }
    public long BlocksHit { get; set; }
    public long BlocksRead { get; set; }
    public long TempFiles { get; set; }
    public long TempBytes { get; set; }
    public long Deadlocks { get; set; }
    public long Commits { get; set; }
    public long Rollbacks { get; set; }
    public int Connections { get; set; }
    public int MaxConnections { get; set; }
    public long WraparoundAge { get; set; }
    public long DatabaseBytes { get; set; }
    public DateTime? StatisticsResetAt { get; set; }

    /// <summary>Why statement statistics were not read at this time, or null.</summary>
    public string? StatementsProblem { get; set; }

    // The sessions of the database at this time (0.7.0 step 2), read by the workers, which may see every role's sessions.
    // Null in snapshots of 0.7.0 step 1.
    public int? ActiveSessions { get; set; }
    public int? IdleSessions { get; set; }
    public int? IdleInTransactionSessions { get; set; }
    public double? LongestTransactionSeconds { get; set; }
    public double? LongestIdleInTransactionSeconds { get; set; }
    public int? LockWaitingSessions { get; set; }
    public double? LongestLockWaitSeconds { get; set; }
}

/// <summary>One table at one time, hypertables measured with their chunks. Counters are cumulative.</summary>
public class PerfTableSnapshot
{
    public DateTime Time { get; set; }
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

public enum PerfMarkerKind
{
    /// <summary>A component started on a new version.</summary>
    Deploy,

    /// <summary>An admin noted a change, such as an index added, so before and after are visible.</summary>
    Manual
}

/// <summary>A moment on the timeline, so before and after are visible.</summary>
public class PerfMarker
{
    public long Id { get; set; }
    public DateTime Time { get; set; }
    public PerfMarkerKind Kind { get; set; }
    public string Label { get; set; } = string.Empty;
}

/// <summary>The settings of the performance monitor: one row, <see cref="PerformanceSettings"/> as JSON.</summary>
public class PerformanceSettingsRow
{
    public int Id { get; set; }
    public string Json { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public enum PerfProbeKind
{
    /// <summary>The instance URL from outside: DNS, TCP, TLS, HTTP version, compression, caching and security headers.</summary>
    EdgeCheck,

    /// <summary>The VPS as the workers see it: CPU, writes to disk with fsync, and round trips to the database.</summary>
    Benchmark
}

/// <summary>
/// An edge check or benchmark an admin asked for (0.7.0 step 3). Web may not reach the internet, so it writes the request and the
/// workers run it and write the result back: <see cref="ItemsJson"/> with what was measured, or <see cref="Problem"/>.
/// </summary>
public class PerfProbe
{
    public Guid Id { get; set; }
    public PerfProbeKind Kind { get; set; }
    public DateTime RequestedAt { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>The results as a JSON array of <see cref="Performance.PerfProbeItem"/>; never a secret, a user or a value of an endpoint.</summary>
    public string? ItemsJson { get; set; }

    public string? Problem { get; set; }

    public const int MaxProblemLength = 500;
}
