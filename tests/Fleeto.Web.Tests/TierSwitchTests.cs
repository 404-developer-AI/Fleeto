using Fleeto.Core.Entities;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Switching endpoints to managed through the web service returns the license problem of EndpointTierService verbatim
/// when the pool is exhausted, and changes nothing.
/// </summary>
[Collection(WebCollection.Name)]
public class TierSwitchTests
{
    private readonly WebFixture _fixture;

    public TierSwitchTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Switch_to_managed_shows_the_license_problem_when_the_pool_is_exhausted()
    {
        var db = _fixture.Database;
        var client = await db.CreateClientAsync();
        var site = await db.CreateSiteAsync(client.Id);
        var first = await db.CreateEndpointAsync(site, hostname: "WS-ONE");
        var second = await db.CreateEndpointAsync(site, hostname: "WS-TWO");
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var caller = WebFixtureBase.Technician();

        int inUse;
        await using (var count = db.DbFactory.CreateSystem())
        {
            inUse = await count.Endpoints.CountAsync(e => e.Tier == EndpointTier.Managed);
        }

        // Exactly one free license.
        await db.LoadTestLicenseAsync(inUse + 1);

        var both = await endpoints.SetTierAsync(caller, [first.Id, second.Id], EndpointTier.Managed);
        Assert.False(both.Success);
        Assert.StartsWith("Not enough licenses:", both.Problem);
        Assert.Contains("Add 1 license", both.Problem);

        await using (var check = db.DbFactory.CreateSystem())
        {
            Assert.Equal(0, await check.Endpoints.CountAsync(e => (e.Id == first.Id || e.Id == second.Id) && e.Tier == EndpointTier.Managed));
        }

        var one = await endpoints.SetTierAsync(caller, [first.Id], EndpointTier.Managed);
        Assert.True(one.Success, one.Problem);

        var next = await endpoints.SetTierAsync(caller, [second.Id], EndpointTier.Managed);
        Assert.False(next.Success);
        Assert.StartsWith("Not enough licenses:", next.Problem);

        var back = await endpoints.SetTierAsync(caller, [first.Id], EndpointTier.AgentOnly);
        Assert.True(back.Success, back.Problem);
    }
}
