using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees maintenance mode in the workers: no check or offline alert opens or escalates while an endpoint, its site or its
/// client is in maintenance; open alerts still resolve; the first failing result after maintenance opens the alert at once;
/// duplicate identity alerts are never suppressed; an end time that passes is audited once and refreshes open pages; and a running
/// maintenance window of the site's policy suppresses alerts for the classes it applies to, with occurrences kept ahead by the workers.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class MaintenanceTests
{
    private readonly WorkersFixture _fixture;

    public MaintenanceTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Client Client, Site Site, Endpoint Endpoint)> ManagedEndpointAsync(string hostname)
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (client, site) = await _fixture.CreateClientAndSiteAsync();
        return (client, site, await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, hostname, EndpointClass.Server));
    }

    private async Task StartAsync<T>(Guid id, DateTime? endsAt) where T : class
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var started = _fixture.Now.AddMinutes(-1);
        var rows = typeof(T) == typeof(Client) ? await db.Clients.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.MaintenanceStartedAt, started).SetProperty(c => c.MaintenanceEndsAt, endsAt))
            : typeof(T) == typeof(Site) ? await db.Sites.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.MaintenanceStartedAt, started).SetProperty(c => c.MaintenanceEndsAt, endsAt))
            : await db.Endpoints.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.MaintenanceStartedAt, started).SetProperty(c => c.MaintenanceEndsAt, endsAt));
        Assert.Equal(1, rows);
    }

    private async Task<List<Alert>> AlertsAsync(Guid endpointId, AlertKind kind)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Alerts.AsNoTracking().Where(a => a.EndpointId == endpointId && a.Kind == kind).OrderBy(a => a.OpenedAt).ToListAsync();
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("site")]
    [InlineData("client")]
    public async Task No_check_alert_opens_in_maintenance_and_the_first_failure_afterwards_opens_it(string level)
    {
        var (client, site, endpoint) = await ManagedEndpointAsync("SRV-MAINT-" + level.ToUpperInvariant());
        var disk = await _fixture.CreateCheckAsync(site, CheckType.DiskFree, 15, 5, failuresBeforeAlert: 2, parameters: """{"drive":"C:"}""");
        var endsAt = _fixture.Now.AddHours(1);
        switch (level)
        {
            case "endpoint": await StartAsync<Endpoint>(endpoint.Id, endsAt); break;
            case "site": await StartAsync<Site>(site.Id, endsAt); break;
            default: await StartAsync<Client>(client.Id, endsAt); break;
        }

        var evaluation = _fixture.CheckEvaluation();
        for (var i = 0; i < 3; i++)
        {
            await _fixture.InsertResultAsync(endpoint, disk, 2, target: "C:");
            await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        }

        Assert.Empty(await AlertsAsync(endpoint.Id, AlertKind.Check));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var state = await db.CheckStates.AsNoTracking().SingleAsync(s => s.EndpointId == endpoint.Id);
            Assert.Equal(CheckStatus.Critical, state.Status);
            Assert.Equal(3, state.ConsecutiveNonOk);
        }

        _fixture.Db.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await _fixture.InsertResultAsync(endpoint, disk, 2, target: "C:");
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);

        var alert = Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Check));
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public async Task An_open_alert_does_not_escalate_in_maintenance_but_still_resolves()
    {
        var (_, site, endpoint) = await ManagedEndpointAsync("SRV-MAINT-OPEN");
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 95);
        var evaluation = _fixture.CheckEvaluation();
        await _fixture.InsertResultAsync(endpoint, cpu, 85);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Equal(AlertSeverity.Warning, Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Check)).Severity);

        await StartAsync<Endpoint>(endpoint.Id, null);
        await _fixture.ClearOutboxAsync();
        await _fixture.InsertResultAsync(endpoint, cpu, 99);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        var alert = Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Check));
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(AlertState.Open, alert.State);

        await _fixture.InsertResultAsync(endpoint, cpu, 10);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Equal(AlertState.Resolved, Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Check)).State);
    }

    [Fact]
    public async Task No_offline_alert_opens_in_maintenance_and_one_opens_once_it_ends()
    {
        var (_, site, endpoint) = await ManagedEndpointAsync("SRV-MAINT-OFFLINE");
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.IsOnline, false).SetProperty(e => e.LastSeenAt, _fixture.Now.AddHours(-1)));
        }

        await StartAsync<Site>(site.Id, _fixture.Now.AddMinutes(30));
        var health = _fixture.EndpointHealth();
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);
        Assert.Empty(await AlertsAsync(endpoint.Id, AlertKind.Offline));

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(31));
        await health.UpdateOfflineAlertsAsync(CancellationToken.None);
        Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.Offline));
    }

    [Fact]
    public async Task A_duplicate_identity_alert_opens_in_maintenance()
    {
        var (client, _, endpoint) = await ManagedEndpointAsync("SRV-MAINT-CLONE");
        await StartAsync<Client>(client.Id, null);
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

        Assert.Single(await AlertsAsync(endpoint.Id, AlertKind.DuplicateIdentity));
    }

    [Fact]
    public async Task A_running_policy_window_suppresses_alerts_for_its_class_only()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (client, site) = await _fixture.CreateClientAndSiteAsync();
        var server = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-WINDOW", EndpointClass.Server);
        var workstation = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-WINDOW", EndpointClass.Workstation);
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 95);

        var now = _fixture.Now;
        var start = now.AddMinutes(-30);
        var policyId = Guid.NewGuid();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.Policies.Add(new Policy
            {
                Id = policyId, ClientId = client.Id, Name = "Patching", CreatedAt = now, UpdatedAt = now,
                MaintenanceWindowsJson = MaintenanceWindowSchedule.Serialize(
                    [new MaintenanceWindow("Patch night", WeekDays.All, start.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), 120, "UTC", CheckAppliesTo.Server)])
            });
            db.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = client.Id, PolicyId = policyId, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        var windows = new MaintenanceWindowService(_fixture.Db.DbFactory, _fixture.Db.Bus, _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<MaintenanceWindowService>.Instance);
        Assert.True(await windows.RefreshAsync(CancellationToken.None) > 0);
        await windows.NotifyBoundariesAsync(CancellationToken.None);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var stored = await db.MaintenanceWindowOccurrences.AsNoTracking().Where(o => o.PolicyId == policyId).OrderBy(o => o.StartsAt).ToListAsync();
            Assert.True(stored.Count >= 8);
            Assert.True(stored[0].StartsAt <= now && stored[0].EndsAt > now);
            Assert.True(stored[^1].StartsAt >= now.AddDays(7));
        }

        var evaluation = _fixture.CheckEvaluation();
        foreach (var endpoint in new[] { server, workstation })
        {
            await _fixture.InsertResultAsync(endpoint, cpu, 99);
            await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        }

        Assert.Empty(await AlertsAsync(server.Id, AlertKind.Check));
        Assert.Single(await AlertsAsync(workstation.Id, AlertKind.Check));

        // The window ends: the next failure opens the alert, and open pages are told.
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(91));
        var before = _fixture.Db.Bus.PayloadsFor(NotificationChannels.EndpointStatus).Count(p => p == Guid.Empty.ToString());
        Assert.True(await windows.NotifyBoundariesAsync(CancellationToken.None));
        Assert.Equal(before + 1, _fixture.Db.Bus.PayloadsFor(NotificationChannels.EndpointStatus).Count(p => p == Guid.Empty.ToString()));
        await _fixture.InsertResultAsync(server, cpu, 99);
        await evaluation.EvaluateEndpointAsync(server.Id, CancellationToken.None);
        Assert.Single(await AlertsAsync(server.Id, AlertKind.Check));
    }

    [Fact]
    public async Task An_end_time_that_passes_is_audited_once_and_refreshes_open_pages()
    {
        var expiry = _fixture.MaintenanceExpiry();
        await expiry.ProcessAsync(CancellationToken.None);

        var (client, site, endpoint) = await ManagedEndpointAsync("SRV-MAINT-EXPIRY");
        await StartAsync<Endpoint>(endpoint.Id, _fixture.Now.AddMinutes(10));
        await StartAsync<Site>(site.Id, _fixture.Now.AddMinutes(20));
        await StartAsync<Client>(client.Id, null);

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(11));
        await expiry.ProcessAsync(CancellationToken.None);
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(10));
        await expiry.ProcessAsync(CancellationToken.None);
        await expiry.ProcessAsync(CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var expired = await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == AuditActions.MaintenanceExpired && (a.TargetId == endpoint.Id.ToString() || a.TargetId == site.Id.ToString() ||
                                                                       a.TargetId == client.Id.ToString()))
            .Select(a => a.TargetType)
            .ToListAsync();
        Assert.Equal(["Endpoint", "Site"], expired.Order().ToArray());
        Assert.Contains(endpoint.Id.ToString(), _fixture.Db.Bus.PayloadsFor(NotificationChannels.EndpointStatus));
        Assert.Contains(Guid.Empty.ToString(), _fixture.Db.Bus.PayloadsFor(NotificationChannels.EndpointStatus));
    }
}
