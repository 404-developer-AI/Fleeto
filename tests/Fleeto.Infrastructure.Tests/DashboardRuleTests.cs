using Fleeto.Core.Dashboards;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The dashboard designer (0.8.0, DASHBOARDS.md): the catalog of tile types, the one placement algorithm of the grid, the checks of a
/// layout the server makes whatever the designer sent, and the rule that picks the dashboard of a user.
/// </summary>
public class DashboardRuleTests
{
    private static DashboardTile Tile(string id, string type, int x, int y, int w, int h, TileSettings? settings = null) =>
        new() { Id = id, Type = type, X = x, Y = y, W = w, H = h, Settings = settings ?? new TileSettings() };

    [Fact]
    public void Every_tile_type_has_a_stable_key_a_permission_sizes_that_fit_and_a_source_when_it_reads_data()
    {
        var keys = DashboardTiles.All.Select(t => t.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(keys.Intersect(DashboardTiles.Retired));
        foreach (var type in DashboardTiles.All)
        {
            Assert.Matches("^[a-z0-9-]+\\.[a-z0-9-]+$", type.Key);
            Assert.Contains(type.Group, DashboardTiles.Groups);
            Assert.False(string.IsNullOrWhiteSpace(type.Name) || string.IsNullOrWhiteSpace(type.Description), type.Key);
            Assert.All(type.Permissions, p => Assert.True(Permissions.IsKnown(p), $"{type.Key}: {p}"));

            // Only content and a tile that is deliberately for everyone may show without a permission.
            Assert.True(type.Permissions.Count > 0 || type.Kind == TileKind.Content || type.ForEveryone, type.Key);
            Assert.Equal(type.Kind == TileKind.Content, type.Group == DashboardTiles.ContentGroup);

            Assert.InRange(type.Min.W, 1, DashboardGrid.Columns);
            Assert.InRange(type.Max.W, type.Min.W, DashboardGrid.Columns);
            Assert.InRange(type.Min.H, 1, DashboardGrid.MaxRows);
            Assert.InRange(type.Max.H, type.Min.H, DashboardGrid.MaxRows);
            Assert.InRange(type.Default.W, type.Min.W, type.Max.W);
            Assert.InRange(type.Default.H, type.Min.H, type.Max.H);
            Assert.True(type.Kind == TileKind.Content || type.Key == DashboardTiles.Advisories || type.Source is not null, type.Key);

            // A tile of the instance as a whole needs a permission of the administration of the instance, or is for everyone.
            if (type.InstanceWide && type.Kind != TileKind.Content && !type.ForEveryone && type.Key != DashboardTiles.Advisories)
            {
                Assert.All(type.Permissions, p => Assert.True(Permissions.Find(p)!.InstanceWide, $"{type.Key}: {p}"));
            }

            // The default settings of every type validate.
            Assert.Null(DashboardRules.Clean(type, new TileSettings()).Problem is { } p && type.Kind != TileKind.Content ? p : null);
        }
    }

    [Fact]
    public void The_default_dashboard_validates_and_keeps_what_the_dashboard_showed_before()
    {
        var (layout, problem) = DashboardRules.Validate(DefaultDashboard.Layout, allowUnknown: false);
        Assert.Null(problem);
        Assert.Equal(DefaultDashboard.Layout.Tiles.Count, layout!.Tiles.Count);
        var types = DefaultDashboard.Layout.Tiles.Select(t => t.Type).ToHashSet();
        Assert.Superset(types, new HashSet<string>
        {
            DashboardTiles.EndpointsOnline, DashboardTiles.EndpointsOffline, DashboardTiles.AlertsOpen, DashboardTiles.AgentsOutOfDate,
            DashboardTiles.PatchCompliance, DashboardTiles.Vulnerabilities, DashboardTiles.Licenses, DashboardTiles.InstanceHealth,
            DashboardTiles.Security, DashboardTiles.Backups, DashboardTiles.AlertList, DashboardTiles.Advisories, DashboardTiles.RecentActivity,
            DashboardTiles.RecentJobs
        });
    }

    [Fact]
    public void A_layout_survives_its_json_with_what_this_release_does_not_know()
    {
        const string json = """
            {"schema":1,"pullUp":true,"scope":{"mode":"All","clientIds":[],"siteIds":[],"tagIds":[]},"future":"kept",
             "tiles":[{"id":"a","type":"alerts.open","x":0,"y":0,"w":2,"h":2,"settings":{"title":"Alerts","newSetting":5},"extra":true},
                      {"id":"b","type":"future.tile","x":2,"y":0,"w":3,"h":2,"settings":{}}]}
            """;
        var layout = DashboardLayout.FromJson(json)!;
        var (valid, problem) = DashboardRules.Validate(layout, allowUnknown: true);
        Assert.Null(problem);
        var again = DashboardLayout.FromJson(valid!.ToJson())!;
        Assert.Equal("future.tile", again.Tiles[1].Type);
        Assert.True(again.Extra!.ContainsKey("future"));
        Assert.True(again.Tiles[0].Extra!.ContainsKey("extra"));
        Assert.True(again.Tiles[0].Settings.Extra!.ContainsKey("newSetting"));

        // An import refuses a tile type it does not know.
        Assert.NotNull(DashboardRules.Validate(layout, allowUnknown: false).Problem);
        Assert.Null(DashboardLayout.FromJson("{not json"));
    }

    [Fact]
    public void Moving_a_tile_pushes_the_tiles_it_lands_on_down_and_pull_up_closes_the_holes()
    {
        IReadOnlyList<DashboardTile> tiles =
        [
            Tile("a", DashboardTiles.EndpointsOnline, 0, 0, 2, 2),
            Tile("b", DashboardTiles.EndpointsOffline, 2, 0, 2, 2),
            Tile("c", DashboardTiles.AlertList, 0, 2, 6, 4),
        ];

        var moved = DashboardGrid.Move(tiles, "a", new GridRect(2, 0, 2, 2), pullUp: false);
        AssertNoOverlap(moved);
        Assert.Equal(new GridRect(2, 0, 2, 2), moved.Single(t => t.Id == "a").Rect);
        Assert.Equal(2, moved.Single(t => t.Id == "b").Y);
        Assert.True(moved.Single(t => t.Id == "c").Y >= 4);

        var pulled = DashboardGrid.Move(tiles, "a", new GridRect(2, 0, 2, 2), pullUp: true);
        AssertNoOverlap(pulled);
        Assert.Equal(0, pulled.Single(t => t.Id == "a").Y);
        Assert.Equal(2, pulled.Single(t => t.Id == "b").Y);
        // The wide tile stays under the one pushed into its columns: nothing above it is free.
        Assert.Equal(4, pulled.Single(t => t.Id == "c").Y);
        Assert.Equal(0, DashboardGrid.Move(tiles, "c", new GridRect(6, 0, 6, 4), pullUp: true).Single(t => t.Id == "c").Y);

        // A place outside the grid or a size outside the type's bounds is clamped.
        var clamped = DashboardGrid.Move(tiles, "c", new GridRect(10, -3, 40, 1), pullUp: true).Single(t => t.Id == "c");
        Assert.Equal(DashboardTiles.Find(DashboardTiles.AlertList)!.Max.W, clamped.W);
        Assert.True(clamped.X + clamped.W <= DashboardGrid.Columns);
        Assert.Equal(DashboardTiles.Find(DashboardTiles.AlertList)!.Min.H, clamped.H);
    }

    [Fact]
    public void A_new_tile_goes_to_the_first_free_place_and_narrow_screens_keep_the_reading_order()
    {
        IReadOnlyList<DashboardTile> tiles = [Tile("a", DashboardTiles.EndpointsOnline, 0, 0, 2, 2), Tile("b", DashboardTiles.AlertList, 4, 0, 8, 6)];
        var added = DashboardGrid.Add(tiles, Tile("n", DashboardTiles.EndpointsOffline, 0, 0, 0, 0), pullUp: true);
        Assert.Equal(new GridRect(2, 0, 2, 2), added.Single(t => t.Id == "n").Rect);
        AssertNoOverlap(added);

        var medium = DashboardGrid.Narrow(DefaultDashboard.Layout.Tiles, DashboardGrid.MediumColumns);
        AssertNoOverlap(medium);
        Assert.All(medium, t => Assert.True(t.X + t.W <= DashboardGrid.MediumColumns, t.Id));
        Assert.Equal(DefaultDashboard.Layout.Tiles.Count, medium.Count);
        Assert.Equal(DefaultDashboard.Layout.Tiles.OrderBy(t => t.Y).ThenBy(t => t.X).Select(t => t.Id),
            medium.OrderBy(t => t.Y).ThenBy(t => t.X).Select(t => t.Id));
        // The six counters of the first row fill the first rows of the medium grid without a hole.
        Assert.Equal(new[] { 0, 2, 4, 0, 2, 4 }, medium.Take(6).Select(t => t.X));
    }

    [Fact]
    public void Leaving_out_a_tile_the_user_may_not_see_moves_the_tiles_below_up()
    {
        var visible = DefaultDashboard.Layout.Tiles.Where(t => t.Type is not DashboardTiles.Licenses and not DashboardTiles.InstanceHealth
            and not DashboardTiles.Security and not DashboardTiles.Backups).ToList();
        var compacted = DashboardGrid.Compact(visible);
        AssertNoOverlap(compacted);
        Assert.Equal(2, compacted.Single(t => t.Type == DashboardTiles.AlertList).Y);
    }

    [Fact]
    public void The_server_refuses_overlap_unsafe_links_wrong_sizes_and_settings_out_of_range()
    {
        DashboardLayout With(params DashboardTile[] tiles) => new() { Tiles = tiles };
        Assert.Null(DashboardRules.Validate(With(Tile("a", DashboardTiles.EndpointsOnline, 0, 0, 2, 2)), false).Problem);
        Assert.NotNull(DashboardRules.Validate(With(Tile("a", DashboardTiles.EndpointsOnline, 0, 0, 2, 2), Tile("b", DashboardTiles.EndpointsOffline, 1, 1, 2, 2)), false).Problem);
        Assert.NotNull(DashboardRules.Validate(With(Tile("a", DashboardTiles.EndpointsOnline, 11, 0, 2, 2)), false).Problem);
        Assert.NotNull(DashboardRules.Validate(With(Tile("a", DashboardTiles.EndpointsOnline, 0, 0, 1, 2)), false).Problem);
        Assert.NotNull(DashboardRules.Validate(With(Tile("A B", DashboardTiles.EndpointsOnline, 0, 0, 2, 2)), false).Problem);
        Assert.NotNull(DashboardRules.Validate(With(Tile("a", DashboardTiles.EndpointsOnline, 0, 0, 2, 2), Tile("a", DashboardTiles.EndpointsOffline, 4, 0, 2, 2)), false).Problem);
        Assert.NotNull(DashboardRules.Validate(new DashboardLayout { Schema = 2 }, false).Problem);
        Assert.NotNull(DashboardRules.Validate(new DashboardLayout { Tiles = Enumerable.Range(0, DashboardGrid.MaxTiles + 1)
            .Select(i => Tile($"t{i}", DashboardTiles.Heading, 0, i, 12, 1)).ToList() }, false).Problem);

        foreach (var url in new[] { "javascript:alert(1)", "data:text/html,x", "http://example.com", "//evil.example", "/\\evil", "https://user:pw@example.com", "https://a b" })
        {
            Assert.NotNull(DashboardRules.LinkProblem(url));
        }

        Assert.Null(DashboardRules.LinkProblem("/alerts"));
        Assert.Null(DashboardRules.LinkProblem("https://status.example.com/page?x=1"));

        var counter = DashboardTiles.Find(DashboardTiles.AgentsOutOfDate)!;
        Assert.NotNull(DashboardRules.Clean(counter, new TileSettings { Warning = 10, Critical = 5 }).Problem);
        Assert.NotNull(DashboardRules.Clean(counter, new TileSettings { Warning = -1 }).Problem);
        Assert.NotNull(DashboardRules.Clean(counter, new TileSettings { Title = new string('x', DashboardRules.MaxTitleLength + 1) }).Problem);
        var below = DashboardTiles.Find(DashboardTiles.PatchCompliance)!;
        Assert.Null(DashboardRules.Clean(below, new TileSettings { Warning = 90, Critical = 70 }).Problem);
        Assert.NotNull(DashboardRules.Clean(DashboardTiles.Find(DashboardTiles.AlertsPerDay)!, new TileSettings { Days = 12 }).Problem);
        Assert.NotNull(DashboardRules.Clean(DashboardTiles.Find(DashboardTiles.Text)!, new TileSettings { Text = new string('x', DashboardRules.MaxTextLength + 1) }).Problem);
        Assert.NotNull(DashboardRules.Clean(DashboardTiles.Find(DashboardTiles.AlertList)!,
            new TileSettings { Scope = new TileScope { Mode = TileScopeMode.Clients } }).Problem);
    }

    [Fact]
    public void Settings_a_type_does_not_offer_are_cleared()
    {
        var licenses = DashboardTiles.Find(DashboardTiles.Licenses)!;
        var (settings, problem) = DashboardRules.Clean(licenses, new TileSettings
        {
            Scope = new TileScope { Mode = TileScopeMode.Clients, ClientIds = [Guid.NewGuid()] },
            Warning = 3, Text = "not here", Url = "/alerts", Severity = AlertSeverity.Critical, FailedOnly = true, Title = "  Seats  "
        });
        Assert.Null(problem);
        Assert.Null(settings!.Scope);
        Assert.Null(settings.Warning);
        Assert.Null(settings.Text);
        Assert.Null(settings.Url);
        Assert.Null(settings.Severity);
        Assert.False(settings.FailedOnly);
        Assert.Equal("Seats", settings.Title);

        // A tile scope that only says "follow the dashboard" is stored as no scope; the class is kept only where the type counts it.
        var patches = DashboardTiles.Find(DashboardTiles.PatchCompliance)!;
        Assert.Null(DashboardRules.Clean(patches, new TileSettings { Scope = TileScope.FollowDashboard }).Settings!.Scope);
        Assert.Null(DashboardRules.Clean(patches, new TileSettings { Scope = TileScope.Everything with { Class = EndpointClass.Server } }).Settings!.Scope!.Class);
    }

    [Fact]
    public void A_user_sees_the_first_published_dashboard_of_its_roles_and_otherwise_the_default()
    {
        Guid admin = Guid.NewGuid(), tech = Guid.NewGuid(), reader = Guid.NewGuid();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), draft = Guid.NewGuid();
        DashboardChoice[] dashboards =
        [
            new(second, 2, true, [tech, reader]),
            new(draft, 0, false, [tech]),
            new(first, 1, true, [tech]),
        ];
        Assert.Equal(first, DashboardRules.Pick(dashboards, [tech]));
        Assert.Equal(second, DashboardRules.Pick(dashboards, [reader]));
        Assert.Equal(first, DashboardRules.Pick(dashboards, [reader, tech]));
        Assert.Null(DashboardRules.Pick(dashboards, [admin]));
        Assert.Null(DashboardRules.Pick(dashboards, []));
    }

    private static void AssertNoOverlap(IReadOnlyList<DashboardTile> tiles)
    {
        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                Assert.False(tiles[i].Rect.Overlaps(tiles[j].Rect), $"{tiles[i].Id} overlaps {tiles[j].Id}");
            }
        }
    }
}
