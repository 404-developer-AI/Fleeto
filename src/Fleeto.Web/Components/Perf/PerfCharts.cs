using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;
using Fleeto.Web.Services;

namespace Fleeto.Web.Components.Perf;

/// <summary>A value of a chart in one bucket: the line, and the band from low to high around it. Null where nothing was measured.</summary>
public readonly record struct PerfChartPoint(DateTime Time, double? Low, double? Value, double? High, int Count);

/// <summary>
/// The charts of Settings, Performance draw with the chart of the check history (an SVG line with a band, no chart library): these
/// helpers turn a timeline of the monitor into its view.
/// </summary>
public static class PerfCharts
{
    public static CheckHistoryView View(PerformanceView view, string name, string unit, IEnumerable<PerfChartPoint> points, double? warning = null,
        double? critical = null)
    {
        var byTime = points.GroupBy(p => p.Time).ToDictionary(g => g.Key, g => g.First());
        var all = new List<HistoryPoint>();
        for (var time = view.From; time < view.To; time += view.Bucket)
        {
            all.Add(byTime.TryGetValue(time, out var point) && point.Value is not null
                ? new HistoryPoint(time, point.Low ?? point.Value, point.Value, point.High ?? point.Value, Math.Max(1, point.Count), 0, 0)
                : new HistoryPoint(time, null, null, null, 0, 0, 0));
        }

        return new CheckHistoryView(Guid.Empty, name, CheckType.CpuUsage, string.Empty, [], new Dictionary<string, string>(), ThresholdKind.HigherIsWorse, unit,
            warning, critical, Range(view.Period), view.From, view.To, view.Bucket, all);
    }

    /// <summary>A flow per bucket: the 95th percentile as the line, the median to the 99th percentile as the band.</summary>
    public static CheckHistoryView Latency(PerformanceView view, string name, IEnumerable<PerfFlowPoint> points, double? threshold = null) =>
        View(view, name, "ms", points.Where(p => p.Flow.Count > 0)
            .Select(p => new PerfChartPoint(p.Time, p.Flow.P50, p.Flow.P95, p.Flow.P99, (int)Math.Min(int.MaxValue, p.Flow.Count))), threshold);

    /// <summary>Counts per bucket of the keys of a timeline, all keys or one, as a line without a band.</summary>
    public static CheckHistoryView Counts(PerformanceView view, string name, IEnumerable<PerfKeyPoint> points, Func<PerfKeyPoint, bool>? filter = null) =>
        View(view, name, string.Empty, points.Where(filter ?? (_ => true)).GroupBy(p => p.Time)
            .Select(g => new PerfChartPoint(g.Key, null, g.Sum(p => (double)p.Count), null, 1)));

    public static bool HasValues(CheckHistoryView view) => view.Points.Any(p => p.HasData);

    private static HistoryRange Range(PerfPeriod period) => period switch
    {
        PerfPeriod.Minutes15 or PerfPeriod.Hour1 => HistoryRange.Hour,
        PerfPeriod.Hours6 or PerfPeriod.Hours24 => HistoryRange.Day,
        _ => HistoryRange.Week
    };

    /// <summary>The share of a flow's time in the database, outside it in external calls, and the rest in its own code.</summary>
    public static (double Db, double External, double Own) Split(PerfSeries flow)
    {
        var db = flow.DbShare;
        var external = Math.Min(1 - db, flow.ExternalShare);
        return (db, external, Math.Max(0, 1 - db - external));
    }
}
