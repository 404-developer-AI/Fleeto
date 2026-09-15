using Fleetify.Core.Entities;
using Fleetify.Workers.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Workers.Tests;

/// <summary>
/// Guarantees of the watchdog alerts (0.2.1): while the watchdog is online a stopped agent is "Agent service stopped" with the state the
/// watchdog reports, never an offline alert; an offline alert opened while both were gone resolves when the watchdog returns; a watchdog that
/// was installed and is gone while the agent is online is "Watchdog stopped"; both follow the policy delay and resolve when the service is
/// back; endpoints that never had a watchdog and agent-only endpoints get neither.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class ServiceAlertTests
{
    private readonly WorkersFixture _fixture;

    public ServiceAlertTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SetAsync(Guid endpointId, bool agentOnline, DateTime agentSeen, bool watchdogOnline, DateTime? watchdogSeen, string watchdogVersion = "0.2.1")
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpointId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.IsOnline, agentOnline)
            .SetProperty(e => e.LastSeenAt, agentSeen)
            .SetProperty(e => e.WatchdogOnline, watchdogOnline)
            .SetProperty(e => e.WatchdogLastSeenAt, watchdogSeen)
            .SetProperty(e => e.WatchdogVersion, watchdogVersion));
    }

    private async Task ReportAsync(Endpoint endpoint, AgentComponent component, ComponentServiceState state, string detail)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.EndpointComponentStates.Add(new EndpointComponentState
        {
            EndpointId = endpoint.Id, ClientId = endpoint.ClientId, Component = component, ServiceState = state, ServiceDetail = detail,
            ServiceStateAt = _fixture.Now
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<Alert>> AlertsAsync(Guid endpointId, AlertKind kind)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Alerts.AsNoTracking().Where(a => a.EndpointId == endpointId && a.Kind == kind).ToListAsync();
    }

    [Fact]
    public async Task A_stopped_agent_with_an_online_watchdog_is_agent_service_stopped_not_offline()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-AGENT-STOPPED");
        var agentOnly = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-AGENT-STOPPED");
        var since = _fixture.Now.AddMinutes(-20);
        await SetAsync(endpoint.Id, false, since, true, _fixture.Now);
        await SetAsync(agentOnly.Id, false, since, true, _fixture.Now);
        await ReportAsync(endpoint, AgentComponent.Agent, ComponentServiceState.Stopped, "Access is denied.");

        var health = _fixture.EndpointHealth();
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);
        await health.UpdateServiceAlertsAsync(CancellationToken.None);
        await health.UpdateServiceAlertsAsync(CancellationToken.None);

        Assert.Empty(await AlertsAsync(endpoint.Id, AlertKind.Offline));
        var alert = Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.AgentStopped));
        Assert.Equal(AlertState.Open, alert.State);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(EndpointHealthService.AgentStoppedTitle("SRV-AGENT-STOPPED", ComponentServiceState.Stopped, since), alert.Title);
        Assert.Equal("Access is denied.", alert.Detail);
        Assert.Empty(await AlertsAsync(agentOnly.Id, AlertKind.AgentStopped));

        // The watchdog goes too: the agent alert gives way to the offline alert.
        await SetAsync(endpoint.Id, false, since, false, since);
        await health.UpdateServiceAlertsAsync(CancellationToken.None);
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);
        Assert.Equal(AlertState.Resolved, Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.AgentStopped)).State);
        Assert.Equal(AlertState.Open, Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Offline)).State);

        // The watchdog returns: the offline alert resolves and the agent alert opens again.
        await SetAsync(endpoint.Id, false, since, true, _fixture.Now);
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);
        await health.UpdateServiceAlertsAsync(CancellationToken.None);
        var offline = Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Offline));
        Assert.Equal(AlertState.Resolved, offline.State);
        Assert.Equal(EndpointHealthService.ResolvedReasonWatchdogOnline, offline.ResolvedReason);
        Assert.Contains(await AlertsAsync(endpoint.Id, AlertKind.AgentStopped), a => a.State == AlertState.Open);

        // The agent comes back.
        await SetAsync(endpoint.Id, true, _fixture.Now, true, _fixture.Now);
        await health.UpdateServiceAlertsAsync(CancellationToken.None);
        Assert.All(await AlertsAsync(endpoint.Id, AlertKind.AgentStopped), a => Assert.Equal(AlertState.Resolved, a.State));
    }

    [Fact]
    public async Task A_watchdog_that_is_gone_while_the_agent_is_online_is_watchdog_stopped()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-WATCHDOG-STOPPED");
        var neverInstalled = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-NO-WATCHDOG");
        var recent = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-WATCHDOG-RECENT");
        var since = _fixture.Now.AddMinutes(-20);
        await SetAsync(endpoint.Id, true, _fixture.Now, false, since);
        await SetAsync(neverInstalled.Id, true, _fixture.Now, false, null, watchdogVersion: "");
        await SetAsync(recent.Id, true, _fixture.Now, false, _fixture.Now.AddMinutes(-2));
        await ReportAsync(endpoint, AgentComponent.Watchdog, ComponentServiceState.Disabled, string.Empty);

        var health = _fixture.EndpointHealth();
        await health.UpdateServiceAlertsAsync(CancellationToken.None);

        var alert = Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.WatchdogStopped));
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(EndpointHealthService.WatchdogStoppedTitle("SRV-WATCHDOG-STOPPED", ComponentServiceState.Disabled, since), alert.Title);
        Assert.Empty(await AlertsAsync(neverInstalled.Id, AlertKind.WatchdogStopped));
        Assert.Empty(await AlertsAsync(recent.Id, AlertKind.WatchdogStopped));

        await SetAsync(endpoint.Id, true, _fixture.Now, true, _fixture.Now);
        await health.UpdateServiceAlertsAsync(CancellationToken.None);
        var resolved = Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.WatchdogStopped));
        Assert.Equal(AlertState.Resolved, resolved.State);
        Assert.Equal(EndpointHealthService.ResolvedReasonWatchdogBack, resolved.ResolvedReason);
    }

    [Fact]
    public async Task A_silent_watchdog_is_marked_offline()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, hostname: "WS-SILENT-WATCHDOG");
        await SetAsync(endpoint.Id, true, _fixture.Now, true, _fixture.Now.AddMinutes(-10));

        var marked = await _fixture.EndpointHealth().MarkStaleEndpointsOfflineAsync(CancellationToken.None);

        Assert.Contains(endpoint.Id, marked);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var stored = await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id);
        Assert.False(stored.WatchdogOnline);
        Assert.True(stored.IsOnline);
    }
}
