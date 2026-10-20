using System.Globalization;
using Fleeto.Core.Entities;
using Fleeto.Web.Components.Shared;
using Fleeto.Web.Services;

namespace Fleeto.Web.Reports;

/// <summary>
/// The process list of a CPU or memory usage result as the browser draws it into an image (0.7.0, <c>fleeto.copyProcessImage</c>):
/// every text is formatted here, so the image says exactly what the check history says.
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
    bool Cpu,
    string ProcessesTitle,
    IReadOnlyList<ProcessListReportImage.ProcessLine> Processes,
    string Empty,
    string Footer)
{
    /// <param name="CpuShare">The CPU share of the machine from 0 to 1, for the bar; 0 in the list of a memory usage check.</param>
    public sealed record ProcessLine(string Name, string Pid, string User, string Cpu, double CpuShare, string Memory);

    /// <summary>The image of <paramref name="report"/>; <paramref name="time"/> writes a moment in the user's own time zone.</summary>
    public static ProcessListReportImage From(ProcessListReport report, Func<DateTime, string> time)
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
}
