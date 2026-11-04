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
/// Column layouts of the endpoint list and the description of an endpoint (0.8.0, decided 2026-11-04): an admin builds layouts and links
/// them to roles, the first in order wins, a user never changes its own columns; a technician gives an endpoint a description, which the
/// list shows and searches, audited without its text; optional columns read their data only when the layout shows them.
/// </summary>
[Collection(WebCollection.Name)]
public class ColumnLayoutServiceTests
{
    private readonly WebFixture _fixture;

    public ColumnLayoutServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private ColumnLayoutService Layouts => _fixture.Services.GetRequiredService<ColumnLayoutService>();
    private EndpointService Endpoints => _fixture.Services.GetRequiredService<EndpointService>();

    private static string Name(string prefix = "Layout") => prefix + " " + Guid.NewGuid().ToString("N")[..8];

    private async Task<Guid> RoleIdAsync(string name)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.Roles.Where(r => r.Name == name).Select(r => r.Id).SingleAsync();
    }

    private static ColumnLayoutInput Input(string name, IReadOnlyCollection<Guid> roles, params string[] columns) =>
        new(name, null, columns, roles);

    [Fact]
    public async Task A_layout_shows_for_its_roles_and_the_first_in_order_wins()
    {
        var admin = WebFixtureBase.Admin();
        var roleName = await _fixture.Database.CreateRoleAsync(Permissions.ClientsView);
        var roleId = await RoleIdAsync(roleName);
        var user = WebFixtureBase.CallerWith(SystemClientScope.Instance, roleName);
        Assert.Null((await Layouts.GetForUserAsync(user)).Id);
        Assert.Equal(EndpointColumns.Default, (await Layouts.GetForUserAsync(user)).Columns);

        var first = await Layouts.CreateAsync(admin, Input(Name(), [roleId], EndpointColumns.Hostname, EndpointColumns.Description, EndpointColumns.Uptime));
        Assert.True(first.Success, first.Problem);
        var shown = await Layouts.GetForUserAsync(user);
        Assert.Equal(first.Value, shown.Id);
        Assert.Equal([EndpointColumns.Hostname, EndpointColumns.Description, EndpointColumns.Uptime], shown.Columns);

        // A second layout for the same role wins once it is moved above the first.
        var second = (await Layouts.CreateAsync(admin, Input(Name("Second"), [roleId], EndpointColumns.IpAddress, EndpointColumns.Hostname))).Value;
        Assert.Equal(first.Value, (await Layouts.GetForUserAsync(user)).Id);
        for (var i = 0; i < ColumnLayoutRules.MaxLayouts && (await Layouts.GetForUserAsync(user)).Id != second; i++)
        {
            Assert.True((await Layouts.MoveAsync(admin, second, up: true)).Success);
        }

        Assert.Equal(second, (await Layouts.GetForUserAsync(user)).Id);
        var overview = await Layouts.ListAsync(admin);
        Assert.Equal(second, overview.Roles.Single(r => r.RoleId == roleId).LayoutId);
        Assert.Equal(overview.Layouts.Count, overview.Layouts.Select(l => l.Priority).Distinct().Count());

        // Unlinking the role from both brings back the default.
        Assert.True((await Layouts.UpdateAsync(admin, second, Input(Name(), [], EndpointColumns.Hostname))).Success);
        Assert.True((await Layouts.DeleteAsync(admin, first.Value)).Success);
        Assert.Null((await Layouts.GetForUserAsync(user)).Id);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var actions = await db.AuditEntries
            .Where(a => a.TargetType == "ColumnLayout" && (a.TargetId == first.Value.ToString() || a.TargetId == second.ToString()))
            .Select(a => a.Action).ToListAsync();
        Assert.Contains(AuditActions.ColumnLayoutCreated, actions);
        Assert.Contains(AuditActions.ColumnLayoutUpdated, actions);
        Assert.Contains(AuditActions.ColumnLayoutOrderChanged, actions);
        Assert.Contains(AuditActions.ColumnLayoutDeleted, actions);
        Assert.True((await Layouts.DeleteAsync(admin, second)).Success);
    }

    [Fact]
    public async Task Only_an_admin_who_sees_every_client_manages_layouts_and_the_server_checks_the_columns()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var technician = WebFixtureBase.Technician();
        var limitedAdmin = WebFixtureBase.CallerWith(new RestrictedClientScope([client.Id]), FleetoRoles.Admin);
        Assert.DoesNotContain(Permissions.ColumnLayoutsManage, BuiltInRoles.TechnicianPermissions);
        foreach (var caller in new[] { technician, limitedAdmin })
        {
            Assert.False((await Layouts.CreateAsync(caller, Input(Name(), [], EndpointColumns.Hostname))).Success);
            await Assert.ThrowsAsync<AccessDeniedException>(() => Layouts.ListAsync(caller));
        }

        var admin = WebFixtureBase.Admin();
        var id = (await Layouts.CreateAsync(admin, Input(Name(), [], EndpointColumns.Hostname))).Value;
        Assert.False((await Layouts.UpdateAsync(technician, id, Input(Name(), [], EndpointColumns.Hostname, EndpointColumns.Tier))).Success);
        Assert.False((await Layouts.MoveAsync(technician, id, up: true)).Success);
        Assert.False((await Layouts.DeleteAsync(limitedAdmin, id)).Success);

        // Whatever the editor sent: the host name stays, a column is there once, an unknown one and an unknown role are refused.
        Assert.False((await Layouts.UpdateAsync(admin, id, Input(Name(), [], EndpointColumns.Description))).Success);
        Assert.False((await Layouts.UpdateAsync(admin, id, Input(Name(), [], EndpointColumns.Hostname, EndpointColumns.Hostname))).Success);
        Assert.False((await Layouts.UpdateAsync(admin, id, Input(Name(), [], EndpointColumns.Hostname, "secret_column"))).Success);
        Assert.False((await Layouts.UpdateAsync(admin, id, Input(Name(), [Guid.NewGuid()], EndpointColumns.Hostname))).Success);
        Assert.True((await Layouts.DeleteAsync(admin, id)).Success);
    }

    [Fact]
    public async Task A_technician_gives_an_endpoint_a_description_the_list_shows_and_searches_and_the_audit_keeps_only_its_length()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, hostname: "SRV-" + Guid.NewGuid().ToString("N")[..6]);
        var technician = WebFixtureBase.Technician();
        var reader = WebFixtureBase.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);
        var marker = "Rack " + Guid.NewGuid().ToString("N")[..8];

        Assert.False((await Endpoints.SetDescriptionAsync(reader, endpoint.Id, marker)).Success);
        Assert.False((await Endpoints.SetDescriptionAsync(technician, endpoint.Id, new string('x', EndpointDescriptionRules.MaxLength + 1))).Success);
        var saved = await Endpoints.SetDescriptionAsync(technician, endpoint.Id, $"  {marker}\r\nleft  ");
        Assert.True(saved.Success, saved.Problem);

        // Another client's user cannot reach it: the scope filter makes it not exist.
        var otherClient = await _fixture.Database.CreateClientAsync();
        var outsider = WebFixtureBase.CallerWith(new RestrictedClientScope([otherClient.Id]), FleetoRoles.Technician);
        Assert.False((await Endpoints.SetDescriptionAsync(outsider, endpoint.Id, "taken over")).Success);

        var page = await Endpoints.ListAsync(technician, new EndpointListQuery(client.Id, null, null, marker, EndpointStatusFilter.All, null));
        var row = Assert.Single(page.Rows);
        Assert.Equal(endpoint.Id, row.Id);
        Assert.Equal($"{marker} left", row.Description);
        Assert.Equal($"{marker} left", (await Endpoints.GetAsync(technician, endpoint.Id))!.Description);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var audit = await db.AuditEntries.Where(a => a.Action == AuditActions.EndpointDescriptionChanged && a.TargetId == endpoint.Id.ToString())
            .ToListAsync();
        var entry = Assert.Single(audit);
        Assert.DoesNotContain(marker, entry.DetailsJson);
        Assert.Contains("\"toLength\":" + $"{marker} left".Length, entry.DetailsJson.Replace(" ", string.Empty));

        // Clearing it stores nothing.
        Assert.True((await Endpoints.SetDescriptionAsync(technician, endpoint.Id, "   ")).Success);
        Assert.Null((await Endpoints.GetAsync(technician, endpoint.Id))!.Description);
    }

    [Fact]
    public async Task Optional_columns_read_the_inventory_and_patch_state_only_when_the_layout_shows_them()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed);
        var now = DateTime.UtcNow;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.InventorySnapshots.Add(new InventorySnapshot
            {
                EndpointId = endpoint.Id,
                ClientId = client.Id,
                ReceivedAt = now,
                CpuModel = "Test CPU",
                CpuCores = 8,
                MemoryTotalBytes = 16L << 30,
                BootTime = now.AddHours(-5),
                DisksJson = """[{"mount":"C:","filesystem":"NTFS","totalBytes":1000,"freeBytes":600},{"mount":"D:","filesystem":"NTFS","totalBytes":1000,"freeBytes":100}]""",
                NetworkInterfacesJson = """[{"name":"lo","ipAddresses":["127.0.0.1/8"]},{"name":"eth0","ipAddresses":["fe80::1/64","169.254.3.4/16","10.0.0.5/24"]}]"""
            });
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = endpoint.Id, ClientId = client.Id, ExternalEndpointId = "a1", ExternalTenantId = "t1", MissingCritical = 2, UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var admin = WebFixtureBase.Admin();
        var query = new EndpointListQuery(null, site.Id, null, null, EndpointStatusFilter.All, null);
        Assert.Null(Assert.Single((await Endpoints.ListAsync(admin, query, EndpointColumns.Default)).Rows).Extras);

        var row = Assert.Single((await Endpoints.ListAsync(admin, query,
            [EndpointColumns.Hostname, EndpointColumns.IpAddress, EndpointColumns.DiskFree, EndpointColumns.Patches])).Rows);
        var extras = Assert.IsType<EndpointRowExtras>(row.Extras);
        Assert.Equal(["10.0.0.5"], extras.IpAddresses);
        Assert.Equal("D:", extras.FullestDrive?.Mount);
        Assert.Equal(8, extras.CpuCores);
        Assert.Equal(2, extras.Patches?.MissingCritical);
    }
}
