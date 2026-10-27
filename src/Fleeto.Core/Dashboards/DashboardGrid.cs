namespace Fleeto.Core.Dashboards;

/// <summary>
/// The grid of a dashboard (DASHBOARDS.md §4): 12 columns on a wide screen, 6 on a medium one, and tiles under each other on a
/// small one. Tiles never overlap. This is the only placement algorithm: the designer script proposes a place, the server places
/// the tile here and sends every position back, so the browser never decides a layout of its own.
/// </summary>
public static class DashboardGrid
{
    public const int Columns = 12;
    public const int MediumColumns = 6;

    /// <summary>The lowest row a tile may reach, so a layout stays a dashboard and not a scroll of thousands of rows.</summary>
    public const int MaxRows = 400;

    public const int MaxTiles = 60;

    /// <summary>
    /// Places the tile <paramref name="id"/> at <paramref name="target"/> (clamped to the grid and its type's sizes) and moves every
    /// tile it lands on down, then pulls every tile up when the layout asks for it.
    /// </summary>
    public static IReadOnlyList<DashboardTile> Move(IReadOnlyList<DashboardTile> tiles, string id, GridRect target, bool pullUp)
    {
        var moved = tiles.FirstOrDefault(t => t.Id == id);
        if (moved is null)
        {
            return tiles;
        }

        var placed = moved.At(Clamp(target, DashboardTiles.Find(moved.Type)));
        var result = PushDown(placed, tiles.Where(t => t.Id != id));
        return pullUp ? Compact(result) : result;
    }

    /// <summary>Adds a tile at the first free place from the top, at its default size.</summary>
    public static IReadOnlyList<DashboardTile> Add(IReadOnlyList<DashboardTile> tiles, DashboardTile tile, bool pullUp)
    {
        var type = DashboardTiles.Find(tile.Type);
        var size = type?.Default ?? new TileSize(Math.Max(1, tile.W), Math.Max(1, tile.H));
        var rect = FirstFree(tiles.Select(t => t.Rect).ToList(), size.W, size.H, Columns);
        var result = tiles.Append(tile.At(rect)).ToList();
        return pullUp ? Compact(result) : result;
    }

    /// <summary>Puts <paramref name="placed"/> first and moves every other tile down past whatever it overlaps, in reading order.</summary>
    public static IReadOnlyList<DashboardTile> PushDown(DashboardTile placed, IEnumerable<DashboardTile> others)
    {
        var result = new List<DashboardTile> { placed };
        foreach (var tile in others.OrderBy(t => t.Y).ThenBy(t => t.X))
        {
            var rect = tile.Rect;
            while (result.FirstOrDefault(r => r.Rect.Overlaps(rect)) is { } blocking)
            {
                rect = rect with { Y = blocking.Rect.Bottom };
            }

            result.Add(tile.At(rect));
        }

        return result;
    }

    /// <summary>Moves every tile up as far as it can, in reading order, so no holes remain above a tile.</summary>
    public static IReadOnlyList<DashboardTile> Compact(IEnumerable<DashboardTile> tiles)
    {
        var result = new List<DashboardTile>();
        foreach (var tile in tiles.OrderBy(t => t.Y).ThenBy(t => t.X))
        {
            var rect = tile.Rect;
            while (rect.Y > 0 && !result.Any(r => r.Rect.Overlaps(rect with { Y = rect.Y - 1 })))
            {
                rect = rect with { Y = rect.Y - 1 };
            }

            result.Add(tile.At(rect));
        }

        return result;
    }

    /// <summary>
    /// The layout for a screen of <paramref name="columns"/> columns: the tiles flow in reading order like text, each at most that wide,
    /// at the first free place after the tile before it, so no column stays empty beside a stack of tiles.
    /// </summary>
    public static IReadOnlyList<DashboardTile> Narrow(IEnumerable<DashboardTile> tiles, int columns)
    {
        var result = new List<DashboardTile>();
        var cursor = new GridRect(0, 0, 0, 0);
        foreach (var tile in tiles.OrderBy(t => t.Y).ThenBy(t => t.X))
        {
            var w = Math.Min(tile.W, columns);
            var rect = FirstFreeAfter(result.Select(r => r.Rect).ToList(), w, tile.H, columns, cursor);
            result.Add(tile.At(rect));
            cursor = rect;
        }

        return result;
    }

    private static GridRect FirstFreeAfter(IReadOnlyList<GridRect> taken, int w, int h, int columns, GridRect after)
    {
        for (var y = after.Y; y < MaxRows; y++)
        {
            for (var x = y == after.Y ? after.Right : 0; x + w <= columns; x++)
            {
                var rect = new GridRect(x, y, w, h);
                if (!taken.Any(t => t.Overlaps(rect)))
                {
                    return rect;
                }
            }
        }

        return new GridRect(0, taken.Count == 0 ? 0 : taken.Max(t => t.Bottom), w, h);
    }

    /// <summary>The first place from the top left where a tile of this size fits.</summary>
    public static GridRect FirstFree(IReadOnlyList<GridRect> taken, int w, int h, int columns)
    {
        w = Math.Clamp(w, 1, columns);
        for (var y = 0; y < MaxRows; y++)
        {
            for (var x = 0; x + w <= columns; x++)
            {
                var rect = new GridRect(x, y, w, h);
                if (!taken.Any(t => t.Overlaps(rect)))
                {
                    return rect;
                }
            }
        }

        return new GridRect(0, taken.Count == 0 ? 0 : taken.Max(t => t.Bottom), w, h);
    }

    /// <summary>Keeps a place inside the grid and inside the sizes of its type.</summary>
    public static GridRect Clamp(GridRect rect, TileType? type)
    {
        var min = type?.Min ?? new TileSize(1, 1);
        var max = type?.Max ?? new TileSize(Columns, MaxRows);
        var w = Math.Clamp(rect.W, min.W, Math.Min(max.W, Columns));
        var h = Math.Clamp(rect.H, min.H, max.H);
        var x = Math.Clamp(rect.X, 0, Columns - w);
        var y = Math.Clamp(rect.Y, 0, MaxRows - h);
        return new GridRect(x, y, w, h);
    }

    /// <summary>The number of rows the tiles take.</summary>
    public static int Rows(IEnumerable<DashboardTile> tiles) => tiles.Select(t => t.Y + t.H).DefaultIfEmpty(0).Max();

    /// <summary>A new tile id, unique within the dashboard.</summary>
    public static string NewTileId(IEnumerable<DashboardTile> tiles)
    {
        var taken = tiles.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        string id;
        do
        {
            id = "t" + Guid.NewGuid().ToString("N")[..8];
        }
        while (taken.Contains(id));

        return id;
    }
}
