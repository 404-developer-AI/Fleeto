using System.Collections.Concurrent;
using Fleeto.Core.Performance;

namespace Fleeto.Infrastructure.Performance;

/// <summary>A flow in the window: kind, key and sub key. Keys are templates and fixed names, never values.</summary>
public readonly record struct PerfSeriesKey(PerfKind Kind, string Key, string SubKey);

/// <summary>What one flow did in the window, read when the window is flushed.</summary>
public sealed record PerfWindowRow(PerfSeriesKey Series, int Count, int Errors, double Sum, double Min, double Max, int[] Histogram, long Bytes,
    int DbCount, double DbMs, double ExternalMs);

/// <summary>
/// The measurements of one minute in one process (PERFORMANCE.md §5): an aggregate per flow, updated without locks. Values are
/// kept as integers in thousandths (microseconds for durations), so sums and extremes are exact under concurrency. Every kind
/// holds at most <see cref="PerfKeys.MaxKeysPerKind"/> flows; once full, new ones are counted under <see cref="PerfKeys.Other"/>.
/// </summary>
public sealed class PerfWindow
{
    private readonly ConcurrentDictionary<PerfSeriesKey, Accumulator> _series = new();
    private readonly int[] _keysPerKind = new int[Enum.GetValues<PerfKind>().Length];

    public PerfWindow(DateTime minute) => Minute = minute;

    /// <summary>The minute the window started in (UTC, seconds zero).</summary>
    public DateTime Minute { get; }

    public int SeriesCount => _series.Count;

    public void Add(PerfKind kind, string key, string subKey, double value, bool histogram, bool error, long bytes, int dbCount, double dbMs,
        double externalMs, int count = 1)
    {
        var accumulator = Get(kind, key, subKey, histogram);
        accumulator.Add(value, error, bytes, dbCount, dbMs, externalMs, count);
    }

    private Accumulator Get(PerfKind kind, string key, string subKey, bool histogram)
    {
        var series = new PerfSeriesKey(kind, PerfKeys.Key(key), PerfKeys.SubKey(subKey));
        if (_series.TryGetValue(series, out var existing))
        {
            return existing;
        }

        // A full kind counts new flows under one overflow key, so a key that carries a value by mistake cannot grow the window.
        if (Volatile.Read(ref _keysPerKind[(int)kind]) >= PerfKeys.MaxKeysPerKind)
        {
            series = new PerfSeriesKey(kind, PerfKeys.Other, string.Empty);
            return _series.GetOrAdd(series, _ => new Accumulator(histogram));
        }

        return _series.GetOrAdd(series, _ =>
        {
            Interlocked.Increment(ref _keysPerKind[(int)kind]);
            return new Accumulator(histogram);
        });
    }

    public IReadOnlyList<PerfWindowRow> Rows() => _series.Select(pair => pair.Value.ToRow(pair.Key)).Where(r => r.Count > 0).ToList();

    private sealed class Accumulator
    {
        private readonly int[]? _buckets;
        private int _count;
        private int _errors;
        private long _sum;
        private long _min = long.MaxValue;
        private long _max = long.MinValue;
        private long _bytes;
        private int _dbCount;
        private long _dbMicros;
        private long _externalMicros;

        public Accumulator(bool histogram) => _buckets = histogram ? new int[PerfHistogram.BucketCount] : null;

        public void Add(double value, bool error, long bytes, int dbCount, double dbMs, double externalMs, int count)
        {
            Interlocked.Add(ref _count, count);
            if (error)
            {
                Interlocked.Increment(ref _errors);
            }

            var scaled = (long)Math.Round(value * 1000);
            Interlocked.Add(ref _sum, scaled * count);
            Extreme(ref _min, scaled, smaller: true);
            Extreme(ref _max, scaled, smaller: false);
            if (_buckets is not null)
            {
                Interlocked.Add(ref _buckets[PerfHistogram.BucketOf(value)], count);
            }

            if (bytes != 0)
            {
                Interlocked.Add(ref _bytes, bytes);
            }

            if (dbCount != 0)
            {
                Interlocked.Add(ref _dbCount, dbCount);
                Interlocked.Add(ref _dbMicros, (long)(dbMs * 1000));
            }

            if (externalMs != 0)
            {
                Interlocked.Add(ref _externalMicros, (long)(externalMs * 1000));
            }
        }

        private static void Extreme(ref long target, long value, bool smaller)
        {
            var current = Volatile.Read(ref target);
            while (smaller ? value < current : value > current)
            {
                var seen = Interlocked.CompareExchange(ref target, value, current);
                if (seen == current)
                {
                    return;
                }

                current = seen;
            }
        }

        public PerfWindowRow ToRow(PerfSeriesKey key)
        {
            var count = Volatile.Read(ref _count);
            var min = Volatile.Read(ref _min);
            var max = Volatile.Read(ref _max);
            return new PerfWindowRow(key, count, Volatile.Read(ref _errors), Interlocked.Read(ref _sum) / 1000d,
                min == long.MaxValue ? 0 : min / 1000d, max == long.MinValue ? 0 : max / 1000d,
                _buckets is null ? [] : PerfHistogram.Trim(_buckets), Interlocked.Read(ref _bytes), Volatile.Read(ref _dbCount),
                Interlocked.Read(ref _dbMicros) / 1000d, Interlocked.Read(ref _externalMicros) / 1000d);
        }
    }
}
