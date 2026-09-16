using System.Text.Json;

namespace Fleeto.Web.Tests;

/// <summary>
/// The web app manifest is what a browser reads when someone installs Fleeto. It is a static file, so nothing but a test
/// keeps it pointing at icons that exist and at the brand colour, and keeps the page linking to it.
/// </summary>
public class WebAppManifestTests
{
    private const string ThemeColor = "#0F766E";

    [Fact]
    public void The_manifest_names_Fleeto_and_every_icon_it_lists_exists()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(WwwRoot(), "manifest.webmanifest")));
        var root = manifest.RootElement;

        Assert.Equal("Fleeto", root.GetProperty("name").GetString());
        Assert.Equal("Fleeto", root.GetProperty("short_name").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("/", root.GetProperty("scope").GetString());
        Assert.Equal(ThemeColor, root.GetProperty("theme_color").GetString());

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        foreach (var icon in icons)
        {
            var source = icon.GetProperty("src").GetString()!;
            Assert.StartsWith("/", source);
            Assert.Equal("image/png", icon.GetProperty("type").GetString());
            var file = Path.Combine(WwwRoot(), source.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"The manifest lists {source}, which is not in wwwroot.");
            AssertPngSize(file, int.Parse(icon.GetProperty("sizes").GetString()!.Split('x')[0]));
        }

        // Android crops a maskable icon to its own shape and falls back to a white box without one.
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "maskable");
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "any" && i.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "any" && i.GetProperty("sizes").GetString() == "512x512");
    }

    [Fact]
    public void The_page_links_the_manifest_the_touch_icon_and_the_theme_color()
    {
        var app = File.ReadAllText(Path.Combine(WwwRoot(), "..", "Components", "App.razor"));

        Assert.Contains("rel=\"manifest\"", app);
        Assert.Contains("manifest.webmanifest", app);
        Assert.Contains("rel=\"apple-touch-icon\"", app);
        Assert.Contains($"<meta name=\"theme-color\" content=\"{ThemeColor}\" />", app);
        AssertPngSize(Path.Combine(WwwRoot(), "icons", "apple-touch-icon.png"), 180);
    }

    /// <summary>Reads width and height straight from the PNG header, so an icon cannot claim a size it does not have.</summary>
    private static void AssertPngSize(string path, int expected)
    {
        var header = new byte[24];
        using (var file = File.OpenRead(path))
        {
            Assert.Equal(header.Length, file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false));
        }

        Assert.Equal<byte[]>([0x89, (byte)'P', (byte)'N', (byte)'G'], header[..4]);
        Assert.Equal(expected, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
        Assert.Equal(expected, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
    }

    private static string WwwRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Fleeto.Web", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The wwwroot of Fleeto.Web was not found above the test output directory.");
    }
}
