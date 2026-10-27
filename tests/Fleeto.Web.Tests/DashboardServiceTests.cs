using Fleeto.Core.Dashboards;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// The dashboard designer (0.8.0, DASHBOARDS.md): dashboards are designed by a user with "Edit dashboards" who sees every client, shown to
/// the users of their roles only once published, first in order wins; a dashboard never widens access, because every tile reads with
/// the permissions and groups of the viewer, which a tile scope only narrows.
/// </summary>
[Collection(WebCollection.Name)]
public class DashboardServiceTests
{
    private readonly WebFixture _fixture;

    public DashboardServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private DashboardDesignService Design => _fixture.Services.GetRequiredService<DashboardDesignService>();
    private DashboardTileSources Sources => _fixture.Services.GetRequiredService<DashboardTileSources>();
    private DashboardScopes Scopes => _fixture.Services.GetRequiredService<DashboardScopes>();
    private DashboardService Data => _fixture.Services.GetRequiredService<DashboardService>();

    private static string Name(string prefix = "Board") => prefix + " " + Guid.NewGuid().ToString("N")[..8];

    private async Task<Guid> RoleIdAsync(string name)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.Roles.Where(r => r.Name == name).Select(r => r.Id).SingleAsync();
    }

    [Fact]
    public async Task A_dashboard_shows_for_its_roles_only_once_published_and_the_first_in_order_wins()
    {
        var admin = WebFixtureBase.Admin();
        var roleName = await _fixture.Database.CreateRoleAsync(Permissions.ClientsView);
        var otherRole = await _fixture.Database.CreateRoleAsync(Permissions.ClientsView);
        var roleId = await RoleIdAsync(roleName);
        var user = WebFixtureBase.CallerWith(SystemClientScope.Instance, roleName);

        var created = await Design.CreateAsync(admin, Name(), "For the night shift", Guid.Empty);
        Assert.True(created.Success, created.Problem);
        var id = created.Value;
        Assert.True((await Design.UpdateAsync(admin, id, Name(), null, [roleId])).Success);

        // Linked, but only a draft: the user still sees the default.
        Assert.Null((await Design.GetForUserAsync(user)).Id);
        Assert.True((await Design.PublishAsync(admin, id)).Success);
        var shown = await Design.GetForUserAsync(user);
        Assert.Equal(id, shown.Id);
        Assert.Equal(DefaultDashboard.Layout.Tiles.Count, shown.Layout.Tiles.Count);
        Assert.Null((await Design.GetForUserAsync(WebFixtureBase.CallerWith(SystemClientScope.Instance, otherRole))).Id);

        // A second dashboard for the same role wins once it is moved above the first.
        var second = (await Design.CreateAsync(admin, Name(), null, null)).Value;
        Assert.True((await Design.UpdateAsync(admin, second, Name("Second"), null, [roleId])).Success);
        Assert.True((await Design.SaveDraftAsync(admin, second, new DashboardLayout
        {
            Tiles = [new DashboardTile { Id = "h", Type = DashboardTiles.Heading, X = 0, Y = 0, W = 12, H = 1, Settings = new TileSettings { Title = "Shift" } }]
        })).Success);
        Assert.True((await Design.PublishAsync(admin, second)).Success);
        Assert.Equal(id, (await Design.GetForUserAsync(user)).Id);
        for (var i = 0; i < DashboardRules.MaxDashboards && (await Design.GetForUserAsync(user)).Id != second; i++)
        {
            Assert.True((await Design.MoveAsync(admin, second, up: true)).Success);
        }

        Assert.Equal(second, (await Design.GetForUserAsync(user)).Id);
        var overview = await Design.ListAsync(admin);
        Assert.Equal(second, overview.Roles.Single(r => r.RoleId == roleId).DashboardId);
        Assert.Equal(overview.Dashboards.Count, overview.Dashboards.Select(d => d.Priority).Distinct().Count());

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var actions = await db.AuditEntries.Where(a => a.TargetType == "Dashboard" && (a.TargetId == id.ToString() || a.TargetId == second.ToString()))
            .Select(a => a.Action).ToListAsync();
        Assert.Contains(AuditActions.DashboardCreated, actions);
        Assert.Contains(AuditActions.DashboardUpdated, actions);
        Assert.Contains(AuditActions.DashboardPublished, actions);
        Assert.Contains(AuditActions.DashboardOrderChanged, actions);

        Assert.True((await Design.DeleteAsync(admin, second)).Success);
        Assert.Equal(id, (await Design.GetForUserAsync(user)).Id);
    }

    [Fact]
    public async Task Only_a_user_with_the_permission_who_sees_every_client_designs_dashboards()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var technician = WebFixtureBase.Technician();
        var limitedAdmin = WebFixtureBase.CallerWith(new RestrictedClientScope([client.Id]), FleetoRoles.Admin);
        foreach (var caller in new[] { technician, limitedAdmin })
        {
            Assert.False((await Design.CreateAsync(caller, Name(), null, null)).Success);
            await Assert.ThrowsAsync<AccessDeniedException>(() => Design.ListAsync(caller));
        }

        var id = (await Design.CreateAsync(WebFixtureBase.Admin(), Name(), null, null)).Value;
        Assert.False((await Design.PublishAsync(technician, id)).Success);
        Assert.False((await Design.SaveDraftAsync(technician, id, DefaultDashboard.Layout)).Success);
        Assert.False((await Design.DeleteAsync(limitedAdmin, id)).Success);
    }

    [Fact]
    public async Task A_draft_that_does_not_validate_is_refused_and_scopes_keep_only_what_exists()
    {
        var admin = WebFixtureBase.Admin();
        var client = await _fixture.Database.CreateClientAsync();
        var id = (await Design.CreateAsync(admin, Name(), null, null)).Value;

        var overlap = new DashboardLayout
        {
            Tiles =
            [
                new DashboardTile { Id = "a", Type = DashboardTiles.EndpointsOnline, X = 0, Y = 0, W = 2, H = 2 },
                new DashboardTile { Id = "b", Type = DashboardTiles.EndpointsOffline, X = 1, Y = 0, W = 2, H = 2 }
            ]
        };
        Assert.False((await Design.SaveDraftAsync(admin, id, overlap)).Success);
        var link = new DashboardLayout
        {
            Tiles = [new DashboardTile { Id = "l", Type = DashboardTiles.Link, X = 0, Y = 0, W = 2, H = 1, Settings = new TileSettings { Url = "javascript:alert(1)" } }]
        };
        Assert.False((await Design.SaveDraftAsync(admin, id, link)).Success);

        var scoped = new DashboardLayout
        {
            Scope = new TileScope { Mode = TileScopeMode.Clients, ClientIds = [client.Id, Guid.NewGuid()] },
            Tiles =
            [
                new DashboardTile
                {
                    Id = "a", Type = DashboardTiles.EndpointsOnline, X = 0, Y = 0, W = 2, H = 2,
                    Settings = new TileSettings { Scope = new TileScope { Mode = TileScopeMode.Sites, SiteIds = [Guid.NewGuid()] } }
                }
            ]
        };
        var saved = await Design.SaveDraftAsync(admin, id, scoped);
        Assert.True(saved.Success, saved.Problem);
        Assert.Equal([client.Id], saved.Value!.Scope.ClientIds);
        Assert.Null(saved.Value.Tiles[0].Settings.Scope);
    }

    [Fact]
    public async Task Publishing_keeps_the_last_versions_and_an_earlier_one_can_become_the_draft()
    {
        var admin = WebFixtureBase.Admin();
        var id = (await Design.CreateAsync(admin, Name(), null, null)).Value;
        for (var i = 1; i <= DashboardRules.MaxVersions + 2; i++)
        {
            var layout = new DashboardLayout
            {
                Tiles = [new DashboardTile { Id = "h", Type = DashboardTiles.Heading, X = 0, Y = 0, W = 12, H = 1, Settings = new TileSettings { Title = $"Version {i}" } }]
            };
            Assert.True((await Design.SaveDraftAsync(admin, id, layout)).Success);
            Assert.True((await Design.PublishAsync(admin, id)).Success);
        }

        Assert.False((await Design.PublishAsync(admin, id)).Success);
        var versions = await Design.ListVersionsAsync(admin, id);
        Assert.Equal(DashboardRules.MaxVersions, versions.Count);
        Assert.Equal(DashboardRules.MaxVersions + 2, versions[0].Version);
        Assert.True((await Design.RestoreVersionAsync(admin, id, 3)).Success);
        var detail = await Design.GetAsync(admin, id);
        Assert.True(detail!.HasDraft);
        Assert.Equal("Version 3", detail.Layout.Tiles[0].Settings.Title);
        Assert.True((await Design.DiscardDraftAsync(admin, id)).Success);
        Assert.Equal($"Version {DashboardRules.MaxVersions + 2}", (await Design.GetAsync(admin, id))!.Layout.Tiles[0].Settings.Title);
    }

    [Fact]
    public async Task An_exported_dashboard_imports_as_a_draft_without_the_clients_this_instance_does_not_have()
    {
        var admin = WebFixtureBase.Admin();
        var client = await _fixture.Database.CreateClientAsync();
        var name = Name("Export");
        var id = (await Design.CreateAsync(admin, name, "Exported", Guid.Empty)).Value;
        var layout = DefaultDashboard.Layout with { Scope = new TileScope { Mode = TileScopeMode.Clients, ClientIds = [client.Id] } };
        Assert.True((await Design.SaveDraftAsync(admin, id, layout)).Success);
        var json = (await Design.ExportAsync(admin, id)).Value!;
        Assert.Contains(DashboardDesignService.ExportFormat, json);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Clients.Where(c => c.Id == client.Id).ExecuteDeleteAsync();
        }

        var imported = await Design.ImportAsync(admin, json);
        Assert.True(imported.Success, imported.Problem);
        Assert.NotNull(imported.Value.Note);
        var detail = (await Design.GetAsync(admin, imported.Value.Id))!;
        Assert.Equal(name + " (imported)", detail.Name);
        Assert.True(detail.HasDraft);
        Assert.Equal(0, detail.PublishedVersion);
        Assert.Equal(TileScopeMode.All, detail.Layout.Scope.Mode);
        Assert.Equal(DefaultDashboard.Layout.Tiles.Count, detail.Layout.Tiles.Count);

        Assert.False((await Design.ImportAsync(admin, "{\"format\":\"something else\"}")).Success);
        Assert.False((await Design.ImportAsync(admin, "not json")).Success);
        Assert.False((await Design.ImportAsync(admin, json.Replace(DashboardTiles.AlertList, "alerts.unknown"))).Success);
    }

    [Fact]
    public async Task Every_source_of_the_catalog_exists_and_reads_without_failing()
    {
        var sources = DashboardTiles.All.Select(t => t.Source).OfType<string>().Distinct().ToList();
        Assert.All(sources, s => Assert.NotNull(Sources.Find(s)));

        var admin = WebFixtureBase.Admin();
        var scope = new TileReadScope(admin, null, "all");
        foreach (var type in DashboardTiles.All.Where(t => t.Source is not null))
        {
            var settings = DashboardRules.Clean(type, new TileSettings()).Settings!;
            await Sources.LoadAsync(new TileRequest(type, settings, scope, 5), CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_dashboard_never_widens_access_a_role_without_the_permission_gets_no_data()
    {
        var (client, endpoint) = await EndpointWithAlertAsync();
        var noAlerts = new Caller(Guid.NewGuid(), "Viewer", "viewer@example.com", ["Viewer"], SystemClientScope.Instance,
            Granted: new HashSet<string> { Permissions.ClientsView });
        var alertList = DashboardTiles.Find(DashboardTiles.AlertList)!;
        var request = new TileRequest(alertList, new TileSettings(), new TileReadScope(noAlerts, null, "all"), 10);
        Assert.Empty((IReadOnlyList<AlertView>)(await Sources.LoadAsync(request, CancellationToken.None))!);
        Assert.Null(await Data.GetAlertCountsAsync(noAlerts, null));
        Assert.False(DashboardTiles.CanSee(alertList, noAlerts.Has));

        // A caller limited to another client gets nothing of this one, even from a tile scope that names it.
        var other = await _fixture.Database.CreateClientAsync();
        var limited = WebFixtureBase.CallerWith(new RestrictedClientScope([other.Id]), FleetoRoles.Technician);
        var narrowed = await Scopes.NarrowAsync(limited, new TileScope { Mode = TileScopeMode.Clients, ClientIds = [client.Id] }, null);
        Assert.Equal(0, (await Data.GetEndpointCountsAsync(narrowed.Caller, null))!.Total);
        Assert.Empty(await Data.GetOpenAlertsAsync(narrowed.Caller, null, null, null, true, 50));
        Assert.Empty(await Data.GetTopClientsAsync(narrowed.Caller, null, 10));
        Assert.DoesNotContain(endpoint.Id, narrowed.Caller.Scope.EndpointIds);
    }

    [Fact]
    public async Task A_tile_scope_narrows_to_its_clients_sites_tags_and_class()
    {
        var (client, endpoint) = await EndpointWithAlertAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id, "Servers");
        await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-1", EndpointClass.Server);
        var tech = WebFixtureBase.Technician();

        var byClient = await Scopes.NarrowAsync(tech, new TileScope { Mode = TileScopeMode.Clients, ClientIds = [client.Id] }, null);
        var counts = (await Data.GetEndpointCountsAsync(byClient.Caller, null))!;
        Assert.Equal(2, counts.Total);
        Assert.Equal(2, counts.Managed);
        Assert.Equal(1, (await Data.GetEndpointCountsAsync(byClient.Caller, EndpointClass.Server))!.Total);
        Assert.Single(await Data.GetOpenAlertsAsync(byClient.Caller, null, null, null, false, 50));
        Assert.Empty(await Data.GetOpenAlertsAsync(byClient.Caller, EndpointClass.Server, null, null, false, 50));

        var bySite = await Scopes.NarrowAsync(tech, new TileScope { Mode = TileScopeMode.Sites, SiteIds = [site.Id] }, null);
        Assert.Equal(1, (await Data.GetEndpointCountsAsync(bySite.Caller, null))!.Total);
        Assert.Empty(await Data.GetOpenAlertsAsync(bySite.Caller, null, null, null, false, 50));

        // A tag that no client carries shows nothing, never everything; one that is carried shows its clients.
        var tags = _fixture.Services.GetRequiredService<TagService>();
        var tagName = "dash-" + Guid.NewGuid().ToString("N")[..6];
        Assert.True((await tags.SetClientTagsAsync(tech, client.Id, [tagName])).Success);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var tagId = await db.Tags.Where(t => t.Name == tagName).Select(t => t.Id).SingleAsync();
            var byTag = await Scopes.NarrowAsync(tech, new TileScope { Mode = TileScopeMode.Tags, TagIds = [tagId] }, null);
            Assert.Equal(2, (await Data.GetEndpointCountsAsync(byTag.Caller, null))!.Total);
            Assert.True((await tags.SetClientTagsAsync(tech, client.Id, [])).Success);
            await db.ClientTags.Where(l => l.TagId == tagId).ExecuteDeleteAsync();
            var empty = await Scopes.NarrowAsync(tech, new TileScope { Mode = TileScopeMode.Tags, TagIds = [tagId] }, null);
            Assert.Equal(0, (await Data.GetEndpointCountsAsync(empty.Caller, null))!.Total);
        }

        // A scope that names only what no longer exists follows the fallback.
        var gone = await Scopes.NarrowAsync(tech, new TileScope { Mode = TileScopeMode.Clients, ClientIds = [Guid.NewGuid()] }, byClient);
        Assert.Same(byClient.Caller, gone.Caller);

        // A user that sees part of the client sees only its own endpoints through a scope on the whole client.
        var partial = WebFixtureBase.CallerWith(new AccessScope(false, false, [client.Id], [], [], [endpoint.SiteId], [endpoint.Id]), FleetoRoles.Technician);
        var partialScope = await Scopes.NarrowAsync(partial, new TileScope { Mode = TileScopeMode.Clients, ClientIds = [client.Id] }, null);
        Assert.Equal(1, (await Data.GetEndpointCountsAsync(partialScope.Caller, null))!.Total);

        // The chart counts the alerts of a client seen in part from its own alerts, not from the counts of the whole client.
        var days = await Data.GetAlertsPerDayAsync(partialScope.Caller, 7);
        Assert.Equal(7, days.Count);
        Assert.Equal(1, days[^1].Critical);
        var outside = WebFixtureBase.CallerWith(new AccessScope(false, false, [client.Id], [], [], [site.Id], []), FleetoRoles.Technician);
        Assert.Equal(0, (await Data.GetAlertsPerDayAsync(outside, 7)).Sum(d => d.Critical + d.Warning));
    }

    private async Task<(Client Client, Endpoint Endpoint)> EndpointWithAlertAsync()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed);
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.Alerts.Add(new Alert
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Kind = AlertKind.Offline, Severity = AlertSeverity.Critical,
            Title = "WS-TEST has been offline", OpenedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return (client, endpoint);
    }
}
