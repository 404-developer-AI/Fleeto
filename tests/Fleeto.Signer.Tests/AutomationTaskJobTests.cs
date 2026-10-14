using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Handlers;
using Microsoft.EntityFrameworkCore;
using JobType = Fleeto.Core.Entities.JobType;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees the jobs of runs of tasks Fleeto starts by itself (0.6.0): they have no user, so the signer decides from the task
/// itself. It signs only while the task exists and is on, still runs this script, applies to the endpoint, starts on what started
/// the run (an occurrence of its schedule, an alert of one of its checks, the endpoint becoming managed) and did not run there in
/// the last 15 minutes. Full control of the workers can so start a linked task at another moment, never another script.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class AutomationTaskJobTests
{
    private readonly SignerFixture _fixture;

    public AutomationTaskJobTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Scope(Client Client, Site Site, Endpoint Endpoint, AutomationTask Task, Script Script, ScriptVersion Version);

    private async Task<Scope> ScopeAsync(bool link = true, Action<AutomationTask>? configure = null)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-TASK");
        var (task, script, version) = await _fixture.Database.CreateTaskAsync(link ? site : null, t =>
        {
            t.ScheduleKind = TaskScheduleKind.Daily;
            t.StartDate = DateOnly.FromDateTime(_fixture.Now).AddDays(-3);
            t.StartMinute = 0;
            configure?.Invoke(t);
        });
        return new Scope(client, site, endpoint, task, script, version);
    }

    /// <summary>The latest occurrence of the task's daily schedule, the moment the workers would have started the run.</summary>
    private DateTime LastOccurrence(AutomationTask task) => AutomationTasks.Occurrences(task, _fixture.Now.AddDays(-2), _fixture.Now)[^1];

    /// <summary>Writes a run, its target and the job the workers would write, and returns the job.</summary>
    private async Task<Job> CreateJobAsync(Scope scope, TaskTrigger trigger, DateTime occurrence, Guid? scriptId = null)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var run = new AutomationTaskRun
        {
            Id = Guid.NewGuid(), AutomationTaskId = scope.Task.Id, ClientId = scope.Client.Id, Trigger = trigger, OccurrenceAt = occurrence,
            InitiatedByName = AutomationTasks.AutomaticInitiatorName(scope.Task.Name, trigger), CreatedAt = _fixture.Now
        };
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = scope.Client.Id, EndpointId = scope.Endpoint.Id, BatchId = run.Id, Type = JobType.Script,
            ScriptId = scriptId ?? scope.Script.Id, ScriptVersionId = scope.Version.Id, ScriptName = scope.Script.Name, ScriptVersionNumber = 1,
            Language = Core.Entities.ScriptLanguage.PowerShell, ScriptSha256 = scope.Version.Sha256, TimeoutSeconds = 600, MaxOutputBytes = 1024 * 1024,
            TaskId = scope.Task.Id, TaskRunId = run.Id, CreatedAt = _fixture.Now, ValidUntil = _fixture.Now.AddHours(24),
            InitiatedByUserId = AutomationTasks.AutomaticInitiator, InitiatedByName = run.InitiatedByName
        };
        db.AutomationTaskRuns.Add(run);
        db.Jobs.Add(job);
        db.AutomationTaskRunTargets.Add(new AutomationTaskRunTarget
        {
            RunId = run.Id, EndpointId = scope.Endpoint.Id, ClientId = scope.Client.Id, AutomationTaskId = scope.Task.Id, DueAt = occurrence,
            ValidUntil = job.ValidUntil, State = TaskTargetState.Started, JobId = job.Id, UpdatedAt = _fixture.Now
        });
        await db.SaveChangesAsync();
        return job;
    }

    private async Task<Job> SignAsync(Job job)
    {
        await _fixture.ProcessAsync(SigningRequestKind.Job, job.ClientId, job.Id, [], "workers:tasks");
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
    }

    [Fact]
    public async Task A_scheduled_run_of_an_enabled_linked_task_is_signed_with_the_script_of_the_task()
    {
        var scope = await ScopeAsync();

        var signed = await SignAsync(await CreateJobAsync(scope, TaskTrigger.Schedule, LastOccurrence(scope.Task)));

        Assert.Equal(JobState.Queued, signed.State);
        var payload = JobPayload.Parser.ParseFrom(signed.Payload);
        Assert.Equal(scope.Version.Body, payload.Script.Body);
        Assert.StartsWith("Fleeto (task ", payload.InitiatedBy);
    }

    [Fact]
    public async Task A_task_that_was_switched_off_or_runs_another_script_is_refused()
    {
        var off = await ScopeAsync(configure: t => t.Enabled = false);
        var refusedOff = await SignAsync(await CreateJobAsync(off, TaskTrigger.Schedule, LastOccurrence(off.Task)));
        Assert.Equal((JobState.Refused, JobHandler.TaskGoneReason), (refusedOff.State, refusedOff.RefusalReason));

        var changed = await ScopeAsync();
        var (_, otherScript, _) = await _fixture.Database.CreateTaskAsync();
        var refusedChanged = await SignAsync(await CreateJobAsync(changed, TaskTrigger.Schedule, LastOccurrence(changed.Task), otherScript.Id));
        Assert.Equal((JobState.Refused, JobHandler.TaskChangedReason), (refusedChanged.State, refusedChanged.RefusalReason));
        Assert.Null(refusedChanged.Payload);
    }

    [Fact]
    public async Task A_run_at_a_moment_the_schedule_does_not_name_is_refused()
    {
        var scope = await ScopeAsync();

        var refused = await SignAsync(await CreateJobAsync(scope, TaskTrigger.Schedule, LastOccurrence(scope.Task).AddMinutes(7)));

        Assert.Equal((JobState.Refused, JobHandler.TaskTriggerReason), (refused.State, refused.RefusalReason));
    }

    [Fact]
    public async Task A_task_that_is_not_linked_to_the_endpoint_is_refused()
    {
        var scope = await ScopeAsync(link: false);

        var refused = await SignAsync(await CreateJobAsync(scope, TaskTrigger.Schedule, LastOccurrence(scope.Task)));

        Assert.Equal((JobState.Refused, JobHandler.TaskTargetReason), (refused.State, refused.RefusalReason));
    }

    [Fact]
    public async Task A_task_runs_at_most_once_in_15_minutes_on_an_endpoint_when_Fleeto_starts_it()
    {
        // Repeated every hour: the workers could start the previous hour and this one together; the signer signs only the first.
        var scope = await ScopeAsync(configure: t => t.RepeatMinutes = 60);
        var occurrences = AutomationTasks.Occurrences(scope.Task, _fixture.Now.AddDays(-2), _fixture.Now);
        Assert.Equal(JobState.Queued, (await SignAsync(await CreateJobAsync(scope, TaskTrigger.Schedule, occurrences[^1]))).State);

        var again = await SignAsync(await CreateJobAsync(scope, TaskTrigger.Schedule, occurrences[^2]));

        Assert.Equal((JobState.Refused, JobHandler.TaskIntervalReason), (again.State, again.RefusalReason));
    }

    [Fact]
    public async Task A_run_for_an_alert_is_signed_only_for_an_alert_of_one_of_the_tasks_checks_on_the_endpoint()
    {
        var checkId = Guid.NewGuid();
        var scope = await ScopeAsync(configure: t => (t.ScheduleKind, t.TriggerCheckIds) = (TaskScheduleKind.None, [checkId]));
        var openedAt = _fixture.Now.AddMinutes(-2);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var template = new MonitoringTemplate { Id = Guid.NewGuid(), Name = "Services " + checkId, CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now };
            db.MonitoringTemplates.Add(template);
            db.CheckDefinitions.Add(new CheckDefinition
            {
                Id = checkId, MonitoringTemplateId = template.Id, Name = "Spooler", Type = Core.Entities.CheckType.ServiceRunning,
                ParametersJson = """{"service":"Spooler"}""", CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
            });
            db.Alerts.Add(new Alert
            {
                Id = Guid.NewGuid(), ClientId = scope.Client.Id, EndpointId = scope.Endpoint.Id, Kind = AlertKind.Check, CheckDefinitionId = checkId,
                Severity = AlertSeverity.Critical, Title = "Spooler is stopped", OpenedAt = openedAt, UpdatedAt = openedAt
            });
            await db.SaveChangesAsync();
        }

        var alertJob = await CreateJobAsync(scope, TaskTrigger.CheckAlert, openedAt);
        var request = await _fixture.ProcessAsync(SigningRequestKind.Job, alertJob.ClientId, alertJob.Id, [], "workers:tasks");
        Assert.True(request.State == SigningRequestState.Completed, $"{request.State}: {request.RefusalReason}");
        Assert.Equal(JobState.Queued, (await SignAsync(alertJob)).State);

        var other = await ScopeAsync(configure: t => (t.ScheduleKind, t.TriggerCheckIds) = (TaskScheduleKind.None, [checkId]));
        var refused = await SignAsync(await CreateJobAsync(other, TaskTrigger.CheckAlert, openedAt));
        Assert.Equal((JobState.Refused, JobHandler.TaskTriggerReason), (refused.State, refused.RefusalReason));
    }

    [Fact]
    public async Task An_unapproved_script_is_refused_where_the_policy_requires_approval_as_for_every_job()
    {
        var scope = await ScopeAsync();
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var policy = new Policy
            {
                Id = Guid.NewGuid(), ClientId = scope.Client.Id, Name = "Approval " + scope.Client.Code, ScriptApprovalRequired = true,
                CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
            };
            db.Policies.Add(policy);
            db.ClientPolicies.Add(new ClientPolicy { ClientId = scope.Client.Id, PolicyId = policy.Id, CreatedAt = _fixture.Now });
            await db.SaveChangesAsync();
        }

        var refused = await SignAsync(await CreateJobAsync(scope, TaskTrigger.Schedule, LastOccurrence(scope.Task)));

        Assert.Equal((JobState.Refused, JobHandler.ApprovalReason), (refused.State, refused.RefusalReason));
    }
}
