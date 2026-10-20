using System.Globalization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Web.Components.Shared;
using Fleeto.Web.Services;

namespace Fleeto.Web.Reports;

/// <summary>
/// The process list of a CPU or memory usage result as the browser draws it into an image (0.7.0, <c>fleeto.copyProcessImage</c>):
/// every text is formatted here and the chart comes from the same <see cref="HistoryChartLayout"/> as the check history, so the
/// image says exactly what the check history says.
/// </summary>
public sealed record ProcessListReportImage(
    string FileName,
    string Title,
    string Hostname,
    string Owner,
    string Check,
    string Value,
    string Measured,
    string? Thresholds,
    ProcessListReportImage.ChartImage Chart,
    bool Cpu,
    string ProcessesTitle,
    IReadOnlyList<ProcessListReportImage.ProcessLine> Processes,
    string Empty,
    string Footer)
{
    /// <param name="CpuShare">The CPU share of the machine from 0 to 1, for the bar; 0 in the list of a memory usage check.</param>
    public sealed record ProcessLine(string Name, string Pid, string User, string Cpu, double CpuShare, string Memory);

    /// <summary>
    /// The chart of the period in the coordinates of <see cref="HistoryChartLayout"/> (<see cref="Width"/> by <see cref="Height"/>),
    /// which the browser scales into the image. Paths are SVG path data.
    /// </summary>
    public sealed record ChartImage(
        string Title,
        double Width,
        double Height,
        double Left,
        double Right,
        double Top,
        double PlotHeight,
        string Band,
        string Average,
        IReadOnlyList<Tick> YTicks,
        IReadOnlyList<Tick> XTicks,
        double? WarningY,
        double? CriticalY,
        IReadOnlyList<Span> Problems,
        IReadOnlyList<Marker> Markers);

    /// <param name="At">The y of a value tick or the x of a time tick.</param>
    public sealed record Tick(double At, string Label);

    public sealed record Span(double X, double Width);

    public sealed record Marker(double X, double Y, bool Selected);

    /// <summary>The image of <paramref name="report"/>; <paramref name="time"/> writes a moment in the user's own time zone.</summary>
    public static ProcessListReportImage From(ProcessListReport report, Func<DateTime, string> time, TimeZoneInfo zone)
    {
        var cpu = report.Type == CheckType.CpuUsage;
        var what = cpu ? "CPU" : "memory";
        var thresholds = new List<string>();
        if (report.WarningThreshold is { } warning)
        {
            thresholds.Add($"warning at {Ui.CheckValue(report.Type, warning)}");
        }

        if (report.CriticalThreshold is { } critical)
        {
            thresholds.Add($"critical at {Ui.CheckValue(report.Type, critical)}");
        }

        return new ProcessListReportImage(
            report.FileName("png"),
            cpu ? "CPU report" : "Memory report",
            report.Hostname,
            string.Join(" · ", new[] { report.ClientCode, report.ClientName, report.SiteName }.Where(p => p.Length > 0)),
            report.CheckName,
            Ui.CheckValue(report.Type, report.Snapshot.Value),
            $"Measured {time(report.Snapshot.At)}",
            thresholds.Count > 0 ? char.ToUpperInvariant(thresholds[0][0]) + string.Join(" · ", thresholds)[1..] : null,
            ChartOf(report, cpu ? "CPU" : "Memory", zone),
            cpu,
            $"Processes using the most {what}",
            [.. report.Snapshot.Processes.Select(p => new ProcessLine(
                p.Name,
                p.Pid.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(p.User) ? "-" : p.User,
                (p.CpuPercent ?? 0).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                Math.Clamp((p.CpuPercent ?? 0) / 100, 0, 1),
                Ui.Bytes(p.MemoryBytes)))],
            "The agent listed no processes.",
            $"Fleeto · by Steaan · {report.Hostname} · generated {time(report.GeneratedAt)} by {report.GeneratedBy}");
    }

    private static ChartImage ChartOf(ProcessListReport report, string what, TimeZoneInfo zone)
    {
        var view = report.History;
        var layout = new HistoryChartLayout(view.Points, view.From, view.To, view.Unit, view.WarningThreshold, view.CriticalThreshold, view.Interval);
        var period = view.Range switch
        {
            HistoryRange.Hour => "the last hour",
            HistoryRange.Day => "the last day",
            HistoryRange.Week => "the last week",
            HistoryRange.Month => "the last month",
            _ => "the last year"
        };
        return new ChartImage(
            $"{what} over {period}",
            HistoryChartLayout.Width,
            HistoryChartLayout.Height,
            HistoryChartLayout.Left,
            HistoryChartLayout.Right,
            HistoryChartLayout.Top,
            layout.PlotHeight,
            layout.HasValues ? layout.BandPath() : string.Empty,
            layout.HasValues ? layout.AveragePath() : string.Empty,
            [.. layout.YTicks().Select(t => new Tick(layout.Y(t), HistoryChartLayout.ValueLabel(t, view.Unit)))],
            [.. layout.XTicks().Select(t => new Tick(layout.X(t), HistoryChartLayout.TimeLabel(t, view.Range, zone)))],
            view.WarningThreshold is { } warning ? layout.Y(warning) : null,
            view.CriticalThreshold is { } critical ? layout.Y(critical) : null,
            [.. view.Points.Where(p => p.Errors + p.NoResponses > 0).Select(p => new Span(layout.X(p.Time), Math.Max(1, layout.BucketWidth - 1)))],
            [.. view.ProcessLists.OrderBy(l => l.Key).Select(l =>
                new Marker(layout.X(l.Key) + layout.BucketWidth / 2, layout.Y(l.Value.Value), l.Key == report.Bucket))]);
    }
}
