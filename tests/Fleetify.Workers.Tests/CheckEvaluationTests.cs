using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Workers.Checks;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Workers.Tests;

/// <summary>
/// Guarantees that check results become the right check states and alerts: thresholds, failures before alert, open,
/// escalate and resolve, deduplication, tier and license enforcement, and per-endpoint cursors that never skip a result
/// committed late.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class CheckEvaluationTests
{
    private readonly WorkersFixture _fixture;

    public CheckEvaluationTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Site Site, Endpoint Endpoint)> ManagedEndpointAsync(EndpointTier tier = EndpointTier.Managed, string hostname = "SRV-DC01")
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, tier, hostname, EndpointClass.Server);
        return (site, endpoint);
    }

    private async Task<List<CheckState>> StatesAsync(Guid endpointId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.CheckStates.AsNoTracking().Where(s => s.EndpointId == endpointId).ToListAsync();
    }

    private async Task<List<Alert>> AlertsAsync(Guid endpointId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Alerts.AsNoTracking().Where(a => a.EndpointId == endpointId).OrderBy(a => a.OpenedAt).ToListAsync();
    }

    [Fact]
    public async Task Thresholds_set_the_check_state_status()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var disk = await _fixture.CreateCheckAsync(site, CheckType.DiskFree, 15, 5, parameters: """{"drive":"*"}""");
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var service = await _fixture.CreateCheckAsync(site, CheckType.ServiceRunning, null, null, parameters: """{"service":"Spooler"}""");

        await _fixture.InsertResultAsync(endpoint, disk, 3, target: "C:");
        await _fixture.InsertResultAsync(endpoint, disk, 12, target: "D:");
        await _fixture.InsertResultAsync(endpoint, disk, 60, target: "E:");
        await _fixture.InsertResultAsync(endpoint, cpu, 0, error: "Performance counters are unavailable");
        await _fixture.InsertResultAsync(endpoint, service, 0);

        await _fixture.CheckEvaluation().EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);

        var states = await StatesAsync(endpoint.Id);
        Assert.Equal(CheckStatus.Critical, states.Single(s => s.Target == "C:").Status);
        Assert.Equal(CheckStatus.Warning, states.Single(s => s.Target == "D:").Status);
        Assert.Equal(CheckStatus.Ok, states.Single(s => s.Target == "E:").Status);
        var cpuState = states.Single(s => s.CheckDefinitionId == cpu.Id);
        Assert.Equal(CheckStatus.Unknown, cpuState.Status);
        Assert.Null(cpuState.Value);
        Assert.Equal(CheckStatus.Critical, states.Single(s => s.CheckDefinitionId == service.Id).Status);

        var alerts = await AlertsAsync(endpoint.Id);
        Assert.Equal(4, alerts.Count);
        Assert.Contains(alerts, a => a.Title == "SRV-DC01 has less than 5% free disk space on C: (3% free)." && a.Severity == AlertSeverity.Critical);
        Assert.Contains(alerts, a => a.Target == "D:" && a.Severity == AlertSeverity.Warning);
        Assert.Contains(alerts, a => a.CheckDefinitionId == cpu.Id && a.Severity == AlertSeverity.Warning && a.Detail == "Performance counters are unavailable");
        Assert.Contains(_fixture.Db.Bus.PayloadsFor(NotificationChannels.EndpointStatus), p => p == endpoint.Id.ToString());
        Assert.All(alerts, a => Assert.Contains(a.Id.ToString(), _fixture.Db.Bus.PayloadsFor(NotificationChannels.Alerts)));
    }

    [Fact]
    public async Task An_alert_opens_only_after_the_configured_number_of_consecutive_failures()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90, failuresBeforeAlert: 3);
        var evaluation = _fixture.CheckEvaluation();

        await _fixture.InsertResultAsync(endpoint, cpu, 95);
        await _fixture.InsertResultAsync(endpoint, cpu, 95);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Equal(2, (await StatesAsync(endpoint.Id)).Single().ConsecutiveNonOk);
        Assert.Empty(await AlertsAsync(endpoint.Id));

        // An OK result resets the count.
        await _fixture.InsertResultAsync(endpoint, cpu, 10);
        await _fixture.InsertResultAsync(endpoint, cpu, 95);
        await _fixture.InsertResultAsync(endpoint, cpu, 95);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Empty(await AlertsAsync(endpoint.Id));

        await _fixture.InsertResultAsync(endpoint, cpu, 95);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        var alert = Assert.Single(await AlertsAsync(endpoint.Id));
        Assert.Equal(AlertState.Open, alert.State);
        Assert.Equal(3, (await StatesAsync(endpoint.Id)).Single().ConsecutiveNonOk);
    }

    [Fact]
    public async Task An_alert_opens_escalates_and_resolves_with_its_check()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var memory = await _fixture.CreateCheckAsync(site, CheckType.MemoryUsage, 80, 90);
        var evaluation = _fixture.CheckEvaluation();

        await _fixture.InsertResultAsync(endpoint, memory, 85);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        var opened = Assert.Single(await AlertsAsync(endpoint.Id));
        Assert.Equal(AlertSeverity.Warning, opened.Severity);
        Assert.Equal(AlertKind.Check, opened.Kind);

        // Acknowledged by a technician, then the value gets worse: escalation reopens it.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.Alerts.Where(a => a.Id == opened.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AlertState.Acknowledged));
        }

        await _fixture.InsertResultAsync(endpoint, memory, 97);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        var escalated = Assert.Single(await AlertsAsync(endpoint.Id));
        Assert.Equal(opened.Id, escalated.Id);
        Assert.Equal(AlertSeverity.Critical, escalated.Severity);
        Assert.Equal(AlertState.Open, escalated.State);
        Assert.Equal("SRV-DC01 is using 97% of its memory, above the 90% threshold.", escalated.Title);

        await _fixture.InsertResultAsync(endpoint, memory, 40);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        var resolved = Assert.Single(await AlertsAsync(endpoint.Id));
        Assert.Equal(AlertState.Resolved, resolved.State);
        Assert.Equal(CheckEvaluationService.ResolvedReasonOk, resolved.ResolvedReason);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Equal(CheckStatus.Ok, (await StatesAsync(endpoint.Id)).Single().Status);
    }

    [Fact]
    public async Task Repeated_and_concurrent_evaluations_keep_a_single_open_alert()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);

        await _fixture.InsertResultAsync(endpoint, cpu, 95);
        await _fixture.CheckEvaluation().EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        await _fixture.InsertResultAsync(endpoint, cpu, 96);
        await _fixture.CheckEvaluation().EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            await _fixture.InsertResultAsync(endpoint, cpu, 97);
        }

        // Two independent services (as two processes would be) race on the same endpoint.
        await Task.WhenAll(
            _fixture.CheckEvaluation().EvaluateEndpointAsync(endpoint.Id, CancellationToken.None),
            _fixture.CheckEvaluation().EvaluateEndpointAsync(endpoint.Id, CancellationToken.None));

        var alert = Assert.Single(await AlertsAsync(endpoint.Id));
        Assert.Equal(AlertState.Open, alert.State);
        Assert.Equal(7, (await StatesAsync(endpoint.Id)).Single().ConsecutiveNonOk);
    }

    [Fact]
    public async Task Results_of_an_agent_only_endpoint_are_ignored()
    {
        var (site, endpoint) = await ManagedEndpointAsync(EndpointTier.AgentOnly);
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var result = await _fixture.InsertResultAsync(endpoint, cpu, 99);

        await _fixture.CheckEvaluation().EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);

        Assert.Empty(await StatesAsync(endpoint.Id));
        Assert.Empty(await AlertsAsync(endpoint.Id));
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var cursor = await db.WorkerWatermarks.SingleAsync(w => w.Name == CheckEvaluationService.WatermarkName(endpoint.Id));
        Assert.Equal(result.Id, cursor.Value);
    }

    [Fact]
    public async Task Results_are_ignored_once_the_license_grace_period_has_ended()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        await _fixture.Db.LoadTestLicenseAsync(1000, expiresAt: _fixture.Now.AddDays(-20));
        try
        {
            await _fixture.InsertResultAsync(endpoint, cpu, 99);
            await _fixture.CheckEvaluation().EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);

            Assert.Empty(await StatesAsync(endpoint.Id));
            Assert.Empty(await AlertsAsync(endpoint.Id));
        }
        finally
        {
            await _fixture.Db.LoadTestLicenseAsync(1000);
        }
    }

    [Fact]
    public async Task A_late_commit_with_a_lower_id_is_evaluated_for_its_own_endpoint()
    {
        var (siteA, endpointA) = await ManagedEndpointAsync(hostname: "WS-A");
        var endpointB = await _fixture.Db.CreateEndpointAsync(siteA, EndpointTier.Managed, "WS-B");
        var cpu = await _fixture.CreateCheckAsync(siteA, CheckType.CpuUsage, 80, 90);
        var baseId = await _fixture.MaxResultIdAsync() + 100_000;

        // Endpoint A already has a cursor at baseId.
        await _fixture.InsertResultWithIdAsync(baseId, endpointA, cpu, 10);
        var evaluation = _fixture.CheckEvaluation();
        await evaluation.RunPassAsync(wideSweep: false, CancellationToken.None);

        // B commits id baseId + 20 and is evaluated; A's transaction with id baseId + 10 commits afterwards.
        await _fixture.InsertResultWithIdAsync(baseId + 20, endpointB, cpu, 10);
        await evaluation.RunPassAsync(wideSweep: false, CancellationToken.None);
        await _fixture.InsertResultWithIdAsync(baseId + 10, endpointA, cpu, 99);
        await evaluation.RunPassAsync(wideSweep: false, CancellationToken.None);

        var stateA = Assert.Single(await StatesAsync(endpointA.Id));
        Assert.Equal(CheckStatus.Critical, stateA.Status);
        Assert.Single(await AlertsAsync(endpointA.Id));
        Assert.Equal(CheckStatus.Ok, Assert.Single(await StatesAsync(endpointB.Id)).Status);
    }

    [Fact]
    public async Task Alerts_of_removed_checks_and_unmanaged_endpoints_are_resolved()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var memory = await _fixture.CreateCheckAsync(site, CheckType.MemoryUsage, 80, 90);
        await _fixture.InsertResultAsync(endpoint, cpu, 99);
        await _fixture.InsertResultAsync(endpoint, memory, 99);
        var evaluation = _fixture.CheckEvaluation();
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Equal(2, (await AlertsAsync(endpoint.Id)).Count(a => a.State == AlertState.Open));

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.CheckDefinitions.Where(d => d.Id == cpu.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Enabled, false));
        }

        await evaluation.ResolveStaleAlertsAsync(CancellationToken.None);
        var alerts = await AlertsAsync(endpoint.Id);
        Assert.Equal(CheckEvaluationService.ResolvedReasonNotApplicable, alerts.Single(a => a.CheckDefinitionId == cpu.Id).ResolvedReason);
        Assert.Equal(AlertState.Open, alerts.Single(a => a.CheckDefinitionId == memory.Id).State);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Tier, EndpointTier.AgentOnly));
        }

        await evaluation.ResolveStaleAlertsAsync(CancellationToken.None);
        var memoryAlert = (await AlertsAsync(endpoint.Id)).Single(a => a.CheckDefinitionId == memory.Id);
        Assert.Equal(AlertState.Resolved, memoryAlert.State);
        Assert.Equal(CheckEvaluationService.ResolvedReasonNotManaged, memoryAlert.ResolvedReason);
    }
}
