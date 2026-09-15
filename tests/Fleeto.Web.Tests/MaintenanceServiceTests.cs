using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees maintenance mode in web: admins and technicians start, change and end it on a client, site or managed endpoint;
/// read-only users and callers of another client cannot; agent-only endpoints are refused; an endpoint kept in maintenance by its
/// site cannot be ended at endpoint level; the reason never reaches the audit log; and the clients panel, the endpoint list and
/// the detail show it.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class MaintenanceServiceTests
{
    private readonly WebFixture _fixture;

    public MaintenanceServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private MaintenanceService Maintenance => _fixture.Services.GetRequiredService<MaintenanceService>();
    private DateTime Now => _fixture.Database.Time.GetUtcNow().UtcDateTime;

    private async Task<(Client Client, Site Site, Endpoint Managed, Endpoint AgentOnly)> ScopeAsync()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var managed = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-MNT-01", EndpointClass.Server);
        var agentOnly = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-MNT-02");
        return (client, site, managed, agentOnly);
    }

    [Fact]
    public async Task A_technician_starts_changes_and_ends_maintenance_with_audit_entries_without_the_reason()
    {
        var (_, _, endpoint, _) = await ScopeAsync();
        var technician = WebFixture.Technician();
        const string reason = "Replacing the RAID controller for customer contact Jan";

        var start = await Maintenance.StartAsync(technician, MaintenanceTarget.Endpoint, endpoint.Id, Now.AddHours(4), reason);
        Assert.True(start.Success, start.Problem);
        var startedAt = Now;
        _fixture.Database.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await Maintenance.StartAsync(technician, MaintenanceTarget.Endpoint, endpoint.Id, null, reason)).Success);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var row = await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id);
            Assert.Equal(startedAt, row.MaintenanceStartedAt!.Value, TimeSpan.FromMilliseconds(1));
            Assert.Null(row.MaintenanceEndsAt);
            Assert.Equal(reason, row.MaintenanceReason);
        }

        var detail = await _fixture.Services.GetRequiredService<EndpointService>().GetAsync(technician, endpoint.Id);
        Assert.Equal(MaintenanceSource.Endpoint, detail!.Maintenance!.Source);
        Assert.True(detail.OwnMaintenanceActive);

        Assert.True((await Maintenance.EndAsync(technician, MaintenanceTarget.Endpoint, endpoint.Id)).Success);
        Assert.False((await Maintenance.EndAsync(technician, MaintenanceTarget.Endpoint, endpoint.Id)).Success);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.Null((await check.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id)).MaintenanceStartedAt);
        var audit = await check.AuditEntries.AsNoTracking().Where(a => a.TargetId == endpoint.Id.ToString() && a.Action.StartsWith("maintenance."))
            .OrderBy(a => a.Id).ToListAsync();
        Assert.Equal([AuditActions.MaintenanceStarted, AuditActions.MaintenanceChanged, AuditActions.MaintenanceEnded], audit.Select(a => a.Action).ToArray());
        Assert.DoesNotContain(audit, a => a.DetailsJson.Contains("RAID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_end_times_read_only_users_agent_only_endpoints_and_other_clients_are_refused()
    {
        var (client, site, managed, agentOnly) = await ScopeAsync();
        var technician = WebFixture.Technician();

        Assert.False((await Maintenance.StartAsync(technician, MaintenanceTarget.Site, site.Id, Now.AddMinutes(-10), null)).Success);
        Assert.False((await Maintenance.StartAsync(technician, MaintenanceTarget.Site, site.Id, Now.AddDays(400), null)).Success);
        Assert.False((await Maintenance.StartAsync(technician, MaintenanceTarget.Site, site.Id, null, new string('x', 501))).Success);
        Assert.False((await Maintenance.StartAsync(technician, MaintenanceTarget.Endpoint, agentOnly.Id, null, null)).Success);

        var readOnly = WebFixture.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);
        Assert.False((await Maintenance.StartAsync(readOnly, MaintenanceTarget.Client, client.Id, null, null)).Success);

        var other = await _fixture.Database.CreateClientAsync();
        var otherCaller = WebFixture.CallerWith(new RestrictedClientScope([other.Id]), FleetoRoles.Technician);
        Assert.False((await Maintenance.StartAsync(otherCaller, MaintenanceTarget.Client, client.Id, null, null)).Success);
        Assert.False((await Maintenance.StartAsync(otherCaller, MaintenanceTarget.Site, site.Id, null, null)).Success);
        Assert.False((await Maintenance.StartAsync(otherCaller, MaintenanceTarget.Endpoint, managed.Id, null, null)).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Null((await db.Clients.AsNoTracking().SingleAsync(c => c.Id == client.Id)).MaintenanceStartedAt);
        Assert.Null((await db.Sites.AsNoTracking().SingleAsync(s => s.Id == site.Id)).MaintenanceStartedAt);
        Assert.Null((await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == managed.Id)).MaintenanceStartedAt);
    }

    [Fact]
    public async Task An_endpoint_kept_in_maintenance_by_its_site_cannot_be_ended_at_endpoint_level()
    {
        var (_, site, endpoint, _) = await ScopeAsync();
        var technician = WebFixture.Technician();
        Assert.True((await Maintenance.StartAsync(technician, MaintenanceTarget.Endpoint, endpoint.Id, Now.AddHours(1), null)).Success);
        Assert.True((await Maintenance.StartAsync(technician, MaintenanceTarget.Site, site.Id, null, null)).Success);

        var refused = await Maintenance.EndAsync(technician, MaintenanceTarget.Endpoint, endpoint.Id);
        Assert.False(refused.Success);
        Assert.Contains("End maintenance on the site", refused.Problem);

        Assert.True((await Maintenance.EndAsync(technician, MaintenanceTarget.Site, site.Id)).Success);
        Assert.True((await Maintenance.EndAsync(technician, MaintenanceTarget.Endpoint, endpoint.Id)).Success);
    }

    [Fact]
    public async Task The_clients_panel_and_the_endpoint_list_show_maintenance_per_client_site_and_endpoint()
    {
        var (client, site, managed, agentOnly) = await ScopeAsync();
        var otherSite = await _fixture.Database.CreateSiteAsync(client.Id, "Branch");
        var branchEndpoint = await _fixture.Database.CreateEndpointAsync(otherSite, EndpointTier.Managed, "SRV-MNT-03", EndpointClass.Server);
        var technician = WebFixture.Technician();
        var caller = WebFixture.CallerWith(new RestrictedClientScope([client.Id]), FleetoRoles.ReadOnly);
        var clients = _fixture.Services.GetRequiredService<ClientService>();
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();

        Assert.True((await Maintenance.StartAsync(technician, MaintenanceTarget.Endpoint, branchEndpoint.Id, Now.AddHours(2), null)).Success);
        var tree = Assert.Single(await clients.ListTreeAsync(caller));
        Assert.False(tree.MaintenanceActive);
        Assert.Equal(1, tree.InMaintenanceCount);
        Assert.Equal(1, tree.Sites.Single(s => s.Id == otherSite.Id).InMaintenanceCount);
        Assert.Equal(0, tree.Sites.Single(s => s.Id == site.Id).InMaintenanceCount);

        Assert.True((await Maintenance.StartAsync(technician, MaintenanceTarget.Site, site.Id, null, "Rack move")).Success);
        tree = Assert.Single(await clients.ListTreeAsync(caller));
        Assert.Equal(3, tree.InMaintenanceCount);
        Assert.True(tree.Sites.Single(s => s.Id == site.Id).MaintenanceActive);

        var page = await endpoints.ListAsync(caller, new EndpointListQuery(client.Id, null, null, null, EndpointStatusFilter.InMaintenance, null));
        Assert.Equal(new[] { agentOnly.Id, managed.Id, branchEndpoint.Id }.Order(), page.Rows.Select(r => r.Id).Order());
        var managedRow = page.Rows.Single(r => r.Id == managed.Id);
        Assert.Equal(MaintenanceSource.Site, managedRow.Maintenance!.Source);
        Assert.Equal("Rack move", managedRow.Maintenance.Reason);
        Assert.False(managedRow.OwnMaintenanceActive);
        Assert.True(page.Rows.Single(r => r.Id == branchEndpoint.Id).OwnMaintenanceActive);
        Assert.Contains(Guid.Empty.ToString(), _fixture.Database.Bus.PayloadsFor(NotificationChannels.EndpointStatus));

        _fixture.Database.Time.Advance(TimeSpan.FromHours(3));
        page = await endpoints.ListAsync(caller, new EndpointListQuery(client.Id, null, null, null, EndpointStatusFilter.InMaintenance, null));
        Assert.DoesNotContain(page.Rows, r => r.Id == branchEndpoint.Id);
        Assert.Equal(2, Assert.Single(await clients.ListTreeAsync(caller)).InMaintenanceCount);
    }
}
