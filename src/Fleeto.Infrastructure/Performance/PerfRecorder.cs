using System.Collections.Concurrent;
using System.Diagnostics;
using Fleeto.Core.Entities;
using Fleeto.Core.Performance;

namespace Fleeto.Infrastructure.Performance;

/// <summary>
/// The performance monitor of one process (0.7.0, PERFORMANCE.md §5). Collectors record into the window of the current minute;
/// nothing here writes to the database: <see cref="PerfFlusher"/> takes the window once a minute and writes it in one batch. Every
/// call first checks one immutable settings snapshot, so with the level Off or a collector switched off a call costs a field read.
/// The monitor measures its own cost and reports it with the runtime of the minute.
/// </summary>
public sealed class PerfRecorder
{
    /// <summary>Slow events kept per minute; beyond this they are counted, not stored.</summary>
    public const int MaxEventsPerMinute = 200;

    private readonly TimeProvider _time;
    private readonly ConcurrentQueue<PerfEvent> _events = new();
    private PerfWindow _window;
    private int _eventsThisMinute;
    private long _overheadTicks;
    private Func<int>? _connections;

    public PerfRecorder(string component, PerfSettingsCache settings, TimeProvider time)
    {
        Component = component;
        Settings = settings;
        _time = time;
        _window = new PerfWindow(MinuteOf(time.GetUtcNow().UtcDateTime));
        Runtime = new PerfRuntimeState();
    }

    /// <summary>A recorder that never records: before the host starts, in the tool, and in tests that do not measure.</summary>
    public static PerfRecorder Disabled { get; } = new("none", PerfSettingsCache.Off, TimeProvider.System);

    /// <summary>web, gateway, workers or signer.</summary>
    public string Component { get; }

    public PerfSettingsCache Settings { get; }

    internal PerfRuntimeState Runtime { get; }

    public bool Collects(PerfCollector collector) => Settings.Current.Collects(collector);

    /// <summary>The number of open circuits (web) or connected agents (gateway), sampled with the runtime.</summary>
    public void UseConnections(Func<int> connections) => _connections = connections;

    internal int ReadConnections()
    {
        try
        {
            return _connections?.Invoke() ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Records one occurrence of a flow with its value (milliseconds unless the kind says otherwise).</summary>
    public void Record(PerfCollector collector, PerfKind kind, string key, string subKey, double value, bool error = false, long bytes = 0,
        int dbCount = 0, double dbMs = 0, double externalMs = 0)
    {
        if (!Collects(collector))
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        Volatile.Read(ref _window).Add(kind, key, subKey, value, HasHistogram(kind), error, bytes, dbCount, dbMs, externalMs);
        AddOverhead(start);
    }

    /// <summary>Counts occurrences of a flow that has no duration: an exception, a connection, a circuit.</summary>
    public void Count(PerfCollector collector, PerfKind kind, string key, string subKey = "", int count = 1)
    {
        if (!Collects(collector) || count <= 0)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        Volatile.Read(ref _window).Add(kind, key, subKey, 0, histogram: false, error: false, bytes: 0, dbCount: 0, dbMs: 0, externalMs: 0, count: count);
        AddOverhead(start);
    }

    /// <summary>
    /// Records a scope that ended: its time, database work and external calls, and keeps it as a slow event when it took longer
    /// than the setting.
    /// </summary>
    public void Complete(PerfCollector collector, PerfKind kind, string key, string subKey, PerfScope scope, bool error, long bytes = 0)
    {
        scope.Dispose();
        if (!Collects(collector))
        {
            return;
        }

        var elapsed = scope.ElapsedMs;
        Record(collector, kind, key, subKey, elapsed, error, bytes, scope.DbCount, scope.DbMs, scope.ExternalMs);
        if (elapsed >= Settings.Current.SlowEventMs)
        {
            Event(kind, key, subKey, elapsed, scope.DbCount, scope.DbMs, scope.ExternalMs, error);
        }
    }

    /// <summary>Keeps one slow event, up to <see cref="MaxEventsPerMinute"/> a minute.</summary>
    public void Event(PerfKind kind, string key, string subKey, double durationMs, int dbCount, double dbMs, double externalMs, bool failed)
    {
        if (!Settings.Current.IsOn || Interlocked.Increment(ref _eventsThisMinute) > MaxEventsPerMinute)
        {
            return;
        }

        _events.Enqueue(new PerfEvent
        {
            Time = _time.GetUtcNow().UtcDateTime,
            Component = Component,
            Kind = kind,
            Key = PerfKeys.Key(key),
            SubKey = PerfKeys.SubKey(subKey),
            DurationMs = durationMs,
            DbCount = dbCount,
            DbMs = dbMs,
            ExternalMs = externalMs,
            Failed = failed
        });
    }

    /// <summary>Adds the time since <paramref name="startTimestamp"/> (a <see cref="Stopwatch"/> timestamp) to the monitor's own cost.</summary>
    public void AddOverhead(long startTimestamp) =>
        Interlocked.Add(ref _overheadTicks, Stopwatch.GetTimestamp() - startTimestamp);

    /// <summary>The monitor's own cost since the last call, in milliseconds.</summary>
    internal double TakeOverheadMs() => Interlocked.Exchange(ref _overheadTicks, 0) * 1000d / Stopwatch.Frequency;

    /// <summary>Starts a new window for <paramref name="minute"/> and returns the one that ends, for the flusher.</summary>
    internal PerfWindow Swap(DateTime minute)
    {
        Interlocked.Exchange(ref _eventsThisMinute, 0);
        return Interlocked.Exchange(ref _window, new PerfWindow(minute));
    }

    internal List<PerfEvent> TakeEvents()
    {
        var events = new List<PerfEvent>();
        while (events.Count < MaxEventsPerMinute * 2 && _events.TryDequeue(out var item))
        {
            events.Add(item);
        }

        return events;
    }

    public static DateTime MinuteOf(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private static bool HasHistogram(PerfKind kind) =>
        kind is not (PerfKind.Exception or PerfKind.Connection or PerfKind.Circuit or PerfKind.Broadcast);
}

/// <summary>The recorder of this process, for collectors that are not created by dependency injection (the base class of loops).</summary>
public static class Perf
{
    private static PerfRecorder _recorder = PerfRecorder.Disabled;

    public static PerfRecorder Recorder => Volatile.Read(ref _recorder);

    /// <summary>Set once by the host when the recorder is created; a test may set its own and put back <see cref="PerfRecorder.Disabled"/>.</summary>
    public static void Use(PerfRecorder recorder) => Volatile.Write(ref _recorder, recorder);
}
