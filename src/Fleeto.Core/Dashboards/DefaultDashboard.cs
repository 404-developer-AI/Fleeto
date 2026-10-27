namespace Fleeto.Core.Dashboards;

/// <summary>
/// The built-in default dashboard (DASHBOARDS.md §2), shown to every user whose roles have no dashboard of the instance. Defined
/// here, like the built-in roles: it cannot be changed, only copied. A feature that adds a tile type decides here whether the
/// default dashboard shows it.
/// </summary>
public static class DefaultDashboard
{
    public const string Name = "Default";

    public const string Description = "The built-in dashboard, for every role without a dashboard of its own. Copy it to change it.";

    public static DashboardLayout Layout { get; } = new()
    {
        PullUp = true,
        Scope = TileScope.Everything,
        Tiles =
        [
            Tile("online", DashboardTiles.EndpointsOnline, 0, 0, 2, 2),
            Tile("offline", DashboardTiles.EndpointsOffline, 2, 0, 2, 2),
            Tile("alerts", DashboardTiles.AlertsOpen, 4, 0, 2, 2),
            Tile("agents", DashboardTiles.AgentsOutOfDate, 6, 0, 2, 2),
            Tile("patches", DashboardTiles.PatchCompliance, 8, 0, 2, 2, new TileSettings { HideWhenEmpty = true }),
            Tile("vulnerabilities", DashboardTiles.Vulnerabilities, 10, 0, 2, 2, new TileSettings { HideWhenEmpty = true }),
            Tile("licenses", DashboardTiles.Licenses, 0, 2, 2, 2),
            Tile("health", DashboardTiles.InstanceHealth, 2, 2, 2, 2),
            Tile("security", DashboardTiles.Security, 4, 2, 2, 2),
            Tile("backups", DashboardTiles.Backups, 6, 2, 2, 2),
            Tile("alert-list", DashboardTiles.AlertList, 0, 4, 7, 9),
            Tile("advisories", DashboardTiles.Advisories, 7, 4, 5, 4),
            Tile("activity", DashboardTiles.RecentActivity, 7, 8, 5, 5),
            Tile("jobs", DashboardTiles.RecentJobs, 7, 13, 5, 5),
        ]
    };

    private static DashboardTile Tile(string id, string type, int x, int y, int w, int h, TileSettings? settings = null) =>
        new() { Id = id, Type = type, X = x, Y = y, W = w, H = h, Settings = settings ?? new TileSettings() };
}
