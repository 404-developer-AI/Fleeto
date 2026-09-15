using System.Globalization;
using System.Text;
using Fleeto.Web.Services;

namespace Fleeto.Web.Components.Shared;

/// <summary>
/// Geometry of the check history line chart, drawn as inline SVG (no chart library). Kept apart from the markup so it can be tested:
/// the average as line segments, the range between minimum and maximum as a band, threshold lines, and axis ticks. A bucket without
/// measurements breaks the line: a gap in the chart is a real gap.
/// </summary>
public sealed class HistoryChartLayout
{
    public const double Width = 800;
    public const double Height = 240;
    public const double Left = 56;
    public const double Right = 12;
    public const double Top = 12;
    public const double Bottom = 28;

    public HistoryChartLayout(IReadOnlyList<HistoryPoint> points, DateTime from, DateTime to, string unit, double? warning, double? critical)
    {
        Points = points;
        From = from;
        To = to;
        var values = points.Where(p => p.Min is not null).SelectMany(p => new[] { p.Min!.Value, p.Max!.Value }).ToList();
        values.AddRange(new[] { warning, critical }.Where(v => v is not null).Select(v => v!.Value));
        (YMin, YMax) = Scale(values, unit);
        Warning = warning;
        Critical = critical;
    }

    public IReadOnlyList<HistoryPoint> Points { get; }
    public DateTime From { get; }
    public DateTime To { get; }
    public double YMin { get; }
    public double YMax { get; }
    public double? Warning { get; }
    public double? Critical { get; }

    public double PlotWidth => Width - Left - Right;
    public double PlotHeight => Height - Top - Bottom;
    public bool HasValues => Points.Any(p => p.Avg is not null);

    public double X(DateTime time) => Left + (time - From).Ticks / (double)(To - From).Ticks * PlotWidth;

    public double Y(double value) => Top + (1 - (value - YMin) / (YMax - YMin)) * PlotHeight;

    /// <summary>Center of a bucket on the x axis.</summary>
    public double BucketX(int index) => X(Points[index].Time) + BucketWidth / 2;

    public double BucketWidth => Points.Count == 0 ? PlotWidth : PlotWidth / Points.Count;

    /// <summary>SVG path of the average: one "M ... L ..." run per stretch of buckets with measurements.</summary>
    public string AveragePath() => Runs(i => Points[i].Avg!.Value, forward: true);

    /// <summary>SVG path of the band between minimum and maximum, one closed shape per stretch.</summary>
    public string BandPath()
    {
        var path = new StringBuilder();
        foreach (var run in Stretches())
        {
            path.Append('M');
            foreach (var i in run)
            {
                path.Append(F(BucketX(i))).Append(',').Append(F(Y(Points[i].Max!.Value))).Append(' ');
            }

            foreach (var i in run.AsEnumerable().Reverse())
            {
                path.Append('L').Append(F(BucketX(i))).Append(',').Append(F(Y(Points[i].Min!.Value))).Append(' ');
            }

            path.Append("Z ");
        }

        return path.ToString().Trim();
    }

    /// <summary>Four values on the y axis.</summary>
    public IReadOnlyList<double> YTicks() => Enumerable.Range(0, 4).Select(i => YMin + (YMax - YMin) * i / 3).ToList();

    /// <summary>Five times on the x axis, from the start to the end of the range.</summary>
    public IReadOnlyList<DateTime> XTicks() => Enumerable.Range(0, 5).Select(i => From + (To - From) * i / 4).ToList();

    public static string F(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private string Runs(Func<int, double> value, bool forward)
    {
        var path = new StringBuilder();
        foreach (var run in Stretches())
        {
            for (var k = 0; k < run.Count; k++)
            {
                var i = run[k];
                path.Append(k == 0 ? 'M' : 'L').Append(F(BucketX(i))).Append(',').Append(F(Y(value(i)))).Append(' ');
            }

            if (run.Count == 1)
            {
                // A lone bucket: a short horizontal stroke, so it is visible.
                var i = run[0];
                path.Append('M').Append(F(BucketX(i) - 2)).Append(',').Append(F(Y(value(i)))).Append(" L").Append(F(BucketX(i) + 2)).Append(',')
                    .Append(F(Y(value(i)))).Append(' ');
            }
        }

        return path.ToString().Trim();
    }

    private IEnumerable<List<int>> Stretches()
    {
        var run = new List<int>();
        for (var i = 0; i < Points.Count; i++)
        {
            if (Points[i].Avg is not null)
            {
                run.Add(i);
                continue;
            }

            if (run.Count > 0)
            {
                yield return run;
                run = [];
            }
        }

        if (run.Count > 0)
        {
            yield return run;
        }
    }

    /// <summary>A y range with some room; percentages stay within 0 and 100, and counts, sizes and times start at 0.</summary>
    private static (double Min, double Max) Scale(IReadOnlyList<double> values, string unit)
    {
        if (unit == "%")
        {
            return (0, 100);
        }

        if (values.Count == 0)
        {
            return (0, 1);
        }

        var min = values.Min();
        var max = values.Max();
        var low = min >= 0 ? 0 : min - Math.Abs(min) * 0.1;
        var high = max <= low ? low + 1 : max + (max - low) * 0.1;
        return (low, high);
    }
}
