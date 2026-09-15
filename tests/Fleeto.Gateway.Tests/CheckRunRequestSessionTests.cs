using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Gateway.Sessions;
using Fleeto.Gateway.Tls;
using Fleeto.Protocol.Agent.V1;
using Microsoft.EntityFrameworkCore;
using CheckType = Fleeto.Core.Entities.CheckType;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Guarantees that check run requests reach a live managed agent exactly once (on notification, at session start and on
/// catch-up), never an agent-only endpoint (tier enforcement, layer 3), never before a requested reset was applied, and
/// never after they expired; and that the session stores the agent's public address.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class CheckRunRequestSessionTests
{
    private readonly GatewayFixture _fixture;

    public CheckRunRequestSessionTests(GatewayFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<CheckRunRequest> AddRequestAsync(Endpoint endpoint, bool reset = false, DateTime? resetAppliedAt = null, TimeSpan? lifetime = null)
    {
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var check = new CheckDefinition
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Name = "Uptime", Type = CheckType.Uptime,
            WarningThreshold = 30, CreatedAt = now, UpdatedAt = now
        };
        var request = new CheckRunRequest
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, CheckDefinitionId = check.Id, Reset = reset,
            ResetAppliedAt = resetAppliedAt, RequestedByUserId = Guid.NewGuid(), RequestedByName = "Tech", RequestedAt = now,
            ExpiresAt = now + (lifetime ?? TimeSpan.FromMinutes(10))
        };
        db.CheckDefinitions.Add(check);
        db.CheckRunRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private static List<RunChecksNow> RunMessages(AgentSession session) =>
        GatewayHarness.Drain(session).Where(m => m.BodyCase == ServerMessage.BodyOneofCase.RunChecksNow).Select(m => m.RunChecksNow).ToList();

    [Fact]
    public async Task A_request_reaches_a_live_managed_agent_once()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        var request = await AddRequestAsync(endpoint);
        await _fixture.Database.Bus.PublishAsync(NotificationChannels.CheckRunRequests, request.Id.ToString());
        await _fixture.Database.Bus.PublishAsync(NotificationChannels.CheckRunRequests, request.Id.ToString());
        await harness.Manager.DeliverRunRequestsAsync([endpoint.Id], CancellationToken.None);

        var message = Assert.Single(RunMessages(session));
        Assert.Equal(request.Id.ToString("D"), message.RequestId);
        Assert.Equal([request.CheckDefinitionId.ToString("D")], message.CheckIds);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.NotNull((await db.CheckRunRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).DeliveredAt);
    }

    [Fact]
    public async Task A_pending_request_is_delivered_when_the_agent_connects()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var request = await AddRequestAsync(endpoint);

        using var session = await harness.OpenAsync(endpoint);

        Assert.Equal(request.Id.ToString("D"), Assert.Single(RunMessages(session)).RequestId);
    }

    [Fact]
    public async Task An_agent_only_endpoint_never_receives_a_request()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.AgentOnly);
        var request = await AddRequestAsync(endpoint);
        using var session = await harness.OpenAsync(endpoint);
        await _fixture.Database.Bus.PublishAsync(NotificationChannels.CheckRunRequests, request.Id.ToString());
        await harness.Manager.DeliverRunRequestsAsync([endpoint.Id], CancellationToken.None);

        Assert.Empty(RunMessages(session));
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Null((await db.CheckRunRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).DeliveredAt);
    }

    [Fact]
    public async Task A_reset_request_waits_for_the_reset_and_an_expired_request_is_not_sent()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        var reset = await AddRequestAsync(endpoint, reset: true);
        var expired = await AddRequestAsync(endpoint, lifetime: TimeSpan.FromSeconds(1));
        _fixture.Database.Time.Advance(TimeSpan.FromSeconds(5));
        await harness.Manager.DeliverRunRequestsAsync([endpoint.Id], CancellationToken.None);
        Assert.Empty(RunMessages(session));

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
            await db.CheckRunRequests.Where(r => r.Id == reset.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ResetAppliedAt, now));
        }

        await _fixture.Database.Bus.PublishAsync(NotificationChannels.CheckRunRequests, reset.Id.ToString());
        Assert.Equal(reset.Id.ToString("D"), Assert.Single(RunMessages(session)).RequestId);
        await using var verify = _fixture.Database.DbFactory.CreateSystem();
        Assert.Null((await verify.CheckRunRequests.AsNoTracking().SingleAsync(r => r.Id == expired.Id)).DeliveredAt);
    }

    [Fact]
    public async Task The_session_stores_the_public_address_of_the_agent()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.AgentOnly);
        using var session = new AgentSession(new AgentIdentity(endpoint.Id, Guid.NewGuid().ToString("N"), "key", DateTime.UtcNow.AddDays(30)),
            "203.0.113.5", harness.Options.SendQueueCapacity, _fixture.Database.Time.GetUtcNow().UtcDateTime)
        {
            PublicIpAddress = "203.0.113.5"
        };

        Assert.True(await harness.Manager.OpenAsync(session, GatewayHarness.Hello(), CancellationToken.None));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var stored = await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id);
        Assert.Equal("203.0.113.5", stored.PublicIpAddress);
        Assert.NotNull(stored.PublicIpSeenAt);
    }
}
