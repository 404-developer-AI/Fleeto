using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Jobs;

/// <summary>
/// Runs tasks by themselves (0.6.0, decided 2026-10-14). Every pass:
/// <list type="number">
/// <item><b>Starts runs</b>: for every enabled task the occurrences of its schedule that passed, the alerts of its chosen checks
/// that opened and the endpoints switched to managed, each as a run per client with one target per endpoint the task applies to.
/// A task the workers see for the first time starts counting from now, so nothing from before it existed starts it.</item>
/// <item><b>Starts the first step</b>: a target whose turn came (the run plus its share of the random delay) gets the job of
/// the task's first step, without a user, and a signing request, unless a previous run of the task is still busy there (skipped,
/// or waiting until it ended), the endpoint can no longer run every step, or the run expired. fleeto-signer decides again.</item>
/// <item><b>Moves on</b> (0.6.0, steps): when the job of a step ended and the step passed (or may fail), the job of the next step
/// is written at once; otherwise, or after the last step, the run ends on the endpoint. A failed run opens an alert of kind Task
/// on its endpoint when the task asks for it, naming the step, and a successful one resolves it.</item>
/// </list>
/// Runs started by hand are written by web (the run names the user); this service runs their steps like any other run, and
/// fleeto-signer checks that user's permissions again for every step.
/// </summary>
public sealed class AutomationTaskService : WorkerLoop
{
    /// <summary>Jobs and outcomes handled per pass; more waiting runs the loop again at once.</summary>
    public const int BatchSize = 200;

    /// <summary>A scheduled occurrence that does not catch up still starts when the workers see it within this time.</summary>
    public static readonly TimeSpan OnTimeWindow = TimeSpan.FromMinutes(5);

    /// <summary>Alerts and managed switches are read again this far back, for rows committed after a later one was read.</summary>
    private static readonly TimeSpan EventLookback = TimeSpan.FromMinutes(2);

    public const string TaskOffReason = "The task was switched off or deleted before this endpoint's turn.";
    public const string NoLongerAppliesReason = "The task no longer applies to this endpoint.";
    public const string PreviousRunningReason = "The previous run of this task was still running on this endpoint.";
    public const string WaitedTooLongReason = "The previous run of this task was still running when this run expired.";
    public const string ExpiredReason = "The run expired before its job could start.";
    public const string RecentReason = "This task already ran on this endpoint less than 15 minutes ago.";
    public const string NoVersionReason = "The script has no version to run.";
    public const string ResolvedReason = "The task ran successfully on this endpoint.";
    public const string ResolvedTaskGoneReason = "The task was deleted or no longer opens an alert when a run fails.";
    public const string JobGoneReason = "The job of this run was deleted before it ended.";
    public const string StepsChangedReason = "The steps of the task changed during this run, so it stopped. The next run uses the task as it is now.";
    public const string SwitchedOffReason = "The task was switched off during this run, so the next step did not start.";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly AlertNotificationService _notifier;
    private readonly LicenseService _licenses;
    private readonly List<IDisposable> _subscriptions = [];

    public AutomationTaskService(IFleetoDbContextFactory dbFactory, INotificationBus bus, AlertNotificationService notifier, LicenseService licenses,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<AutomationTaskService> logger)
        : base("tasks", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _notifier = notifier;
        _licenses = licenses;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override void OnStarting()
    {
        // A changed task, a new alert and an ended job each wake the loop; the interval catches what a lost notification missed.
        foreach (var channel in new[] { NotificationChannels.Tasks, NotificationChannels.Alerts, NotificationChannels.Jobs })
        {
            _subscriptions.Add(_bus.Subscribe(channel, (_, _) =>
            {
                Wake();
                return Task.CompletedTask;
            }));
        }
    }

    protected override void OnStopping()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await StartRunsAsync(cancellationToken);
        // Runs that ended first: an endpoint where the previous run just ended is free for the next one in the same pass.
        var followed = await FollowOutcomesAsync(cancellationToken);
        var started = await StartJobsAsync(cancellationToken);
        return started >= BatchSize || followed >= BatchSize;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // 1. Runs
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>Starts the runs that are due for every enabled task. Returns the number of runs written.</summary>
    public async Task<int> StartRunsAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var tasks = await db.AutomationTasks.AsNoTracking().Where(t => t.Enabled).OrderBy(t => t.CreatedAt).ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            return 0;
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var states = await db.AutomationTaskStates.ToDictionaryAsync(s => s.AutomationTaskId, cancellationToken);
        var plans = await TaskPlan.LoadManyAsync(db, tasks, cancellationToken);
        var written = 0;
        var touched = new HashSet<Guid>();

        foreach (var task in tasks)
        {
            var hash = AutomationTasks.ScheduleHash(task);
            if (!states.TryGetValue(task.Id, out var state))
            {
                // New to the workers: count from now, so nothing from before the task existed starts it.
                state = new AutomationTaskState
                {
                    AutomationTaskId = task.Id, LastOccurrenceAt = now, AlertsSeenUntil = now, ManagedSeenUntil = now, ScheduleHash = hash,
                    NextOccurrenceAt = AutomationTasks.Next(task, now), UpdatedAt = now
                };
                db.AutomationTaskStates.Add(state);
                states[task.Id] = state;
                touched.Add(task.Id);
                continue;
            }

            if (state.ScheduleHash != hash)
            {
                // An edited schedule counts from the edit: the old one's occurrences no longer apply.
                state.ScheduleHash = hash;
                state.LastOccurrenceAt = now;
                state.NextOccurrenceAt = AutomationTasks.Next(task, now);
                state.UpdatedAt = now;
                touched.Add(task.Id);
            }

            var plan = plans[task.Id];
            written += await StartScheduledAsync(db, task, plan, state, license, now, cancellationToken);
            written += await StartForAlertsAsync(db, task, plan, state, license, now, cancellationToken);
            written += await StartForManagedAsync(db, task, plan, state, license, now, cancellationToken);
            if (db.ChangeTracker.HasChanges())
            {
                touched.Add(task.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var taskId in touched)
        {
            await _bus.PublishAsync(NotificationChannels.Tasks, taskId.ToString(), cancellationToken);
        }

        return written;
    }

    private static async Task<int> StartScheduledAsync(FleetoDbContext db, AutomationTask task, TaskPlan plan, AutomationTaskState state,
        LicenseStatus license, DateTime now, CancellationToken cancellationToken)
    {
        if (task.ScheduleKind == TaskScheduleKind.None)
        {
            return 0;
        }

        var occurrences = AutomationTasks.Occurrences(task, state.LastOccurrenceAt ?? now, now);
        if (occurrences.Count == 0)
        {
            if (state.NextOccurrenceAt is null || state.NextOccurrenceAt <= now)
            {
                state.NextOccurrenceAt = AutomationTasks.Next(task, now);
                state.UpdatedAt = now;
            }

            return 0;
        }

        state.LastOccurrenceAt = occurrences[^1];
        state.NextOccurrenceAt = AutomationTasks.Next(task, now);
        state.UpdatedAt = now;

        // Missed occurrences (the workers were stopped) start once, as the latest; without catching up only one that is on time.
        var occurrence = occurrences[^1];
        if (!task.CatchUp && now - occurrence > OnTimeWindow)
        {
            return 0;
        }

        var endpoints = await AutomationTaskTargets.LoadAsync(db, task, plan, license, null, cancellationToken);
        return await AddRunsAsync(db, task, TaskTrigger.Schedule, occurrence, null, endpoints, state.NextOccurrenceAt, now, cancellationToken);
    }

    private static async Task<int> StartForAlertsAsync(FleetoDbContext db, AutomationTask task, TaskPlan plan, AutomationTaskState state,
        LicenseStatus license, DateTime now, CancellationToken cancellationToken)
    {
        if (task.TriggerCheckIds.Count == 0)
        {
            return 0;
        }

        var checkIds = task.TriggerCheckIds;
        var since = state.AlertsSeenUntil - EventLookback;
        var alerts = await db.Alerts.AsNoTracking()
            .Where(a => a.Kind == AlertKind.Check && a.CheckDefinitionId != null && checkIds.Contains(a.CheckDefinitionId.Value) &&
                        a.OpenedAt > since && a.OpenedAt <= now)
            .OrderBy(a => a.OpenedAt)
            .Take(BatchSize)
            .Select(a => new { a.EndpointId, a.OpenedAt, a.CheckDefinitionId })
            .ToListAsync(cancellationToken);
        if (alerts.Count == 0)
        {
            return 0;
        }

        state.AlertsSeenUntil = alerts.Count == BatchSize ? alerts[^1].OpenedAt : Max(state.AlertsSeenUntil, alerts[^1].OpenedAt);
        state.UpdatedAt = now;
        var names = await db.CheckDefinitions.AsNoTracking().Where(c => checkIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var written = 0;
        foreach (var alert in alerts)
        {
            // The same alert never starts the task twice, and an endpoint at most once per cooldown.
            var cooldownFrom = alert.OpenedAt - AutomationTasks.CheckAlertCooldown;
            var recent = await (
                    from t in db.AutomationTaskRunTargets
                    join r in db.AutomationTaskRuns on t.RunId equals r.Id
                    where t.AutomationTaskId == task.Id && t.EndpointId == alert.EndpointId && r.Trigger == TaskTrigger.CheckAlert &&
                          r.OccurrenceAt > cooldownFrom
                    select t.RunId)
                .AnyAsync(cancellationToken);
            if (recent || db.ChangeTracker.Entries<AutomationTaskRunTarget>().Any(e => e.Entity.AutomationTaskId == task.Id && e.Entity.EndpointId == alert.EndpointId))
            {
                continue;
            }

            var endpoints = await AutomationTaskTargets.LoadAsync(db, task, plan, license, [alert.EndpointId], cancellationToken);
            written += await AddRunsAsync(db, task, TaskTrigger.CheckAlert, alert.OpenedAt, names.GetValueOrDefault(alert.CheckDefinitionId!.Value),
                endpoints, null, now, cancellationToken);
        }

        return written;
    }

    private static async Task<int> StartForManagedAsync(FleetoDbContext db, AutomationTask task, TaskPlan plan, AutomationTaskState state,
        LicenseStatus license, DateTime now, CancellationToken cancellationToken)
    {
        if (!task.OnManaged)
        {
            return 0;
        }

        var since = state.ManagedSeenUntil - EventLookback;
        var switched = await AutomationTaskTargets.Linked(db, task).AsNoTracking()
            .Where(e => e.ManagedSince != null && e.ManagedSince > since && e.ManagedSince <= now)
            .OrderBy(e => e.ManagedSince)
            .Take(BatchSize)
            .Select(e => new { e.Id, ManagedSince = e.ManagedSince!.Value })
            .ToListAsync(cancellationToken);
        if (switched.Count == 0)
        {
            return 0;
        }

        state.ManagedSeenUntil = switched.Count == BatchSize ? switched[^1].ManagedSince : Max(state.ManagedSeenUntil, switched[^1].ManagedSince);
        state.UpdatedAt = now;
        var written = 0;
        foreach (var endpoint in switched)
        {
            var seen = await (
                    from t in db.AutomationTaskRunTargets
                    join r in db.AutomationTaskRuns on t.RunId equals r.Id
                    where t.AutomationTaskId == task.Id && t.EndpointId == endpoint.Id && r.Trigger == TaskTrigger.Managed &&
                          r.OccurrenceAt == endpoint.ManagedSince
                    select t.RunId)
                .AnyAsync(cancellationToken);
            if (seen)
            {
                continue;
            }

            var endpoints = await AutomationTaskTargets.LoadAsync(db, task, plan, license, [endpoint.Id], cancellationToken);
            written += await AddRunsAsync(db, task, TaskTrigger.Managed, endpoint.ManagedSince, null, endpoints, null, now, cancellationToken);
        }

        return written;
    }

    /// <summary>
    /// Writes one run per client with a target per endpoint. Endpoints that are not managed are left out (a task runs on managed
    /// endpoints only, and listing every agent-only endpoint in every run says nothing); one that cannot run the script is listed
    /// as skipped with the reason, so a task that never runs somewhere shows why. A scheduled occurrence runs once per client.
    /// </summary>
    private static async Task<int> AddRunsAsync(FleetoDbContext db, AutomationTask task, TaskTrigger trigger, DateTime occurrence, string? checkName,
        IReadOnlyList<TaskEndpoint> endpoints, DateTime? nextOccurrence, DateTime now, CancellationToken cancellationToken)
    {
        var written = 0;
        foreach (var client in endpoints.Where(e => e.Problem != AutomationTaskTargets.NotManagedProblem).GroupBy(e => e.ClientId))
        {
            if (trigger == TaskTrigger.Schedule && await db.AutomationTaskRuns.AnyAsync(r =>
                    r.AutomationTaskId == task.Id && r.ClientId == client.Key && r.Trigger == TaskTrigger.Schedule && r.OccurrenceAt == occurrence,
                    cancellationToken))
            {
                continue;
            }

            var run = new AutomationTaskRun
            {
                Id = Guid.NewGuid(), AutomationTaskId = task.Id, ClientId = client.Key, Trigger = trigger, OccurrenceAt = occurrence,
                InitiatedByName = Cut(AutomationTasks.AutomaticInitiatorName(task.Name, trigger), 200), CheckName = checkName, CreatedAt = now
            };
            db.AutomationTaskRuns.Add(run);
            foreach (var endpoint in client)
            {
                var due = occurrence + AutomationTasks.DelayFor(run.Id, endpoint.Id, task.RandomDelayMinutes);
                var validUntil = AutomationTasks.ValidUntil(task, trigger, due, nextOccurrence);
                db.AutomationTaskRunTargets.Add(new AutomationTaskRunTarget
                {
                    RunId = run.Id, EndpointId = endpoint.Id, ClientId = endpoint.ClientId, AutomationTaskId = task.Id, DueAt = due,
                    ValidUntil = validUntil, State = endpoint.Problem is null ? TaskTargetState.Pending : TaskTargetState.Skipped,
                    Reason = endpoint.Problem, UpdatedAt = now
                });
            }

            written++;
        }

        return written;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // 2. Jobs
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>Writes the job of the first step of targets whose turn came. Returns the number of targets handled.</summary>
    public async Task<int> StartJobsAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var targets = await db.AutomationTaskRunTargets
            .Where(t => t.State == TaskTargetState.Pending && t.DueAt <= now)
            .OrderBy(t => t.DueAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (targets.Count == 0)
        {
            return 0;
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var taskIds = targets.Select(t => t.AutomationTaskId).Distinct().ToList();
        var tasks = await db.AutomationTasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        var plans = await TaskPlan.LoadManyAsync(db, tasks.Values.ToList(), cancellationToken);
        var runIds = targets.Select(t => t.RunId).Distinct().ToList();
        var runs = await db.AutomationTaskRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
        var started = new List<Job>();

        foreach (var target in targets)
        {
            target.UpdatedAt = now;
            if (!tasks.TryGetValue(target.AutomationTaskId, out var task) || !task.Enabled || !runs.TryGetValue(target.RunId, out var run))
            {
                Skip(target, TaskOffReason);
                continue;
            }

            var plan = plans[task.Id];
            // Another run of the task is still busy on the endpoint: one of its steps runs, or the next one is about to start.
            var runId = target.RunId;
            var active = await db.AutomationTaskRunTargets.AsNoTracking()
                             .AnyAsync(t => t.AutomationTaskId == task.Id && t.EndpointId == target.EndpointId && t.RunId != runId &&
                                            t.State == TaskTargetState.Started && t.FinishedAt == null && t.JobId != null, cancellationToken) ||
                         await db.Jobs.AsNoTracking()
                             .AnyAsync(j => j.TaskId == task.Id && j.EndpointId == target.EndpointId && j.TaskRunId != runId &&
                                            (j.State == JobState.PendingSignature || j.State == JobState.Queued || j.State == JobState.Running), cancellationToken);
            if (now >= target.ValidUntil - TimeSpan.FromMinutes(1))
            {
                Skip(target, active ? WaitedTooLongReason : ExpiredReason);
                continue;
            }

            if (active)
            {
                if (task.Overlap == TaskOverlap.Skip)
                {
                    Skip(target, PreviousRunningReason);
                }

                // Waiting: the target stays pending and is looked at again in a next pass.
                continue;
            }

            var endpoints = await AutomationTaskTargets.LoadAsync(db, task, plan, license, [target.EndpointId], cancellationToken);
            if (endpoints.Count == 0)
            {
                Skip(target, NoLongerAppliesReason);
                continue;
            }

            if (endpoints[0].Problem is { } problem)
            {
                Skip(target, problem);
                continue;
            }

            // A run started by hand is the user's own decision; fleeto-signer checks that user again for every step.
            var since = now - AutomationTasks.AutomaticSigningInterval;
            if (run.Trigger != TaskTrigger.Manual &&
                await db.Jobs.AsNoTracking().AnyAsync(j => j.TaskId == task.Id && j.EndpointId == target.EndpointId && j.TaskRunId != runId &&
                                                           j.InitiatedByUserId == AutomationTasks.AutomaticInitiator && j.CreatedAt > since, cancellationToken))
            {
                Skip(target, RecentReason);
                continue;
            }

            var (job, reason) = await WriteStepJobAsync(db, task, plan, plan.First, run, target, Min(target.ValidUntil, now + ScriptRules.MaxValidity), now,
                cancellationToken);
            if (job is null)
            {
                Skip(target, reason!);
                continue;
            }

            started.Add(job);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var endpointId in started.Select(j => j.EndpointId).Distinct())
        {
            await _bus.PublishAsync(NotificationChannels.Jobs, endpointId.ToString(), cancellationToken);
        }

        foreach (var taskId in targets.Select(t => t.AutomationTaskId).Distinct())
        {
            await _bus.PublishAsync(NotificationChannels.Tasks, taskId.ToString(), cancellationToken);
        }

        return targets.Count;
    }

    /// <summary>
    /// Writes the job of one step of a run on one endpoint without a user, with its signing request and audit entry, in one
    /// transaction, and points the target at it. Returns the job, or why it could not be written (a script without a version).
    /// </summary>
    private static async Task<(Job? Job, string? Reason)> WriteStepJobAsync(FleetoDbContext db, AutomationTask task, TaskPlan plan, AutomationTaskStep step,
        AutomationTaskRun run, AutomationTaskRunTarget target, DateTime validUntil, DateTime now, CancellationToken cancellationToken)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = target.ClientId, EndpointId = target.EndpointId, BatchId = run.Id, TaskId = task.Id, TaskRunId = run.Id,
            // A step a build before steps knows only as the task itself has no row, and its job no step.
            TaskStepId = step.Id == Guid.Empty ? null : step.Id,
            CreatedAt = now, ValidUntil = validUntil, InitiatedByUserId = AutomationTasks.AutomaticInitiator, InitiatedByName = run.InitiatedByName
        };
        if (step.Kind == TaskStepKind.Restart)
        {
            // Nothing to run: fleeto-signer composes the restart and sets its wait from the step.
            job.Type = JobType.Restart;
            job.ScriptName = RestartRules.JobName;
            job.TimeoutSeconds = RestartRules.TimeoutSeconds(step.RestartWaitMinutes);
            job.MaxOutputBytes = RestartRules.MaxOutputBytes;
            job.RunAs = JobRunAs.Service;
        }
        else
        {
            var script = plan.ScriptOf(step);
            var version = script?.CurrentVersionId is { } versionId
                ? await db.ScriptVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
                : null;
            if (script is null || version is null)
            {
                return (null, script is null ? AutomationTaskTargets.ScriptGoneProblem : NoVersionReason);
            }

            job.Type = JobType.Script;
            job.ScriptId = script.Id;
            job.ScriptVersionId = version.Id;
            job.ScriptName = script.Name;
            job.ScriptVersionNumber = version.Number;
            job.Language = script.Language;
            job.ScriptSha256 = version.Sha256;
            job.TimeoutSeconds = version.TimeoutSeconds;
            // The signer sets the cap of the endpoint's policy; this is the value the job starts with.
            job.MaxOutputBytes = ScriptRules.DefaultMaxOutputBytes;
            job.RunAs = step.RunAs;
        }

        // The job first: the database lets the workers ask for a signature only for a job of theirs that already exists.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        target.State = TaskTargetState.Started;
        target.JobId = job.Id;
        target.OutcomeHandledAt = null;
        target.UpdatedAt = now;
        db.SigningRequests.Add(new SigningRequest
        {
            Id = Guid.NewGuid(), ClientId = job.ClientId, Kind = SigningRequestKind.Job, SubjectId = job.Id, Payload = [],
            RequestedBy = "workers:tasks", CreatedAt = now
        });
        db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.JobCreated, "Job", job.Id.ToString(), job.ClientId,
            AuditActorType.System, "fleeto-workers", "fleeto-workers", new
            {
                EndpointId = job.EndpointId, Task = task.Name, TaskId = task.Id, RunId = run.Id, Trigger = run.Trigger.ToString(), Step = step.Position,
                Type = job.Type.ToString(), job.ScriptName, job.ScriptVersionNumber, job.ScriptSha256, job.ValidUntil, RunAs = job.RunAs.ToString()
            }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (job, null);
    }

    private static void Skip(AutomationTaskRunTarget target, string reason)
    {
        target.State = TaskTargetState.Skipped;
        target.Reason = reason;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // 3. Outcomes
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// For every job of a run that ended: starts the next step when the step passed (or may fail), or ends the run on the endpoint
    /// and opens or resolves its alert. Returns the number handled.
    /// </summary>
    public async Task<int> FollowOutcomesAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        // An anonymous projection, so the targets in it are tracked and the changes below are saved.
        var ended = (await (
                    from t in db.AutomationTaskRunTargets
                    join j in db.Jobs on t.JobId equals j.Id
                    where t.JobId != null && t.OutcomeHandledAt == null &&
                          j.State != JobState.PendingSignature && j.State != JobState.Queued && j.State != JobState.Running
                    orderby j.CompletedAt
                    select new { Target = t, j.TaskStepId, j.State, j.Result, j.ExitCode })
                .Take(BatchSize)
                .ToListAsync(cancellationToken))
            .Select(e => new EndedStep(e.Target, e.TaskStepId, e.State, e.Result, e.ExitCode))
            .ToList();

        // A run whose job was deleted (retention, a deleted endpoint's jobs) can never move on: it ends.
        var orphans = await db.AutomationTaskRunTargets
            .Where(t => t.State == TaskTargetState.Started && t.JobId == null && t.FinishedAt == null)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            Finish(orphan, TaskTargetOutcome.Failed, JobGoneReason, now);
        }

        var transitions = new List<AlertTransition>();
        var started = new List<Job>();
        if (ended.Count > 0)
        {
            var taskIds = ended.Select(e => e.Target.AutomationTaskId).Distinct().ToList();
            var tasks = await db.AutomationTasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
            var plans = await TaskPlan.LoadManyAsync(db, tasks.Values.ToList(), cancellationToken);
            var runIds = ended.Select(e => e.Target.RunId).Distinct().ToList();
            var runs = await db.AutomationTaskRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
            var endpointIds = ended.Select(e => e.Target.EndpointId).Distinct().ToList();
            var endpoints = await db.Endpoints.AsNoTracking().Where(e => endpointIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.Hostname, cancellationToken);
            var inMaintenance = (await db.Endpoints.AsNoTracking()
                    .Where(e => endpointIds.Contains(e.Id))
                    .Where(MaintenanceRules.EndpointInMaintenance(now, db.MaintenanceWindowOccurrences, EffectivePolicies.Query(db)))
                    .Select(e => e.Id)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
            var license = await _licenses.GetStatusAsync(db, cancellationToken);

            foreach (var outcome in ended)
            {
                var target = outcome.Target;
                target.OutcomeHandledAt = now;
                target.UpdatedAt = now;
                if (!tasks.TryGetValue(target.AutomationTaskId, out var task) || !runs.TryGetValue(target.RunId, out var run))
                {
                    Finish(target, TaskTargetOutcome.Cancelled, TaskOffReason, now);
                    continue;
                }

                var plan = plans[task.Id];
                if (plan.Find(outcome.StepId) is not { } step)
                {
                    Finish(target, TaskTargetOutcome.Failed, StepsChangedReason, now);
                    continue;
                }

                if (AutomationTasks.MayContinue(step, outcome.State, outcome.Result, outcome.ExitCode) && plan.After(step) is { } next)
                {
                    var reason = !task.Enabled ? SwitchedOffReason : await StartNextAsync(db, task, plan, next, run, target, license, started, now, cancellationToken);
                    if (reason is null)
                    {
                        continue;
                    }

                    // The next step could not start: the run ends here, as failed.
                    Finish(target, TaskTargetOutcome.Failed, Cut(reason, 500), now);
                    await AlertAsync(db, task, plan, target, run, endpoints, inMaintenance, transitions, now, cancellationToken, reason);
                    continue;
                }

                var result = await ResultOfRunAsync(db, plan, target, cancellationToken);
                Finish(target, result.Outcome, null, now);
                if (outcome.State is JobState.Cancelled)
                {
                    continue;
                }

                await AlertAsync(db, task, plan, target, run, endpoints, inMaintenance, transitions, now, cancellationToken, null);
            }
        }

        // An alert of a task that was deleted or no longer alerts has nothing left to resolve it: it resolves now.
        var alerting = await db.AutomationTasks.AsNoTracking().Where(t => t.AlertOnFailure).Select(t => t.Id.ToString()).ToListAsync(cancellationToken);
        var orphaned = await db.Alerts.Where(a => a.Kind == AlertKind.Task && a.State != AlertState.Resolved && !alerting.Contains(a.Target))
            .Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var alert in orphaned)
        {
            Resolve(alert, ResolvedTaskGoneReason, transitions, now);
        }

        var licenseNow = await _licenses.GetStatusAsync(db, cancellationToken);
        var released = await AlertCleanup.ResolveNotManagedAsync(db, AlertKind.Task, licenseNow, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AlertCleanup.PublishAsync(_bus, transitions.Select(t => t.AlertId).Concat(released), cancellationToken);
        foreach (var endpointId in started.Select(j => j.EndpointId).Distinct())
        {
            await _bus.PublishAsync(NotificationChannels.Jobs, endpointId.ToString(), cancellationToken);
        }

        foreach (var taskId in ended.Select(e => e.Target.AutomationTaskId).Concat(orphans.Select(o => o.AutomationTaskId)).Distinct())
        {
            await _bus.PublishAsync(NotificationChannels.Tasks, taskId.ToString(), cancellationToken);
        }

        return ended.Count + orphans.Count;
    }

    private sealed record EndedStep(AutomationTaskRunTarget Target, Guid? StepId, JobState State, JobResult? Result, int? ExitCode);

    /// <summary>
    /// Writes the job of the next step, or returns why it cannot start: the endpoint no longer applies or can no longer run every
    /// step. The endpoint is online (its last job just ended), so the job waits as long as a run by an event may.
    /// </summary>
    private static async Task<string?> StartNextAsync(FleetoDbContext db, AutomationTask task, TaskPlan plan, AutomationTaskStep next, AutomationTaskRun run,
        AutomationTaskRunTarget target, LicenseStatus license, List<Job> started, DateTime now, CancellationToken cancellationToken)
    {
        var label = AutomationTasks.StepLabel(next.Position, next.Kind, plan.ScriptOf(next)?.Name);
        var endpoints = await AutomationTaskTargets.LoadAsync(db, task, plan, license, [target.EndpointId], cancellationToken);
        if (endpoints.Count == 0)
        {
            return $"{Capitalize(label)} did not start: {NoLongerAppliesReason}";
        }

        if (endpoints[0].Problem is { } problem)
        {
            return $"{Capitalize(label)} did not start: {problem}";
        }

        var (job, reason) = await WriteStepJobAsync(db, task, plan, next, run, target, now + AutomationTasks.NextStepValidity(task), now, cancellationToken);
        if (job is null)
        {
            return $"{Capitalize(label)} did not start: {reason}";
        }

        started.Add(job);
        return null;
    }

    /// <summary>The jobs of a run on one endpoint, each judged by its step: the run succeeded when every step passed.</summary>
    private static async Task<(TaskTargetOutcome Outcome, IReadOnlyList<(AutomationTaskStep Step, Job Job)> Failed)> ResultOfRunAsync(FleetoDbContext db,
        TaskPlan plan, AutomationTaskRunTarget target, CancellationToken cancellationToken)
    {
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.TaskRunId == target.RunId && j.EndpointId == target.EndpointId)
            .OrderBy(j => j.CreatedAt).ToListAsync(cancellationToken);
        var failed = new List<(AutomationTaskStep, Job)>();
        foreach (var job in jobs)
        {
            if (plan.Find(job.TaskStepId) is not { } step)
            {
                continue;
            }

            if (!AutomationTasks.Passed(step, job.State, job.Result, job.ExitCode))
            {
                failed.Add((step, job));
            }
        }

        var outcome = jobs.Any(j => j.State == JobState.Cancelled) ? TaskTargetOutcome.Cancelled
            : failed.Count == 0 ? TaskTargetOutcome.Succeeded
            : TaskTargetOutcome.Failed;
        return (outcome, failed);
    }

    /// <summary>
    /// Opens or resolves the alert of the task on the endpoint for a run that ended. A run that succeeded resolves it. A run that
    /// failed opens one when the task asks for it, outside maintenance: for a failed, lost or refused job, for an expired job of a
    /// later step (the endpoint was there for the step before), or when <paramref name="stopReason"/> says the next step did not
    /// start. An expired first step is a missed run, not a failure.
    /// </summary>
    private static async Task AlertAsync(FleetoDbContext db, AutomationTask task, TaskPlan plan, AutomationTaskRunTarget target, AutomationTaskRun run,
        IReadOnlyDictionary<Guid, string> endpoints, HashSet<Guid> inMaintenance, List<AlertTransition> transitions, DateTime now,
        CancellationToken cancellationToken, string? stopReason)
    {
        var alertTarget = task.Id.ToString();
        var open = await db.Alerts.FirstOrDefaultAsync(a => a.EndpointId == target.EndpointId && a.Kind == AlertKind.Task &&
                                                            a.Target == alertTarget && a.State != AlertState.Resolved, cancellationToken)
                   ?? db.Alerts.Local.FirstOrDefault(a => a.EndpointId == target.EndpointId && a.Kind == AlertKind.Task &&
                                                          a.Target == alertTarget && a.State != AlertState.Resolved);
        var (outcome, failed) = await ResultOfRunAsync(db, plan, target, cancellationToken);
        if (stopReason is null && outcome == TaskTargetOutcome.Succeeded)
        {
            if (open is not null)
            {
                Resolve(open, ResolvedReason, transitions, now);
            }

            return;
        }

        var alertable = failed.LastOrDefault(f => AutomationTasks.IsFailure(f.Job.State) || (f.Job.State == JobState.Expired && f.Step.Position > 1));
        if ((stopReason is null && alertable.Job is null) || !task.AlertOnFailure || open is not null || inMaintenance.Contains(target.EndpointId))
        {
            return;
        }

        var hostname = endpoints.GetValueOrDefault(target.EndpointId, "the endpoint");
        var title = alertable.Job is { } job && stopReason is null
            ? AutomationTasks.FailureTitle(task.Name, hostname, job.State, job.ExitCode, job.RefusalReason ?? job.Error, plan.Label(alertable.Step),
                alertable.Step.Kind == TaskStepKind.Restart)
            : Cut($"Task {task.Name} stopped on {hostname}: {stopReason} Open the run on the task to see its output.", 500);
        var detail = alertable.Job is { } failedJob ? failedJob.RefusalReason ?? failedJob.Error : stopReason;
        var alert = new Alert
        {
            Id = Guid.NewGuid(), ClientId = target.ClientId, EndpointId = target.EndpointId, Kind = AlertKind.Task,
            Target = alertTarget, Severity = task.AlertSeverity, State = AlertState.Open, Title = title,
            Detail = Cut($"Task {task.Name}, run {run.Id}. {detail}".Trim(), 2000),
            OpenedAt = now, UpdatedAt = now
        };
        db.Alerts.Add(alert);
        transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Opened));
    }

    private static void Finish(AutomationTaskRunTarget target, TaskTargetOutcome outcome, string? reason, DateTime now)
    {
        target.FinishedAt = now;
        target.Outcome = outcome;
        target.OutcomeHandledAt ??= now;
        target.UpdatedAt = now;
        if (reason is not null)
        {
            target.Reason = reason;
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static void Resolve(Alert alert, string reason, List<AlertTransition> transitions, DateTime now)
    {
        alert.State = AlertState.Resolved;
        alert.ResolvedAt = now;
        alert.ResolvedReason = reason;
        alert.UpdatedAt = now;
        transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Resolved));
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
