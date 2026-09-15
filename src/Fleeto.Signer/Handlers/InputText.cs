using System.Text;

namespace Fleeto.Signer.Handlers;

/// <summary>Cleans free text reported by agents before it is stored: printable characters only, trimmed, bounded.</summary>
internal static class InputText
{
    /// <summary>Largest CSR the signer accepts. A P-256 CSR is a few hundred bytes.</summary>
    public const int MaxCsrBytes = 16 * 1024;

    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                    or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        // Do not cut a surrogate pair in half.
        var cut = char.IsHighSurrogate(cleaned[maxLength - 1]) ? maxLength - 1 : maxLength;
        return cleaned[..cut].TrimEnd();
    }
}
