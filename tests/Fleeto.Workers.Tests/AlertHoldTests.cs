using Fleeto.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees that an alert hold is maintenance for the source of the alert (decided 2026-10-18): while it lasts no alert of the same
/// check and target, or the same service alert, opens again after the held alert resolved; other checks of the endpoint still alert;
/// once the hold ends the first failure opens the alert; and a duplicate identity alert is never kept quiet by a hold.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class AlertHoldTests
{
    private readonly WorkersFixture _fixture;

    public AlertHoldTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Site Site, Endpoint Endpoint)> ManagedEndpointAsync(string hostname)
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        return (site, await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, hostname, EndpointClass.Server));
    }

    private async Task<List<Alert>> AlertsAsync(Guid endpointId, AlertKind kind)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Alerts.AsNoTracking().Where(a => a.EndpointId == endpointId && a.Kind == kind).OrderBy(a => a.OpenedAt).ToListAsync();
    }

    private async Task HoldAsync(Guid alertId, DateTime until)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var now = _fixture.Now;
        Assert.Equal(1, await db.Alerts.Where(a => a.Id == alertId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.HeldUntil, until).SetProperty(a => a.HeldAt, now)));
    }

    [Fact]
    public async Task A_check_that_fails_again_after_its_held_alert_resolved_opens_no_alert_until_the_hold_ends()
    {
        var (site, endpoint) = await ManagedEndpointAsync("SRV-HOLD-CHECK");
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 95);
        var memory = await _fixture.CreateCheckAsync(site, CheckType.MemoryUsage, 80, 95);
        var evaluation = _fixture.CheckEvaluation();
        await _fixture.InsertResultAsync(endpoint, cpu, 99);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        var held = Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Check));
        await HoldAsync(held.Id, _fixture.Now.AddHours(1));

        _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));
        await _fixture.InsertResultAsync(endpoint, cpu, 10);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Equal(AlertState.Resolved, Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Check)).State);

        // The same check fails again within the hold: nothing opens. Another check of the endpoint still alerts.
        _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));
        await _fixture.InsertResultAsync(endpoint, cpu, 99);
        await _fixture.InsertResultAsync(endpoint, memory, 99);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        var alerts = await AlertsAsync(endpoint.Id, AlertKind.Check);
        Assert.Equal(2, alerts.Count);
        Assert.DoesNotContain(alerts, a => a.CheckDefinitionId == cpu.Id && a.State != AlertState.Resolved);
        Assert.Contains(alerts, a => a.CheckDefinitionId == memory.Id && a.State == AlertState.Open);

        // After the hold the first failing result opens it at once.
        _fixture.Db.Time.Advance(TimeSpan.FromHours(1));
        await _fixture.InsertResultAsync(endpoint, cpu, 99);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Contains(await AlertsAsync(endpoint.Id, AlertKind.Check), a => a.CheckDefinitionId == cpu.Id && a.State == AlertState.Open);

        // The expired hold of the resolved alert is cleared without an email.
        Assert.DoesNotContain(held.Id, await _fixture.AlertHolds().EndExpiredHoldsAsync(CancellationToken.None));
        await _fixture.AlertHolds().ClearResolvedHoldsAsync(CancellationToken.None);
        Assert.Null((await AlertsAsync(endpoint.Id, AlertKind.Check)).Single(a => a.Id == held.Id).HeldUntil);
    }

    [Fact]
    public async Task An_endpoint_that_goes_offline_again_within_the_hold_of_its_offline_alert_gets_no_new_one_until_the_hold_ends()
    {
        var (_, endpoint) = await ManagedEndpointAsync("SRV-HOLD-OFFLINE");
        await SetOnlineAsync(endpoint.Id, false, _fixture.Now.AddHours(-1));
        var health = _fixture.EndpointHealth();
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);
        var held = Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Offline));
        await HoldAsync(held.Id, _fixture.Now.AddHours(2));

        await SetOnlineAsync(endpoint.Id, true, _fixture.Now);
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);
        Assert.Equal(AlertState.Resolved, Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Offline)).State);

        await SetOnlineAsync(endpoint.Id, false, _fixture.Now.AddHours(-1));
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);
        Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Offline));

        _fixture.Db.Time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);
        var alerts = await AlertsAsync(endpoint.Id, AlertKind.Offline);
        Assert.Equal(2, alerts.Count);
        Assert.Equal(AlertState.Open, alerts[^1].State);
    }

    [Fact]
    public async Task A_hold_on_a_duplicate_identity_alert_does_not_keep_the_next_one_from_opening()
    {
        var (_, endpoint) = await ManagedEndpointAsync("SRV-HOLD-CLONE");
        await ReportDuplicateAsync(endpoint);
        var held = Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.DuplicateIdentity));
        await HoldAsync(held.Id, _fixture.Now.AddHours(1));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.Alerts.Where(a => a.Id == held.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AlertState.Resolved));
        }

        await ReportDuplicateAsync(endpoint);
        Assert.Equal(2, (await AlertsAsync(endpoint.Id, AlertKind.DuplicateIdentity)).Count);
    }

    private async Task SetOnlineAsync(Guid endpointId, bool online, DateTime lastSeen)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpointId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.IsOnline, online).SetProperty(e => e.LastSeenAt, lastSeen));
    }

    private async Task ReportDuplicateAsync(Endpoint endpoint)
    {
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.EndpointEvents.Add(new EndpointEvent
            {
                ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = EndpointEventKind.DuplicateIdentity, Time = _fixture.Now
            });
            await db.SaveChangesAsync();
        }

        var events = _fixture.EndpointEvents();
        while (await events.ProcessPendingAsync(CancellationToken.None) > 0)
        {
        }
    }
}
