using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Web.Reports;
using Fleeto.Web.Services;

namespace Fleeto.Web.Tests;

/// <summary>
/// The PDF writer of Fleeto and the storage report it makes (0.7.0): a file every reader opens (header, objects where the cross
/// reference table says, trailer), text that cannot break out of its string, characters outside the standard fonts as <c>?</c>,
/// long paths shortened in the middle, and a report that runs over as many pages as its folders need.
/// </summary>
public sealed partial class StorageReportPdfTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_file_has_a_cross_reference_table_that_points_at_every_object()
    {
        var document = new PdfDocument();
        document.AddPage().Text(40, 60, "First page", 12, PdfColor.Hex("#1C1917"));
        document.AddPage().Rect(40, 40, 100, 20, PdfColor.Hex("#0F766E"));
        var bytes = document.Save("Test (report)", Now);
        var text = Encoding.Latin1.GetString(bytes);

        Assert.StartsWith("%PDF-1.4\n", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/Count 2", text);

        var startXref = int.Parse(XrefStart().Match(text).Groups[1].Value);
        Assert.StartsWith("xref\n0 ", text[startXref..]);
        var offsets = XrefEntry().Matches(text[startXref..]).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(9, offsets.Count);
        for (var i = 0; i < offsets.Count; i++)
        {
            Assert.StartsWith($"{i + 1} 0 obj", text[offsets[i]..]);
        }

        Assert.Contains("First page", Contents(bytes)[0]);
    }

    [Fact]
    public void Text_cannot_break_out_of_its_string_and_unknown_characters_print_as_a_question_mark()
    {
        Assert.Equal(@"(a\(b\)c\\d)", PdfText.Literal(@"a(b)c\d"));
        Assert.Equal(@"(caf\351 \267 \205)", PdfText.Literal("caf\u00E9 \u00B7 \u2026"));
        Assert.Equal("(?? ?)", PdfText.Literal("\u4E2D\u6587 \n"));
    }

    [Fact]
    public void The_width_tables_cover_every_printable_ASCII_character_and_long_paths_keep_both_ends()
    {
        Assert.Equal(278 * 12 / 1000f, PdfText.Width(" ", 12));
        Assert.Equal(584 * 10 / 1000f, PdfText.Width("~", 10));
        Assert.Equal(584 * 10 / 1000f, PdfText.Width("~", 10, bold: true));

        var path = @"C:\Users\someone\AppData\Local\Microsoft\Outlook\a-very-long-mailbox-name.ost";
        var fitted = PdfText.Fit(path, 150, 9, keepEnd: true);
        Assert.True(PdfText.Width(fitted, 9) <= 150);
        Assert.StartsWith(@"C:\", fitted);
        Assert.EndsWith(".ost", fitted);
        Assert.Contains(PdfText.Ellipsis, fitted);
        Assert.Equal("short", PdfText.Fit("short", 150, 9));
    }

    [Fact]
    public void A_storage_report_has_the_drive_and_top_10_first_and_every_folder_on_the_following_pages()
    {
        var folders = new List<StorageFolderRow> { Folder(@"C:\", 0, 600) };
        for (var i = 0; i < 120; i++)
        {
            folders.Add(Folder($@"C:\Folder{i:000}", 1, 120 - i));
            folders.Add(Folder($@"C:\Folder{i:000}\Sub (x)", 2, 1));
        }

        var files = Enumerable.Range(0, 50).Select(i => new StorageFileEntry($@"C:\Data\file{i:00}.bak", (50 - i) * Gb, Now)).ToList();
        var summary = new StorageVolumeSummary(Guid.NewGuid(), "C:", "NTFS", 1000 * Gb, 400 * Gb, Now, true, null, StorageScanMethod.Mft, 1200,
            123_456, 7_890);
        var detail = new StorageVolumeDetail(summary, folders, files, null, null, null);
        var report = new StorageReport("SRV-01", "ACME", "Acme Corp", "Main office", detail, StorageService.TopFolders(folders),
            StorageService.TopFiles(files), "Christiaan | Datawolk", Now);

        var bytes = StorageReportPdf.Render(report, t => t.ToString("yyyy-MM-dd HH:mm"));
        var pages = Contents(bytes);

        Assert.True(pages.Count >= 5, $"expected the 241 folders to run over several pages, got {pages.Count}");
        Assert.Contains("(SRV-01)", pages[0]);
        Assert.Contains("(60% full)", pages[0]);
        Assert.Contains(@"(C:\\Folder000)", pages[0]);
        Assert.DoesNotContain(@"(C:\\Folder010)", pages[0]);
        Assert.Contains(@"(Sub \(x\))", string.Join("", pages));
        Assert.Contains($"(Fleeto \\267 by Steaan \\267 page {pages.Count} of {pages.Count})", pages[^1]);
        Assert.All(pages, p => Assert.Contains("(Storage report)", p));
    }

    private static StorageFolderRow Folder(string path, int depth, long gigabytes) =>
        new(path, StorageRules.FolderName(path), depth == 0 ? null : StorageRules.ParentPath(path), depth, depth < 2, gigabytes * Gb, 10, 1,
            null, depth == 1 ? Gb : null, null);

    /// <summary>The decompressed content stream of every page, in order.</summary>
    private static List<string> Contents(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var result = new List<string>();
        foreach (Match match in Stream().Matches(text))
        {
            var start = match.Index + match.Length;
            var length = int.Parse(match.Groups[1].Value);
            using var input = new ZLibStream(new MemoryStream(pdf, start, length), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            result.Add(Encoding.Latin1.GetString(output.ToArray()));
        }

        return result;
    }

    [GeneratedRegex(@"startxref\n(\d+)\n%%EOF")]
    private static partial Regex XrefStart();

    [GeneratedRegex(@"(\d{10}) 00000 n ")]
    private static partial Regex XrefEntry();

    [GeneratedRegex(@"<< /Length (\d+) /Filter /FlateDecode >>\nstream\n")]
    private static partial Regex Stream();
}
