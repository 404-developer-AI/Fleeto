using System.Diagnostics;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Performance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Fleeto.Infrastructure.Performance;

/// <summary>
/// Writes the window of the performance monitor once a minute (PERFORMANCE.md §5, spec §2: never a write per request): on every
/// minute boundary the window is swapped for a fresh one and its rows go to the database in one binary COPY, with the runtime
/// row and the slow events of the minute. On shutdown the unfinished minute is written too. A failed write is dropped with a
/// warning: measurements are never worth retrying at the cost of the work they measure.
/// </summary>
public sealed class PerfFlusher : BackgroundService
{
    /// <summary>Writers that took the old window just before the swap get this long to finish.</summary>
    private static readonly TimeSpan SwapGrace = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(15);

    private readonly PerfRecorder _recorder;
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;
    private readonly ILogger<PerfFlusher> _logger;
    private readonly int _poolMax;
    private DateTime _lastWarning = DateTime.MinValue;

    public PerfFlusher(PerfRecorder recorder, NpgsqlDataSource dataSource, TimeProvider time, ILogger<PerfFlusher> logger)
    {
        _recorder = recorder;
        _dataSource = dataSource;
        _time = time;
        _logger = logger;
        _poolMax = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).MaxPoolSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var next = PerfRecorder.MinuteOf(now).AddMinutes(1);
            try
            {
                await Task.Delay(next - now, _time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await FlushAsync(next, CancellationToken.None);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // The unfinished minute: a restart within the minute then writes it twice, and the reader adds both up.
        await FlushAsync(PerfRecorder.MinuteOf(_time.GetUtcNow().UtcDateTime).AddMinutes(1), cancellationToken);
    }

    /// <summary>Swaps the window and writes the one that ends. <paramref name="nextMinute"/> is the minute of the new window.</summary>
    public async Task FlushAsync(DateTime nextMinute, CancellationToken cancellationToken)
    {
        var window = _recorder.Swap(nextMinute);
        await Task.Delay(SwapGrace, CancellationToken.None).ConfigureAwait(false);

        var start = Stopwatch.GetTimestamp();
        var settings = _recorder.Settings.Current;
        var rows = window.Rows();
        var runtime = _recorder.Runtime.Take();
        var events = _recorder.TakeEvents();
        var fingerprints = _recorder.Statements.TakeSeen();
        if (!settings.IsOn && rows.Count == 0)
        {
            _recorder.TakeOverheadMs();
            return;
        }

        using var scope = PerfContext.Begin(PerfKeys.MonitorSource);
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await WriteMinuteAsync(connection, window.Minute, rows, cancellationToken);
            if (runtime is not null)
            {
                // A minute in which statements were measured ran in Diagnose, however short; else the level of its middle.
                var level = rows.Any(r => r.Series.Kind == PerfKind.Statement) ? PerfLevel.Diagnose : _recorder.LevelAt(window.Minute.AddSeconds(30));
                await WriteRuntimeAsync(connection, window.Minute, runtime, level, cancellationToken);
            }

            await WriteEventsAsync(connection, events, cancellationToken);
            await WriteFingerprintsAsync(connection, window.Minute, fingerprints, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (now - _lastWarning >= WarningInterval)
            {
                _lastWarning = now;
                _logger.LogWarning(ex, "The performance measurements of {Minute:HH:mm} could not be written and were dropped", window.Minute);
            }
        }
        finally
        {
            // The flush counts toward the monitor's own cost of the next minute.
            _recorder.AddOverhead(start);
        }
    }

    private async Task WriteMinuteAsync(NpgsqlConnection connection, DateTime minute, IReadOnlyList<PerfWindowRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var writer = await connection.BeginBinaryImportAsync("""
            COPY "PerfMinutes" ("Minute", "Component", "Kind", "Key", "SubKey", "Count", "Errors", "Sum", "Min", "Max", "Histogram", "Bytes",
                                "DbCount", "DbMs", "ExternalMs") FROM STDIN (FORMAT BINARY)
            """, cancellationToken);
        foreach (var row in rows)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(minute, NpgsqlDbType.TimestampTz, cancellationToken);
            await writer.WriteAsync(_recorder.Component, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(row.Series.Kind.ToString(), NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(row.Series.Key, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(row.Series.SubKey, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(row.Count, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(row.Errors, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(row.Sum, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(row.Min, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(row.Max, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(row.Histogram, NpgsqlDbType.Array | NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(row.Bytes, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(row.DbCount, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(row.DbMs, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(row.ExternalMs, NpgsqlDbType.Double, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private async Task WriteRuntimeAsync(NpgsqlConnection connection, DateTime minute, RuntimeMinute runtime, PerfLevel level, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO "PerfRuntimeMinutes" ("Minute", "Component", "Version", "Level", "CpuPercent", "CpuPercentMax", "Cores", "WorkingSetBytes",
                "HeapBytes", "GcPauseMs", "AllocatedBytes", "ThreadPoolThreads", "ThreadPoolQueue", "LockContentions", "Exceptions", "Connections",
                "DbPoolBusy", "DbPoolIdle", "DbPoolMax", "DbPoolTimeouts", "OverheadMs")
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = minute, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = _recorder.Component });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = FleetoVersion.Current.Length <= 50 ? FleetoVersion.Current : FleetoVersion.Current[..50] });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = level.ToString() });
        command.Parameters.Add(new NpgsqlParameter<double> { TypedValue = runtime.CpuPercent });
        command.Parameters.Add(new NpgsqlParameter<double> { TypedValue = runtime.CpuPercentMax });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Environment.ProcessorCount });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = runtime.WorkingSetBytes });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = runtime.HeapBytes });
        command.Parameters.Add(new NpgsqlParameter<double> { TypedValue = runtime.GcPauseMs });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = runtime.AllocatedBytes });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = runtime.ThreadPoolThreads });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = runtime.ThreadPoolQueue });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = runtime.LockContentions });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = runtime.Exceptions });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = runtime.Connections });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = runtime.PoolBusy });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = runtime.PoolIdle });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = _poolMax });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = runtime.PoolTimeouts });
        command.Parameters.Add(new NpgsqlParameter<double> { TypedValue = _recorder.TakeOverheadMs() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteEventsAsync(NpgsqlConnection connection, IReadOnlyList<PerfEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        await using var writer = await connection.BeginBinaryImportAsync("""
            COPY "PerfEvents" ("Time", "Component", "Kind", "Key", "SubKey", "DurationMs", "DbCount", "DbMs", "ExternalMs", "Failed", "Statements")
            FROM STDIN (FORMAT BINARY)
            """, cancellationToken);
        foreach (var e in events)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(e.Time, NpgsqlDbType.TimestampTz, cancellationToken);
            await writer.WriteAsync(e.Component, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(e.Kind.ToString(), NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(e.Key, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(e.SubKey, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(e.DurationMs, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(e.DbCount, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(e.DbMs, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(e.ExternalMs, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(e.Failed, NpgsqlDbType.Boolean, cancellationToken);
            if (e.Statements is null)
            {
                await writer.WriteNullAsync(cancellationToken);
            }
            else
            {
                await writer.WriteAsync(e.Statements, NpgsqlDbType.Varchar, cancellationToken);
            }
        }

        await writer.CompleteAsync(cancellationToken);
    }

    /// <summary>
    /// The statements seen in the minute (Diagnose), one upsert: new ones are added, known ones get the minute as last seen and the
    /// callers found since, up to five. Callers travel as one text per statement, separated by a bar no method name contains.
    /// </summary>
    private async Task WriteFingerprintsAsync(NpgsqlConnection connection, DateTime minute, IReadOnlyList<PerfStatements.Fingerprint> fingerprints,
        CancellationToken cancellationToken)
    {
        if (fingerprints.Count == 0)
        {
            return;
        }

        // LIMIT 5 is PerfFingerprint.MaxCallers.
        await using var command = new NpgsqlCommand("""
            INSERT INTO "PerfFingerprints" ("Component", "Hash", "Sql", "Callers", "FirstSeen", "LastSeen")
            SELECT $1, t.hash, t.sql, CASE WHEN t.callers = '' THEN ARRAY[]::text[] ELSE string_to_array(t.callers, '|') END, $5, $5
            FROM unnest($2::text[], $3::text[], $4::text[]) AS t(hash, sql, callers)
            ON CONFLICT ("Component", "Hash") DO UPDATE SET
              "LastSeen" = GREATEST("PerfFingerprints"."LastSeen", EXCLUDED."LastSeen"),
              "Callers" = COALESCE((SELECT array_agg(c ORDER BY c) FROM (
                  SELECT DISTINCT c FROM unnest("PerfFingerprints"."Callers" || EXCLUDED."Callers") AS c ORDER BY c LIMIT 5) AS callers),
                ARRAY[]::text[])
            """, connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = _recorder.Component });
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = fingerprints.Select(f => f.Hash).ToArray() });
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = fingerprints.Select(f => f.Sql).ToArray() });
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = fingerprints.Select(f => string.Join('|', f.Callers)).ToArray() });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = minute, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
