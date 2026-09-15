using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Queries behind the clients workspace: the clients panel with sites and counts, the endpoint list per scope with class
/// counts, search on hostname and signed-in user, status and tier filters, and client scoping of every result.
/// </summary>
[Collection(WebCollection.Name)]
public class ClientsWorkspaceQueryTests
{
    private readonly WebFixture _fixture;

    public ClientsWorkspaceQueryTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Endpoint_list_counts_classes_and_filters_by_scope_search_status_and_tier()
    {
        var db = _fixture.Database;
        var client = await db.CreateClientAsync();
        var siteA = await db.CreateSiteAsync(client.Id, "HQ");
        var siteB = await db.CreateSiteAsync(client.Id, "Branch");
        var server = await db.CreateEndpointAsync(siteA, EndpointTier.Managed, "WSQ-SRV-01", EndpointClass.Server);
        var workstation = await db.CreateEndpointAsync(siteA, EndpointTier.AgentOnly, "WSQ-LAPTOP-01");
        var branchWorkstation = await db.CreateEndpointAsync(siteB, EndpointTier.AgentOnly, "WSQ-LAPTOP-02");
        await using (var context = db.DbFactory.CreateSystem())
        {
            var now = DateTime.UtcNow;
            var serverEntity = await context.Endpoints.FindAsync(server.Id);
            serverEntity!.IsOnline = true;
            context.InventorySnapshots.Add(new InventorySnapshot
            {
                EndpointId = workstation.Id, ClientId = client.Id, ReceivedAt = now, Hash = "h", LoggedOnUser = @"CONTOSO\jdoe"
            });
            context.Alerts.Add(new Alert
            {
                Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = server.Id, Kind = AlertKind.Offline, Severity = AlertSeverity.Critical,
                Title = "Offline", OpenedAt = now, UpdatedAt = now
            });
            await context.SaveChangesAsync();
        }

        var caller = WebFixtureBase.CallerWith(new RestrictedClientScope([client.Id]), FleetoRoles.ReadOnly);
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();

        var clientServers = await endpoints.ListAsync(caller, new EndpointListQuery(client.Id, null, EndpointClass.Server, null, EndpointStatusFilter.All, null));
        Assert.Equal(1, clientServers.ServerCount);
        Assert.Equal(2, clientServers.WorkstationCount);
        var serverRow = Assert.Single(clientServers.Rows);
        Assert.Equal(server.Id, serverRow.Id);
        Assert.Equal(1, serverRow.OpenAlertCount);
        Assert.True(serverRow.HasCriticalAlert);
        Assert.Equal("HQ", serverRow.SiteName);

        var siteMixed = await endpoints.ListAsync(caller, new EndpointListQuery(client.Id, siteB.Id, null, null, EndpointStatusFilter.All, null));
        Assert.Equal(branchWorkstation.Id, Assert.Single(siteMixed.Rows).Id);

        var byUser = await endpoints.ListAsync(caller, new EndpointListQuery(client.Id, null, null, "jdoe", EndpointStatusFilter.All, null));
        var userRow = Assert.Single(byUser.Rows);
        Assert.Equal(workstation.Id, userRow.Id);
        Assert.Equal(@"CONTOSO\jdoe", userRow.LoggedOnUser);

        var online = await endpoints.ListAsync(caller, new EndpointListQuery(client.Id, null, null, null, EndpointStatusFilter.Online, null));
        Assert.Equal(server.Id, Assert.Single(online.Rows).Id);

        var withAlerts = await endpoints.ListAsync(caller, new EndpointListQuery(client.Id, null, null, null, EndpointStatusFilter.WithOpenAlerts, null));
        Assert.Equal(server.Id, Assert.Single(withAlerts.Rows).Id);

        var agentOnly = await endpoints.ListAsync(caller, new EndpointListQuery(client.Id, null, null, null, EndpointStatusFilter.All, EndpointTier.AgentOnly));
        Assert.Equal(2, agentOnly.Rows.Count);
        Assert.Equal(0, agentOnly.ServerCount);
    }

    [Fact]
    public async Task All_clients_list_and_clients_panel_only_contain_clients_in_scope()
    {
        var db = _fixture.Database;
        var clientA = await db.CreateClientAsync();
        var clientB = await db.CreateClientAsync();
        var siteA = await db.CreateSiteAsync(clientA.Id, "Monitoring");
        var siteB = await db.CreateSiteAsync(clientB.Id, "Monitoring");
        var endpointA = await db.CreateEndpointAsync(siteA, hostname: "SCOPE-A");
        await db.CreateEndpointAsync(siteB, hostname: "SCOPE-B");

        var caller = WebFixtureBase.CallerWith(new RestrictedClientScope([clientA.Id]), FleetoRoles.ReadOnly);
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var clients = _fixture.Services.GetRequiredService<ClientService>();
        var sites = _fixture.Services.GetRequiredService<SiteService>();

        var all = await endpoints.ListAsync(caller, new EndpointListQuery(null, null, null, "SCOPE-", EndpointStatusFilter.All, null));
        var row = Assert.Single(all.Rows);
        Assert.Equal(endpointA.Id, row.Id);
        Assert.Equal(clientA.Code, row.ClientCode);

        var tree = await clients.ListTreeAsync(caller);
        var treeClient = Assert.Single(tree);
        Assert.Equal(clientA.Id, treeClient.Id);
        var treeSite = Assert.Single(treeClient.Sites);
        Assert.Equal(siteA.Id, treeSite.Id);
        Assert.Equal(1, treeSite.EndpointCount);

        Assert.NotNull(await sites.GetSummaryAsync(caller, siteA.Id));
        Assert.Null(await sites.GetSummaryAsync(caller, siteB.Id));
    }

    [Fact]
    public async Task Clients_panel_search_matches_site_names_too()
    {
        var db = _fixture.Database;
        var client = await db.CreateClientAsync();
        await db.CreateSiteAsync(client.Id, "Unique-Warehouse-Site");

        var caller = WebFixtureBase.CallerWith(new RestrictedClientScope([client.Id]), FleetoRoles.ReadOnly);
        var clients = _fixture.Services.GetRequiredService<ClientService>();

        var tree = await clients.ListTreeAsync(caller, "warehouse");

        Assert.Equal(client.Id, Assert.Single(tree).Id);
    }
}
