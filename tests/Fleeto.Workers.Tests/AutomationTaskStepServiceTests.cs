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
/// The steps of tasks in the workers (0.6.0, decided 2026-10-15), with the production grants and database rules of the workers role:
/// on an endpoint a step starts only when the step before it ended, a restart step becomes a restart job, a failed step stops the
/// run unless its exit code counts as success or it may fail, the run ends with its outcome and an alert that names the step, an
/// endpoint that cannot run every step is skipped before the first, and a run started by hand runs its steps like any other.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class AutomationTaskStepServiceTests : IAsyncLifetime
{
    private readonly WorkersFixture _fixture;
    private NpgsqlDataSource? _workersSource;
    private IFleetoDbContextFactory _workers = null!;

    public AutomationTaskStepServiceTests(WorkersFixture fixture) => _fixture = fixture;

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

    /// <summary>A managed Windows endpoint of this release and a task every 15 minutes on its site with these steps after its own script.</summary>
    private async Task<(Endpoint Endpoint, AutomationTask Task, IReadOnlyList<AutomationTaskStep> Steps)> SeedAsync(string prefix, string agentVersion,
        Action<AutomationTaskStep>? firstStep, params AutomationTaskStep[] more)
    {
        var client = await _fixture.Db.CreateClientAsync();
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Steps " + Guid.NewGuid().ToString("N")[..6]);
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, prefix + "-" + Guid.NewGuid().ToString("N")[..6]);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.AgentVersion, agentVersion));
        }

        var (task, script, _) = await _fixture.Db.CreateTaskAsync(site, t =>
        {
            t.ScheduleKind = TaskScheduleKind.Daily;
            t.StartDate = t.StartDate.AddDays(-1);
            t.StartMinute = 0;
            t.RepeatMinutes = 15;
        });
        var first = new AutomationTaskStep { Kind = TaskStepKind.Script, ScriptId = script.Id };
        firstStep?.Invoke(first);
        var steps = await _fixture.Db.AddTaskStepsAsync(task, [first, .. more]);
        return (endpoint, task, steps);
    }

    private async Task<List<Job>> JobsAsync(Guid endpointId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Jobs.AsNoTracking().Where(j => j.EndpointId == endpointId).OrderBy(j => j.CreatedAt).ToListAsync();
    }

    /// <summary>The job of a step (the test clock stands still, so jobs cannot be told apart by when they were written).</summary>
    private async Task<Job> JobOfAsync(Guid endpointId, AutomationTaskStep step, Guid? runId = null) =>
        Assert.Single((await JobsAsync(endpointId)).Where(j => j.TaskStepId == step.Id && (runId is null || j.TaskRunId == runId)));

    private async Task<AutomationTaskRunTarget> TargetAsync(Guid taskId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.AutomationTaskRunTargets.AsNoTracking().Where(t => t.AutomationTaskId == taskId).OrderBy(t => t.DueAt).FirstAsync();
    }

    private static async Task PassAsync(AutomationTaskService service)
    {
        await service.StartRunsAsync(CancellationToken.None);
        await service.FollowOutcomesAsync(CancellationToken.None);
        await service.StartJobsAsync(CancellationToken.None);
    }

    /// <summary>The first pass starts counting; 16 minutes later an occurrence starts the run.</summary>
    private async Task StartRunAsync(AutomationTaskService service)
    {
        await PassAsync(service);
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await PassAsync(service);
    }

    private async Task EndAsync(Guid jobId, JobState state, int? exitCode = 0)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
        job.Signature = new byte[64];
        job.State = state;
        job.Result = JobResult.Exited;
        job.ExitCode = exitCode;
        job.CompletedAt = Now;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_steps_run_one_after_the_other_through_a_restart_and_the_run_succeeds_after_the_last()
    {
        var (endpoint, task, steps) = await SeedAsync("STEPS", "0.6.0", null,
            new AutomationTaskStep { Kind = TaskStepKind.Restart, RestartWaitMinutes = 20 }, new AutomationTaskStep { Kind = TaskStepKind.Script });
        var service = Service();
        await StartRunAsync(service);

        var first = Assert.Single(await JobsAsync(endpoint.Id));
        Assert.Equal((steps[0].Id, JobType.Script), (first.TaskStepId, first.Type));
        await PassAsync(service);
        Assert.Single(await JobsAsync(endpoint.Id));

        await EndAsync(first.Id, JobState.Succeeded);
        await PassAsync(service);
        var restart = await JobOfAsync(endpoint.Id, steps[1]);
        Assert.Equal((steps[1].Id, JobType.Restart, (Guid?)null, 20 * 60), (restart.TaskStepId, restart.Type, restart.ScriptId, restart.TimeoutSeconds));
        Assert.Equal((first.TaskRunId, AutomationTasks.AutomaticInitiator), (restart.TaskRunId, restart.InitiatedByUserId));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.True(await db.SigningRequests.AnyAsync(r => r.SubjectId == restart.Id));
        }

        await EndAsync(restart.Id, JobState.Succeeded);
        await PassAsync(service);
        var last = await JobOfAsync(endpoint.Id, steps[2]);
        Assert.Null((await TargetAsync(task.Id)).FinishedAt);

        await EndAsync(last.Id, JobState.Succeeded);
        await PassAsync(service);
        var target = await TargetAsync(task.Id);
        Assert.Equal((TaskTargetOutcome.Succeeded, last.Id), (target.Outcome, target.JobId));
        Assert.NotNull(target.FinishedAt);
        Assert.Equal(3, (await JobsAsync(endpoint.Id)).Count);
    }

    [Fact]
    public async Task A_failed_step_stops_the_run_and_its_alert_names_the_step()
    {
        var (endpoint, task, _) = await SeedAsync("STOP", "0.6.0", null, new AutomationTaskStep { Kind = TaskStepKind.Script });
        var service = Service();
        await StartRunAsync(service);

        await EndAsync(Assert.Single(await JobsAsync(endpoint.Id)).Id, JobState.Failed, 2);
        await PassAsync(service);

        Assert.Single(await JobsAsync(endpoint.Id));
        Assert.Equal(TaskTargetOutcome.Failed, (await TargetAsync(task.Id)).Outcome);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var alert = await db.Alerts.SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.Task);
        Assert.Contains("exit code 2 at step 1", alert.Title);
    }

    [Fact]
    public async Task An_exit_code_that_counts_as_success_and_a_step_that_may_fail_let_the_run_go_on_and_the_failure_still_counts()
    {
        var (endpoint, task, steps) = await SeedAsync("GOON", "0.6.0", s => s.SuccessExitCodes = [3],
            new AutomationTaskStep { Kind = TaskStepKind.Script, ContinueOnFailure = true }, new AutomationTaskStep { Kind = TaskStepKind.Script });
        var service = Service();
        await StartRunAsync(service);

        await EndAsync((await JobOfAsync(endpoint.Id, steps[0])).Id, JobState.Failed, 3);
        await PassAsync(service);
        await EndAsync((await JobOfAsync(endpoint.Id, steps[1])).Id, JobState.Failed, 1);
        await PassAsync(service);
        await EndAsync((await JobOfAsync(endpoint.Id, steps[2])).Id, JobState.Succeeded);
        await PassAsync(service);

        Assert.Equal(TaskTargetOutcome.Failed, (await TargetAsync(task.Id)).Outcome);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.Contains("at step 2", (await db.Alerts.SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.Task)).Title);
    }

    [Fact]
    public async Task An_endpoint_that_cannot_restart_is_skipped_before_the_first_step()
    {
        var (endpoint, task, _) = await SeedAsync("OLDAGENT", "0.5.0", null, new AutomationTaskStep { Kind = TaskStepKind.Restart, RestartWaitMinutes = 30 });
        var service = Service();
        await StartRunAsync(service);

        Assert.Empty(await JobsAsync(endpoint.Id));
        var target = await TargetAsync(task.Id);
        Assert.Equal(TaskTargetState.Skipped, target.State);
        Assert.Contains("cannot restart for a task yet", target.Reason);
    }

    [Fact]
    public async Task A_run_that_is_still_between_two_steps_keeps_the_next_run_off_the_endpoint()
    {
        var (endpoint, task, _) = await SeedAsync("BETWEEN", "0.6.0", null, new AutomationTaskStep { Kind = TaskStepKind.Script });
        var service = Service();
        await StartRunAsync(service);
        await EndAsync(Assert.Single(await JobsAsync(endpoint.Id)).Id, JobState.Succeeded);
        await PassAsync(service);
        Assert.Equal(2, (await JobsAsync(endpoint.Id)).Count);

        // The next occurrence comes while step 2 still waits for its signature.
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await PassAsync(service);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var targets = await db.AutomationTaskRunTargets.AsNoTracking().Where(t => t.AutomationTaskId == task.Id).OrderBy(t => t.DueAt).ToListAsync();
        Assert.Equal(2, targets.Count);
        Assert.Equal((TaskTargetState.Skipped, AutomationTaskService.PreviousRunningReason), (targets[1].State, targets[1].Reason));
    }

    [Fact]
    public async Task A_run_started_by_hand_runs_its_steps_without_a_user_on_the_job_and_without_waiting_15_minutes()
    {
        var (endpoint, task, steps) = await SeedAsync("MANUAL", "0.6.0", null, new AutomationTaskStep { Kind = TaskStepKind.Script });
        var service = Service();
        await StartRunAsync(service);
        var scheduled = Assert.Single(await JobsAsync(endpoint.Id));
        await EndAsync(scheduled.Id, JobState.Failed, 1);
        await PassAsync(service);

        // A user presses Run now a minute after the scheduled run: web writes the run with the user and a waiting endpoint.
        var userId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var run = new AutomationTaskRun
            {
                Id = runId, AutomationTaskId = task.Id, ClientId = endpoint.ClientId, Trigger = TaskTrigger.Manual, OccurrenceAt = Now,
                InitiatedByName = "Tess", InitiatedByUserId = userId, CreatedAt = Now
            };
            db.AutomationTaskRuns.Add(run);
            db.AutomationTaskRunTargets.Add(new AutomationTaskRunTarget
            {
                RunId = run.Id, EndpointId = endpoint.Id, ClientId = endpoint.ClientId, AutomationTaskId = task.Id, DueAt = Now,
                ValidUntil = Now.AddHours(24), UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        await PassAsync(service);

        var job = await JobOfAsync(endpoint.Id, steps[0], runId);
        Assert.Equal((AutomationTasks.AutomaticInitiator, "Tess"), (job.InitiatedByUserId, job.InitiatedByName));
    }
}
