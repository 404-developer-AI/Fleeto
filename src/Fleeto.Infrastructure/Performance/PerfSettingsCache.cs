using Fleeto.Core.Performance;

namespace Fleeto.Infrastructure.Performance;

/// <summary>
/// The settings of the performance monitor as one immutable snapshot (PERFORMANCE.md §5). Hot paths read <see cref="Current"/>;
/// <see cref="PerfSettingsLoader"/> replaces it after a change is announced and every 30 seconds. Never a database read per call.
/// </summary>
public sealed class PerfSettingsCache
{
    private PerformanceSettings _current;

    public PerfSettingsCache(PerformanceSettings? initial = null) => _current = initial ?? PerformanceSettings.Default;

    /// <summary>A cache that stays on level Off.</summary>
    public static PerfSettingsCache Off { get; } = new(PerformanceSettings.Default with { Level = PerfLevel.Off });

    public PerformanceSettings Current => Volatile.Read(ref _current);

    public void Set(PerformanceSettings settings) => Volatile.Write(ref _current, settings);
}

/// <summary>The runtime of the current minute, added up from the samples every 10 seconds.</summary>
internal sealed class PerfRuntimeState
{
    private readonly Lock _lock = new();
    private int _samples;
    private double _cpuSum;
    private double _cpuMax;
    private long _workingSet;
    private long _heap;
    private double _gcPauseMs;
    private long _allocated;
    private int _threads;
    private int _queue;
    private long _contentions;
    private int _exceptions;
    private int _connections;
    private int _poolBusy;
    private int _poolIdle;
    private int _poolTimeouts;

    public void Add(RuntimeSample sample)
    {
        lock (_lock)
        {
            _samples++;
            _cpuSum += sample.CpuPercent;
            _cpuMax = Math.Max(_cpuMax, sample.CpuPercent);
            _workingSet = Math.Max(_workingSet, sample.WorkingSetBytes);
            _heap = Math.Max(_heap, sample.HeapBytes);
            _gcPauseMs += sample.GcPauseMs;
            _allocated += sample.AllocatedBytes;
            _threads = Math.Max(_threads, sample.ThreadPoolThreads);
            _queue = Math.Max(_queue, sample.ThreadPoolQueue);
            _contentions += sample.LockContentions;
            _exceptions += sample.Exceptions;
            _connections = Math.Max(_connections, sample.Connections);
            _poolBusy = Math.Max(_poolBusy, sample.PoolBusy);
            _poolIdle = Math.Max(_poolIdle, sample.PoolIdle);
            _poolTimeouts += sample.PoolTimeouts;
        }
    }

    /// <summary>The minute so far, and a fresh start for the next one. Null without a sample.</summary>
    public RuntimeMinute? Take()
    {
        lock (_lock)
        {
            if (_samples == 0)
            {
                return null;
            }

            var minute = new RuntimeMinute(_cpuSum / _samples, _cpuMax, _workingSet, _heap, _gcPauseMs, _allocated, _threads, _queue, _contentions,
                _exceptions, _connections, _poolBusy, _poolIdle, _poolTimeouts);
            _samples = 0;
            _cpuSum = _cpuMax = _gcPauseMs = 0;
            _workingSet = _heap = _allocated = _contentions = 0;
            _threads = _queue = _exceptions = _connections = _poolBusy = _poolIdle = _poolTimeouts = 0;
            return minute;
        }
    }
}

internal sealed record RuntimeSample(double CpuPercent, long WorkingSetBytes, long HeapBytes, double GcPauseMs, long AllocatedBytes, int ThreadPoolThreads,
    int ThreadPoolQueue, long LockContentions, int Exceptions, int Connections, int PoolBusy, int PoolIdle, int PoolTimeouts);

internal sealed record RuntimeMinute(double CpuPercent, double CpuPercentMax, long WorkingSetBytes, long HeapBytes, double GcPauseMs, long AllocatedBytes,
    int ThreadPoolThreads, int ThreadPoolQueue, long LockContentions, int Exceptions, int Connections, int PoolBusy, int PoolIdle, int PoolTimeouts);
