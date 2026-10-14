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
/// <item><b>Starts jobs</b>: a target whose turn came (the run plus its share of the random delay) gets a job without a user
/// and a signing request, unless the previous job of the task still runs there (skipped, or waiting until it ended), the
/// endpoint can no longer run it, or the run expired. fleeto-signer decides again before it signs.</item>
/// <item><b>Follows the outcome</b>: a job that failed opens an alert of kind Task on its endpoint when the task asks for it,
/// and a successful one resolves it.</item>
/// </list>
/// Runs started by hand are written by web with the user as initiator; this service only follows their outcome.
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
        var started = await StartJobsAsync(cancellationToken);
        var followed = await FollowOutcomesAsync(cancellationToken);
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
        var scriptIds = tasks.Select(t => t.ScriptId).Distinct().ToList();
        var scripts = await db.Scripts.AsNoTracking().Where(s => scriptIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
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

            if (!scripts.TryGetValue(task.ScriptId, out var script))
            {
                continue;
            }

            written += await StartScheduledAsync(db, task, script, state, license, now, cancellationToken);
            written += await StartForAlertsAsync(db, task, script, state, license, now, cancellationToken);
            written += await StartForManagedAsync(db, task, script, state, license, now, cancellationToken);
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

    private static async Task<int> StartScheduledAsync(FleetoDbContext db, AutomationTask task, Script script, AutomationTaskState state,
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

        var endpoints = await AutomationTaskTargets.LoadAsync(db, task, script, license, null, cancellationToken);
        return await AddRunsAsync(db, task, TaskTrigger.Schedule, occurrence, null, endpoints, state.NextOccurrenceAt, now, cancellationToken);
    }

    private static async Task<int> StartForAlertsAsync(FleetoDbContext db, AutomationTask task, Script script, AutomationTaskState state,
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

            var endpoints = await AutomationTaskTargets.LoadAsync(db, task, script, license, [alert.EndpointId], cancellationToken);
            written += await AddRunsAsync(db, task, TaskTrigger.CheckAlert, alert.OpenedAt, names.GetValueOrDefault(alert.CheckDefinitionId!.Value),
                endpoints, null, now, cancellationToken);
        }

        return written;
    }

    private static async Task<int> StartForManagedAsync(FleetoDbContext db, AutomationTask task, Script script, AutomationTaskState state,
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

            var endpoints = await AutomationTaskTargets.LoadAsync(db, task, script, license, [endpoint.Id], cancellationToken);
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

    /// <summary>Writes the jobs of targets whose turn came. Returns the number of targets handled.</summary>
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
        var scriptIds = tasks.Values.Select(t => t.ScriptId).Distinct().ToList();
        var scripts = await db.Scripts.AsNoTracking().Where(s => scriptIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        var runIds = targets.Select(t => t.RunId).Distinct().ToList();
        var runs = await db.AutomationTaskRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
        var started = new List<Job>();

        foreach (var target in targets)
        {
            target.UpdatedAt = now;
            if (!tasks.TryGetValue(target.AutomationTaskId, out var task) || !task.Enabled || !scripts.TryGetValue(task.ScriptId, out var script) ||
                !runs.TryGetValue(target.RunId, out var run))
            {
                Skip(target, TaskOffReason);
                continue;
            }

            var active = await db.Jobs.AsNoTracking()
                .AnyAsync(j => j.TaskId == task.Id && j.EndpointId == target.EndpointId &&
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

            var endpoints = await AutomationTaskTargets.LoadAsync(db, task, script, license, [target.EndpointId], cancellationToken);
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

            var since = now - AutomationTasks.AutomaticSigningInterval;
            if (await db.Jobs.AsNoTracking().AnyAsync(j => j.TaskId == task.Id && j.EndpointId == target.EndpointId &&
                                                           j.InitiatedByUserId == AutomationTasks.AutomaticInitiator && j.CreatedAt > since, cancellationToken))
            {
                Skip(target, RecentReason);
                continue;
            }

            var version = script.CurrentVersionId is { } versionId
                ? await db.ScriptVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
                : null;
            if (version is null)
            {
                Skip(target, NoVersionReason);
                continue;
            }

            var job = new Job
            {
                Id = Guid.NewGuid(), ClientId = target.ClientId, EndpointId = target.EndpointId, BatchId = run.Id, Type = JobType.Script,
                ScriptId = script.Id, ScriptVersionId = version.Id, ScriptName = script.Name, ScriptVersionNumber = version.Number,
                Language = script.Language, ScriptSha256 = version.Sha256, TimeoutSeconds = version.TimeoutSeconds,
                // The signer sets the cap of the endpoint's policy; this is the value the job starts with.
                MaxOutputBytes = ScriptRules.DefaultMaxOutputBytes, RunAs = task.RunAs, TaskId = task.Id, TaskRunId = run.Id,
                CreatedAt = now, ValidUntil = Min(target.ValidUntil, now + ScriptRules.MaxValidity),
                InitiatedByUserId = AutomationTasks.AutomaticInitiator, InitiatedByName = run.InitiatedByName
            };

            // The job first: the database lets the workers ask for a signature only for a job of theirs that already exists.
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                db.Jobs.Add(job);
                await db.SaveChangesAsync(cancellationToken);
                target.State = TaskTargetState.Started;
                target.JobId = job.Id;
                db.SigningRequests.Add(new SigningRequest
                {
                    Id = Guid.NewGuid(), ClientId = job.ClientId, Kind = SigningRequestKind.Job, SubjectId = job.Id, Payload = [],
                    RequestedBy = "workers:tasks", CreatedAt = now
                });
                db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.JobCreated, "Job", job.Id.ToString(), job.ClientId,
                    AuditActorType.System, "fleeto-workers", "fleeto-workers", new
                    {
                        EndpointId = job.EndpointId, Task = task.Name, TaskId = task.Id, RunId = run.Id, Trigger = run.Trigger.ToString(),
                        job.ScriptName, job.ScriptVersionNumber, job.ScriptSha256, job.ValidUntil, RunAs = job.RunAs.ToString()
                    }), now));
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
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

    private static void Skip(AutomationTaskRunTarget target, string reason)
    {
        target.State = TaskTargetState.Skipped;
        target.Reason = reason;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // 3. Outcomes
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>Opens or resolves the alert of a task on an endpoint for every job of a run that ended. Returns the number handled.</summary>
    public async Task<int> FollowOutcomesAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var ended = await (
                from t in db.AutomationTaskRunTargets
                join j in db.Jobs on t.JobId equals j.Id
                where t.JobId != null && t.OutcomeHandledAt == null &&
                      j.State != JobState.PendingSignature && j.State != JobState.Queued && j.State != JobState.Running
                orderby j.CompletedAt
                select new { Target = t, j.State, j.ExitCode, Reason = j.RefusalReason ?? j.Error, j.CompletedAt })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var transitions = new List<AlertTransition>();
        if (ended.Count > 0)
        {
            var taskIds = ended.Select(e => e.Target.AutomationTaskId).Distinct().ToList();
            var tasks = await db.AutomationTasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
            var endpointIds = ended.Select(e => e.Target.EndpointId).Distinct().ToList();
            var endpoints = await db.Endpoints.AsNoTracking().Where(e => endpointIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.Hostname, cancellationToken);
            var inMaintenance = (await db.Endpoints.AsNoTracking()
                    .Where(e => endpointIds.Contains(e.Id))
                    .Where(MaintenanceRules.EndpointInMaintenance(now, db.MaintenanceWindowOccurrences, EffectivePolicies.Query(db)))
                    .Select(e => e.Id)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            // The newest outcome per task and endpoint decides; older ones in the same pass only get marked.
            foreach (var outcome in ended.OrderBy(e => e.CompletedAt))
            {
                outcome.Target.OutcomeHandledAt = now;
                outcome.Target.UpdatedAt = now;
                if (!tasks.TryGetValue(outcome.Target.AutomationTaskId, out var task) || outcome.State is JobState.Expired or JobState.Cancelled)
                {
                    continue;
                }

                var target = task.Id.ToString();
                var open = await db.Alerts.FirstOrDefaultAsync(a => a.EndpointId == outcome.Target.EndpointId && a.Kind == AlertKind.Task &&
                                                                    a.Target == target && a.State != AlertState.Resolved, cancellationToken)
                           ?? db.Alerts.Local.FirstOrDefault(a => a.EndpointId == outcome.Target.EndpointId && a.Kind == AlertKind.Task &&
                                                                  a.Target == target && a.State != AlertState.Resolved);
                if (outcome.State == JobState.Succeeded)
                {
                    if (open is not null)
                    {
                        Resolve(open, ResolvedReason, transitions, now);
                    }

                    continue;
                }

                if (!AutomationTasks.IsFailure(outcome.State) || !task.AlertOnFailure || open is not null ||
                    inMaintenance.Contains(outcome.Target.EndpointId))
                {
                    continue;
                }

                var hostname = endpoints.GetValueOrDefault(outcome.Target.EndpointId, "the endpoint");
                var alert = new Alert
                {
                    Id = Guid.NewGuid(), ClientId = outcome.Target.ClientId, EndpointId = outcome.Target.EndpointId, Kind = AlertKind.Task,
                    Target = target, Severity = task.AlertSeverity, State = AlertState.Open,
                    Title = AutomationTasks.FailureTitle(task.Name, hostname, outcome.State, outcome.ExitCode, outcome.Reason),
                    Detail = Cut($"Task {task.Name}, run {outcome.Target.RunId}. {outcome.Reason}".Trim(), 2000),
                    OpenedAt = now, UpdatedAt = now
                };
                db.Alerts.Add(alert);
                transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Opened));
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

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var released = await AlertCleanup.ResolveNotManagedAsync(db, AlertKind.Task, license, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AlertCleanup.PublishAsync(_bus, transitions.Select(t => t.AlertId).Concat(released), cancellationToken);
        return ended.Count;
    }

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
