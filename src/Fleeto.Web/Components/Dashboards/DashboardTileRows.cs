using Fleeto.Core.Dashboards;

namespace Fleeto.Web.Components.Dashboards;

/// <summary>How many rows a list tile reads: what fits its height, so a taller tile shows more without a read of everything.</summary>
public static class DashboardTileRows
{
    public static int For(DashboardTile tile) => Math.Clamp(tile.H * 2 - 1, 3, 50);
}
