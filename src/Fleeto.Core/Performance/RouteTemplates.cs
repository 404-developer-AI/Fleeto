namespace Fleeto.Core.Performance;

/// <summary>
/// Finds the route template a path belongs to, so a Blazor interaction is keyed by its page (<c>/clients/{ClientId:guid}</c>) and
/// never by its URL. Matches like the Blazor router does for what Fleeto uses: literal segments without case, parameters with an
/// optional constraint, optional parameters and catch-all parameters. A literal segment beats a parameter, so
/// <c>/settings/tasks/new</c> is not taken for <c>/settings/tasks/{TaskId:guid}</c>.
/// </summary>
public sealed class RouteTemplates
{
    public const string Unmatched = "unmatched";

    private readonly IReadOnlyList<Template> _templates;

    public RouteTemplates(IEnumerable<string> templates)
    {
        _templates = templates
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Parse)
            .OrderByDescending(t => t.Literals)
            .ThenByDescending(t => t.Segments.Count)
            .ToList();
    }

    public IReadOnlyList<string> All => _templates.Select(t => t.Text).ToList();

    /// <summary>The template of an absolute or relative URL, or <see cref="Unmatched"/>.</summary>
    public string Match(string? url)
    {
        var path = PathOf(url);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var template in _templates)
        {
            if (Matches(template, segments))
            {
                return template.Text;
            }
        }

        return Unmatched;
    }

    private static string PathOf(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "/";
        }

        var path = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath;
        }

        var cut = path.IndexOfAny(['?', '#']);
        return cut >= 0 ? path[..cut] : path;
    }

    private static bool Matches(Template template, string[] segments)
    {
        var s = 0;
        for (var t = 0; t < template.Segments.Count; t++)
        {
            var part = template.Segments[t];
            if (part.CatchAll)
            {
                return true;
            }

            if (s >= segments.Length)
            {
                // Only optional parameters may be left over.
                return template.Segments.Skip(t).All(p => p.Optional || p.CatchAll);
            }

            var segment = Uri.UnescapeDataString(segments[s]);
            if (part.Literal is { } literal)
            {
                if (!string.Equals(literal, segment, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else if (!Satisfies(part.Constraint, segment))
            {
                return false;
            }

            s++;
        }

        return s == segments.Length;
    }

    private static bool Satisfies(string? constraint, string segment) => constraint?.ToLowerInvariant() switch
    {
        null => segment.Length > 0,
        "guid" => Guid.TryParse(segment, out _),
        "int" => int.TryParse(segment, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _),
        "long" => long.TryParse(segment, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _),
        "bool" => bool.TryParse(segment, out _),
        _ => segment.Length > 0
    };

    private static Template Parse(string text)
    {
        var parts = new List<Part>();
        foreach (var raw in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith('{') && raw.EndsWith('}'))
            {
                var inner = raw[1..^1];
                var catchAll = inner.StartsWith('*');
                inner = inner.TrimStart('*');
                var optional = inner.EndsWith('?');
                inner = inner.TrimEnd('?');
                var colon = inner.IndexOf(':');
                parts.Add(new Part(null, colon >= 0 ? inner[(colon + 1)..] : null, optional, catchAll));
            }
            else
            {
                parts.Add(new Part(raw, null, false, false));
            }
        }

        var normalized = "/" + string.Join('/', text.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return new Template(normalized, parts, parts.Count(p => p.Literal is not null));
    }

    private sealed record Part(string? Literal, string? Constraint, bool Optional, bool CatchAll);

    private sealed record Template(string Text, IReadOnlyList<Part> Segments, int Literals);
}
