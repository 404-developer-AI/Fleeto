using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Gateway.Sessions;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using PatchScanRequest = Fleeto.Core.Entities.PatchScanRequest;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Patch scans in the gateway (0.9.0): a scan report is stored once per scan id, cleaned and capped, and acknowledged only after it is
/// stored; an agent-only endpoint's report is acknowledged and dropped (tier enforcement, layer 3), as are a report of an unknown tool
/// and a flood over the daily limit; and "Scan now" reaches a live managed agent once, never an agent-only endpoint.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class PatchScanSessionTests
{
    private readonly GatewayFixture _fixture;

    public PatchScanSessionTests(GatewayFixture fixture) => _fixture = fixture;

    private static PatchScanReport Report(string? scanId = null, string engine = "apt", string? requestId = null)
    {
        var report = new PatchScanReport
        {
            ScanId = scanId ?? Guid.NewGuid().ToString("D"),
            StartedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            DurationMs = 4200,
            Engine = engine,
            RestartRequired = true,
            RequestId = requestId ?? string.Empty
        };
        report.Updates.Add(new PatchUpdate
        {
            Id = "libssl3:amd64", Name = "libssl3", Version = "3.0.2-0ubuntu1.12", InstalledVersion = "3.0.2-0ubuntu1.10",
            Classification = "security", Origin = "jammy-security"
        });
        report.Updates.Add(new PatchUpdate { Id = "tzdata:all", Name = "tzdata\0", Version = "2024a-0ubuntu0.22.04", Classification = "other", Origin = "jammy-updates" });
        report.Updates.Add(new PatchUpdate { Id = "blank", Name = " " });
        return report;
    }

    private static List<ServerMessage> Acks(AgentSession session) =>
        GatewayHarness.Drain(session).Where(m => m.BodyCase == ServerMessage.BodyOneofCase.PatchScanAck).ToList();

    private async Task<List<PatchScan>> ScansAsync(Guid endpointId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.PatchScans.AsNoTracking().Where(s => s.EndpointId == endpointId).ToListAsync();
    }

    [Fact]
    public async Task A_report_is_stored_once_cleaned_and_acknowledged_every_time()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        var report = Report();
        await harness.Manager.HandleAsync(session, new AgentMessage { PatchScan = report }, CancellationToken.None);
        await harness.Manager.HandleAsync(session, new AgentMessage { PatchScan = report }, CancellationToken.None);

        var acks = Acks(session);
        Assert.Equal(2, acks.Count);
        Assert.All(acks, a => Assert.Equal(report.ScanId, a.PatchScanAck.ScanId));
        var scan = Assert.Single(await ScansAsync(endpoint.Id));
        Assert.Equal("apt", scan.Tool);
        Assert.True(scan.RestartRequired);
        Assert.Null(scan.Problem);
        Assert.Null(scan.ProcessedAt);
        Assert.Equal(endpoint.ClientId, scan.ClientId);
        Assert.Equal(endpoint.AgentVersion, scan.AgentVersion);
        // The update without a name is dropped, the control character cleaned.
        Assert.Equal(2, scan.UpdateCount);
        var updates = PatchScanRules.Parse(scan.UpdatesJson);
        Assert.Equal(["libssl3", "tzdata"], updates.Select(u => u.Name));
        Assert.True(updates[0].Security);
        Assert.Equal("3.0.2-0ubuntu1.10", updates[0].InstalledVersion);
        Assert.False(updates[1].Security);
    }

    [Fact]
    public async Task A_report_of_an_agent_only_endpoint_or_an_unknown_tool_is_acknowledged_and_not_stored()
    {
        using var harness = _fixture.CreateHarness();
        var agentOnly = await _fixture.CreateEndpointAsync(EndpointTier.AgentOnly);
        using (var session = await harness.OpenAsync(agentOnly))
        {
            GatewayHarness.Drain(session);
            await harness.Manager.HandleAsync(session, new AgentMessage { PatchScan = Report() }, CancellationToken.None);
            Assert.Single(Acks(session));
        }

        Assert.Empty(await ScansAsync(agentOnly.Id));

        var managed = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var managedSession = await harness.OpenAsync(managed);
        GatewayHarness.Drain(managedSession);
        await harness.Manager.HandleAsync(managedSession, new AgentMessage { PatchScan = Report(engine: "zypper") }, CancellationToken.None);
        Assert.Single(Acks(managedSession));
        Assert.Empty(await ScansAsync(managed.Id));
    }

    [Fact]
    public async Task A_report_without_a_scan_id_closes_the_session()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);

        await harness.Manager.HandleAsync(session, new AgentMessage { PatchScan = Report(scanId: "not-a-uuid") }, CancellationToken.None);

        Assert.True(session.IsClosing);
        Assert.Empty(await ScansAsync(endpoint.Id));
    }

    [Fact]
    public async Task More_updates_than_the_limit_are_cut_and_the_scan_says_so()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        var report = new PatchScanReport { ScanId = Guid.NewGuid().ToString("D"), Engine = "dnf", Problem = new string('x', 5000) };
        for (var i = 0; i < PatchScanRules.MaxUpdates + 10; i++)
        {
            report.Updates.Add(new PatchUpdate { Id = $"pkg{i}:x86_64", Name = $"pkg{i}", Version = "1.0-1.el9" });
        }

        await harness.Manager.HandleAsync(session, new AgentMessage { PatchScan = report }, CancellationToken.None);

        var scan = Assert.Single(await ScansAsync(endpoint.Id));
        Assert.Equal(PatchScanRules.MaxUpdates, scan.UpdateCount);
        Assert.True(scan.Truncated);
        Assert.Equal(PatchScanRules.MaxProblemLength, scan.Problem!.Length);
    }

    [Fact]
    public async Task Too_many_reports_in_a_day_are_acknowledged_and_dropped()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        for (var i = 0; i < PatchScanRules.MaxScansPerEndpointPerDay + 2; i++)
        {
            await harness.Manager.HandleAsync(session, new AgentMessage { PatchScan = Report() }, CancellationToken.None);
        }

        Assert.Equal(PatchScanRules.MaxScansPerEndpointPerDay + 2, Acks(session).Count);
        Assert.Equal(PatchScanRules.MaxScansPerEndpointPerDay, (await ScansAsync(endpoint.Id)).Count);
    }

    private async Task<PatchScanRequest> AddRequestAsync(Endpoint endpoint, TimeSpan? lifetime = null, TimeSpan? age = null)
    {
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var request = new PatchScanRequest
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, RequestedByUserId = Guid.NewGuid(),
            RequestedByName = "Tech", RequestedAt = now - (age ?? TimeSpan.Zero), ExpiresAt = now + (lifetime ?? TimeSpan.FromMinutes(15))
        };
        db.PatchScanRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private static List<Protocol.Agent.V1.PatchScanRequest> RequestMessages(AgentSession session) =>
        GatewayHarness.Drain(session).Where(m => m.BodyCase == ServerMessage.BodyOneofCase.PatchScanRequest).Select(m => m.PatchScanRequest).ToList();

    [Fact]
    public async Task Pending_requests_reach_a_live_managed_agent_as_one_message_once()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        var older = await AddRequestAsync(endpoint, age: TimeSpan.FromMinutes(1));
        var newer = await AddRequestAsync(endpoint);
        await _fixture.Database.Bus.PublishAsync(NotificationChannels.PatchScanRequests, newer.Id.ToString());
        await harness.Manager.DeliverPatchScanRequestsAsync([endpoint.Id], CancellationToken.None);
        await harness.Manager.DeliverPatchScanRequestsAsync([endpoint.Id], CancellationToken.None);

        Assert.Equal(older.Id.ToString("D"), Assert.Single(RequestMessages(session)).RequestId);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.All(await db.PatchScanRequests.AsNoTracking().Where(r => r.EndpointId == endpoint.Id).ToListAsync(), r => Assert.NotNull(r.DeliveredAt));
    }

    [Fact]
    public async Task A_pending_request_is_delivered_when_the_agent_connects_and_an_expired_one_never()
    {
        using var harness = _fixture.CreateHarness();
        var pendingEndpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var request = await AddRequestAsync(pendingEndpoint);
        using (var session = await harness.OpenAsync(pendingEndpoint))
        {
            Assert.Equal(request.Id.ToString("D"), Assert.Single(RequestMessages(session)).RequestId);
        }

        var expiredEndpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        await AddRequestAsync(expiredEndpoint, lifetime: TimeSpan.FromSeconds(1));
        _fixture.Database.Time.Advance(TimeSpan.FromSeconds(5));
        using var expiredSession = await harness.OpenAsync(expiredEndpoint);
        Assert.Empty(RequestMessages(expiredSession));
    }

    [Fact]
    public async Task An_agent_only_endpoint_never_receives_a_request()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.AgentOnly);
        var request = await AddRequestAsync(endpoint);
        using var session = await harness.OpenAsync(endpoint);
        await harness.Manager.DeliverPatchScanRequestsAsync([endpoint.Id], CancellationToken.None);

        Assert.Empty(RequestMessages(session));
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Null((await db.PatchScanRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).DeliveredAt);
    }
}
