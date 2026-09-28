using System.Globalization;

namespace Fleeto.Core.Domain;

/// <summary>Sizes in words for text that leaves the UI (alert details, emails): "512 MB", "1.4 GB".</summary>
public static class ByteSize
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return size.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
