using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Workers.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees that endpoints whose agent went silent are marked offline, that managed endpoints get an offline alert
/// after the policy's delay which resolves when they return, and that a duplicate agent identity opens one critical alert.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class EndpointHealthTests
{
    private readonly WorkersFixture _fixture;

    public EndpointHealthTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SetOnlineAsync(Guid endpointId, bool online, DateTime? lastSeenAt)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpointId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.IsOnline, online)
            .SetProperty(e => e.LastSeenAt, lastSeenAt));
    }

    private async Task<List<Alert>> AlertsAsync(Guid endpointId, AlertKind kind)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Alerts.AsNoTracking().Where(a => a.EndpointId == endpointId && a.Kind == kind).ToListAsync();
    }

    [Fact]
    public async Task An_online_endpoint_without_recent_messages_is_marked_offline()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var silent = await _fixture.Db.CreateEndpointAsync(site, hostname: "WS-SILENT");
        var healthy = await _fixture.Db.CreateEndpointAsync(site, hostname: "WS-HEALTHY");
        await SetOnlineAsync(silent.Id, true, _fixture.Now.AddMinutes(-5));
        await SetOnlineAsync(healthy.Id, true, _fixture.Now.AddSeconds(-30));

        var marked = await _fixture.EndpointHealth().MarkStaleEndpointsOfflineAsync(CancellationToken.None);

        Assert.Contains(silent.Id, marked);
        Assert.DoesNotContain(healthy.Id, marked);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await db.Endpoints.Where(e => e.Id == silent.Id).Select(e => e.IsOnline).SingleAsync());
        Assert.True(await db.Endpoints.Where(e => e.Id == healthy.Id).Select(e => e.IsOnline).SingleAsync());
        Assert.Contains(silent.Id.ToString(), _fixture.Db.Bus.PayloadsFor(NotificationChannels.EndpointStatus));
    }

    [Fact]
    public async Task A_managed_endpoint_offline_past_the_policy_delay_gets_an_alert_that_resolves_when_it_returns()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var managed = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-OFFLINE");
        var agentOnly = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-AGENTONLY");
        var recent = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-RECENT");
        var lastSeen = _fixture.Now.AddMinutes(-11);
        await SetOnlineAsync(managed.Id, false, lastSeen);
        await SetOnlineAsync(agentOnly.Id, false, _fixture.Now.AddMinutes(-30));
        await SetOnlineAsync(recent.Id, false, _fixture.Now.AddMinutes(-2));

        var health = _fixture.EndpointHealth();
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);

        var alert = Assert.Single(await AlertsAsync(managed.Id, AlertKind.Offline));
        Assert.Equal(AlertState.Open, alert.State);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(EndpointHealthService.OfflineTitle("SRV-OFFLINE", lastSeen), alert.Title);
        Assert.StartsWith("SRV-OFFLINE has been offline since ", alert.Title);
        Assert.EndsWith(" UTC. Check that the endpoint is powered on and connected.", alert.Title);
        Assert.Empty(await AlertsAsync(agentOnly.Id, AlertKind.Offline));
        Assert.Empty(await AlertsAsync(recent.Id, AlertKind.Offline));

        await SetOnlineAsync(managed.Id, true, _fixture.Now);
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);

        var resolved = Assert.Single(await AlertsAsync(managed.Id, AlertKind.Offline));
        Assert.Equal(AlertState.Resolved, resolved.State);
        Assert.Equal(EndpointHealthService.ResolvedReasonOnline, resolved.ResolvedReason);
    }

    [Fact]
    public async Task A_duplicate_identity_event_opens_one_critical_alert()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, hostname: "WS-CLONE");
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            foreach (var kind in new[] { EndpointEventKind.Connected, EndpointEventKind.DuplicateIdentity, EndpointEventKind.DuplicateIdentity, EndpointEventKind.Disconnected })
            {
                db.EndpointEvents.Add(new EndpointEvent
                {
                    ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = kind, Detail = "Second connection from 192.0.2.10", Time = _fixture.Now
                });
            }

            await db.SaveChangesAsync();
        }

        var events = _fixture.EndpointEvents();
        while (await events.ProcessPendingAsync(CancellationToken.None) > 0)
        {
        }

        // A later duplicate report while the alert is open does not open a second one.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.EndpointEvents.Add(new EndpointEvent
            {
                ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = EndpointEventKind.DuplicateIdentity, Time = _fixture.Now
            });
            await db.SaveChangesAsync();
        }

        await events.ProcessPendingAsync(CancellationToken.None);

        var alert = Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.DuplicateIdentity));
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("Two endpoints use the agent identity of WS-CLONE. Revoke the agent on the endpoint page and enroll the copies again.", alert.Title);
        await using var check = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await check.EndpointEvents.AnyAsync(e => e.EndpointId == endpoint.Id && e.ProcessedAt == null));
    }

    [Fact]
    public async Task A_duplicate_identity_alert_resolves_after_24_quiet_hours_only()
    {
        // 0.6.0: a copy that is still running keeps connecting and keeps the alert open; one that stopped lets it resolve.
        var (_, site) = await _fixture.CreateClientAndSiteAsync();

        async Task<Guid> DuplicateAsync(string hostname, TimeSpan openedAgo, TimeSpan? lastDuplicateAgo)
        {
            var endpoint = await _fixture.Db.CreateEndpointAsync(site, hostname: hostname);
            await using var db = _fixture.Db.DbFactory.CreateSystem();
            db.Alerts.Add(new Alert
            {
                Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = AlertKind.DuplicateIdentity, Target = string.Empty,
                Severity = AlertSeverity.Critical, State = AlertState.Open, Title = EndpointEventService.DuplicateIdentityTitle(hostname),
                OpenedAt = _fixture.Now - openedAgo, UpdatedAt = _fixture.Now - openedAgo
            });
            if (lastDuplicateAgo is { } ago)
            {
                db.EndpointEvents.Add(new EndpointEvent
                {
                    ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = EndpointEventKind.DuplicateIdentity,
                    Time = _fixture.Now - ago, ProcessedAt = _fixture.Now - ago
                });
            }

            await db.SaveChangesAsync();
            return endpoint.Id;
        }

        var quiet = await DuplicateAsync("WS-QUIET", TimeSpan.FromHours(30), TimeSpan.FromHours(25));
        var stillRunning = await DuplicateAsync("WS-RUNNING", TimeSpan.FromHours(30), TimeSpan.FromHours(1));
        var recent = await DuplicateAsync("WS-RECENT", TimeSpan.FromHours(2), lastDuplicateAgo: null);

        Assert.True(await _fixture.EndpointEvents().ResolveQuietDuplicatesAsync(CancellationToken.None) >= 1);

        var resolved = Assert.Single(await AlertsAsync(quiet, AlertKind.DuplicateIdentity));
        Assert.Equal(AlertState.Resolved, resolved.State);
        Assert.Equal(EndpointEventService.ResolvedReasonDuplicateQuiet, resolved.ResolvedReason);
        Assert.NotEqual(AlertState.Resolved, Assert.Single(await AlertsAsync(stillRunning, AlertKind.DuplicateIdentity)).State);
        Assert.NotEqual(AlertState.Resolved, Assert.Single(await AlertsAsync(recent, AlertKind.DuplicateIdentity)).State);
    }
}
