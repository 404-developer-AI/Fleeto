using System.Text;
using System.Text.RegularExpressions;

namespace Fleeto.Core.Performance;

/// <summary>
/// The last line of defence for everything the performance monitor shows or exports (PERFORMANCE.md §9, spec §11). Keys are
/// value-free by construction; this catches what slips through: a value in a hand-written SQL constant, an exception type name,
/// a label an admin typed. SQL is normalized first, so no literal survives; free text has email addresses, GUIDs, IP
/// addresses, tokens and host names masked, and the words given at construction (the client codes and endpoint host names of the
/// instance) masked as well.
/// </summary>
public sealed partial class PerfRedactor
{
    public const int MaxRouteLength = 200;

    private readonly IReadOnlyDictionary<string, string> _words;

    /// <param name="words">Word → placeholder, compared without case, e.g. a client code → <c>{client}</c>.</param>
    public PerfRedactor(IReadOnlyDictionary<string, string>? words = null)
    {
        _words = words is null || words.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(words, StringComparer.OrdinalIgnoreCase);
    }

    public static PerfRedactor Default { get; } = new();

    /// <summary>
    /// Builds the words of an instance: client codes become <c>{client}</c>, endpoint host names <c>{host}</c>. Words shorter than
    /// three characters are left out, because they would mask ordinary words.
    /// </summary>
    public static PerfRedactor ForInstance(IEnumerable<string> clientCodes, IEnumerable<string> hostnames)
    {
        var words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hostnames.Where(h => h.Length >= 3))
        {
            words[host] = "{host}";
            if (host.IndexOf('.') is > 0 and var dot)
            {
                words.TryAdd(host[..dot], "{host}");
            }
        }

        foreach (var code in clientCodes.Where(c => c.Length >= 3))
        {
            words[code] = "{client}";
        }

        return new PerfRedactor(words);
    }

    /// <summary>Free text: exception type names, route templates, explanations.</summary>
    public string Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = EmailPattern().Replace(text, "{email}");
        result = GuidPattern().Replace(result, "{guid}");
        result = JwtPattern().Replace(result, "{token}");
        result = Ipv4Pattern().Replace(result, "{ip}");
        result = Ipv6Pattern().Replace(result, m => m.Value.Contains("::", StringComparison.Ordinal) || m.Value.Count(c => c == ':') == 7 ? "{ip}" : m.Value);
        result = HostPattern().Replace(result, "{host}");
        result = TokenPattern().Replace(result, m => m.Value.Any(char.IsDigit) && m.Value.Any(char.IsLetter) ? "{token}" : m.Value);
        if (_words.Count > 0)
        {
            result = WordPattern().Replace(result, m => _words.TryGetValue(m.Value, out var placeholder) ? placeholder : m.Value);
        }

        return result;
    }

    /// <summary>Text an admin typed, such as a marker label: like <see cref="Text"/>, and every run of digits masked as well.</summary>
    public string AdminText(string? text) => DigitsPattern().Replace(Text(text), "{n}");

    /// <summary>SQL: normalized (every literal to <c>?</c>), then masked as text.</summary>
    public string Sql(string? sql) => Text(SqlFingerprint.Normalize(sql));

    /// <summary>
    /// A route as a browser or a request reports it: query and fragment removed, only safe characters kept, every segment that
    /// looks like a value (digits, a GUID, long hexadecimal, an email address, over 40 characters) as <c>{id}</c>. Template
    /// segments in braces stay. Null when nothing sane is left.
    /// </summary>
    public static string? Route(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return null;
        }

        var path = route;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder();
        foreach (var raw in segments)
        {
            var segment = raw;
            if (!(segment.StartsWith('{') && segment.EndsWith('}')))
            {
                segment = Uri.UnescapeDataString(segment);
                if (IsValueLike(segment))
                {
                    segment = "{id}";
                }
                else if (!SafeSegmentPattern().IsMatch(segment))
                {
                    segment = "{id}";
                }
            }
            else if (!TemplateSegmentPattern().IsMatch(segment))
            {
                segment = "{id}";
            }

            result.Append('/').Append(segment);
        }

        var value = result.Length == 0 ? "/" : result.ToString();
        if (value.Length > MaxRouteLength)
        {
            value = value[..MaxRouteLength];
        }

        return value;
    }

    private static bool IsValueLike(string segment) =>
        segment.Length > 40 ||
        segment.Contains('@') ||
        DigitsOnlyPattern().IsMatch(segment) ||
        Guid.TryParse(segment, out _) ||
        LongHexPattern().IsMatch(segment);

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![\w:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6Pattern();

    // At least three lowercase labels and a top-level domain of letters: rmm.customer.example. Never "pg_catalog.pg_class" or a
    // type name such as "System.Net.Http.HttpRequestException", which is written with capitals.
    [GeneratedRegex(@"\b(?:[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.){2,}[a-z]{2,24}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"[A-Za-z0-9_\-+/=]{32,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[\p{L}\p{N}_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsPattern();

    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsOnlyPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{16,}$", RegexOptions.CultureInvariant)]
    private static partial Regex LongHexPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSegmentPattern();

    [GeneratedRegex(@"^\{\*{0,2}[A-Za-z0-9_]+(?::[A-Za-z0-9_]+)*\??\}$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateSegmentPattern();
}
