using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Gateway.Sessions;
using Fleeto.Protocol.Agent.V1;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Refresh on an endpoint (0.6.0): the gateway asks a live agent for its inventory when a recent request is stored, also an
/// agent-only one; the notification alone sends nothing, and neither does an old request or one the inventory already answered.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class InventoryRequestSessionTests
{
    private readonly GatewayFixture _fixture;

    public InventoryRequestSessionTests(GatewayFixture fixture) => _fixture = fixture;

    private async Task RequestAsync(Endpoint endpoint, TimeSpan ago)
    {
        var at = _fixture.Database.Time.GetUtcNow().UtcDateTime - ago;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.InventoryRequestedAt, at));
    }

    private static int InventoryRequests(AgentSession session) =>
        GatewayHarness.Drain(session).Count(m => m.BodyCase == ServerMessage.BodyOneofCase.InventoryRequest);

    [Fact]
    public async Task A_recent_request_reaches_the_live_agent()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.AgentOnly);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        // Without a stored request the notification sends nothing.
        await _fixture.Database.Bus.PublishAsync(NotificationChannels.InventoryRequests, endpoint.Id.ToString());
        Assert.Equal(0, InventoryRequests(session));

        await RequestAsync(endpoint, TimeSpan.FromSeconds(5));
        await _fixture.Database.Bus.PublishAsync(NotificationChannels.InventoryRequests, endpoint.Id.ToString());
        Assert.Equal(1, InventoryRequests(session));

        // After a lost notification the resync finds it.
        await _fixture.Database.Bus.PublishAsync(NotificationChannels.InventoryRequests, NotificationBusEvents.Resync);
        Assert.Equal(1, InventoryRequests(session));
    }

    [Fact]
    public async Task An_old_or_answered_request_is_not_sent()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        await RequestAsync(endpoint, InventoryRefreshRules.DeliveryWindow + TimeSpan.FromMinutes(1));
        await harness.Manager.DeliverInventoryRequestsAsync([endpoint.Id], CancellationToken.None);
        Assert.Equal(0, InventoryRequests(session));

        await RequestAsync(endpoint, TimeSpan.FromSeconds(5));
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.InventorySnapshots.Add(new InventorySnapshot
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ReceivedAt = _fixture.Database.Time.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        await harness.Manager.DeliverInventoryRequestsAsync([endpoint.Id], CancellationToken.None);
        Assert.Equal(0, InventoryRequests(session));
    }
}
