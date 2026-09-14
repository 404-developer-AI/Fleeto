using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;
using Fleetify.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleetify.Web.Tests;

/// <summary>
/// A caller restricted to client A can neither read nor change sites, endpoints or tokens of client B through the web
/// services; the client scope reaches every service call.
/// </summary>
[Collection(WebCollection.Name)]
public class CrossClientTests
{
    private readonly WebFixture _fixture;

    public CrossClientTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Restricted_caller_cannot_read_or_change_another_clients_sites_and_endpoints()
    {
        var db = _fixture.Database;
        var clientA = await db.CreateClientAsync();
        var clientB = await db.CreateClientAsync();
        var siteA = await db.CreateSiteAsync(clientA.Id);
        var siteB = await db.CreateSiteAsync(clientB.Id);
        var siteB2 = await db.CreateSiteAsync(clientB.Id, "Second");
        var endpointB = await db.CreateEndpointAsync(siteB, hostname: "B-ENDPOINT");
        var (_, tokenB) = await db.CreateEnrollmentTokenAsync(siteB);

        var caller = WebFixtureBase.CallerWith(new RestrictedClientScope([clientA.Id]), FleetifyRoles.Technician);
        var clients = _fixture.Services.GetRequiredService<ClientService>();
        var sites = _fixture.Services.GetRequiredService<SiteService>();
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var enrollment = _fixture.Services.GetRequiredService<EnrollmentService>();

        // Reads: the other client's data is simply not there.
        Assert.NotNull(await sites.GetAsync(caller, siteA.Id));
        Assert.Null(await sites.GetAsync(caller, siteB.Id));
        Assert.Null(await clients.GetAsync(caller, clientB.Id));
        Assert.DoesNotContain(await clients.ListAsync(caller), c => c.Id == clientB.Id);
        Assert.Empty(await sites.ListEndpointsAsync(caller, siteB.Id));
        Assert.Null(await endpoints.GetAsync(caller, endpointB.Id));
        Assert.Null(await endpoints.GetInventoryAsync(caller, endpointB.Id));
        Assert.Empty(await enrollment.ListAsync(caller, siteB.Id));

        // Writes: refused as not found, and nothing changes.
        Assert.False((await sites.UpdateAsync(caller, siteB.Id, "Renamed", null)).Success);
        Assert.False((await sites.DeleteAsync(caller, siteB2.Id)).Success);
        Assert.False((await sites.CreateAsync(caller, clientB.Id, "Injected", null)).Success);
        Assert.False((await endpoints.SetClassOverrideAsync(caller, endpointB.Id, EndpointClass.Server)).Success);
        Assert.False((await endpoints.MoveAsync(caller, endpointB.Id, siteB2.Id)).Success);
        Assert.False((await endpoints.SetTierAsync(caller, [endpointB.Id], EndpointTier.Managed)).Success);
        Assert.False((await endpoints.RevokeAsync(caller, endpointB.Id, "test")).Success);
        Assert.False((await endpoints.DeleteAsync(caller, endpointB.Id)).Success);
        Assert.False((await enrollment.CreateAsync(caller, siteB.Id, "Token", TimeSpan.FromDays(1), 1)).Success);
        Assert.False((await enrollment.RevokeAsync(caller, tokenB.Id)).Success);
        Assert.False((await clients.RenameAsync(caller, clientB.Id, "Renamed")).Success);
        Assert.False((await clients.DeleteAsync(caller, clientB.Id, clientB.Code)).Success);

        // A move across clients is refused even for an unrestricted caller.
        var moveAcross = await endpoints.MoveAsync(WebFixtureBase.Technician(), endpointB.Id, siteA.Id);
        Assert.False(moveAcross.Success);

        await using var check = db.DbFactory.CreateSystem();
        Assert.Equal("Monitoring", (await check.Sites.SingleAsync(s => s.Id == siteB.Id)).Name);
        Assert.True(await check.Sites.AnyAsync(s => s.Id == siteB2.Id));
        Assert.False(await check.Sites.AnyAsync(s => s.Name == "Injected"));
        var endpoint = await check.Endpoints.SingleAsync(e => e.Id == endpointB.Id);
        Assert.Null(endpoint.ClassOverride);
        Assert.Equal(siteB.Id, endpoint.SiteId);
        Assert.Equal(EndpointTier.AgentOnly, endpoint.Tier);
        Assert.Null((await check.EnrollmentTokens.SingleAsync(t => t.Id == tokenB.Id)).RevokedAt);
        Assert.True(await check.Clients.AnyAsync(c => c.Id == clientB.Id && c.Name == "Test client"));
    }
}
