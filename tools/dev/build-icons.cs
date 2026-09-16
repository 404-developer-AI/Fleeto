// Renders the Fleeto mark from src/Fleeto.Web/wwwroot/favicon.svg into the PNG icons the web app manifest needs.
// Run it with .NET 10 file-based execution from the repository root:
//
//     dotnet run tools/dev/build-icons.cs
//
// The mark is a teal rounded square with a white heartbeat line (branding-fleeto.md §5). Both shapes are simple
// enough to rasterize here, so generating the icons needs no image library and no design tool.

using System.IO.Compression;

const string OutputDirectory = "src/Fleeto.Web/wwwroot/icons";

// The mark in the 32-unit coordinate system of favicon.svg. Keep these in step with that file.
var teal = (R: 0x0F, G: 0x76, B: 0x6E);
double[,] line =
{
    { 5, 17 }, { 10, 17 }, { 12.5, 11 }, { 16.5, 22 }, { 19.5, 14 }, { 21, 17 }, { 27, 17 }
};
const double StrokeWidth = 2.2;
const double CornerRadius = 8;
const double Canvas = 32;

var icons = new (string Name, int Size, bool Rounded, double Scale)[]
{
    // Browsers and the manifest ("any" purpose): the rounded square is part of the icon.
    ("icon-192.png", 192, true, 1.0),
    ("icon-512.png", 512, true, 1.0),
    // Maskable: the platform crops the icon to its own shape, so the teal fills the square edge to edge and the
    // heartbeat line stays inside the safe zone (the middle 80%).
    ("icon-maskable-512.png", 512, false, 0.66),
    // iOS applies its own rounding and shows transparency as black, so this one is square as well.
    ("apple-touch-icon.png", 180, false, 0.9)
};

Directory.CreateDirectory(OutputDirectory);
foreach (var icon in icons)
{
    var pixels = Render(icon.Size, icon.Rounded, icon.Scale);
    var path = Path.Combine(OutputDirectory, icon.Name);
    File.WriteAllBytes(path, EncodePng(pixels, icon.Size));
    Console.WriteLine($"{path} ({icon.Size}x{icon.Size})");
}

// Draws one icon as straight RGBA bytes. Every pixel is sampled 4x4 times so the rounded corners and the line keep
// smooth edges; coverage of the white line is blended over the teal, which is itself blended over transparency.
byte[] Render(int size, bool rounded, double scale)
{
    const int Samples = 4;
    var pixels = new byte[size * size * 4];
    var unit = Canvas / size;
    var offset = (Canvas - Canvas * scale) / 2;

    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            double background = 0, foreground = 0;
            for (var sy = 0; sy < Samples; sy++)
            {
                for (var sx = 0; sx < Samples; sx++)
                {
                    // Sample point in the 32-unit space, then mapped back into the mark's own space when it is scaled.
                    var px = (x + (sx + 0.5) / Samples) * unit;
                    var py = (y + (sy + 0.5) / Samples) * unit;
                    if (!rounded || InsideRoundedSquare(px, py))
                    {
                        background++;
                    }

                    var mx = (px - offset) / scale;
                    var my = (py - offset) / scale;
                    if (DistanceToLine(mx, my) <= StrokeWidth / 2)
                    {
                        foreground++;
                    }
                }
            }

            var total = (double)(Samples * Samples);
            WritePixel(pixels, (y * size + x) * 4, background / total, foreground / total);
        }
    }

    return pixels;
}

void WritePixel(byte[] pixels, int index, double background, double foreground)
{
    // White over teal over nothing: the alpha is whatever either shape covers, the colour is their mix.
    var alpha = Math.Max(background, foreground);
    if (alpha <= 0)
    {
        return;
    }

    var white = foreground / alpha;
    pixels[index] = Channel(teal.R, white);
    pixels[index + 1] = Channel(teal.G, white);
    pixels[index + 2] = Channel(teal.B, white);
    pixels[index + 3] = (byte)Math.Round(alpha * 255);
}

byte Channel(int value, double white) => (byte)Math.Round(value + (255 - value) * white);

bool InsideRoundedSquare(double x, double y)
{
    // Distance to the rounded square: only the four corner quadrants curve, the rest is the plain square.
    var cx = Math.Min(Math.Max(x, CornerRadius), Canvas - CornerRadius);
    var cy = Math.Min(Math.Max(y, CornerRadius), Canvas - CornerRadius);
    if (x >= 0 && x <= Canvas && y >= 0 && y <= Canvas && (x == cx || y == cy))
    {
        return true;
    }

    return Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) <= CornerRadius;
}

// Shortest distance to the heartbeat polyline. Measuring the distance gives round caps and round joins for free,
// which is what the SVG asks for.
double DistanceToLine(double x, double y)
{
    var best = double.MaxValue;
    for (var i = 0; i < line.GetLength(0) - 1; i++)
    {
        var ax = line[i, 0];
        var ay = line[i, 1];
        var dx = line[i + 1, 0] - ax;
        var dy = line[i + 1, 1] - ay;
        var length = dx * dx + dy * dy;
        var t = length == 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / length, 0, 1);
        var ox = x - (ax + t * dx);
        var oy = y - (ay + t * dy);
        best = Math.Min(best, Math.Sqrt(ox * ox + oy * oy));
    }

    return best;
}

// Minimal PNG writer: one IHDR, one IDAT with zlib-compressed scanlines, one IEND. No filtering, which costs a few
// kilobytes on icons this small and keeps the encoder short enough to read.
byte[] EncodePng(byte[] pixels, int size)
{
    var raw = new byte[(size * 4 + 1) * size];
    for (var y = 0; y < size; y++)
    {
        Array.Copy(pixels, y * size * 4, raw, y * (size * 4 + 1) + 1, size * 4);
    }

    var png = new MemoryStream();
    png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

    var header = new MemoryStream();
    WriteBigEndian(header, size);
    WriteBigEndian(header, size);
    header.Write([8, 6, 0, 0, 0]); // 8 bits per channel, truecolour with alpha, no interlacing.
    WriteChunk(png, "IHDR", header.ToArray());
    WriteChunk(png, "IDAT", Deflate(raw));
    WriteChunk(png, "IEND", []);
    return png.ToArray();
}

byte[] Deflate(byte[] data)
{
    var compressed = new MemoryStream();
    using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
    {
        deflate.Write(data);
    }

    return compressed.ToArray();
}

void WriteChunk(Stream stream, string type, byte[] data)
{
    var name = System.Text.Encoding.ASCII.GetBytes(type);
    WriteBigEndian(stream, data.Length);
    stream.Write(name);
    stream.Write(data);
    var crc = new byte[4];
    var value = Crc32([.. name, .. data]);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, value);
    stream.Write(crc);
}

void WriteBigEndian(Stream stream, int value)
{
    var bytes = new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
    stream.Write(bytes);
}

uint Crc32(byte[] data)
{
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
    {
        crc ^= b;
        for (var i = 0; i < 8; i++)
        {
            crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
        }
    }

    return crc ^ 0xFFFFFFFFu;
}
