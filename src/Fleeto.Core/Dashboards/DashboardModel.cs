using System.Text.Json;
using System.Text.Json.Serialization;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Dashboards;

/// <summary>
/// The layout of a dashboard (0.8.0, DASHBOARDS.md §8): its tiles on the grid and the scope they follow. Stored as jsonb, with
/// <see cref="Schema"/> so a later release can migrate it. What a release does not know (a newer tile type or setting) is kept in
/// <see cref="Extra"/> and written back unchanged, so an image rollback never loses part of a dashboard.
/// </summary>
public sealed record DashboardLayout
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; } = CurrentSchema;

    /// <summary>After every change each tile moves up as far as it can, so no holes remain.</summary>
    public bool PullUp { get; init; } = true;

    /// <summary>The scope a tile follows unless it has its own. Never <see cref="TileScopeMode.Dashboard"/>.</summary>
    public TileScope Scope { get; init; } = TileScope.Everything;

    public IReadOnlyList<DashboardTile> Tiles { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    public static DashboardLayout Empty { get; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, DashboardJson.Options);

    /// <summary>Reads a stored or imported layout; null when it is not a layout at all.</summary>
    public static DashboardLayout? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DashboardLayout>(json, DashboardJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public DashboardTile? Find(string id) => Tiles.FirstOrDefault(t => t.Id == id);

    public DashboardLayout WithTiles(IEnumerable<DashboardTile> tiles) => this with { Tiles = tiles.ToList() };
}

/// <summary>One tile on the grid: its type from <see cref="DashboardTiles"/>, its place in columns and rows, and its settings.</summary>
public sealed record DashboardTile
{
    /// <summary>Unique within the dashboard, lower case letters, digits and dashes.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The key of the tile type; a key this release does not know is kept and skipped on screen.</summary>
    public string Type { get; init; } = string.Empty;

    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }

    public TileSettings Settings { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore]
    public GridRect Rect => new(X, Y, W, H);

    public DashboardTile At(GridRect rect) => this with { X = rect.X, Y = rect.Y, W = rect.W, H = rect.H };
}

/// <summary>A place on the grid in columns and rows.</summary>
public readonly record struct GridRect(int X, int Y, int W, int H)
{
    public int Bottom => Y + H;
    public int Right => X + W;

    public bool Overlaps(GridRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

/// <summary>
/// The settings of a tile. One record for every tile type: the catalog says which fields a type uses
/// (<see cref="TileOptions"/>), and validation clears the others, so a stored tile never carries a setting it ignores.
/// </summary>
public sealed record TileSettings
{
    /// <summary>A title of its own instead of the name of the tile type.</summary>
    public string? Title { get; init; }

    /// <summary>A color from the palette of Fleeto for the border of the tile.</summary>
    public TagColor? Accent { get; init; }

    public bool HideOnSmallScreens { get; init; }

    /// <summary>The tile is left out, and the tiles below move up, while it has nothing to show.</summary>
    public bool HideWhenEmpty { get; init; }

    /// <summary>Null follows the scope of the dashboard.</summary>
    public TileScope? Scope { get; init; }

    /// <summary>From this value the tile turns warning (or below it, for a tile where lower is worse).</summary>
    public int? Warning { get; init; }

    public int? Critical { get; init; }

    public AlertSeverity? Severity { get; init; }

    /// <summary>Empty is every kind.</summary>
    public IReadOnlyList<AlertKind>? AlertKinds { get; init; }

    public bool IncludeHeld { get; init; }

    public EndpointTier? Tier { get; init; }

    public bool FailedOnly { get; init; }

    /// <summary>The period of a chart in days: 7, 30 or 90.</summary>
    public int? Days { get; init; }

    /// <summary>How a chart of endpoints is grouped.</summary>
    public EndpointGrouping? Grouping { get; init; }

    /// <summary>Markdown of a text tile.</summary>
    public string? Text { get; init; }

    /// <summary>The address of a link tile: a path of Fleeto or an https address.</summary>
    public string? Url { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public enum EndpointGrouping
{
    OperatingSystem,
    Class,
    Tier
}

public enum TileScopeMode
{
    /// <summary>The scope of the dashboard (only on a tile).</summary>
    Dashboard,

    /// <summary>Every client the user sees.</summary>
    All,
    Clients,
    Sites,

    /// <summary>The clients that carry at least one of the tags.</summary>
    Tags
}

/// <summary>
/// What a tile looks at. A scope only narrows: the user still sees nothing outside its groups. Names of clients, sites and tags
/// are never stored, only their ids, which are resolved when the dashboard is shown.
/// </summary>
public sealed record TileScope
{
    public TileScopeMode Mode { get; init; } = TileScopeMode.All;
    public IReadOnlyList<Guid> ClientIds { get; init; } = [];
    public IReadOnlyList<Guid> SiteIds { get; init; } = [];
    public IReadOnlyList<Guid> TagIds { get; init; } = [];

    /// <summary>Only servers or only workstations, for the tiles that count endpoints or their alerts.</summary>
    public EndpointClass? Class { get; init; }

    public static TileScope Everything { get; } = new();

    public static TileScope FollowDashboard { get; } = new() { Mode = TileScopeMode.Dashboard };

    /// <summary>The ids this scope narrows to, for its mode.</summary>
    [JsonIgnore]
    public IReadOnlyList<Guid> Ids => Mode switch
    {
        TileScopeMode.Clients => ClientIds,
        TileScopeMode.Sites => SiteIds,
        TileScopeMode.Tags => TagIds,
        _ => []
    };

    /// <summary>Keeps only the ids of its own mode, sorted and without duplicates, so equal scopes compare equal.</summary>
    public TileScope Normalized() => new()
    {
        Mode = Mode,
        ClientIds = Mode == TileScopeMode.Clients ? ClientIds.Distinct().Order().ToList() : [],
        SiteIds = Mode == TileScopeMode.Sites ? SiteIds.Distinct().Order().ToList() : [],
        TagIds = Mode == TileScopeMode.Tags ? TagIds.Distinct().Order().ToList() : [],
        Class = Class
    };

    /// <summary>A stable text of the scope, so tiles with the same scope share one read.</summary>
    public string Key()
    {
        var normalized = Normalized();
        return $"{normalized.Mode}:{normalized.Class}:{string.Join(',', normalized.Ids)}";
    }
}

public static class DashboardJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16
    };
}
