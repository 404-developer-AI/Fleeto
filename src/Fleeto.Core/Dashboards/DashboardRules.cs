using System.Text.RegularExpressions;

namespace Fleeto.Core.Dashboards;

/// <summary>A dashboard as the rule that picks one for a user sees it: its order and the roles it is linked to.</summary>
public sealed record DashboardChoice(Guid Id, int Priority, bool Published, IReadOnlyCollection<Guid> RoleIds);

/// <summary>
/// Rules of the dashboard designer (0.8.0, DASHBOARDS.md): which dashboard a user sees, and what a layout may hold. The server
/// checks every layout here on save, publish and import, whatever the designer sent.
/// </summary>
public static partial class DashboardRules
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 500;
    public const int MaxTitleLength = 60;
    public const int MaxTextLength = 4000;
    public const int MaxUrlLength = 500;
    public const int MaxScopeIds = 100;
    public const int MaxThreshold = 1_000_000;
    public const int MaxDashboards = 100;
    public const int MaxVersions = 20;

    /// <summary>The largest import file accepted.</summary>
    public const int MaxImportBytes = 256 * 1024;

    public static readonly IReadOnlyList<int> ChartDays = [7, 30, 90];

    /// <summary>
    /// The dashboard a user sees: the first published one in priority order that is linked to one of its roles, or null for the
    /// built-in default dashboard. One rule, used by the page and by Settings when it says which dashboard a role sees.
    /// </summary>
    public static Guid? Pick(IEnumerable<DashboardChoice> dashboards, IReadOnlyCollection<Guid> roleIds) =>
        dashboards.Where(d => d.Published && d.RoleIds.Any(roleIds.Contains))
            .OrderBy(d => d.Priority)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefault();

    public static string? NameProblem(string? name)
    {
        var value = name?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return "Enter a name for the dashboard.";
        }

        return value.Length > MaxNameLength ? $"A dashboard name has at most {MaxNameLength} characters." : null;
    }

    /// <summary>
    /// Checks a layout and returns it cleaned, or the problem to show. Settings a tile type does not offer are cleared, so a stored
    /// tile never carries one. A tile type this release does not know is kept as it is, unless <paramref name="allowUnknown"/> is
    /// false (an import), but still counts for overlap.
    /// </summary>
    public static (DashboardLayout? Layout, string? Problem) Validate(DashboardLayout? layout, bool allowUnknown)
    {
        if (layout is null)
        {
            return (null, "The dashboard could not be read. Refresh the page and try again.");
        }

        if (layout.Schema != DashboardLayout.CurrentSchema)
        {
            return (null, $"This dashboard was made by another version of Fleeto (layout version {layout.Schema}). Update Fleeto first.");
        }

        if (layout.Tiles.Count > DashboardGrid.MaxTiles)
        {
            return (null, $"A dashboard holds at most {DashboardGrid.MaxTiles} tiles. Remove a tile first.");
        }

        if (ScopeProblem(layout.Scope, onTile: false) is { } dashboardScope)
        {
            return (null, dashboardScope);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<DashboardTile>();
        foreach (var tile in layout.Tiles)
        {
            if (!TileIdPattern().IsMatch(tile.Id) || !ids.Add(tile.Id))
            {
                return (null, "A tile of the dashboard has no valid id. Refresh the page and try again.");
            }

            var type = DashboardTiles.Find(tile.Type);
            if (type is null && (!allowUnknown || tile.Type.Length > 100))
            {
                return (null, $"The tile type \"{Cut(tile.Type, 40)}\" does not exist in this version of Fleeto.");
            }

            var min = type?.Min ?? new TileSize(1, 1);
            var max = type?.Max ?? new TileSize(DashboardGrid.Columns, DashboardGrid.MaxRows);
            if (tile.W < min.W || tile.W > max.W || tile.H < min.H || tile.H > max.H || tile.X < 0 || tile.Y < 0 ||
                tile.X + tile.W > DashboardGrid.Columns || tile.Y + tile.H > DashboardGrid.MaxRows)
            {
                return (null, $"The tile \"{type?.Name ?? tile.Type}\" does not fit its place or size. Move or resize it.");
            }

            if (cleaned.FirstOrDefault(c => c.Rect.Overlaps(tile.Rect)) is not null)
            {
                return (null, "Two tiles overlap. Move one of them.");
            }

            if (type is null)
            {
                cleaned.Add(tile);
                continue;
            }

            var (settings, problem) = Clean(type, tile.Settings);
            if (problem is not null)
            {
                return (null, $"{type.Name}: {problem}");
            }

            cleaned.Add(tile with { Settings = settings! });
        }

        return (layout with { Scope = layout.Scope.Normalized(), Tiles = cleaned }, null);
    }

    /// <summary>The settings of a tile with only what its type offers, or the problem.</summary>
    public static (TileSettings? Settings, string? Problem) Clean(TileType type, TileSettings settings)
    {
        var title = string.IsNullOrWhiteSpace(settings.Title) ? null : settings.Title.Trim();
        if (title is { Length: > MaxTitleLength })
        {
            return (null, $"a title has at most {MaxTitleLength} characters.");
        }

        if (title is not null && title.Any(char.IsControl))
        {
            return (null, "a title cannot hold control characters.");
        }

        TileScope? scope = null;
        if (type.Has(TileOptions.Scope) && settings.Scope is { } own)
        {
            if (ScopeProblem(own, onTile: true) is { } problem)
            {
                return (null, problem);
            }

            scope = own.Mode == TileScopeMode.Dashboard && own.Class is null
                ? null
                : own.Normalized() with { Class = type.Has(TileOptions.Class) ? own.Class : null };
        }

        var thresholds = type.Has(TileOptions.ThresholdsAbove) || type.Has(TileOptions.ThresholdsBelow);
        int? warning = thresholds ? settings.Warning : null, critical = thresholds ? settings.Critical : null;
        if (warning is < 0 or > MaxThreshold || critical is < 0 or > MaxThreshold)
        {
            return (null, $"a threshold is a number from 0 to {MaxThreshold}.");
        }

        if (warning is { } w && critical is { } c &&
            (type.Has(TileOptions.ThresholdsAbove) ? c < w : c > w))
        {
            return (null, type.Has(TileOptions.ThresholdsAbove)
                ? "the critical threshold cannot be lower than the warning threshold."
                : "the critical threshold cannot be higher than the warning threshold.");
        }

        var days = type.Has(TileOptions.Days) ? settings.Days ?? 30 : (int?)null;
        if (days is { } d && !ChartDays.Contains(d))
        {
            return (null, "a chart shows 7, 30 or 90 days.");
        }

        var text = type.Has(TileOptions.Text) ? settings.Text ?? string.Empty : null;
        if (text is { Length: > MaxTextLength })
        {
            return (null, $"a text has at most {MaxTextLength} characters.");
        }

        var url = type.Has(TileOptions.Url) ? settings.Url?.Trim() : null;
        if (type.Has(TileOptions.Url) && LinkProblem(url) is { } linkProblem)
        {
            return (null, linkProblem);
        }

        var kinds = type.Has(TileOptions.AlertKinds) ? settings.AlertKinds?.Distinct().Order().ToList() : null;
        if (kinds is { Count: 0 } || (kinds is not null && kinds.Any(k => !Enum.IsDefined(k))))
        {
            kinds = null;
        }

        if (settings.Accent is { } accent && !Enum.IsDefined(accent))
        {
            return (null, "the accent is not a color of the palette.");
        }

        return (new TileSettings
        {
            Title = title,
            Accent = settings.Accent,
            HideOnSmallScreens = settings.HideOnSmallScreens,
            HideWhenEmpty = type.Has(TileOptions.HideWhenEmpty) && settings.HideWhenEmpty,
            Scope = scope,
            Warning = warning,
            Critical = critical,
            Severity = type.Has(TileOptions.Severity) ? settings.Severity : null,
            AlertKinds = kinds,
            IncludeHeld = type.Has(TileOptions.IncludeHeld) && settings.IncludeHeld,
            Tier = type.Has(TileOptions.Tier) ? settings.Tier : null,
            FailedOnly = type.Has(TileOptions.FailedOnly) && settings.FailedOnly,
            Days = days,
            Grouping = type.Has(TileOptions.Grouping) ? settings.Grouping ?? EndpointGrouping.OperatingSystem : null,
            Text = text,
            Url = url,
            Extra = settings.Extra
        }, null);
    }

    /// <summary>
    /// A link tile goes to a page of Fleeto (a path that starts with one slash) or to an https address, never to another scheme
    /// such as <c>javascript:</c> or <c>data:</c>.
    /// </summary>
    public static string? LinkProblem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "enter the address of the link.";
        }

        if (url.Length > MaxUrlLength || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return $"a link is an address of at most {MaxUrlLength} characters without spaces.";
        }

        if (url.StartsWith('/'))
        {
            return url.StartsWith("//", StringComparison.Ordinal) || url.Contains('\\')
                ? "a path of Fleeto starts with one slash, such as /alerts."
                : null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host) &&
               string.IsNullOrEmpty(uri.UserInfo)
            ? null
            : "a link goes to a path of Fleeto such as /alerts, or to an https address.";
    }

    /// <summary>A link to an outside address opens in a new tab.</summary>
    public static bool IsExternal(string url) => !url.StartsWith('/');

    private static string? ScopeProblem(TileScope scope, bool onTile)
    {
        if (!Enum.IsDefined(scope.Mode) || (!onTile && scope.Mode == TileScopeMode.Dashboard))
        {
            return "The scope of the dashboard is not valid.";
        }

        if (scope.Class is { } cls && !Enum.IsDefined(cls))
        {
            return "The scope names a class that does not exist.";
        }

        if (scope.ClientIds.Count > MaxScopeIds || scope.SiteIds.Count > MaxScopeIds || scope.TagIds.Count > MaxScopeIds)
        {
            return $"A scope names at most {MaxScopeIds} clients, sites or tags.";
        }

        return scope.Mode is TileScopeMode.Clients or TileScopeMode.Sites or TileScopeMode.Tags && scope.Ids.Count == 0
            ? scope.Mode switch
            {
                TileScopeMode.Clients => "Choose at least one client for the scope.",
                TileScopeMode.Sites => "Choose at least one site for the scope.",
                _ => "Choose at least one tag for the scope."
            }
            : null;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex TileIdPattern();
}
