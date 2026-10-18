using System.Diagnostics;
using Fleeto.Core.Performance;
using Microsoft.Extensions.Hosting;

namespace Fleeto.Infrastructure.Performance;

/// <summary>
/// Samples the runtime of the process every 10 seconds (PERFORMANCE.md §4.6): CPU as a share of the whole machine, working set,
/// heap, garbage collection pauses and allocations, thread pool, lock contention, exceptions, open connections and the database
/// pool. The flusher writes the minute as one row with the version and level.
/// </summary>
public sealed class PerfRuntimeSampler : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly PerfRecorder _recorder;
    private readonly PerfMeterListener _meters;
    private readonly TimeProvider _time;
    private Snapshot? _previous;

    public PerfRuntimeSampler(PerfRecorder recorder, PerfMeterListener meters, TimeProvider time)
    {
        _recorder = recorder;
        _meters = meters;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meters.Start();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Sample();
                await Task.Delay(Interval, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>Takes one sample; the first only sets the baseline of the counters.</summary>
    internal void Sample()
    {
        var start = Stopwatch.GetTimestamp();
        var now = Snapshot.Take(_meters);
        var previous = _previous;
        _previous = now;
        if (previous is null || !_recorder.Collects(PerfCollector.Runtime))
        {
            _recorder.AddOverhead(start);
            return;
        }

        _meters.Observe();
        var elapsed = (now.Timestamp - previous.Timestamp) / (double)Stopwatch.Frequency;
        var cpu = elapsed <= 0 ? 0 : (now.Cpu - previous.Cpu).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100;
        var (used, idle) = _meters.Pool;
        _recorder.Runtime.Add(new RuntimeSample(
            Math.Clamp(cpu, 0, 100),
            Environment.WorkingSet,
            GC.GetTotalMemory(false),
            (now.GcPause - previous.GcPause).TotalMilliseconds,
            Math.Max(0, now.Allocated - previous.Allocated),
            ThreadPool.ThreadCount,
            (int)Math.Min(int.MaxValue, ThreadPool.PendingWorkItemCount),
            Math.Max(0, now.Contentions - previous.Contentions),
            (int)Math.Clamp(now.Exceptions - previous.Exceptions, 0, int.MaxValue),
            _recorder.ReadConnections(),
            Math.Max(0, used),
            Math.Max(0, idle),
            (int)Math.Clamp(now.PoolTimeouts - previous.PoolTimeouts, 0, int.MaxValue)));
        _recorder.AddOverhead(start);
    }

    private sealed record Snapshot(long Timestamp, TimeSpan Cpu, TimeSpan GcPause, long Allocated, long Contentions, long Exceptions, long PoolTimeouts)
    {
        public static Snapshot Take(PerfMeterListener meters) => new(
            Stopwatch.GetTimestamp(),
            Environment.CpuUsage.TotalTime,
            GC.GetTotalPauseDuration(),
            GC.GetTotalAllocatedBytes(),
            Monitor.LockContentionCount,
            meters.ExceptionsTotal,
            meters.PoolTimeoutsTotal);
    }
}
