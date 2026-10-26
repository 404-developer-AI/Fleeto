namespace Fleeto.Core.Performance;

/// <summary>What a period is compared with (0.7.0 step 3, spec §8, Comparisons).</summary>
public enum PerfComparisonKind
{
    /// <summary>The period of equal length just before it.</summary>
    PreviousPeriod,

    /// <summary>The time the version before the current one ran, within the period: before and after a deploy.</summary>
    Version
}

/// <summary>One flow in both parts of a comparison. A change is the 95th percentile of now against before, as a share.</summary>
public sealed record PerfComparisonRow(PerfKind Kind, string Key, string SubKey, long BeforeCount, double? BeforeP95, long NowCount, double? NowP95)
{
    /// <summary>The change of the 95th percentile, 0.25 for 25 % slower, or null when one side is unknown.</summary>
    public double? Change => BeforeP95 is > 0 and var before && NowP95 is { } now ? (now - before) / before : null;
}

/// <summary>
/// A comparison of the flows of two stretches of time. <paramref name="Before"/> and <paramref name="Now"/> name them: two version
/// numbers, or "previous period" and "this period".
/// </summary>
public sealed record PerfComparison(
    PerfComparisonKind Kind,
    string Before,
    string Now,
    DateTime BeforeFrom,
    DateTime BeforeTo,
    DateTime NowFrom,
    DateTime NowTo,
    IReadOnlyList<PerfComparisonRow> Rows)
{
    /// <summary>The kinds of flows a comparison holds: what users and agents wait for, and the loops.</summary>
    public static readonly IReadOnlyList<PerfKind> Kinds = [PerfKind.Interaction, PerfKind.Http, PerfKind.WorkerRun, PerfKind.AgentMessage, PerfKind.Vital];

    /// <summary>Rows kept per comparison: those that changed most, of flows with enough samples on both sides.</summary>
    public const int MaxRows = 30;

    /// <summary>
    /// Compares the flows of two stretches: per flow (component left out, so a page measured by one web is one row) the 95th
    /// percentile on both sides, where both have at least <paramref name="minSamples"/>, the largest changes first.
    /// </summary>
    public static IReadOnlyList<PerfComparisonRow> Compare(IReadOnlyList<PerfSeries> before, IReadOnlyList<PerfSeries> now, double minSamples)
    {
        static Dictionary<(PerfKind, string, string), (long Count, double? P95)> Group(IReadOnlyList<PerfSeries> series) =>
            series.Where(s => Kinds.Contains(s.Kind) && !(s.Kind == PerfKind.Vital && s.SubKey == PerfVitals.View))
                .GroupBy(s => (s.Kind, s.Key, s.SubKey))
                .ToDictionary(g => g.Key, g =>
                {
                    var histogram = g.Aggregate((int[]?)null, (total, s) => PerfHistogram.Add(total, s.Histogram)) ?? [];
                    return (g.Sum(s => s.Count), PerfHistogram.Percentile(histogram, 0.95, g.Max(s => s.Max)));
                });

        var old = Group(before);
        var current = Group(now);
        return current
            .Where(pair => pair.Value.Count >= minSamples && old.TryGetValue(pair.Key, out var o) && o.Count >= minSamples)
            .Select(pair =>
            {
                var o = old[pair.Key];
                return new PerfComparisonRow(pair.Key.Item1, pair.Key.Item2, pair.Key.Item3, o.Count, o.P95, pair.Value.Count, pair.Value.P95);
            })
            .OrderByDescending(r => Math.Abs(r.Change ?? 0))
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(MaxRows)
            .ToList();
    }
}
