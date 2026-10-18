using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fleeto.Core.Performance;

/// <summary>
/// Normalizes SQL so that statements differing only in values are one statement (PERFORMANCE.md §5, spec §5): comments removed,
/// string, dollar-quoted and numeric literals to <c>?</c>, IN lists and VALUES rows collapsed, whitespace collapsed. Parameters
/// (<c>$1</c>) and quoted identifiers stay, so the result still names the tables and columns to look for in the code. The
/// fingerprint is a short hash of the normalized text.
/// </summary>
public static partial class SqlFingerprint
{
    public const int MaxLength = 4000;

    public static string Normalize(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        var text = StripLiterals(sql);
        text = NumberPattern().Replace(text, "?");
        text = WhitespacePattern().Replace(text, " ").Trim();
        text = InListPattern().Replace(text, "$1(?)");
        text = ArrayPattern().Replace(text, "ARRAY[?]");
        text = ValuesPattern().Replace(text, "VALUES $1");
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    /// <summary>A short, stable hash of the normalized text: 16 hexadecimal characters.</summary>
    public static string Hash(string normalized) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];

    /// <summary>
    /// Removes comments and replaces every string literal (also <c>E'...'</c> and dollar quoting) with <c>?</c>, keeping
    /// double-quoted identifiers. One pass over the text, so a quote inside a comment or an identifier never confuses it.
    /// </summary>
    private static string StripLiterals(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                result.Append(' ');
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                result.Append(' ');
                continue;
            }

            if (c == '"')
            {
                var end = i + 1;
                while (end < sql.Length && !(sql[end] == '"' && (end + 1 >= sql.Length || sql[end + 1] != '"')))
                {
                    end += sql[end] == '"' ? 2 : 1;
                }

                end = Math.Min(end + 1, sql.Length);
                result.Append(sql, i, end - i);
                i = end;
                continue;
            }

            if (c == '\'' || ((c is 'E' or 'e') && i + 1 < sql.Length && sql[i + 1] == '\'' && (i == 0 || !IsWordChar(sql[i - 1]))))
            {
                var start = c == '\'' ? i : i + 1;
                var escapes = c != '\'';
                var end = start + 1;
                while (end < sql.Length)
                {
                    if (escapes && sql[end] == '\\')
                    {
                        end += 2;
                        continue;
                    }

                    if (sql[end] == '\'')
                    {
                        if (end + 1 < sql.Length && sql[end + 1] == '\'')
                        {
                            end += 2;
                            continue;
                        }

                        break;
                    }

                    end++;
                }

                result.Append('?');
                i = Math.Min(end + 1, sql.Length);
                continue;
            }

            if (c == '$' && (i == 0 || !IsWordChar(sql[i - 1])) && DollarTag(sql, i) is { } tag)
            {
                var end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                result.Append('?');
                i = end < 0 ? sql.Length : end + tag.Length;
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    /// <summary>The opening tag of a dollar-quoted string at <paramref name="index"/> (<c>$$</c> or <c>$tag$</c>), or null for a parameter.</summary>
    private static string? DollarTag(string sql, int index)
    {
        var end = index + 1;
        while (end < sql.Length && (char.IsLetter(sql[end]) || sql[end] == '_' || (end > index + 1 && char.IsDigit(sql[end]))))
        {
            end++;
        }

        return end < sql.Length && sql[end] == '$' ? sql[index..(end + 1)] : null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // A number that is not part of an identifier, a parameter ($1) or a quoted name.
    [GeneratedRegex(@"(?<![\w$""\.])-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b(?![\w""])", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"(\bIN\s*)\(\s*(?:\?|\$\d+)(?:\s*,\s*(?:\?|\$\d+))*\s*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InListPattern();

    [GeneratedRegex(@"\bARRAY\s*\[\s*(?:\?|\$\d+)(?:\s*,\s*(?:\?|\$\d+))*\s*\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ArrayPattern();

    [GeneratedRegex(@"\bVALUES\s*(\([^()]*\))(?:\s*,\s*\([^()]*\))+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ValuesPattern();
}
