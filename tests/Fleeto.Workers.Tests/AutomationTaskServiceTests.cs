using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Testing;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Tasks in the workers (0.6.0), with the production grants and database rules of the workers role: a task starts runs on its
/// schedule, for an alert of one of its checks and when an endpoint becomes managed, never for what happened before the task
/// existed; a run writes a job without a user and its signing request for every endpoint it applies to, skips one where the
/// previous job still runs or the script cannot run, and a failed job opens an alert of kind Task that the next successful one
/// resolves.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class AutomationTaskServiceTests : IAsyncLifetime
{
    private readonly WorkersFixture _fixture;
    private NpgsqlDataSource? _workersSource;
    private IFleetoDbContextFactory _workers = null!;

    public AutomationTaskServiceTests(WorkersFixture fixture) => _fixture = fixture;

    private DateTime Now => _fixture.Now;

    public async Task InitializeAsync()
    {
        await using (var command = _fixture.Db.DataSource.CreateCommand(string.Join('\n', DatabaseRoles.Application.Select(r =>
                         $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{r}') THEN CREATE ROLE {r} NOLOGIN; END IF; END $$;"))))
        {
            await command.ExecuteNonQueryAsync();
        }

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.Database.ExecuteSqlRawAsync(DatabaseGrants.BuildSql());
        }

        // The superuser connects and every session runs as the workers role: the same privileges and rules as production.
        var builder = new NpgsqlConnectionStringBuilder(_fixture.Db.ConnectionString) { Options = "-c role=" + DatabaseRoles.Workers };
        _workersSource = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        _workers = new FleetoDbContextFactory(FleetoDbContextFactory.BuildOptions(_workersSource));
        await _fixture.Db.LoadTestLicenseAsync(1000);
    }

    public async Task DisposeAsync()
    {
        if (_workersSource is not null)
        {
            await _workersSource.DisposeAsync();
        }
    }

    private AutomationTaskService Service() =>
        new(_workers, _fixture.Db.Bus, new AlertNotificationService(_fixture.Db.Time), _fixture.Db.Licenses, _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<AutomationTaskService>.Instance);

    private async Task<(Client Client, Site Site, Endpoint Endpoint)> SeedAsync(string prefix, EndpointTier tier = EndpointTier.Managed)
    {
        var client = await _fixture.Db.CreateClientAsync();
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Tasks " + Guid.NewGuid().ToString("N")[..6]);
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, tier, prefix + "-" + Guid.NewGuid().ToString("N")[..6]);
        return (client, site, endpoint);
    }

    /// <summary>A task that runs every 15 minutes all day, from yesterday.</summary>
    private static Action<AutomationTask> EveryQuarter(Action<AutomationTask>? more = null) => t =>
    {
        t.ScheduleKind = TaskScheduleKind.Daily;
        t.StartDate = t.StartDate.AddDays(-1);
        t.StartMinute = 0;
        t.RepeatMinutes = 15;
        more?.Invoke(t);
    };

    private async Task<List<Job>> JobsAsync(Guid endpointId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Jobs.AsNoTracking().Where(j => j.EndpointId == endpointId).OrderBy(j => j.CreatedAt).ToListAsync();
    }

    private async Task<List<AutomationTaskRunTarget>> TargetsAsync(Guid taskId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.AutomationTaskRunTargets.AsNoTracking().Where(t => t.AutomationTaskId == taskId).OrderBy(t => t.DueAt).ToListAsync();
    }

    private async Task PassAsync(AutomationTaskService service)
    {
        await service.StartRunsAsync(CancellationToken.None);
        await service.StartJobsAsync(CancellationToken.None);
        await service.FollowOutcomesAsync(CancellationToken.None);
    }

    private async Task EndJobAsync(Guid jobId, JobState state, int? exitCode)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
        job.Signature = new byte[64];
        job.State = state;
        job.ExitCode = exitCode;
        job.CompletedAt = Now;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_schedule_starts_a_job_without_a_user_for_every_endpoint_from_the_moment_the_task_exists()
    {
        var (_, site, endpoint) = await SeedAsync("SCHED");
        var (task, script, version) = await _fixture.Db.CreateTaskAsync(site, EveryQuarter());
        var service = Service();

        // The first pass only starts counting: yesterday's occurrences do not run.
        await PassAsync(service);
        Assert.Empty(await JobsAsync(endpoint.Id));

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await PassAsync(service);

        var job = Assert.Single(await JobsAsync(endpoint.Id));
        Assert.Equal((task.Id, script.Id, version.Id, AutomationTasks.AutomaticInitiator), (job.TaskId!.Value, job.ScriptId!.Value, job.ScriptVersionId!.Value, job.InitiatedByUserId));
        Assert.Equal(JobState.PendingSignature, job.State);
        Assert.StartsWith("Fleeto (task ", job.InitiatedByName);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.True(await db.SigningRequests.AnyAsync(r => r.SubjectId == job.Id && r.Kind == SigningRequestKind.Job));
        var run = await db.AutomationTaskRuns.SingleAsync(r => r.Id == job.TaskRunId);
        Assert.Equal(TaskTrigger.Schedule, run.Trigger);
        Assert.True(AutomationTasks.IsOccurrence(task, run.OccurrenceAt));
    }

    [Fact]
    public async Task An_endpoint_where_the_previous_run_still_runs_is_skipped_or_waits_as_the_task_says()
    {
        var (_, site, endpoint) = await SeedAsync("OVERLAP");
        var (skipTask, _, _) = await _fixture.Db.CreateTaskAsync(site, EveryQuarter());
        var (waitTask, _, _) = await _fixture.Db.CreateTaskAsync(site, EveryQuarter(t => t.Overlap = TaskOverlap.Wait));
        var service = Service();
        await PassAsync(service);
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await PassAsync(service);
        Assert.Equal(2, (await JobsAsync(endpoint.Id)).Count);

        // The jobs are still queued when the next occurrence passes.
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await PassAsync(service);

        var skipped = (await TargetsAsync(skipTask.Id))[^1];
        Assert.Equal((TaskTargetState.Skipped, AutomationTaskService.PreviousRunningReason), (skipped.State, skipped.Reason));
        var waiting = (await TargetsAsync(waitTask.Id))[^1];
        Assert.Equal(TaskTargetState.Pending, waiting.State);

        // Once the previous job ended, the waiting endpoint starts.
        var first = (await JobsAsync(endpoint.Id)).Single(j => j.TaskId == waitTask.Id);
        await EndJobAsync(first.Id, JobState.Succeeded, 0);
        await PassAsync(service);
        Assert.Equal(TaskTargetState.Started, (await TargetsAsync(waitTask.Id))[^1].State);
    }

    [Fact]
    public async Task An_endpoint_that_cannot_run_the_script_is_listed_as_skipped_and_an_agent_only_one_is_left_out()
    {
        var (client, site, windows) = await SeedAsync("PLAT");
        var linux = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "LNX-" + Guid.NewGuid().ToString("N")[..6]);
        var agentOnly = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.AgentOnly, "FREE-" + Guid.NewGuid().ToString("N")[..6]);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == linux.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.OsPlatform, "linux"));
        }

        var (task, _, _) = await _fixture.Db.CreateTaskAsync(site, EveryQuarter());
        var service = Service();
        await PassAsync(service);
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await PassAsync(service);

        var targets = await TargetsAsync(task.Id);
        Assert.Equal(TaskTargetState.Started, targets.Single(t => t.EndpointId == windows.Id).State);
        Assert.Equal(TaskTargetState.Skipped, targets.Single(t => t.EndpointId == linux.Id).State);
        Assert.Contains("does not run on this operating system", targets.Single(t => t.EndpointId == linux.Id).Reason);
        Assert.DoesNotContain(targets, t => t.EndpointId == agentOnly.Id);
        Assert.All(targets, t => Assert.Equal(client.Id, t.ClientId));
    }

    [Fact]
    public async Task An_alert_of_a_chosen_check_starts_the_task_on_its_endpoint_at_most_once_an_hour()
    {
        var (client, site, endpoint) = await SeedAsync("ALERT");
        var checkId = Guid.NewGuid();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var template = new MonitoringTemplate { Id = Guid.NewGuid(), Name = "Spooler " + checkId, CreatedAt = Now, UpdatedAt = Now };
            db.MonitoringTemplates.Add(template);
            db.CheckDefinitions.Add(new CheckDefinition
            {
                Id = checkId, MonitoringTemplateId = template.Id, Name = "Print spooler", Type = CheckType.ServiceRunning,
                ParametersJson = """{"service":"Spooler"}""", CreatedAt = Now, UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        var (task, _, _) = await _fixture.Db.CreateTaskAsync(site, t => t.TriggerCheckIds = [checkId]);
        var service = Service();
        await PassAsync(service);

        async Task OpenAlertAsync()
        {
            _fixture.Db.Time.Advance(TimeSpan.FromMinutes(1));
            await using var db = _fixture.Db.DbFactory.CreateSystem();
            db.Alerts.Add(new Alert
            {
                Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Kind = AlertKind.Check, CheckDefinitionId = checkId,
                Target = Guid.NewGuid().ToString(), Severity = AlertSeverity.Critical, Title = "Print spooler is stopped", OpenedAt = Now, UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        await OpenAlertAsync();
        await PassAsync(service);
        var job = Assert.Single(await JobsAsync(endpoint.Id));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var run = await db.AutomationTaskRuns.SingleAsync(r => r.Id == job.TaskRunId);
            Assert.Equal((TaskTrigger.CheckAlert, "Print spooler"), (run.Trigger, run.CheckName));
        }

        await OpenAlertAsync();
        await PassAsync(service);
        Assert.Single(await JobsAsync(endpoint.Id));
        Assert.Single(await TargetsAsync(task.Id));
    }

    [Fact]
    public async Task An_endpoint_switched_to_managed_starts_the_task_once()
    {
        var (_, site, endpoint) = await SeedAsync("MANAGED", EndpointTier.AgentOnly);
        var (task, _, _) = await _fixture.Db.CreateTaskAsync(site, t => t.OnManaged = true);
        var service = Service();
        await PassAsync(service);

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(1));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var row = await db.Endpoints.SingleAsync(e => e.Id == endpoint.Id);
            (row.Tier, row.ManagedSince) = (EndpointTier.Managed, Now);
            await db.SaveChangesAsync();
        }

        await PassAsync(service);
        await PassAsync(service);

        var job = Assert.Single(await JobsAsync(endpoint.Id));
        Assert.Equal(task.Id, job.TaskId);
    }

    [Fact]
    public async Task A_failed_run_opens_an_alert_on_the_endpoint_and_the_next_successful_run_resolves_it()
    {
        var (_, site, endpoint) = await SeedAsync("FAIL");
        var (task, _, _) = await _fixture.Db.CreateTaskAsync(site, EveryQuarter(t => t.AlertSeverity = AlertSeverity.Critical));
        var service = Service();
        await PassAsync(service);
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await PassAsync(service);

        var first = Assert.Single(await JobsAsync(endpoint.Id));
        await EndJobAsync(first.Id, JobState.Failed, 3);
        await PassAsync(service);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var alert = await db.Alerts.SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.Task);
            Assert.Equal((task.Id.ToString(), AlertSeverity.Critical, AlertState.Open), (alert.Target, alert.Severity, alert.State));
            Assert.Contains("exit code 3", alert.Title);
        }

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await PassAsync(service);
        var second = (await JobsAsync(endpoint.Id)).Single(j => j.Id != first.Id);
        await EndJobAsync(second.Id, JobState.Succeeded, 0);
        await PassAsync(service);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var alert = await db.Alerts.SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.Task);
            Assert.Equal((AlertState.Resolved, AutomationTaskService.ResolvedReason), (alert.State, alert.ResolvedReason));
        }
    }

    [Fact]
    public async Task A_task_that_is_switched_off_starts_nothing()
    {
        var (_, site, endpoint) = await SeedAsync("OFF");
        await _fixture.Db.CreateTaskAsync(site, EveryQuarter(t => t.Enabled = false));
        var service = Service();
        await PassAsync(service);
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await PassAsync(service);

        Assert.Empty(await JobsAsync(endpoint.Id));
    }
}
