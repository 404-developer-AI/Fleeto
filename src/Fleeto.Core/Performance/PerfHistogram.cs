namespace Fleeto.Core.Performance;

/// <summary>
/// The fixed log-linear buckets of every duration the performance monitor keeps (0.7.0, PERFORMANCE.md §5). Fixed buckets make
/// histograms additive: minutes add up to hours and any period still gives percentiles exact to the bucket. A histogram is an
/// array of counts per bucket with its trailing zeros left out, so a narrow minute stays small in the database.
/// </summary>
public static class PerfHistogram
{
    /// <summary>Upper bound of every bucket in milliseconds, inclusive. The last bucket holds everything above the last bound.</summary>
    public static readonly IReadOnlyList<double> Bounds =
    [
        0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 5, 7.5, 10, 15, 20, 30, 40, 50, 75, 100, 150, 200, 300, 400, 500, 750,
        1_000, 1_500, 2_000, 3_000, 5_000, 7_500, 10_000, 15_000, 30_000, 60_000, 120_000, 300_000
    ];

    /// <summary>Bounds plus the open bucket above the last one.</summary>
    public static int BucketCount => Bounds.Count + 1;

    /// <summary>The bucket a value in milliseconds falls in.</summary>
    public static int BucketOf(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || milliseconds <= Bounds[0])
        {
            return 0;
        }

        // Binary search: the first bound at or above the value.
        int low = 0, high = Bounds.Count - 1;
        if (milliseconds > Bounds[high])
        {
            return Bounds.Count;
        }

        while (low < high)
        {
            var mid = (low + high) / 2;
            if (Bounds[mid] >= milliseconds)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }

    /// <summary>The lower bound of a bucket in milliseconds.</summary>
    public static double LowerBound(int bucket) => bucket <= 0 ? 0 : Bounds[Math.Min(bucket, Bounds.Count) - 1];

    /// <summary>The upper bound of a bucket; the open bucket answers its lower bound, the only value known about it.</summary>
    public static double UpperBound(int bucket) => bucket < Bounds.Count ? Bounds[bucket] : Bounds[^1];

    /// <summary>Counts without their trailing zeros, as stored.</summary>
    public static int[] Trim(ReadOnlySpan<int> counts)
    {
        var length = counts.Length;
        while (length > 0 && counts[length - 1] == 0)
        {
            length--;
        }

        return counts[..length].ToArray();
    }

    /// <summary>Adds <paramref name="add"/> into <paramref name="total"/>, which grows when needed. Lengths may differ.</summary>
    public static int[] Add(int[]? total, IReadOnlyList<int>? add)
    {
        total ??= [];
        if (add is null || add.Count == 0)
        {
            return total;
        }

        if (total.Length < add.Count)
        {
            Array.Resize(ref total, add.Count);
        }

        for (var i = 0; i < add.Count; i++)
        {
            total[i] += add[i];
        }

        return total;
    }

    public static long Total(IReadOnlyList<int>? counts)
    {
        long total = 0;
        if (counts is not null)
        {
            foreach (var count in counts)
            {
                total += count;
            }
        }

        return total;
    }

    /// <summary>
    /// The value below which <paramref name="quantile"/> (0 to 1) of the observations fall, interpolated linearly within its
    /// bucket and never above the largest value seen when that is known. Null without observations.
    /// </summary>
    public static double? Percentile(IReadOnlyList<int>? counts, double quantile, double? max = null)
    {
        var total = Total(counts);
        if (total == 0)
        {
            return null;
        }

        quantile = Math.Clamp(quantile, 0, 1);
        var rank = quantile * total;
        long seen = 0;
        for (var bucket = 0; bucket < counts!.Count; bucket++)
        {
            var count = counts[bucket];
            if (count == 0)
            {
                continue;
            }

            if (seen + count >= rank)
            {
                var lower = LowerBound(bucket);
                var upper = bucket < Bounds.Count ? Bounds[bucket] : max ?? lower;
                var fraction = (rank - seen) / count;
                var value = lower + (Math.Max(upper, lower) - lower) * Math.Clamp(fraction, 0, 1);
                return max is { } m ? Math.Min(value, m) : value;
            }

            seen += count;
        }

        return max ?? UpperBound(counts.Count - 1);
    }

    /// <summary>The mean, with every bucket counted at its middle. Null without observations.</summary>
    public static double? Mean(IReadOnlyList<int>? counts, double? max = null)
    {
        var total = Total(counts);
        if (total == 0)
        {
            return null;
        }

        double sum = 0;
        for (var bucket = 0; bucket < counts!.Count; bucket++)
        {
            if (counts[bucket] != 0)
            {
                sum += Middle(bucket, max) * counts[bucket];
            }
        }

        return sum / total;
    }

    /// <summary>
    /// The waiting time above <paramref name="thresholdMs"/>, summed over every observation: the impact of a slow flow. Each
    /// bucket counts at its middle, so the estimate is exact to the bucket.
    /// </summary>
    public static double TimeAbove(IReadOnlyList<int>? counts, double thresholdMs, double? max = null)
    {
        if (counts is null)
        {
            return 0;
        }

        double total = 0;
        for (var bucket = 0; bucket < counts.Count; bucket++)
        {
            if (counts[bucket] == 0)
            {
                continue;
            }

            var middle = Middle(bucket, max);
            if (middle > thresholdMs)
            {
                total += (middle - thresholdMs) * counts[bucket];
            }
        }

        return total;
    }

    private static double Middle(int bucket, double? max)
    {
        var lower = LowerBound(bucket);
        var upper = bucket < Bounds.Count ? Bounds[bucket] : Math.Max(max ?? lower, lower);
        return (lower + upper) / 2;
    }
}
