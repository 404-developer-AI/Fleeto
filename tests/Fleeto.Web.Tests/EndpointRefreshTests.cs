using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Refresh on an endpoint (0.6.0): a live agent is asked for a fresh inventory at most once a minute, by any user who can see
/// the endpoint; an offline endpoint only reloads, and another client's endpoint is not found.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class EndpointRefreshTests
{
    private readonly WebFixture _fixture;

    public EndpointRefreshTests(WebFixture fixture) => _fixture = fixture;

    private async Task<Endpoint> EndpointAsync(bool online)
    {
        var db = _fixture.Database;
        var client = await db.CreateClientAsync();
        var endpoint = await db.CreateEndpointAsync(await db.CreateSiteAsync(client.Id));
        await using var context = db.DbFactory.CreateSystem();
        await context.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.IsOnline, online));
        return endpoint;
    }

    private async Task<DateTime?> RequestedAtAsync(Guid endpointId)
    {
        await using var context = _fixture.Database.DbFactory.CreateSystem();
        return await context.Endpoints.Where(e => e.Id == endpointId).Select(e => e.InventoryRequestedAt).SingleAsync();
    }

    [Fact]
    public async Task A_live_agent_is_asked_once_a_minute_also_by_a_read_only_user()
    {
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var endpoint = await EndpointAsync(online: true);
        var readOnly = WebFixtureBase.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);

        var first = await endpoints.RequestInventoryAsync(readOnly, endpoint.Id);
        Assert.True(first is { Success: true, Value: true });
        Assert.Contains(endpoint.Id.ToString(), _fixture.Database.Bus.PayloadsFor(NotificationChannels.InventoryRequests));
        var requestedAt = await RequestedAtAsync(endpoint.Id);
        Assert.NotNull(requestedAt);

        var again = await endpoints.RequestInventoryAsync(WebFixtureBase.Technician(), endpoint.Id);
        Assert.True(again is { Success: true, Value: false });
        Assert.Equal(requestedAt, await RequestedAtAsync(endpoint.Id));

        // A minute later it may ask again.
        await using (var context = _fixture.Database.DbFactory.CreateSystem())
        {
            await context.Endpoints.Where(e => e.Id == endpoint.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.InventoryRequestedAt, requestedAt - InventoryRefreshRules.Interval));
        }

        Assert.True((await endpoints.RequestInventoryAsync(WebFixtureBase.Technician(), endpoint.Id)).Value);
    }

    [Fact]
    public async Task An_offline_endpoint_only_reloads_and_another_clients_endpoint_is_not_found()
    {
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var offline = await EndpointAsync(online: false);

        var result = await endpoints.RequestInventoryAsync(WebFixtureBase.Technician(), offline.Id);
        Assert.True(result is { Success: true, Value: false });
        Assert.Null(await RequestedAtAsync(offline.Id));

        var other = await EndpointAsync(online: true);
        var restricted = WebFixtureBase.CallerWith(new RestrictedClientScope([offline.ClientId]), FleetoRoles.Technician);
        Assert.False((await endpoints.RequestInventoryAsync(restricted, other.Id)).Success);
        Assert.Null(await RequestedAtAsync(other.Id));
    }
}
