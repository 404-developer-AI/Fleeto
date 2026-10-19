using System.Globalization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Web.Components.Shared;
using Fleeto.Web.Services;

namespace Fleeto.Web.Reports;

/// <summary>
/// The PDF report of the storage of one drive (0.7.0), in the branding of Fleeto (branding-fleeto.md §4 and §5): the drive and the
/// top 10 folders and files on the first page, then every folder of the scan as a tree with its growth and every file.
/// </summary>
public static class StorageReportPdf
{
    private static readonly PdfColor Primary = PdfColor.Hex("#0F766E");
    private static readonly PdfColor PrimaryTint = PdfColor.Hex("#F0FDFA");
    private static readonly PdfColor PrimaryTintStrong = PdfColor.Hex("#CCFBF1");
    private static readonly PdfColor Text = PdfColor.Hex("#1C1917");
    private static readonly PdfColor Secondary = PdfColor.Hex("#78716C");
    private static readonly PdfColor Muted = PdfColor.Hex("#A8A29E");
    private static readonly PdfColor Border = PdfColor.Hex("#E7E5E4");
    private static readonly PdfColor Surface = PdfColor.Hex("#FAFAF9");
    private static readonly PdfColor Warning = PdfColor.Hex("#B45309");
    private static readonly PdfColor White = PdfColor.Hex("#FFFFFF");

    private const float Margin = 40;
    private const float ContentWidth = PdfDocument.Width - 2 * Margin;
    private const float Right = PdfDocument.Width - Margin;
    private const float ContentTop = 92;
    private const float ContentBottom = PdfDocument.Height - 56;
    private const float RowHeight = 15;
    private const float Body = 8.5f;

    /// <summary>The report as a PDF file. <paramref name="time"/> writes a moment in the user's own time zone.</summary>
    public static byte[] Render(StorageReport report, Func<DateTime, string> time)
    {
        var document = new PdfDocument();
        var writer = new Writer(document);
        var summary = report.Summary;

        // Title block.
        var page = writer.Page;
        page.Text(Margin, writer.Y + 18, PdfText.Fit(report.Hostname, ContentWidth, 20, bold: true), 20, Text, bold: true);
        var owner = string.Join(" · ", new[] { report.ClientCode, report.ClientName, report.SiteName }.Where(p => p.Length > 0));
        page.Text(Margin, writer.Y + 34, PdfText.Fit(owner, ContentWidth, 10), 10, Secondary);
        writer.Y += 52;

        // The drive.
        page.RoundedRect(Margin, writer.Y, ContentWidth, 74, 8, Surface);
        var drive = $"Drive {summary.Volume}" + (summary.Filesystem.Length > 0 ? $" ({summary.Filesystem})" : string.Empty);
        page.Text(Margin + 14, writer.Y + 22, drive, 12, Text, bold: true);
        var share = summary.TotalBytes > 0 ? (double)report.UsedBytes / summary.TotalBytes : 0;
        page.TextRight(Right - 14, writer.Y + 22, $"{Percent(share)} full", 12, Text, bold: true);
        page.RoundedRect(Margin + 14, writer.Y + 32, ContentWidth - 28, 9, 4.5f, PrimaryTintStrong);
        page.RoundedRect(Margin + 14, writer.Y + 32, (float)((ContentWidth - 28) * Math.Clamp(share, 0, 1)), 9, 4.5f, Primary);
        page.Text(Margin + 14, writer.Y + 58,
            $"{Ui.Bytes(report.UsedBytes)} used of {Ui.Bytes(summary.TotalBytes)} · {Ui.Bytes(summary.FreeBytes)} free", Body + 0.5f, Text);
        var scanned = $"Scanned {time(summary.ScannedAt)} · {Count(summary.FileCount, "file")} · {Count(summary.FolderCount, "folder")}";
        page.TextRight(Right - 14, writer.Y + 58, scanned, Body, Secondary);
        writer.Y += 86;
        if (!summary.Complete)
        {
            page.Text(Margin, writer.Y + 4, PdfText.Fit("This scan is incomplete" + (string.IsNullOrEmpty(summary.Error) ? "" : $": {summary.Error}") +
                ". Sizes are at least the values shown.", ContentWidth, Body), Body, Warning);
            writer.Y += 16;
        }

        // Top 10 folders.
        writer.Heading($"Largest folders on {summary.Volume}", "Directly below the drive, so no folder is counted twice.");
        var folderColumns = new[] { new Column("Folder", 0), new Column("Size on disk", 330, true), new Column("Of the drive", 345), new Column("7 days", ContentWidth, true) };
        writer.Header(folderColumns);
        if (report.TopFolders.Count == 0)
        {
            writer.Note("The scan listed no folders below the drive.");
        }

        foreach (var folder in report.TopFolders)
        {
            writer.Row(folderColumns, p =>
            {
                var y = writer.Y + 10.5f;
                p.Text(Margin + 4, y, PdfText.Fit(folder.Path, 300, Body, keepEnd: true), Body, Text);
                p.TextRight(Margin + 330, y, Ui.Bytes(folder.SizeBytes), Body, Text);
                Bar(p, Margin + 345, writer.Y + 4.5f, 90, folder.SizeBytes, summary.TotalBytes);
                p.TextRight(Right - 4, y, Change(folder.ChangeWeek), Body, Changed(folder.ChangeWeek));
            });
        }

        writer.Y += 14;
        writer.Heading($"Largest files on {summary.Volume}", null);
        var fileColumns = new[] { new Column("File", 0), new Column("Size on disk", 410, true), new Column("Changed", ContentWidth, true) };
        writer.Header(fileColumns);
        if (report.TopFiles.Count == 0)
        {
            writer.Note("The scan listed no files.");
        }

        foreach (var file in report.TopFiles)
        {
            writer.Row(fileColumns, p => FileRow(p, writer.Y, file, time));
        }

        // Every folder, as a tree.
        writer.NewPage();
        writer.Heading($"All scanned folders on {summary.Volume}",
            $"The {StorageRules.MaxFolders} largest folders of the drive, each below its parent, largest first. Growth compares with the scans of 7 and 30 days ago.");
        var treeColumns = new[]
        {
            new Column("Folder", 0), new Column("Size on disk", 300, true), new Column("Files", 365, true), new Column("7 days", 440, true),
            new Column("30 days", ContentWidth, true)
        };
        writer.Header(treeColumns);
        foreach (var folder in report.Detail.Folders)
        {
            writer.Row(treeColumns, p =>
            {
                var y = writer.Y + 10.5f;
                var indent = Math.Min(folder.Depth, 12) * 9f;
                var name = folder.Depth == 0 ? folder.Path : folder.Name;
                p.Text(Margin + 4 + indent, y, PdfText.Fit(name, 230 - indent, Body, bold: folder.Depth <= 1), Body, Text, bold: folder.Depth <= 1);
                p.TextRight(Margin + 300, y, Ui.Bytes(folder.SizeBytes), Body, Text);
                p.TextRight(Margin + 365, y, folder.FileCount.ToString("N0", CultureInfo.InvariantCulture), Body, Secondary);
                p.TextRight(Margin + 440, y, Change(folder.ChangeWeek), Body, Changed(folder.ChangeWeek));
                p.TextRight(Right - 4, y, Change(folder.ChangeMonth), Body, Changed(folder.ChangeMonth));
            }, treeColumns);
        }

        writer.Y += 14;
        writer.Heading($"All scanned files on {summary.Volume}", $"The {StorageRules.MaxFiles} largest files of the drive.");
        writer.Header(fileColumns);
        if (report.Detail.Files.Count == 0)
        {
            writer.Note("The scan listed no files.");
        }

        foreach (var file in report.Detail.Files.OrderByDescending(f => f.SizeBytes))
        {
            writer.Row(fileColumns, p => FileRow(p, writer.Y, file, time), fileColumns);
        }

        writer.Y += 14;
        writer.Note("Sizes are the space the files take on disk, as the Fleeto agent measured them during the scan.");

        // Header and footer on every page, now that the number of pages is known.
        var footer = $"{report.Hostname} · drive {summary.Volume} · generated {time(report.GeneratedAt)} by {report.GeneratedBy}";
        for (var i = 0; i < document.Pages.Count; i++)
        {
            var p = document.Pages[i];
            Logo(p, Margin, 34);
            p.Text(Margin + 30, 51, "Fleeto", 14, Text, bold: true);
            p.TextRight(Right, 51, "Storage report", 10, Secondary);
            p.Line(Margin, 70, Right, 70, 0.75f, Border);
            p.Line(Margin, PdfDocument.Height - 40, Right, PdfDocument.Height - 40, 0.75f, Border);
            p.Text(Margin, PdfDocument.Height - 26, PdfText.Fit(footer, ContentWidth - 120, 7.5f), 7.5f, Muted);
            p.TextRight(Right, PdfDocument.Height - 26, $"Fleeto · by Steaan · page {i + 1} of {document.Pages.Count}", 7.5f, Muted);
        }

        return document.Save($"Storage report {report.Hostname} {summary.Volume}", report.GeneratedAt);
    }

    private static void FileRow(PdfPage page, float top, StorageFileEntry file, Func<DateTime, string> time)
    {
        var y = top + 10.5f;
        page.Text(Margin + 4, y, PdfText.Fit(file.Path, 330, Body, keepEnd: true), Body, Text);
        page.TextRight(Margin + 410, y, Ui.Bytes(file.SizeBytes), Body, Text);
        page.TextRight(Right - 4, y, file.ModifiedAt is { } modified ? time(modified) : "-", Body, Secondary);
    }

    /// <summary>The mark of Fleeto (branding §5): a teal rounded square with a white heartbeat line.</summary>
    private static void Logo(PdfPage page, float x, float y)
    {
        const float size = 22;
        page.RoundedRect(x, y, size, size, 5.5f, Primary);
        page.Polyline(
        [
            (x + 3.5f, y + 12), (x + 7.5f, y + 12), (x + 9.5f, y + 7), (x + 12.5f, y + 16.5f), (x + 14.5f, y + 10), (x + 16, y + 12),
            (x + 18.5f, y + 12)
        ], 1.6f, White);
    }

    private static void Bar(PdfPage page, float x, float y, float width, long bytes, long total)
    {
        var share = total > 0 ? Math.Clamp((double)bytes / total, 0, 1) : 0;
        page.RoundedRect(x, y, width, 6, 3, PrimaryTint);
        page.RoundedRect(x, y, (float)(width * share), 6, 3, Primary);
        page.Text(x + width + 6, y + 5.5f, Percent(share), 7.5f, Secondary);
    }

    private static string Percent(double share) => (share * 100).ToString(share < 0.1 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";

    private static string Change(long? change) => change is { } bytes ? (bytes == 0 ? "-" : Ui.BytesChange(bytes)) : "-";

    private static PdfColor Changed(long? change) => change is > 0 ? Warning : Secondary;

    private static string Count(long value, string noun) =>
        value.ToString("N0", CultureInfo.InvariantCulture) + " " + (value == 1 ? noun : noun + "s");

    /// <summary>A column of a table: its title and where it ends (right aligned) or starts.</summary>
    private sealed record Column(string Title, float Position, bool AlignRight = false);

    /// <summary>Places content down the pages, starting a new page when a row no longer fits.</summary>
    private sealed class Writer
    {
        private readonly PdfDocument _document;
        private int _rows;

        public Writer(PdfDocument document)
        {
            _document = document;
            Page = document.AddPage();
            Y = ContentTop;
        }

        public PdfPage Page { get; private set; }

        public float Y { get; set; }

        public void NewPage()
        {
            Page = _document.AddPage();
            Y = ContentTop;
        }

        public void Heading(string title, string? subtitle)
        {
            if (Y + 60 > ContentBottom)
            {
                NewPage();
            }

            Page.Text(Margin, Y + 12, title, 12, Text, bold: true);
            Y += 18;
            if (subtitle is not null)
            {
                Page.Text(Margin, Y + 8, PdfText.Fit(subtitle, ContentWidth, 8), 8, Secondary);
                Y += 14;
            }

            Y += 2;
        }

        public void Header(IReadOnlyList<Column> columns)
        {
            Page.Rect(Margin, Y, ContentWidth, RowHeight, PrimaryTint);
            foreach (var column in columns)
            {
                if (column.AlignRight)
                {
                    Page.TextRight(Margin + column.Position - 4, Y + 10.5f, column.Title, 7.5f, Primary, bold: true);
                }
                else
                {
                    Page.Text(Margin + column.Position + 4, Y + 10.5f, column.Title, 7.5f, Primary, bold: true);
                }
            }

            Y += RowHeight;
            _rows = 0;
        }

        /// <summary>One table row; with <paramref name="repeat"/> the header comes back on a new page.</summary>
        public void Row(IReadOnlyList<Column> columns, Action<PdfPage> draw, IReadOnlyList<Column>? repeat = null)
        {
            if (Y + RowHeight > ContentBottom)
            {
                NewPage();
                Header(repeat ?? columns);
            }

            if (_rows % 2 == 1)
            {
                Page.Rect(Margin, Y, ContentWidth, RowHeight, Surface);
            }

            draw(Page);
            Page.Line(Margin, Y + RowHeight, Right, Y + RowHeight, 0.4f, Border);
            Y += RowHeight;
            _rows++;
        }

        public void Note(string text)
        {
            if (Y + RowHeight > ContentBottom)
            {
                NewPage();
            }

            Page.Text(Margin, Y + 11, PdfText.Fit(text, ContentWidth, 8), 8, Secondary);
            Y += RowHeight;
        }
    }
}
