using System.Globalization;
using Fleeto.Core.Domain;
using Fleeto.Web.Services;

namespace Fleeto.Web.Reports;

/// <summary>
/// The end-of-life report of one client as a PDF (0.8.0 step 4, VULNERABILITIES.md §12.5), in the branding of Fleeto like the storage
/// report: the counts, then the endpoints at end of life, ending soon, on a paid extension, without an announced date and not recognised,
/// each with its operating system and dates, and where the dates come from.
/// </summary>
public static class EndOfLifeReportPdf
{
    private static readonly PdfColor Primary = PdfColor.Hex("#0F766E");
    private static readonly PdfColor PrimaryTint = PdfColor.Hex("#F0FDFA");
    private static readonly PdfColor Text = PdfColor.Hex("#1C1917");
    private static readonly PdfColor Secondary = PdfColor.Hex("#78716C");
    private static readonly PdfColor Muted = PdfColor.Hex("#A8A29E");
    private static readonly PdfColor Border = PdfColor.Hex("#E7E5E4");
    private static readonly PdfColor Surface = PdfColor.Hex("#FAFAF9");
    private static readonly PdfColor Error = PdfColor.Hex("#B91C1C");
    private static readonly PdfColor Warning = PdfColor.Hex("#B45309");
    private static readonly PdfColor White = PdfColor.Hex("#FFFFFF");

    private const float Margin = 40;
    private const float ContentWidth = PdfDocument.Width - 2 * Margin;
    private const float Right = PdfDocument.Width - Margin;
    private const float ContentTop = 92;
    private const float ContentBottom = PdfDocument.Height - 56;
    private const float RowHeight = 15;
    private const float Body = 8.5f;

    private static readonly Column[] Columns =
    [
        new("Endpoint", 0), new("Site", 130), new("Operating system", 215), new("End of life", 405), new("Extension until", 465)
    ];

    /// <summary>The report as a PDF file. <paramref name="time"/> writes a moment in the user's own time zone.</summary>
    public static byte[] Render(EndOfLifeClientReport report, Func<DateTime, string> time)
    {
        var document = new PdfDocument();
        var writer = new Writer(document);
        var page = writer.Page;
        page.Text(Margin, writer.Y + 18, PdfText.Fit($"[{report.ClientCode}] {report.ClientName}", ContentWidth, 20, bold: true), 20, Text, bold: true);
        page.Text(Margin, writer.Y + 34,
            $"Operating systems at or near end of life on {Day(report.Day)} · ending soon means within {report.PeriodMonths} months", 10, Secondary);
        writer.Y += 52;

        // The counts.
        var counts = report.Counts;
        (string Label, int Value, PdfColor Color)[] cards =
        [
            ("End of life", counts.EndOfLife, counts.EndOfLife > 0 ? Error : Text),
            ("Ending soon", counts.EndingSoon, counts.EndingSoon > 0 ? Warning : Text),
            ("Paid extension", counts.Extended, Text),
            ("Supported", counts.Supported, Text),
            ("Not recognised", counts.NotRecognised + counts.NoDate, Text)
        ];
        var cardWidth = (ContentWidth - 4 * 8) / cards.Length;
        for (var i = 0; i < cards.Length; i++)
        {
            var x = Margin + i * (cardWidth + 8);
            page.RoundedRect(x, writer.Y, cardWidth, 50, 6, Surface);
            page.Text(x + 10, writer.Y + 16, cards[i].Label, 8, Secondary);
            page.Text(x + 10, writer.Y + 38, cards[i].Value.ToString("N0", CultureInfo.InvariantCulture), 16, cards[i].Color, bold: true);
        }

        writer.Y += 64;

        Section(writer, report, EndOfLifeStatus.EndOfLife, "End of life",
            "These operating systems no longer get security updates. Upgrade or replace them, or buy a paid extension where one exists.");
        Section(writer, report, EndOfLifeStatus.EndingSoon, "Ending soon", "Security updates stop on the date shown. Plan the upgrade.");
        Section(writer, report, EndOfLifeStatus.Extended, "On a paid extension",
            "Past their end of life, covered by a paid extension (Extended Security Updates, Ubuntu Pro, Extended LTS) until the date shown.");
        Section(writer, report, EndOfLifeStatus.NoDate, "No end of life announced", "The vendor has not announced an end of life for these releases yet.");
        Section(writer, report, EndOfLifeStatus.NotRecognised, "Not recognised",
            "Fleeto could not recognise the release of these operating systems, so it cannot say when they stop getting updates.");
        if (report.Rows.Count == 0)
        {
            writer.Note("Every managed endpoint of this client runs an operating system that gets security updates for longer than the period above.");
        }

        writer.Y += 10;
        writer.Note("Dates of Windows, Windows Server, Ubuntu, Debian, AlmaLinux and Proxmox VE: endoflife.date" +
                    (report.DataAsOf is { } asOf ? $", fetched {time(asOf)}." : "."));
        writer.Note("Dates of Synology DSM: the Software Life Cycle Policy of Synology. Only managed endpoints are included.");

        var footer = $"[{report.ClientCode}] {report.ClientName} · report of {time(report.CreatedAt)} · generated {time(report.GeneratedAt)} by {report.GeneratedBy}";
        for (var i = 0; i < document.Pages.Count; i++)
        {
            var p = document.Pages[i];
            Logo(p, Margin, 34);
            p.Text(Margin + 30, 51, "Fleeto", 14, Text, bold: true);
            p.TextRight(Right, 51, "End-of-life report", 10, Secondary);
            p.Line(Margin, 70, Right, 70, 0.75f, Border);
            p.Line(Margin, PdfDocument.Height - 40, Right, PdfDocument.Height - 40, 0.75f, Border);
            p.Text(Margin, PdfDocument.Height - 26, PdfText.Fit(footer, ContentWidth - 120, 7.5f), 7.5f, Muted);
            p.TextRight(Right, PdfDocument.Height - 26, $"Fleeto · by Steaan · page {i + 1} of {document.Pages.Count}", 7.5f, Muted);
        }

        return document.Save($"End-of-life report {report.ClientCode} {report.Day:yyyy-MM-dd}", report.GeneratedAt);
    }

    private static void Section(Writer writer, EndOfLifeClientReport report, EndOfLifeStatus status, string title, string subtitle)
    {
        var rows = report.Rows.Where(r => r.Status == status).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        writer.Heading($"{title} ({rows.Count})", subtitle);
        writer.Header(Columns);
        foreach (var row in rows)
        {
            writer.Row(Columns, p =>
            {
                var y = writer.Y + 10.5f;
                p.Text(Margin + 4, y, PdfText.Fit(row.Hostname, 122, Body, bold: true), Body, Text, bold: true);
                p.Text(Margin + 134, y, PdfText.Fit(row.SiteName, 78, Body), Body, Secondary);
                p.Text(Margin + 219, y, PdfText.Fit(row.OsName, 182, Body), Body, Text);
                p.Text(Margin + 409, y, row.EndOfLifeOn is { } on ? Day(on) : "-", Body, status == EndOfLifeStatus.EndOfLife ? Error : Text);
                p.Text(Margin + 469, y, row.ExtensionUntil is { } until ? Day(until) : "-", Body, Secondary);
            });
        }

        writer.Y += 12;
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    private sealed record Column(string Title, float Position);

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

        private void NewPage()
        {
            Page = _document.AddPage();
            Y = ContentTop;
        }

        public void Heading(string title, string subtitle)
        {
            if (Y + 60 > ContentBottom)
            {
                NewPage();
            }

            Page.Text(Margin, Y + 12, title, 12, Text, bold: true);
            Y += 18;
            Page.Text(Margin, Y + 8, PdfText.Fit(subtitle, ContentWidth, 8), 8, Secondary);
            Y += 16;
        }

        public void Header(IReadOnlyList<Column> columns)
        {
            Page.Rect(Margin, Y, ContentWidth, RowHeight, PrimaryTint);
            foreach (var column in columns)
            {
                Page.Text(Margin + column.Position + 4, Y + 10.5f, column.Title, 7.5f, Primary, bold: true);
            }

            Y += RowHeight;
            _rows = 0;
        }

        public void Row(IReadOnlyList<Column> columns, Action<PdfPage> draw)
        {
            if (Y + RowHeight > ContentBottom)
            {
                NewPage();
                Header(columns);
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
