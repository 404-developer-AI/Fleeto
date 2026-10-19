using System.Globalization;
using Fleeto.Web.Components.Shared;
using Fleeto.Web.Services;

namespace Fleeto.Web.Reports;

/// <summary>
/// The storage summary of a drive as the browser draws it into an image (0.7.0, <c>fleeto.copyStorageImage</c>): every text is
/// formatted here, so the image says exactly what the PDF and the Storage tab say.
/// </summary>
public sealed record StorageReportImage(
    string FileName,
    string Hostname,
    string Owner,
    string Drive,
    string PercentFull,
    double UsedShare,
    string Usage,
    string Scanned,
    string? Warning,
    string FoldersTitle,
    IReadOnlyList<StorageReportImage.FolderLine> Folders,
    string FilesTitle,
    IReadOnlyList<StorageReportImage.FileLine> Files,
    string Empty,
    string Footer)
{
    public sealed record FolderLine(string Path, string Size, double Share, string Percent);

    public sealed record FileLine(string Path, string Size, string Changed);

    /// <summary>The summary of <paramref name="report"/>; <paramref name="time"/> writes a moment in the user's own time zone.</summary>
    public static StorageReportImage From(StorageReport report, Func<DateTime, string> time)
    {
        var summary = report.Summary;
        var total = Math.Max(1, summary.TotalBytes);
        double Share(long bytes) => Math.Clamp((double)bytes / total, 0, 1);
        var used = summary.TotalBytes > 0 ? Share(report.UsedBytes) : 0;
        return new StorageReportImage(
            report.FileName("png"),
            report.Hostname,
            string.Join(" · ", new[] { report.ClientCode, report.ClientName, report.SiteName }.Where(p => p.Length > 0)),
            $"Drive {summary.Volume}" + (summary.Filesystem.Length > 0 ? $" ({summary.Filesystem})" : string.Empty),
            Percent(used) + " full",
            used,
            $"{Ui.Bytes(report.UsedBytes)} used of {Ui.Bytes(summary.TotalBytes)} · {Ui.Bytes(summary.FreeBytes)} free",
            $"Scanned {time(summary.ScannedAt)}",
            summary.Complete ? null : "This scan is incomplete. Sizes are at least the values shown.",
            $"Largest folders on {summary.Volume}",
            [.. report.TopFolders.Select(f => new FolderLine(f.Path, Ui.Bytes(f.SizeBytes), Share(f.SizeBytes), Percent(Share(f.SizeBytes))))],
            $"Largest files on {summary.Volume}",
            [.. report.TopFiles.Select(f => new FileLine(f.Path, Ui.Bytes(f.SizeBytes), f.ModifiedAt is { } modified ? time(modified) : "-"))],
            "The scan listed none.",
            $"Fleeto · by Steaan · {report.Hostname} · generated {time(report.GeneratedAt)} by {report.GeneratedBy}");
    }

    private static string Percent(double share) => (share * 100).ToString(share < 0.1 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";
}
