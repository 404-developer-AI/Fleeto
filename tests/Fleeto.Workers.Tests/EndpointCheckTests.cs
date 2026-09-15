using Fleeto.Core.Entities;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Checks;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees that checks adjusted per endpoint (overrides, disabled checks, extra templates, endpoint-only checks) are the
/// ones evaluated, that a reset resolves alerts and never shows a made-up OK, that results ingested before a reset cannot
/// overwrite it, and that alert holds suppress emails and end with one.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class EndpointCheckTests
{
    private readonly WorkersFixture _fixture;

    public EndpointCheckTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Site Site, Endpoint Endpoint)> ManagedEndpointAsync(EndpointTier tier = EndpointTier.Managed)
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, tier, "SRV-APP01", EndpointClass.Server);
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

    private async Task AddAsync(params object[] rows)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private EndpointCheckOverride Override(Endpoint endpoint, CheckDefinition check) => new()
    {
        EndpointId = endpoint.Id,
        CheckDefinitionId = check.Id,
        ClientId = endpoint.ClientId,
        CreatedAt = _fixture.Now,
        UpdatedAt = _fixture.Now
    };

    private CheckRunRequest RunRequest(Endpoint endpoint, CheckDefinition check, bool reset) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = endpoint.ClientId,
        EndpointId = endpoint.Id,
        CheckDefinitionId = check.Id,
        Reset = reset,
        RequestedByUserId = Guid.NewGuid(),
        RequestedByName = "Tech One",
        RequestedAt = _fixture.Now,
        ExpiresAt = _fixture.Now.AddMinutes(10)
    };

    [Fact]
    public async Task Overridden_thresholds_and_failures_apply_to_that_endpoint_only()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var other = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-APP02", EndpointClass.Server);
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var adjustment = Override(endpoint, cpu);
        adjustment.OverrideThresholds = true;
        adjustment.WarningThreshold = 95;
        adjustment.CriticalThreshold = 99;
        adjustment.FailuresBeforeAlert = 2;
        await AddAsync(adjustment);

        var evaluation = _fixture.CheckEvaluation();
        await _fixture.InsertResultAsync(endpoint, cpu, 96);
        await _fixture.InsertResultAsync(other, cpu, 96);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        await evaluation.EvaluateEndpointAsync(other.Id, CancellationToken.None);

        Assert.Equal(CheckStatus.Warning, (await StatesAsync(endpoint.Id)).Single().Status);
        Assert.Empty(await AlertsAsync(endpoint.Id));
        Assert.Equal(AlertSeverity.Critical, Assert.Single(await AlertsAsync(other.Id)).Severity);

        await _fixture.InsertResultAsync(endpoint, cpu, 96);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        var alert = Assert.Single(await AlertsAsync(endpoint.Id));
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal("SRV-APP01 has used 96% CPU on average, above the 95% threshold.", alert.Title);
    }

    [Fact]
    public async Task A_check_disabled_on_the_endpoint_is_not_evaluated_and_its_alert_resolves()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var memory = await _fixture.CreateCheckAsync(site, CheckType.MemoryUsage, 80, 90);
        var evaluation = _fixture.CheckEvaluation();
        await _fixture.InsertResultAsync(endpoint, memory, 95);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Single(await AlertsAsync(endpoint.Id));

        var adjustment = Override(endpoint, memory);
        adjustment.Disabled = true;
        await AddAsync(adjustment);
        await evaluation.ResolveStaleAlertsAsync(CancellationToken.None);
        var resolved = Assert.Single(await AlertsAsync(endpoint.Id));
        Assert.Equal(AlertState.Resolved, resolved.State);
        Assert.Equal(CheckEvaluationService.ResolvedReasonNotApplicable, resolved.ResolvedReason);

        _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));
        await _fixture.InsertResultAsync(endpoint, memory, 99);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Single(await AlertsAsync(endpoint.Id));

        await evaluation.DeleteStaleStatesAsync(CancellationToken.None);
        Assert.Empty(await StatesAsync(endpoint.Id));
    }

    [Fact]
    public async Task Checks_of_an_endpoint_template_link_and_endpoint_only_checks_are_evaluated()
    {
        var (_, endpoint) = await ManagedEndpointAsync();
        var linked = await _fixture.CreateCheckAsync(null, CheckType.Uptime, 30, 60);
        await AddAsync(new EndpointMonitoringTemplate
        {
            EndpointId = endpoint.Id,
            ClientId = endpoint.ClientId,
            MonitoringTemplateId = linked.MonitoringTemplateId!.Value,
            CreatedAt = _fixture.Now
        });
        var own = new CheckDefinition
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            Name = "Spooler",
            Type = CheckType.ServiceRunning,
            ParametersJson = """{"service":"Spooler"}""",
            CreatedAt = _fixture.Now,
            UpdatedAt = _fixture.Now
        };
        await AddAsync(own);

        await _fixture.InsertResultAsync(endpoint, linked, 45);
        await _fixture.InsertResultAsync(endpoint, own, 0);
        await _fixture.CheckEvaluation().EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);

        var states = await StatesAsync(endpoint.Id);
        Assert.Equal(CheckStatus.Warning, states.Single(s => s.CheckDefinitionId == linked.Id).Status);
        Assert.Equal(CheckStatus.Critical, states.Single(s => s.CheckDefinitionId == own.Id).Status);
        Assert.Equal(2, (await AlertsAsync(endpoint.Id)).Count);
    }

    [Fact]
    public async Task A_reset_resolves_the_alert_neutralizes_the_state_and_ignores_older_results()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var disk = await _fixture.CreateCheckAsync(site, CheckType.DiskFree, 15, 5, failuresBeforeAlert: 2, parameters: """{"drive":"C:"}""");
        var evaluation = _fixture.CheckEvaluation();
        await _fixture.InsertResultAsync(endpoint, disk, 3, target: "C:");
        await _fixture.InsertResultAsync(endpoint, disk, 3, target: "C:");
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Single(await AlertsAsync(endpoint.Id));

        // A result ingested before the reset but evaluated after it.
        await _fixture.InsertResultAsync(endpoint, disk, 2, target: "C:");
        _fixture.Db.Time.Advance(TimeSpan.FromSeconds(2));
        var request = RunRequest(endpoint, disk, reset: true);
        await AddAsync(request);
        Assert.Equal(1, await _fixture.CheckRunRequests().ApplyPendingResetsAsync(CancellationToken.None));

        var alert = Assert.Single(await AlertsAsync(endpoint.Id));
        Assert.Equal(AlertState.Resolved, alert.State);
        Assert.Equal("Reset by Tech One", alert.ResolvedReason);
        var state = Assert.Single(await StatesAsync(endpoint.Id));
        Assert.Equal(CheckStatus.Unknown, state.Status);
        Assert.Null(state.Value);
        Assert.Equal(0, state.ConsecutiveNonOk);
        Assert.True(state.RerunRequested);

        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        state = Assert.Single(await StatesAsync(endpoint.Id));
        Assert.True(state.RerunRequested);
        Assert.Equal(CheckStatus.Unknown, state.Status);
        Assert.Single(await AlertsAsync(endpoint.Id));

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var stored = await db.CheckRunRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id);
            Assert.NotNull(stored.ResetAppliedAt);
            Assert.Null(stored.Outcome);
        }

        // The first new result shows the real state; the alert needs the configured number of failures again.
        _fixture.Db.Time.Advance(TimeSpan.FromSeconds(2));
        await _fixture.InsertResultAsync(endpoint, disk, 3, target: "C:");
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        state = Assert.Single(await StatesAsync(endpoint.Id));
        Assert.False(state.RerunRequested);
        Assert.Equal(CheckStatus.Critical, state.Status);
        Assert.Equal(1, state.ConsecutiveNonOk);
        Assert.DoesNotContain(await AlertsAsync(endpoint.Id), a => a.State != AlertState.Resolved);
    }

    [Fact]
    public async Task A_reset_is_refused_for_an_agent_only_endpoint_a_check_that_does_not_apply_and_an_expired_request()
    {
        var (site, agentOnly) = await ManagedEndpointAsync(EndpointTier.AgentOnly);
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var managed = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-APP03", EndpointClass.Server);
        var unlinked = await _fixture.CreateCheckAsync(null, CheckType.CpuUsage, 80, 90);

        var notManaged = RunRequest(agentOnly, cpu, reset: true);
        var notApplicable = RunRequest(managed, unlinked, reset: true);
        var expired = RunRequest(managed, cpu, reset: true);
        expired.RequestedAt = _fixture.Now.AddMinutes(-20);
        expired.ExpiresAt = _fixture.Now.AddMinutes(-10);
        await AddAsync(notManaged, notApplicable, expired);

        var service = _fixture.CheckRunRequests();
        await service.ApplyPendingResetsAsync(CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var rows = await db.CheckRunRequests.AsNoTracking()
            .Where(r => r.Id == notManaged.Id || r.Id == notApplicable.Id || r.Id == expired.Id)
            .ToDictionaryAsync(r => r.Id);
        Assert.Equal(CheckRunRequestOutcome.NotManaged, rows[notManaged.Id].Outcome);
        Assert.Equal(CheckRunRequestOutcome.NotApplicable, rows[notApplicable.Id].Outcome);
        Assert.Equal(CheckRunRequestOutcome.Expired, rows[expired.Id].Outcome);
        Assert.All(rows.Values, r => Assert.Null(r.ResetAppliedAt));
    }

    [Fact]
    public async Task Undelivered_run_requests_expire()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var request = RunRequest(endpoint, cpu, reset: false);
        await AddAsync(request);

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(11));
        await _fixture.CheckRunRequests().ExpireAsync(CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.Equal(CheckRunRequestOutcome.Expired, (await db.CheckRunRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).Outcome);
    }

    [Fact]
    public async Task A_held_alert_sends_no_escalation_email_and_one_email_when_the_hold_ends()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var recipient = $"hold-{Guid.NewGuid():N}@test.example";
        var channelId = Guid.NewGuid();
        await AddAsync(new NotificationChannel
        {
            Id = channelId, Name = "Hold " + channelId.ToString("N")[..8], Recipients = recipient, MinimumSeverity = AlertSeverity.Warning,
            NotifyOnResolve = true, CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
        });

        try
        {
            var evaluation = _fixture.CheckEvaluation();
            await _fixture.InsertResultAsync(endpoint, cpu, 85);
            await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
            var alert = Assert.Single(await AlertsAsync(endpoint.Id));

            await using (var db = _fixture.Db.DbFactory.CreateSystem())
            {
                var holdUntil = _fixture.Now.AddHours(1);
                await db.Alerts.Where(a => a.Id == alert.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.HeldUntil, holdUntil)
                    .SetProperty(a => a.HeldAt, _fixture.Now));
            }

            _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));
            await _fixture.InsertResultAsync(endpoint, cpu, 95);
            await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
            Assert.Equal(AlertSeverity.Critical, Assert.Single(await AlertsAsync(endpoint.Id)).Severity);

            var holds = _fixture.AlertHolds();
            Assert.Empty(await holds.EndExpiredHoldsAsync(CancellationToken.None));

            _fixture.Db.Time.Advance(TimeSpan.FromHours(1));
            Assert.Contains(alert.Id, await holds.EndExpiredHoldsAsync(CancellationToken.None));
            var ended = Assert.Single(await AlertsAsync(endpoint.Id));
            Assert.Null(ended.HeldUntil);
            Assert.NotEqual(AlertState.Resolved, ended.State);

            await using (var db = _fixture.Db.DbFactory.CreateSystem())
            {
                var categories = await db.OutboxEmails.AsNoTracking().Where(e => e.ToAddress == recipient).OrderBy(e => e.CreatedAt)
                    .Select(e => e.Category).ToListAsync();
                Assert.Equal([AlertNotificationService.CategoryOpened, AlertNotificationService.CategoryHoldEnded], categories);
            }
        }
        finally
        {
            await using var db = _fixture.Db.DbFactory.CreateSystem();
            await db.NotificationChannels.Where(c => c.Id == channelId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task A_hold_that_ends_on_a_resolved_alert_sends_nothing()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var evaluation = _fixture.CheckEvaluation();
        await _fixture.InsertResultAsync(endpoint, cpu, 85);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        var alert = Assert.Single(await AlertsAsync(endpoint.Id));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var holdUntil = _fixture.Now.AddMinutes(30);
            await db.Alerts.Where(a => a.Id == alert.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.HeldUntil, holdUntil));
        }

        _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));
        await _fixture.InsertResultAsync(endpoint, cpu, 10);
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        Assert.Equal(AlertState.Resolved, Assert.Single(await AlertsAsync(endpoint.Id)).State);

        _fixture.Db.Time.Advance(TimeSpan.FromHours(1));
        Assert.DoesNotContain(alert.Id, await _fixture.AlertHolds().EndExpiredHoldsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_template_change_reaches_endpoints_that_link_it_directly()
    {
        var (_, endpoint) = await ManagedEndpointAsync();
        var check = await _fixture.CreateCheckAsync(null, CheckType.Uptime, 30, 60);
        await AddAsync(new EndpointMonitoringTemplate
        {
            EndpointId = endpoint.Id, ClientId = endpoint.ClientId, MonitoringTemplateId = check.MonitoringTemplateId!.Value, CreatedAt = _fixture.Now
        });

        var fanout = _fixture.Fanout();
        await fanout.ProcessPendingAsync(CancellationToken.None);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.SigningRequests.Where(r => r.State == SigningRequestState.Pending && r.SubjectId == endpoint.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, SigningRequestState.Completed));
        _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.MonitoringTemplate, ScopeId = check.MonitoringTemplateId, CreatedAt = _fixture.Now });
        await db.SaveChangesAsync();

        await fanout.ProcessPendingAsync(CancellationToken.None);
        Assert.True(await db.SigningRequests.AnyAsync(r => r.State == SigningRequestState.Pending && r.SubjectId == endpoint.Id));
    }

    [Fact]
    public async Task Retention_removes_old_check_run_requests()
    {
        var (site, endpoint) = await ManagedEndpointAsync();
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var old = RunRequest(endpoint, cpu, reset: false);
        old.RequestedAt = _fixture.Now.AddDays(-8);
        old.ExpiresAt = old.RequestedAt.AddMinutes(10);
        var recent = RunRequest(endpoint, cpu, reset: false);
        await AddAsync(old, recent);

        await _fixture.Retention().RunAsync(CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await db.CheckRunRequests.AnyAsync(r => r.Id == old.Id));
        Assert.True(await db.CheckRunRequests.AnyAsync(r => r.Id == recent.Id));
    }
}
