using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Fleeto.Web.Reports;

/// <summary>An RGB color for <see cref="PdfPage"/>, from a hex value such as <c>#0F766E</c>.</summary>
public readonly record struct PdfColor(byte R, byte G, byte B)
{
    public static PdfColor Hex(string hex)
    {
        var value = Convert.ToInt32(hex.TrimStart('#'), 16);
        return new PdfColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    internal string Operands =>
        string.Create(CultureInfo.InvariantCulture, $"{R / 255d:0.###} {G / 255d:0.###} {B / 255d:0.###}");
}

/// <summary>
/// A small PDF writer for the reports of Fleeto (0.7.0): A4 pages with text in the standard Helvetica fonts, filled shapes and
/// lines, nothing else. Written in Fleeto rather than taken from a library, so a report adds no dependency (CLAUDE.md, dependency
/// policy). The standard fonts need no embedding and use WinAnsiEncoding: a character outside it prints as <c>?</c>.
/// Coordinates run from the top left of the page in points, as on screen; the writer turns them around for PDF.
/// </summary>
public sealed class PdfDocument
{
    public const float Width = 595.28f;
    public const float Height = 841.89f;

    private readonly List<PdfPage> _pages = [];

    public IReadOnlyList<PdfPage> Pages => _pages;

    public PdfPage AddPage()
    {
        var page = new PdfPage();
        _pages.Add(page);
        return page;
    }

    /// <summary>The finished file. <paramref name="title"/> lands in the document properties.</summary>
    public byte[] Save(string title, DateTime createdUtc)
    {
        if (_pages.Count == 0)
        {
            AddPage();
        }

        using var output = new MemoryStream();
        var offsets = new List<long>();

        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));

        void Object(int number, string body)
        {
            offsets.Add(output.Position);
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        // A comment with bytes above 127 tells transfer tools the file is binary.
        output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        // 1 catalog, 2 pages, 3 and 4 fonts, 5 info, then a page and its content per page.
        var kids = string.Join(" ", Enumerable.Range(0, _pages.Count).Select(i => $"{6 + i * 2} 0 R"));
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, $"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>");
        Object(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Object(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        Object(5, $"<< /Title {PdfText.Literal(title)} /Producer (Fleeto) /CreationDate (D:{createdUtc:yyyyMMddHHmmss}Z) >>");

        var size = string.Create(CultureInfo.InvariantCulture, $"{Width:0.##} {Height:0.##}");
        for (var i = 0; i < _pages.Count; i++)
        {
            var pageNumber = 6 + i * 2;
            Object(pageNumber,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {size}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageNumber + 1} 0 R >>");

            var content = Compress(Encoding.Latin1.GetBytes(_pages[i].Content));
            offsets.Add(output.Position);
            Write($"{pageNumber + 1} 0 obj\n<< /Length {content.Length} /Filter /FlateDecode >>\nstream\n");
            output.Write(content);
            Write("\nendstream\nendobj\n");
        }

        var xref = output.Position;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(table.ToString());
        return output.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return buffer.ToArray();
    }
}

/// <summary>One page of a <see cref="PdfDocument"/>; every call adds to its content stream.</summary>
public sealed class PdfPage
{
    private readonly StringBuilder _content = new();

    internal string Content => _content.ToString();

    /// <summary>Text with its baseline at <paramref name="y"/> from the top.</summary>
    public void Text(float x, float y, string text, float size, PdfColor color, bool bold = false)
    {
        if (text.Length == 0)
        {
            return;
        }

        Append($"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {color.Operands} rg {N(x)} {N(PdfDocument.Height - y)} Td {PdfText.Literal(text)} Tj ET");
    }

    /// <summary>Text that ends at <paramref name="right"/>.</summary>
    public void TextRight(float right, float y, string text, float size, PdfColor color, bool bold = false) =>
        Text(right - PdfText.Width(text, size, bold), y, text, size, color, bold);

    /// <summary>A filled rectangle with its top left corner at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Rect(float x, float y, float width, float height, PdfColor color)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Append($"{color.Operands} rg {N(x)} {N(PdfDocument.Height - y - height)} {N(width)} {N(height)} re f");
    }

    /// <summary>A filled rectangle with rounded corners.</summary>
    public void RoundedRect(float x, float y, float width, float height, float radius, PdfColor color)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        if (radius <= 0)
        {
            Rect(x, y, width, height, color);
            return;
        }

        // A quarter circle as a Bezier curve: control points at 0.5523 of the radius.
        var k = radius * 0.5523f;
        var left = x;
        var right = x + width;
        var top = PdfDocument.Height - y;
        var bottom = top - height;
        Append($"{color.Operands} rg {N(left + radius)} {N(bottom)} m {N(right - radius)} {N(bottom)} l " +
               $"{N(right - radius + k)} {N(bottom)} {N(right)} {N(bottom + radius - k)} {N(right)} {N(bottom + radius)} c " +
               $"{N(right)} {N(top - radius)} l {N(right)} {N(top - radius + k)} {N(right - radius + k)} {N(top)} {N(right - radius)} {N(top)} c " +
               $"{N(left + radius)} {N(top)} l {N(left + radius - k)} {N(top)} {N(left)} {N(top - radius + k)} {N(left)} {N(top - radius)} c " +
               $"{N(left)} {N(bottom + radius)} l {N(left)} {N(bottom + radius - k)} {N(left + radius - k)} {N(bottom)} {N(left + radius)} {N(bottom)} c f");
    }

    public void Line(float x1, float y1, float x2, float y2, float width, PdfColor color) =>
        Polyline([(x1, y1), (x2, y2)], width, color);

    /// <summary>Connected line segments with round joins, such as the heartbeat line of the logo.</summary>
    public void Polyline(IReadOnlyList<(float X, float Y)> points, float width, PdfColor color)
    {
        if (points.Count < 2)
        {
            return;
        }

        var path = new StringBuilder();
        path.Append(CultureInfo.InvariantCulture, $"{N(points[0].X)} {N(PdfDocument.Height - points[0].Y)} m");
        foreach (var (px, py) in points.Skip(1))
        {
            path.Append(CultureInfo.InvariantCulture, $" {N(px)} {N(PdfDocument.Height - py)} l");
        }

        Append($"{color.Operands} RG {N(width)} w 1 J 1 j {path} S");
    }

    private void Append(string operation) => _content.Append(operation).Append('\n');

    private static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Measuring and encoding text in the standard Helvetica fonts.</summary>
public static class PdfText
{
    public const string Ellipsis = "…";

    // Advance widths in thousandths of the font size for the characters 32 to 126, from the Adobe font metrics of the standard fonts.
    private static readonly short[] Regular =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667,
        611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833,
        556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584
    ];

    private static readonly short[] Bold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        333, 333, 584, 584, 584, 611, 975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667,
        611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556, 333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889,
        611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584
    ];

    /// <summary>The width of <paramref name="text"/> in points. Characters outside ASCII count as an average letter.</summary>
    public static float Width(string text, float size, bool bold = false)
    {
        var table = bold ? Bold : Regular;
        var total = 0;
        foreach (var c in text)
        {
            total += c is >= ' ' and <= '~' ? table[c - ' '] : c == '…' ? 1000 : 556;
        }

        return total * size / 1000f;
    }

    /// <summary>
    /// The text, shortened with an ellipsis to fit <paramref name="maxWidth"/>. <paramref name="keepEnd"/> cuts the middle, so a
    /// path keeps its drive and its file name.
    /// </summary>
    public static string Fit(string text, float maxWidth, float size, bool bold = false, bool keepEnd = false)
    {
        if (Width(text, size, bold) <= maxWidth)
        {
            return text;
        }

        if (keepEnd)
        {
            var head = text.Length / 2;
            var tail = text.Length - head;
            while (head + tail > 0)
            {
                if (tail >= head && tail > 0)
                {
                    tail--;
                }
                else
                {
                    head--;
                }

                var candidate = text[..head] + Ellipsis + text[^tail..];
                if (Width(candidate, size, bold) <= maxWidth)
                {
                    return candidate;
                }
            }

            return Ellipsis;
        }

        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length].TrimEnd() + Ellipsis;
            if (Width(candidate, size, bold) <= maxWidth)
            {
                return candidate;
            }
        }

        return Ellipsis;
    }

    /// <summary>A PDF string literal in WinAnsiEncoding: delimiters escaped, other bytes outside ASCII as octal.</summary>
    public static string Literal(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('(');
        foreach (var c in text)
        {
            var code = WinAnsi(c);
            switch (code)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    builder.Append('\\').Append((char)code);
                    break;
                case >= 32 and <= 126:
                    builder.Append((char)code);
                    break;
                default:
                    builder.Append('\\').Append(Convert.ToString(code, 8).PadLeft(3, '0'));
                    break;
            }
        }

        return builder.Append(')').ToString();
    }

    /// <summary>The WinAnsiEncoding byte of a character; <c>?</c> for one the standard fonts cannot show.</summary>
    internal static byte WinAnsi(char c) => c switch
    {
        >= ' ' and <= '~' => (byte)c,
        >= ' ' and <= 'ÿ' => (byte)c,
        '€' => 0x80, '‚' => 0x82, 'ƒ' => 0x83, '„' => 0x84, '…' => 0x85, '†' => 0x86, '‡' => 0x87,
        'ˆ' => 0x88, '‰' => 0x89, 'Š' => 0x8A, '‹' => 0x8B, 'Œ' => 0x8C, 'Ž' => 0x8E, '‘' => 0x91,
        '’' => 0x92, '“' => 0x93, '”' => 0x94, '•' => 0x95, '–' => 0x96, '—' => 0x97, '˜' => 0x98,
        '™' => 0x99, 'š' => 0x9A, '›' => 0x9B, 'œ' => 0x9C, 'ž' => 0x9E, 'Ÿ' => 0x9F,
        _ => (byte)'?'
    };
}
