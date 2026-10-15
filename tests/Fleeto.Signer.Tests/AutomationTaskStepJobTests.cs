using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Handlers;
using Microsoft.EntityFrameworkCore;
using JobType = Fleeto.Core.Entities.JobType;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees the steps of tasks (0.6.0, decided 2026-10-15): the signer signs a step only after the job of the step before it
/// ended and passed (or may fail), never a step twice or out of order, judged from the jobs the gateway completed; it composes a
/// restart itself with the wait of the step, only for an agent that knows it; and a run started by hand is signed only while the
/// user who started it may still do so.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class AutomationTaskStepJobTests
{
    private readonly SignerFixture _fixture;

    public AutomationTaskStepJobTests(SignerFixture fixture) => _fixture = fixture;

    private sealed record Scope(Client Client, Endpoint Endpoint, AutomationTask Task, ScriptVersion Version, IReadOnlyList<AutomationTaskStep> Steps);

    /// <summary>A daily task of three steps (its script, a restart of 20 minutes, another script) on a Windows endpoint of this release.</summary>
    private async Task<Scope> ScopeAsync(string agentVersion = "0.6.0", Action<AutomationTaskStep>? firstStep = null)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-STEPS");
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.AgentVersion, agentVersion));
        }

        var (task, script, version) = await _fixture.Database.CreateTaskAsync(site, t =>
        {
            t.ScheduleKind = TaskScheduleKind.Daily;
            t.StartDate = DateOnly.FromDateTime(_fixture.Now).AddDays(-3);
            t.StartMinute = 0;
        });
        var first = new AutomationTaskStep { Kind = TaskStepKind.Script, ScriptId = script.Id };
        firstStep?.Invoke(first);
        var steps = await _fixture.Database.AddTaskStepsAsync(task, first,
            new AutomationTaskStep { Kind = TaskStepKind.Restart, RestartWaitMinutes = 20 },
            new AutomationTaskStep { Kind = TaskStepKind.Script });
        return new Scope(client, endpoint, task, version, steps);
    }

    private DateTime LastOccurrence(AutomationTask task) => AutomationTasks.Occurrences(task, _fixture.Now.AddDays(-2), _fixture.Now)[^1];

    private async Task<Guid> CreateRunAsync(Scope scope, TaskTrigger trigger, DateTime occurrence, Guid? userId = null)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var run = new AutomationTaskRun
        {
            Id = Guid.NewGuid(), AutomationTaskId = scope.Task.Id, ClientId = scope.Client.Id, Trigger = trigger, OccurrenceAt = occurrence,
            InitiatedByName = "Run", InitiatedByUserId = userId, CreatedAt = _fixture.Now
        };
        db.AutomationTaskRuns.Add(run);
        db.AutomationTaskRunTargets.Add(new AutomationTaskRunTarget
        {
            RunId = run.Id, EndpointId = scope.Endpoint.Id, ClientId = scope.Client.Id, AutomationTaskId = scope.Task.Id, DueAt = occurrence,
            ValidUntil = _fixture.Now.AddHours(24), State = TaskTargetState.Started, UpdatedAt = _fixture.Now
        });
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>Writes the job of a step as the workers would, and points the run's target at it.</summary>
    private async Task<Job> StepJobAsync(Scope scope, Guid runId, AutomationTaskStep step)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = scope.Client.Id, EndpointId = scope.Endpoint.Id, BatchId = runId, TaskId = scope.Task.Id, TaskRunId = runId,
            TaskStepId = step.Id, CreatedAt = _fixture.Now, ValidUntil = _fixture.Now.AddHours(24), InitiatedByUserId = AutomationTasks.AutomaticInitiator,
            InitiatedByName = "Run", TimeoutSeconds = 600, MaxOutputBytes = 1024 * 1024
        };
        if (step.Kind == TaskStepKind.Restart)
        {
            job.Type = JobType.Restart;
            job.ScriptName = RestartRules.JobName;
        }
        else
        {
            var version = await db.ScriptVersions.SingleAsync(v => v.ScriptId == step.ScriptId);
            job.Type = JobType.Script;
            job.ScriptId = step.ScriptId;
            job.ScriptVersionId = version.Id;
            job.ScriptName = "Step";
            job.ScriptVersionNumber = 1;
            job.Language = Core.Entities.ScriptLanguage.PowerShell;
            job.ScriptSha256 = version.Sha256;
            job.RunAs = step.RunAs;
        }

        db.Jobs.Add(job);
        await db.AutomationTaskRunTargets.Where(t => t.RunId == runId && t.EndpointId == scope.Endpoint.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.JobId, (Guid?)null));
        await db.SaveChangesAsync();
        await db.AutomationTaskRunTargets.Where(t => t.RunId == runId && t.EndpointId == scope.Endpoint.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.JobId, job.Id));
        return job;
    }

    private async Task<Job> SignAsync(Job job)
    {
        await _fixture.ProcessAsync(SigningRequestKind.Job, job.ClientId, job.Id, [], "workers:tasks");
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
    }

    private async Task EndAsync(Job job, JobState state, int? exitCode = null, ExitStatus? exitStatus = null)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.State, state)
            .SetProperty(j => j.Result, state == JobState.Cancelled ? null : Core.Entities.JobResult.Exited)
            .SetProperty(j => j.ExitCode, exitCode)
            .SetProperty(j => j.ExitStatus, exitStatus)
            .SetProperty(j => j.CompletedAt, _fixture.Now));
    }

    [Fact]
    public async Task A_step_is_not_signed_before_the_step_before_it_ended()
    {
        var scope = await ScopeAsync();
        var runId = await CreateRunAsync(scope, TaskTrigger.Schedule, LastOccurrence(scope.Task));
        Assert.Equal(JobState.Queued, (await SignAsync(await StepJobAsync(scope, runId, scope.Steps[0]))).State);

        var early = await SignAsync(await StepJobAsync(scope, runId, scope.Steps[1]));

        Assert.Equal((JobState.Refused, JobHandler.TaskOrderReason), (early.State, early.RefusalReason));
    }

    [Fact]
    public async Task A_restart_follows_its_own_runs_step_and_carries_the_wait_of_its_step()
    {
        var scope = await ScopeAsync();
        var runId = await CreateRunAsync(scope, TaskTrigger.Schedule, LastOccurrence(scope.Task));
        var first = await SignAsync(await StepJobAsync(scope, runId, scope.Steps[0]));
        await EndAsync(first, JobState.Succeeded, 0);

        // Step 1 of another run does not open the restart of a run without it.
        var otherRun = await CreateRunAsync(scope, TaskTrigger.Schedule, AutomationTasks.Occurrences(scope.Task, _fixture.Now.AddDays(-3), _fixture.Now)[^2]);
        var skipped = await SignAsync(await StepJobAsync(scope, otherRun, scope.Steps[1]));
        Assert.Equal((JobState.Refused, JobHandler.TaskOrderReason), (skipped.State, skipped.RefusalReason));

        // Within 15 minutes of step 1, in the same run: the restart is signed, composed by the signer.
        var restart = await SignAsync(await StepJobAsync(scope, runId, scope.Steps[1]));
        Assert.Equal(JobState.Queued, restart.State);
        var payload = JobPayload.Parser.ParseFrom(restart.Payload);
        Assert.Equal((Protocol.Agent.V1.JobType.Restart, 20u * 60), (payload.Type, payload.TimeoutSeconds));
        Assert.Null(payload.Script);
        Assert.NotNull(payload.Restart);
    }

    [Theory]
    [InlineData(JobState.Failed, 1, "", false, false, null)]
    [InlineData(JobState.Failed, 3, "3", false, true, null)]
    [InlineData(JobState.Failed, 1, "", true, true, null)]
    [InlineData(JobState.Cancelled, null, "", true, false, null)]
    // Exit code rules of the script (0.6.0): the gateway ended a warning as succeeded; an error fails also with a code the step once
    // counted as success, because the script decides now.
    [InlineData(JobState.Succeeded, 1, "", false, true, ExitStatus.Warning)]
    [InlineData(JobState.Succeeded, 4, "", false, true, ExitStatus.Info)]
    [InlineData(JobState.Failed, 3, "3", false, false, ExitStatus.Error)]
    public async Task A_step_after_a_failed_one_is_signed_only_when_its_exit_code_counts_as_success_or_it_may_fail(JobState state, int? exitCode,
        string successCodes, bool continueOnFailure, bool signed, ExitStatus? exitStatus)
    {
        var scope = await ScopeAsync(firstStep: s =>
        {
            s.SuccessExitCodes = successCodes.Length == 0 ? [] : [int.Parse(successCodes)];
            s.ContinueOnFailure = continueOnFailure;
        });
        var runId = await CreateRunAsync(scope, TaskTrigger.Schedule, LastOccurrence(scope.Task));
        var first = await SignAsync(await StepJobAsync(scope, runId, scope.Steps[0]));
        await EndAsync(first, state, exitCode, exitStatus);

        var next = await SignAsync(await StepJobAsync(scope, runId, scope.Steps[1]));

        Assert.Equal(signed ? JobState.Queued : JobState.Refused, next.State);
    }

    [Fact]
    public async Task A_restart_is_refused_for_an_agent_that_does_not_know_it_and_for_a_job_a_user_started()
    {
        var old = await ScopeAsync(agentVersion: "0.5.0");
        var runId = await CreateRunAsync(old, TaskTrigger.Schedule, LastOccurrence(old.Task));
        await EndAsync(await SignAsync(await StepJobAsync(old, runId, old.Steps[0])), JobState.Succeeded, 0);
        var refused = await SignAsync(await StepJobAsync(old, runId, old.Steps[1]));
        Assert.Equal((JobState.Refused, RestartRules.Problem("windows", "0.5.0")), (refused.State, refused.RefusalReason));

        var scope = await ScopeAsync();
        var userId = (await _fixture.Database.CreateUserAsync(FleetoRoles.Admin)).Id;
        Job byUser;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            byUser = new Job
            {
                Id = Guid.NewGuid(), ClientId = scope.Client.Id, EndpointId = scope.Endpoint.Id, BatchId = Guid.NewGuid(), Type = JobType.Restart,
                ScriptName = RestartRules.JobName, CreatedAt = _fixture.Now, ValidUntil = _fixture.Now.AddHours(1), InitiatedByUserId = userId,
                InitiatedByName = "Admin", TimeoutSeconds = 600
            };
            db.Jobs.Add(byUser);
            await db.SaveChangesAsync();
        }

        var refusedUser = await SignAsync(byUser);
        Assert.Equal((JobState.Refused, JobHandler.RestartUserReason), (refusedUser.State, refusedUser.RefusalReason));
    }

    [Fact]
    public async Task A_run_started_by_hand_is_signed_while_its_user_may_run_tasks_and_restart_and_refused_once_it_may_not()
    {
        var scope = await ScopeAsync();
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);
        var runId = await CreateRunAsync(scope, TaskTrigger.Manual, _fixture.Now, technician.Id);

        var first = await SignAsync(await StepJobAsync(scope, runId, scope.Steps[0]));
        Assert.Equal(JobState.Queued, first.State);
        await EndAsync(first, JobState.Succeeded, 0);

        // The technician becomes read-only before the restart: the restart is refused.
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var readOnly = await db.Roles.SingleAsync(r => r.Name == FleetoRoles.ReadOnly);
            var link = await db.UserRoles.SingleAsync(r => r.UserId == technician.Id);
            db.UserRoles.Remove(link);
            await db.SaveChangesAsync();
            db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = technician.Id, RoleId = readOnly.Id });
            await db.SaveChangesAsync();
        }

        var restart = await SignAsync(await StepJobAsync(scope, runId, scope.Steps[1]));
        Assert.Equal((JobState.Refused, JobHandler.InitiatorReason), (restart.State, restart.RefusalReason));
    }

    [Fact]
    public async Task A_run_started_by_hand_without_its_user_is_refused()
    {
        var scope = await ScopeAsync();
        var runId = await CreateRunAsync(scope, TaskTrigger.Manual, _fixture.Now);

        var refused = await SignAsync(await StepJobAsync(scope, runId, scope.Steps[0]));

        Assert.Equal((JobState.Refused, JobHandler.TaskTriggerReason), (refused.State, refused.RefusalReason));
    }
}
