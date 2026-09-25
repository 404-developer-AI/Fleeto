namespace Fleeto.Web.Security;

/// <summary>
/// Makes a value from a request safe to write to the log. The console log is line based, so a line break in a path or query
/// value could forge an entry that looks like it came from Fleeto itself.
/// </summary>
public static class LogText
{
    public const int DefaultMaxLength = 200;

    public static string Clean(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var clean = value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        if (clean.Any(char.IsControl))
        {
            clean = string.Concat(clean.Select(c => char.IsControl(c) ? '?' : c));
        }

        return clean.Length <= maxLength ? clean : clean[..maxLength] + "...";
    }
}
