using System.Globalization;
using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>
/// One step as a user sets it (0.6.0, steps): a library script with its account and options, or a restart with its wait.
/// <see cref="Id"/> is that of an existing step, or null for a new one. What the exit codes of a script mean is set on the script.
/// </summary>
public sealed record TaskStepInput(Guid? Id, TaskStepKind Kind, Guid? ScriptId, JobRunAs RunAs, bool ContinueOnFailure, int? RestartWaitMinutes)
{
    public static TaskStepInput Script(Guid scriptId, JobRunAs runAs = JobRunAs.Service) => new(null, TaskStepKind.Script, scriptId, runAs, false, null);

    public static TaskStepInput Restart(int waitMinutes = RestartRules.DefaultWaitMinutes) =>
        new(null, TaskStepKind.Restart, null, JobRunAs.Service, false, waitMinutes);
}

/// <summary>What a user sets on a task (0.6.0). <see cref="ClientId"/> counts only when the task is created.</summary>
public sealed record TaskInput(
    string? Name,
    string? Description,
    Guid? ClientId,
    IReadOnlyList<TaskStepInput> Steps,
    CheckAppliesTo AppliesTo,
    bool Enabled,
    TaskScheduleKind ScheduleKind,
    string TimeZone,
    DateOnly StartDate,
    int StartMinute,
    DateOnly? EndDate,
    int Every,
    WeekDays WeekDays,
    int MonthDay,
    int? RepeatMinutes,
    IReadOnlyList<Guid> TriggerCheckIds,
    bool OnManaged,
    int RandomDelayMinutes,
    bool CatchUp,
    TaskOverlap Overlap,
    bool AlertOnFailure,
    AlertSeverity AlertSeverity);

/// <param name="StepSummary">The script of a task of one step, or "3 steps: A, Restart, B".</param>
public sealed record TaskListItem(Guid Id, string Name, Guid? ClientId, string? ClientCode, string StepSummary, string Schedule, string Triggers,
    bool Enabled, int Links, DateTime? NextRunAt, DateTime? LastRunAt);

/// <summary>Where a task is linked: a client, a site or an endpoint, with its name.</summary>
public sealed record TaskLinkView(LinkLevel Level, Guid Id, Guid ClientId, string Name);

/// <summary>A check of a monitoring template whose alert can start a task.</summary>
public sealed record TaskCheckOption(Guid Id, string Name, string TemplateName);

/// <summary>A script a task can run: global, or of the task's client.</summary>
public sealed record TaskScriptOption(Guid Id, string Name, ScriptLanguage Language, string? ClientCode);

/// <summary>A step of a task as the task page shows it.</summary>
public sealed record TaskStepView(Guid Id, int Position, TaskStepKind Kind, Guid? ScriptId, string? ScriptName, ScriptLanguage? Language, JobRunAs RunAs,
    bool ContinueOnFailure, int? RestartWaitMinutes)
{
    public string Label => Kind == TaskStepKind.Restart ? "Restart the endpoint" : ScriptName ?? "Script not available";
}

public sealed record TaskDetail(Guid Id, TaskInput Input, string? ClientCode, IReadOnlyList<TaskStepView> Steps,
    IReadOnlyList<TaskLinkView> Links, IReadOnlyList<TaskCheckOption> TriggerChecks, DateTime? NextRunAt, string UpdatedByName, DateTime UpdatedAt,
    IReadOnlyList<TaskTemplateLinkView> Templates);

/// <summary>A monitoring template a task is linked to, or can be: the task runs on every endpoint that gets the template.</summary>
public sealed record TaskTemplateLinkView(Guid Id, string Name, string? ClientCode, CheckAppliesTo AppliesTo);

public sealed record TaskRunItem(Guid Id, Guid ClientId, string? ClientCode, TaskTrigger Trigger, DateTime OccurrenceAt, DateTime CreatedAt,
    string InitiatedByName, string? CheckName, int Endpoints, int Waiting, int Running, int Succeeded, int Failed, int Skipped);

/// <summary>
/// One endpoint of a run: the target and the job of its current (or last) step, with the start of its output, how the run ended
/// there, and every step that ran (0.6.0, steps). <see cref="HasWarning"/>: a step ended with an exit code its script lists as a warning.
/// </summary>
public sealed record TaskRunTargetItem(Guid EndpointId, string Hostname, TaskTargetState State, DateTime DueAt, string? Reason, Guid? JobId,
    JobState? JobState, int? ExitCode, string? Problem, DateTime? StartedAt, DateTime? CompletedAt, string OutputPreview, long OutputBytes,
    JobResult? JobResult, TaskTargetOutcome? Outcome = null, DateTime? FinishedAt = null, bool HasWarning = false)
{
    /// <summary>The steps of the run on this endpoint that have a job, in order.</summary>
    public IReadOnlyList<TaskRunStepItem> Steps { get; init; } = [];

    /// <summary>How many steps the task has now.</summary>
    public int StepCount { get; init; } = 1;
}

/// <summary>
/// One step of a run on one endpoint: its job, what its exit code meant by the rules of its script, and whether the step passed.
/// </summary>
public sealed record TaskRunStepItem(Guid JobId, int Position, string Name, TaskStepKind Kind, JobState State, JobResult? Result, int? ExitCode,
    bool Passed, string? Problem, DateTime? StartedAt, DateTime? CompletedAt, string OutputPreview, long OutputBytes, ExitStatus? ExitStatus = null);

/// <summary>
/// A task that applies to one endpoint (0.6.0, the Tasks tab): where it is linked, when it runs next and how its last run on the
/// endpoint ended. <see cref="Problem"/> says why the endpoint cannot run it now.
/// </summary>
public sealed record EndpointTaskItem(Guid Id, string Name, string StepSummary, bool Enabled, IReadOnlyList<LinkLevel> LinkedThrough,
    IReadOnlyList<string> LinkedTemplates, string Schedule, string Triggers, DateTime? NextRunAt, int RandomDelayMinutes, string? Problem,
    EndpointTaskRunItem? LastRun)
{
    /// <summary>Linked to this endpoint itself, so it can be unlinked here.</summary>
    public bool LinkedToEndpoint => LinkedThrough.Contains(LinkLevel.Endpoint);
}

/// <summary>A task that can be linked to an endpoint from its Tasks tab.</summary>
public sealed record TaskOption(Guid Id, string Name, string? ClientCode, string StepSummary);

/// <summary>One run of a task on one endpoint: why it started, and the job with its result once it was written.</summary>
public sealed record EndpointTaskRunItem(Guid RunId, Guid TaskId, string TaskName, TaskTrigger Trigger, string InitiatedByName, string? CheckName,
    DateTime CreatedAt, TaskRunTargetItem Target)
{
    /// <summary>When it ran, or when it is or was due.</summary>
    public DateTime At => Target.StartedAt ?? Target.DueAt;
}

/// <summary>The Tasks tab of an endpoint: the tasks that apply to it and the newest runs on it.</summary>
public sealed record EndpointTasksView(IReadOnlyList<EndpointTaskItem> Tasks, IReadOnlyList<EndpointTaskRunItem> History);

/// <summary>The clients, sites and endpoints a task can be linked to, within the caller's groups.</summary>
public sealed record TaskLinkChoices(IReadOnlyList<TaskLinkView> Clients, IReadOnlyList<TaskLinkView> Sites, IReadOnlyList<TaskLinkView> Endpoints);

/// <summary>
/// Tasks in web (0.6.0): library scripts that run by themselves, linked to clients, sites and endpoints. Web writes the task and
/// its links and starts a run by hand as the user who presses Run now; the workers start every other run, and fleeto-signer
/// decides again before anything is signed (ARCHITECTURE.md §4, Tasks). A task of a client is of the client as a whole; a global
/// task is changed only by a user with access to everything it is linked to.
/// </summary>
public sealed class TaskService
{
    /// <summary>Characters of output shown per endpoint in a run; the rest is in the job.</summary>
    public const int PreviewLength = 300;

    /// <summary>Output per endpoint and stream in the CSV export.</summary>
    public const int ExportStdoutBytes = 64 * 1024;

    public const int ExportStderrBytes = 16 * 1024;

    /// <summary>Endpoints offered per client when linking; a search narrows them.</summary>
    public const int MaxLinkChoices = 500;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly JobService _jobs;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;

    public TaskService(IFleetoDbContextFactory dbFactory, LicenseService licenses, JobService jobs, INotificationBus bus, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _jobs = jobs;
        _bus = bus;
        _time = time;
    }

    /// <summary>The tasks the caller sees; with <paramref name="monitoringTemplateId"/> only those linked to that monitoring template.</summary>
    public async Task<IReadOnlyList<TaskListItem>> ListAsync(Caller caller, Guid? monitoringTemplateId = null, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksView);
        await using var db = _dbFactory.Create(caller.Scope);
        var query = db.AutomationTasks.AsNoTracking();
        if (monitoringTemplateId is { } templateId)
        {
            query = query.Where(t => db.MonitoringTemplateAutomationTasks.Any(l => l.AutomationTaskId == t.Id && l.MonitoringTemplateId == templateId));
        }

        var tasks = await query.OrderBy(t => t.Name).ToListAsync(cancellationToken);
        var ids = tasks.Select(t => t.Id).ToList();
        var clientCodes = await db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        var plans = await TaskPlan.LoadManyAsync(db, tasks, cancellationToken);
        var states = await db.AutomationTaskStates.AsNoTracking().Where(s => ids.Contains(s.AutomationTaskId))
            .ToDictionaryAsync(s => s.AutomationTaskId, s => s.NextOccurrenceAt, cancellationToken);
        var links = (await db.ClientAutomationTasks.Where(l => ids.Contains(l.AutomationTaskId)).Select(l => l.AutomationTaskId).ToListAsync(cancellationToken))
            .Concat(await db.SiteAutomationTasks.Where(l => ids.Contains(l.AutomationTaskId)).Select(l => l.AutomationTaskId).ToListAsync(cancellationToken))
            .Concat(await db.EndpointAutomationTasks.Where(l => ids.Contains(l.AutomationTaskId)).Select(l => l.AutomationTaskId).ToListAsync(cancellationToken))
            .Concat(await db.MonitoringTemplateAutomationTasks.Where(l => ids.Contains(l.AutomationTaskId)).Select(l => l.AutomationTaskId).ToListAsync(cancellationToken))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var lastRuns = (await db.AutomationTaskRuns.AsNoTracking().Where(r => ids.Contains(r.AutomationTaskId))
                .GroupBy(r => r.AutomationTaskId).Select(g => new { g.Key, Last = g.Max(r => r.CreatedAt) }).ToListAsync(cancellationToken))
            .ToDictionary(r => r.Key, r => r.Last);
        return tasks.Select(t => new TaskListItem(t.Id, t.Name, t.ClientId, t.ClientId is { } c ? clientCodes.GetValueOrDefault(c) : null,
            StepSummary(plans[t.Id]), AutomationTasks.Describe(t), Triggers(t), t.Enabled,
            links.GetValueOrDefault(t.Id), t.Enabled ? states.GetValueOrDefault(t.Id) : null,
            lastRuns.TryGetValue(t.Id, out var last) ? last : null)).ToList();
    }

    public async Task<TaskDetail?> GetAsync(Caller caller, Guid taskId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksView);
        await using var db = _dbFactory.Create(caller.Scope);
        var task = await db.AutomationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var plan = await TaskPlan.LoadAsync(db, task, cancellationToken);
        var clientCode = task.ClientId is { } clientId
            ? await db.Clients.Where(c => c.Id == clientId).Select(c => c.Code).SingleOrDefaultAsync(cancellationToken)
            : null;
        var checkIds = task.TriggerCheckIds;
        var checks = await (
                from c in db.CheckDefinitions.IgnoreQueryFilters().AsNoTracking()
                join t in db.MonitoringTemplates.IgnoreQueryFilters() on c.MonitoringTemplateId equals t.Id
                where checkIds.Contains(c.Id)
                select new TaskCheckOption(c.Id, c.Name, t.Name))
            .ToListAsync(cancellationToken);
        var next = await db.AutomationTaskStates.AsNoTracking().Where(s => s.AutomationTaskId == taskId)
            .Select(s => s.NextOccurrenceAt).SingleOrDefaultAsync(cancellationToken);
        return new TaskDetail(task.Id, ToInput(task, plan), clientCode, StepViews(plan),
            await LinksAsync(db, taskId, cancellationToken), checks.OrderBy(c => c.TemplateName).ThenBy(c => c.Name).ToList(),
            task.Enabled ? next : null, task.UpdatedByName, task.UpdatedAt, await TemplateLinksAsync(db, taskId, cancellationToken));
    }

    /// <summary>The scripts a task of this client (null: global) can run: global ones, and the client's own for a client's task.</summary>
    public async Task<IReadOnlyList<TaskScriptOption>> GetScriptChoicesAsync(Caller caller, Guid? clientId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksView);
        await using var db = _dbFactory.Create(caller.Scope);
        return await db.Scripts.AsNoTracking()
            .Where(s => s.CurrentVersionId != null && (s.ClientId == null || (clientId != null && s.ClientId == clientId)))
            .OrderBy(s => s.Name)
            .Select(s => new TaskScriptOption(s.Id, s.Name, s.Language, db.Clients.Where(c => c.Id == s.ClientId).Select(c => c.Code).FirstOrDefault()))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The checks of monitoring templates whose alert can start a task of this client (null: global templates only).</summary>
    public async Task<IReadOnlyList<TaskCheckOption>> GetCheckChoicesAsync(Caller caller, Guid? clientId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksView);
        await using var db = _dbFactory.Create(caller.Scope);
        return await (
                from c in db.CheckDefinitions.AsNoTracking()
                join t in db.MonitoringTemplates on c.MonitoringTemplateId equals t.Id
                where t.ClientId == null || (clientId != null && t.ClientId == clientId)
                orderby t.Name, c.Name
                select new TaskCheckOption(c.Id, c.Name, t.Name))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Creates a task (taskId null) or changes one, with its steps. Problems come from <see cref="AutomationTasks.Validate"/>,
    /// <see cref="AutomationTasks.ValidateSteps"/> and the checks here: every script of a step is in the library with a version,
    /// global or of the task's client, and a restart step needs the permission Restart endpoints.
    /// </summary>
    public async Task<ServiceResult<Guid>> SaveAsync(Caller caller, Guid? taskId, TaskInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.TasksEdit))
        {
            return ServiceResult<Guid>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime;
        AutomationTask task;
        string? stepsBefore = null;
        if (taskId is { } id)
        {
            var existing = await db.AutomationTasks.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (existing is null)
            {
                return ServiceResult<Guid>.NotFound("task");
            }

            if (await TemplateAccess.ProblemAsync(db, caller, TemplateKind.Task, existing.Id, existing.ClientId, false, cancellationToken) is { } accessProblem)
            {
                return ServiceResult<Guid>.Fail(accessProblem);
            }

            task = existing;
            stepsBefore = StepSummary(await TaskPlan.LoadAsync(db, task, cancellationToken));
        }
        else
        {
            if (input.ClientId is { } clientId && !await db.Clients.AnyAsync(c => c.Id == clientId, cancellationToken))
            {
                return ServiceResult<Guid>.NotFound("client");
            }

            if (TemplateAccess.CreateProblem(db, caller, input.ClientId) is { } createProblem)
            {
                return ServiceResult<Guid>.Fail(createProblem);
            }

            task = new AutomationTask { Id = Guid.NewGuid(), ClientId = input.ClientId, CreatedAt = now };
        }

        var stepInputs = input.Steps ?? [];
        if (stepInputs.Any(s => s.Kind == TaskStepKind.Restart) && !caller.Has(Permissions.EndpointsRestart))
        {
            return ServiceResult<Guid>.Fail("A restart step needs the permission Restart endpoints. Ask an admin for it, or remove the restart step.");
        }

        var scriptIds = stepInputs.Where(s => s.Kind == TaskStepKind.Script && s.ScriptId is not null).Select(s => s.ScriptId!.Value).Distinct().ToList();
        var scripts = await db.Scripts.AsNoTracking().Where(s => scriptIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        for (var i = 0; i < stepInputs.Count; i++)
        {
            if (stepInputs[i].Kind != TaskStepKind.Script)
            {
                continue;
            }

            var prefix = stepInputs.Count > 1 ? $"Step {i + 1}: " : string.Empty;
            if (stepInputs[i].ScriptId is not { } scriptId || !scripts.TryGetValue(scriptId, out var script) || script.CurrentVersionId is null)
            {
                return ServiceResult<Guid>.Fail(prefix + "Choose a script from the library that has a version.");
            }

            if (script.ClientId is { } scriptClient && scriptClient != task.ClientId)
            {
                return ServiceResult<Guid>.Fail(prefix + (task.ClientId is null
                    ? "A task for every client can only run a script for every client. Choose another script, or make the task for that client."
                    : "The script belongs to another client. Choose a script for every client or one of this client."));
            }
        }

        var checkIds = input.TriggerCheckIds.Distinct().ToList();
        var ownerId = task.ClientId;
        var validChecks = await (
                from c in db.CheckDefinitions.AsNoTracking()
                join t in db.MonitoringTemplates on c.MonitoringTemplateId equals t.Id
                where checkIds.Contains(c.Id) && (t.ClientId == null || (ownerId != null && t.ClientId == ownerId))
                select c.Id)
            .CountAsync(cancellationToken);
        if (validChecks != checkIds.Count)
        {
            return ServiceResult<Guid>.Fail("A chosen check no longer exists or belongs to a monitoring template of another client. Choose the checks again.");
        }

        var existingSteps = taskId is null
            ? []
            : await db.AutomationTaskSteps.Where(s => s.AutomationTaskId == task.Id).ToListAsync(cancellationToken);
        var steps = new List<AutomationTaskStep>();
        for (var i = 0; i < stepInputs.Count; i++)
        {
            var stepInput = stepInputs[i];
            var step = stepInput.Id is { } stepId ? existingSteps.FirstOrDefault(s => s.Id == stepId) : null;
            step ??= new AutomationTaskStep { Id = Guid.NewGuid(), AutomationTaskId = task.Id, ClientId = task.ClientId };
            step.Kind = stepInput.Kind;
            step.ScriptId = stepInput.Kind == TaskStepKind.Script ? stepInput.ScriptId : null;
            step.RunAs = stepInput.Kind == TaskStepKind.Script ? stepInput.RunAs : JobRunAs.Service;
            step.ContinueOnFailure = stepInput.ContinueOnFailure;
            // The exit codes a step counted as success moved to its script (0.6.0); a restart never had any.
            if (stepInput.Kind == TaskStepKind.Restart)
            {
                step.SuccessExitCodes = [];
            }

            step.RestartWaitMinutes = stepInput.Kind == TaskStepKind.Restart ? stepInput.RestartWaitMinutes : null;
            steps.Add(step);
        }

        var before = taskId is null ? null : Snapshot(task, stepsBefore);
        Apply(task, input, checkIds);
        // The first script step is kept on the task itself, for a build without steps (TaskSteps migration).
        if (steps.FirstOrDefault(s => s.Kind == TaskStepKind.Script) is { ScriptId: { } firstScript } first)
        {
            task.ScriptId = firstScript;
            task.RunAs = first.RunAs;
        }

        task.UpdatedByName = Cut(caller.Name, 200);
        task.UpdatedAt = now;
        var problems = AutomationTasks.Validate(task).Concat(AutomationTasks.ValidateSteps(steps)).ToList();
        if (problems.Count > 0)
        {
            return ServiceResult<Guid>.Fail(string.Join(" ", problems));
        }

        var duplicate = await db.AutomationTasks.IgnoreQueryFilters()
            .AnyAsync(t => t.Id != task.Id && t.ClientId == task.ClientId && t.Name == task.Name, cancellationToken);
        if (duplicate)
        {
            return ServiceResult<Guid>.Fail($"A task named {task.Name} already exists there. Choose another name.");
        }

        var plan = TaskPlan.From(task, steps, scripts);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (taskId is null)
            {
                db.AutomationTasks.Add(task);
            }

            // Steps that stay move to a free position first, so a new order never meets the unique position half way.
            foreach (var removed in existingSteps.Where(e => steps.All(s => s.Id != e.Id)))
            {
                db.AutomationTaskSteps.Remove(removed);
            }

            var kept = existingSteps.Where(e => steps.Any(s => s.Id == e.Id)).ToList();
            foreach (var step in kept)
            {
                step.Position = 1000 + steps.IndexOf(step);
            }

            await db.SaveChangesAsync(cancellationToken);
            for (var i = 0; i < steps.Count; i++)
            {
                steps[i].Position = i + 1;
                if (!kept.Contains(steps[i]))
                {
                    db.AutomationTaskSteps.Add(steps[i]);
                }
            }

            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(taskId is null ? AuditActions.TaskCreated : AuditActions.TaskUpdated, "Task",
                task.Id.ToString(), task.ClientId, new
                {
                    task.Name, Steps = StepSummary(plan), AppliesTo = task.AppliesTo.ToString(), task.Enabled,
                    Schedule = AutomationTasks.Describe(task), Triggers = Triggers(task), task.RandomDelayMinutes, task.CatchUp,
                    Overlap = task.Overlap.ToString(), task.AlertOnFailure, Before = before
                }), now));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await PublishAsync(task.Id, cancellationToken);
        return ServiceResult<Guid>.Ok(task.Id);
    }

    public async Task<ServiceResult> SetEnabledAsync(Caller caller, Guid taskId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.TasksEdit))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var task = await db.AutomationTasks.SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            return ServiceResult.NotFound("task");
        }

        if (await TemplateAccess.ProblemAsync(db, caller, TemplateKind.Task, task.Id, task.ClientId, false, cancellationToken) is { } accessProblem)
        {
            return ServiceResult.Fail(accessProblem);
        }

        if (task.Enabled == enabled)
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        task.Enabled = enabled;
        task.UpdatedByName = Cut(caller.Name, 200);
        task.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.TaskUpdated, "Task", task.Id.ToString(), task.ClientId,
            new { task.Name, Change = enabled ? "enabled" : "disabled" }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(task.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Deletes a task with its links and runs; its jobs stay in the history of their endpoints, and its alerts resolve.</summary>
    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid taskId, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.TasksEdit))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var task = await db.AutomationTasks.SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            return ServiceResult.NotFound("task");
        }

        if (await TemplateAccess.ProblemAsync(db, caller, TemplateKind.Task, task.Id, task.ClientId, false, cancellationToken) is { } accessProblem)
        {
            return ServiceResult.Fail(accessProblem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.AutomationTasks.Remove(task);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.TaskDeleted, "Task", task.Id.ToString(), task.ClientId, new { task.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(task.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Links
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>The clients, sites and endpoints the caller can link a task of this client (null: global) to.</summary>
    public async Task<TaskLinkChoices> GetLinkChoicesAsync(Caller caller, Guid? taskClientId, Guid? clientId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksEdit);
        await using var db = _dbFactory.Create(caller.Scope);
        var clients = await db.Clients.AsNoTracking()
            .Where(c => taskClientId == null || c.Id == taskClientId)
            .OrderBy(c => c.Code)
            .Select(c => new TaskLinkView(LinkLevel.Client, c.Id, c.Id, c.Code + " " + c.Name))
            .ToListAsync(cancellationToken);
        if (clientId is not { } chosen || (taskClientId is { } owner && owner != chosen))
        {
            return new TaskLinkChoices(clients, [], []);
        }

        var sites = await db.Sites.AsNoTracking().Where(s => s.ClientId == chosen).OrderBy(s => s.Name)
            .Select(s => new TaskLinkView(LinkLevel.Site, s.Id, s.ClientId, s.Name)).ToListAsync(cancellationToken);
        var endpoints = await db.Endpoints.AsNoTracking().Where(e => e.ClientId == chosen && e.Source == EndpointSource.Agent).OrderBy(e => e.Hostname)
            .Take(MaxLinkChoices)
            .Select(e => new TaskLinkView(LinkLevel.Endpoint, e.Id, e.ClientId, e.Hostname)).ToListAsync(cancellationToken);
        // Every client the caller sees is offered to find its sites and endpoints; linking the client itself needs all of it.
        return new TaskLinkChoices(clients, sites.Where(s => caller.HasWholeSite(s.ClientId, s.Id)).ToList(), endpoints);
    }

    /// <summary>Links a task to a client, site or endpoint. A client needs the whole client, a site the whole site.</summary>
    public async Task<ServiceResult> LinkAsync(Caller caller, Guid taskId, LinkLevel level, Guid id, CancellationToken cancellationToken = default) =>
        await ChangeLinkAsync(caller, taskId, level, id, true, cancellationToken);

    public async Task<ServiceResult> UnlinkAsync(Caller caller, Guid taskId, LinkLevel level, Guid id, CancellationToken cancellationToken = default) =>
        await ChangeLinkAsync(caller, taskId, level, id, false, cancellationToken);

    /// <summary>
    /// The monitoring templates a task can be linked to: global ones, and those of the task's client for a client's task; those
    /// already linked are left out.
    /// </summary>
    public async Task<IReadOnlyList<TaskTemplateLinkView>> GetTemplateChoicesAsync(Caller caller, Guid taskId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksEdit);
        await using var db = _dbFactory.Create(caller.Scope);
        var task = await db.AutomationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            return [];
        }

        var taskClientId = task.ClientId;
        var linked = db.MonitoringTemplateAutomationTasks.Where(l => l.AutomationTaskId == taskId).Select(l => l.MonitoringTemplateId);
        return await (
                from t in db.MonitoringTemplates.AsNoTracking()
                where (t.ClientId == null || taskClientId == null || t.ClientId == taskClientId) && !linked.Contains(t.Id)
                join c in db.Clients on t.ClientId equals c.Id into owners
                from c in owners.DefaultIfEmpty()
                orderby t.Name
                select new TaskTemplateLinkView(t.Id, t.Name, c.Code, t.AppliesTo))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The tasks that can be linked to a monitoring template from its page: global ones, and for a template of a client also that
    /// client's; for a global template also the tasks of a client, which then run only on that client's endpoints. Those already
    /// linked are left out.
    /// </summary>
    public async Task<IReadOnlyList<TaskOption>> GetTaskChoicesForTemplateAsync(Caller caller, Guid templateId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksEdit);
        await using var db = _dbFactory.Create(caller.Scope);
        var template = await db.MonitoringTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null)
        {
            return [];
        }

        var templateClientId = template.ClientId;
        var linked = db.MonitoringTemplateAutomationTasks.Where(l => l.MonitoringTemplateId == templateId).Select(l => l.AutomationTaskId);
        return await OptionsAsync(db, db.AutomationTasks.AsNoTracking()
            .Where(t => (t.ClientId == null || templateClientId == null || t.ClientId == templateClientId) && !linked.Contains(t.Id)), cancellationToken);
    }

    private static async Task<IReadOnlyList<TaskOption>> OptionsAsync(FleetoDbContext db, IQueryable<AutomationTask> query, CancellationToken cancellationToken)
    {
        var tasks = await query.OrderBy(t => t.Name).ToListAsync(cancellationToken);
        var plans = await TaskPlan.LoadManyAsync(db, tasks, cancellationToken);
        var codes = await db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        return tasks.Select(t => new TaskOption(t.Id, t.Name, t.ClientId is { } c ? codes.GetValueOrDefault(c) : null, StepSummary(plans[t.Id]))).ToList();
    }

    /// <summary>
    /// Links a task to a monitoring template (0.6.0): it then runs on every endpoint that gets the template, also where the
    /// template is linked later. That changes what the template does wherever it is used, so it needs the permissions to edit
    /// tasks and monitoring templates and, for a template for every client, access to everything the template is used on.
    /// </summary>
    public async Task<ServiceResult> LinkTemplateAsync(Caller caller, Guid taskId, Guid templateId, CancellationToken cancellationToken = default) =>
        await ChangeTemplateLinkAsync(caller, taskId, templateId, true, cancellationToken);

    public async Task<ServiceResult> UnlinkTemplateAsync(Caller caller, Guid taskId, Guid templateId, CancellationToken cancellationToken = default) =>
        await ChangeTemplateLinkAsync(caller, taskId, templateId, false, cancellationToken);

    private async Task<ServiceResult> ChangeTemplateLinkAsync(Caller caller, Guid taskId, Guid templateId, bool link, CancellationToken cancellationToken)
    {
        if (!caller.Has(Permissions.TasksEdit) || !caller.Has(Permissions.MonitoringTemplatesEdit))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var task = await db.AutomationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            return ServiceResult.NotFound("task");
        }

        var template = await db.MonitoringTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null)
        {
            return ServiceResult.NotFound("monitoring template");
        }

        if (task.ClientId is { } taskClient && template.ClientId is { } templateClient && taskClient != templateClient)
        {
            return ServiceResult.Fail("A task of a client can only be linked to a monitoring template of that client or one for every client.");
        }

        // The task then runs wherever the template is used: the caller needs access to all of it, as for a change of the template.
        if (await TemplateAccess.ProblemAsync(db, caller, TemplateKind.MonitoringTemplate, template.Id, template.ClientId, false, cancellationToken)
            is { } accessProblem)
        {
            return ServiceResult.Fail(accessProblem);
        }

        var clientId = template.ClientId ?? task.ClientId;
        if (clientId is null)
        {
            // Both are for every client, so the link is instance-wide; the access check above allowed it.
            db.AllowInstanceWideWrites();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var changed = await ToggleAsync(db.MonitoringTemplateAutomationTasks, l => l.MonitoringTemplateId == templateId && l.AutomationTaskId == taskId,
            () => new MonitoringTemplateAutomationTask { MonitoringTemplateId = templateId, AutomationTaskId = taskId, ClientId = clientId, CreatedAt = now },
            link, cancellationToken);
        if (!changed)
        {
            return ServiceResult.Ok();
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.TaskUpdated, "Task", taskId.ToString(), clientId,
            new { task.Name, Change = link ? "linked" : "unlinked", Level = "MonitoringTemplate", Target = template.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(taskId, cancellationToken);
        return ServiceResult.Ok();
    }

    private static async Task<IReadOnlyList<TaskTemplateLinkView>> TemplateLinksAsync(FleetoDbContext db, Guid taskId, CancellationToken cancellationToken) =>
        await (
                from l in db.MonitoringTemplateAutomationTasks
                where l.AutomationTaskId == taskId
                join t in db.MonitoringTemplates on l.MonitoringTemplateId equals t.Id
                join c in db.Clients on t.ClientId equals c.Id into owners
                from c in owners.DefaultIfEmpty()
                orderby t.Name
                select new TaskTemplateLinkView(t.Id, t.Name, c.Code, t.AppliesTo))
            .ToListAsync(cancellationToken);

    private async Task<ServiceResult> ChangeLinkAsync(Caller caller, Guid taskId, LinkLevel level, Guid id, bool link, CancellationToken cancellationToken)
    {
        if (!caller.Has(Permissions.TasksEdit))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var task = await db.AutomationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            return ServiceResult.NotFound("task");
        }

        Guid clientId;
        string name;
        switch (level)
        {
            case LinkLevel.Client:
                var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
                if (client is null)
                {
                    return ServiceResult.NotFound("client");
                }

                if (!caller.HasWholeClient(client.Id))
                {
                    return ServiceResult.Fail(ServiceResult.WholeClientProblem);
                }

                (clientId, name) = (client.Id, client.Code);
                break;
            case LinkLevel.Site:
                var site = await db.Sites.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
                if (site is null)
                {
                    return ServiceResult.NotFound("site");
                }

                if (!caller.HasWholeSite(site.ClientId, site.Id))
                {
                    return ServiceResult.Fail(ServiceResult.WholeSiteProblem);
                }

                (clientId, name) = (site.ClientId, site.Name);
                break;
            case LinkLevel.Endpoint:
                var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
                if (endpoint is null)
                {
                    return ServiceResult.NotFound("endpoint");
                }

                (clientId, name) = (endpoint.ClientId, endpoint.Hostname);
                break;
            default:
                return ServiceResult.Fail("Choose a client, a site or an endpoint.");
        }

        if (task.ClientId is { } owner && owner != clientId)
        {
            return ServiceResult.Fail("A task of a client can only be linked within that client. Make a task for every client to link it elsewhere.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var changed = level switch
        {
            LinkLevel.Client => await ToggleAsync(db.ClientAutomationTasks, l => l.ClientId == id && l.AutomationTaskId == taskId,
                () => new ClientAutomationTask { ClientId = id, AutomationTaskId = taskId, CreatedAt = now }, link, cancellationToken),
            LinkLevel.Site => await ToggleAsync(db.SiteAutomationTasks, l => l.SiteId == id && l.AutomationTaskId == taskId,
                () => new SiteAutomationTask { SiteId = id, ClientId = clientId, AutomationTaskId = taskId, CreatedAt = now }, link, cancellationToken),
            _ => await ToggleAsync(db.EndpointAutomationTasks, l => l.EndpointId == id && l.AutomationTaskId == taskId,
                () => new EndpointAutomationTask { EndpointId = id, ClientId = clientId, AutomationTaskId = taskId, CreatedAt = now }, link, cancellationToken)
        };
        if (!changed)
        {
            return ServiceResult.Ok();
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.TaskUpdated, "Task", taskId.ToString(), clientId,
            new { task.Name, Change = link ? "linked" : "unlinked", Level = level.ToString(), Target = name }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(taskId, cancellationToken);
        return ServiceResult.Ok();
    }

    private static async Task<bool> ToggleAsync<T>(DbSet<T> set, System.Linq.Expressions.Expression<Func<T, bool>> match, Func<T> create, bool link,
        CancellationToken cancellationToken) where T : class
    {
        var existing = await set.SingleOrDefaultAsync(match, cancellationToken);
        if (link == (existing is not null))
        {
            return false;
        }

        if (link)
        {
            set.Add(create());
        }
        else
        {
            set.Remove(existing!);
        }

        return true;
    }

    private static async Task<IReadOnlyList<TaskLinkView>> LinksAsync(FleetoDbContext db, Guid taskId, CancellationToken cancellationToken)
    {
        var clients = await (from l in db.ClientAutomationTasks join c in db.Clients on l.ClientId equals c.Id
                where l.AutomationTaskId == taskId select new TaskLinkView(LinkLevel.Client, c.Id, c.Id, c.Code + " " + c.Name))
            .ToListAsync(cancellationToken);
        var sites = await (from l in db.SiteAutomationTasks join s in db.Sites on l.SiteId equals s.Id join c in db.Clients on s.ClientId equals c.Id
                where l.AutomationTaskId == taskId select new TaskLinkView(LinkLevel.Site, s.Id, s.ClientId, c.Code + " · " + s.Name))
            .ToListAsync(cancellationToken);
        var endpoints = await (from l in db.EndpointAutomationTasks join e in db.Endpoints on l.EndpointId equals e.Id join c in db.Clients on e.ClientId equals c.Id
                where l.AutomationTaskId == taskId select new TaskLinkView(LinkLevel.Endpoint, e.Id, e.ClientId, c.Code + " · " + e.Hostname))
            .ToListAsync(cancellationToken);
        return [.. clients.OrderBy(l => l.Name), .. sites.OrderBy(l => l.Name), .. endpoints.OrderBy(l => l.Name)];
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Runs
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the task by hand on the endpoints it applies to within the caller's groups (or only <paramref name="endpointIds"/>): one
    /// run per client that names the caller, with a target per endpoint that can run every step now. The workers run the steps one
    /// after the other, and fleeto-signer checks the caller's permissions and access again for every step. An endpoint where a run
    /// of the task is still busy is skipped. Above the notice threshold every admin is told.
    /// </summary>
    public async Task<ServiceResult<JobRunResult>> RunNowAsync(Caller caller, Guid taskId, IReadOnlyCollection<Guid>? endpointIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.TasksRun) || !caller.Has(Permissions.ScriptsRun))
        {
            return ServiceResult<JobRunResult>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var task = await db.AutomationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            return ServiceResult<JobRunResult>.NotFound("task");
        }

        if (!task.Enabled)
        {
            return ServiceResult<JobRunResult>.Fail("The task is switched off. Switch it on to run it.");
        }

        var plan = await TaskPlan.LoadAsync(db, task, cancellationToken);
        if (plan.HasRestart && !caller.Has(Permissions.EndpointsRestart))
        {
            return ServiceResult<JobRunResult>.Fail("The task restarts endpoints, which needs the permission Restart endpoints. Ask an admin to run it.");
        }

        var versionIds = plan.Scripts.Values.Where(s => s.CurrentVersionId is not null).Select(s => s.CurrentVersionId!.Value).ToList();
        var versions = await db.ScriptVersions.IgnoreQueryFilters().AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.ScriptId, cancellationToken);
        var scriptSteps = plan.Steps.Where(s => s.Kind == TaskStepKind.Script).ToList();
        if (scriptSteps.Any(s => s.ScriptId is not { } id || !versions.ContainsKey(id)))
        {
            return ServiceResult<JobRunResult>.Fail("A script of the task is not available, or has no version.");
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var endpoints = (await AutomationTaskTargets.LoadAsync(db, task, plan, license, endpointIds, cancellationToken))
            .Where(e => e.Problem != AutomationTaskTargets.NotManagedProblem).ToList();
        if (endpoints.Count == 0)
        {
            return ServiceResult<JobRunResult>.Fail("The task is not linked to a managed endpoint you have access to. Link it first.");
        }

        if (endpoints.Count > ScriptRules.MaxEndpointsPerRun)
        {
            return ServiceResult<JobRunResult>.Fail(
                $"The task applies to {endpoints.Count} endpoints, more than the {ScriptRules.MaxEndpointsPerRun} a run by hand may start. Wait for its schedule, or link it to fewer endpoints.");
        }

        var ids = endpoints.Select(e => e.Id).ToList();
        var approval = (await (
                from ep in EffectivePolicies.Query(db).Where(p => ids.Contains(p.EndpointId))
                join p in db.Policies.IgnoreQueryFilters() on ep.PolicyId equals p.Id
                where p.ScriptApprovalRequired
                select ep.EndpointId)
            .ToListAsync(cancellationToken))
            .ToHashSet();
        var unapproved = scriptSteps.Any(s => !versions[s.ScriptId!.Value].IsApproved);
        var busy = (await db.AutomationTaskRunTargets.AsNoTracking()
                .Where(t => t.AutomationTaskId == taskId && ids.Contains(t.EndpointId) &&
                            (t.State == TaskTargetState.Pending || (t.State == TaskTargetState.Started && t.FinishedAt == null && t.JobId != null)))
                .Select(t => t.EndpointId).ToListAsync(cancellationToken))
            .Concat(await db.Jobs.AsNoTracking()
                .Where(j => j.TaskId == taskId && ids.Contains(j.EndpointId) &&
                            (j.State == JobState.PendingSignature || j.State == JobState.Queued || j.State == JobState.Running))
                .Select(j => j.EndpointId).ToListAsync(cancellationToken))
            .ToHashSet();

        var now = _time.GetUtcNow().UtcDateTime;
        var name = Cut(caller.Name, 200);
        var started = new List<TaskEndpoint>();
        var skipped = new List<JobRunTarget>();
        var runs = new List<AutomationTaskRun>();
        foreach (var client in endpoints.GroupBy(e => e.ClientId))
        {
            var run = new AutomationTaskRun
            {
                Id = Guid.NewGuid(), AutomationTaskId = taskId, ClientId = client.Key, Trigger = TaskTrigger.Manual, OccurrenceAt = now,
                InitiatedByName = name, InitiatedByUserId = caller.UserId, CreatedAt = now
            };
            runs.Add(run);
            db.AutomationTaskRuns.Add(run);
            foreach (var endpoint in client)
            {
                var problem = endpoint.Problem
                              ?? (busy.Contains(endpoint.Id) ? "The previous run of this task is still running on this endpoint." : null)
                              ?? (unapproved && approval.Contains(endpoint.Id)
                                  ? "The policy requires approved scripts, and a script of the task is not approved. Ask somebody else who may approve scripts to approve it."
                                  : null);
                db.AutomationTaskRunTargets.Add(new AutomationTaskRunTarget
                {
                    RunId = run.Id, EndpointId = endpoint.Id, ClientId = endpoint.ClientId, AutomationTaskId = taskId, DueAt = now,
                    ValidUntil = now + AutomationTasks.EventValidity, UpdatedAt = now,
                    State = problem is null ? TaskTargetState.Pending : TaskTargetState.Skipped, Reason = problem
                });
                if (problem is null)
                {
                    started.Add(endpoint);
                }
                else
                {
                    skipped.Add(new JobRunTarget(endpoint.Id, endpoint.Hostname, problem));
                }
            }
        }

        if (started.Count == 0)
        {
            return ServiceResult<JobRunResult>.Fail(skipped.Count == 1
                ? $"Nothing was started: {skipped[0].Problem}"
                : $"Nothing was started. None of the endpoints can run the task now, for example {skipped[0].Hostname}: {skipped[0].Problem}");
        }

        var notified = await _jobs.AddAdminNoticeAsync(db, caller, started.Select(e => e.Id).ToList(), $"task {task.Name}", 0, skipped.Count, now,
            cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.TaskRunStarted, "Task", taskId.ToString(), task.ClientId, new
        {
            task.Name, Runs = runs.Select(r => r.Id).ToList(), Endpoints = started.Count, Skipped = skipped.Count, Steps = StepSummary(plan),
            AdminsNotified = notified
        }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(taskId, cancellationToken);
        return ServiceResult<JobRunResult>.Ok(new JobRunResult(runs[0].Id, started.Count, skipped, notified));
    }

    /// <summary>The newest runs of a task the caller can see, with counts per state.</summary>
    public async Task<IReadOnlyList<TaskRunItem>> ListRunsAsync(Caller caller, Guid taskId, int limit = 50, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksView);
        await using var db = _dbFactory.Create(caller.Scope);
        var runs = await db.AutomationTaskRuns.AsNoTracking().Where(r => r.AutomationTaskId == taskId)
            .OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
        var runIds = runs.Select(r => r.Id).ToList();
        var targets = await db.AutomationTaskRunTargets.AsNoTracking().Where(t => runIds.Contains(t.RunId))
            .Select(t => new { t.RunId, t.State, t.Outcome, t.FinishedAt })
            .ToListAsync(cancellationToken);
        var codes = await db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        return runs.Select(r =>
        {
            var of = targets.Where(t => t.RunId == r.Id).ToList();
            return new TaskRunItem(r.Id, r.ClientId, codes.GetValueOrDefault(r.ClientId), r.Trigger, r.OccurrenceAt, r.CreatedAt, r.InitiatedByName,
                r.CheckName, of.Count,
                of.Count(t => t.State == TaskTargetState.Pending),
                of.Count(t => t.State == TaskTargetState.Started && t.FinishedAt == null),
                of.Count(t => t.Outcome == TaskTargetOutcome.Succeeded),
                of.Count(t => t.Outcome == TaskTargetOutcome.Failed),
                of.Count(t => t.State == TaskTargetState.Skipped || t.Outcome == TaskTargetOutcome.Cancelled));
        }).ToList();
    }

    /// <summary>The endpoints of one run the caller can see, each with its state, its steps and the start of their output.</summary>
    public async Task<IReadOnlyList<TaskRunTargetItem>> GetRunAsync(Caller caller, Guid runId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksView);
        await using var db = _dbFactory.Create(caller.Scope);
        var run = await db.AutomationTaskRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return [];
        }

        var rows = await LoadTargetsAsync(db, runId, cancellationToken);
        return await WithStepsAsync(db, rows.Select(r => (runId, run.AutomationTaskId, r)).ToList(), true, cancellationToken);
    }

    /// <summary>
    /// Fills in the steps of these targets from the jobs of their runs, judged by the steps of the task as they are now, and the
    /// start of the output of every step when <paramref name="previews"/> is set.
    /// </summary>
    private static async Task<IReadOnlyList<TaskRunTargetItem>> WithStepsAsync(FleetoDbContext db,
        IReadOnlyList<(Guid RunId, Guid TaskId, TaskRunTargetItem Target)> rows, bool previews, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var runIds = rows.Select(r => r.RunId).Distinct().ToList();
        var endpointIds = rows.Select(r => r.Target.EndpointId).Distinct().ToList();
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.TaskRunId != null && runIds.Contains(j.TaskRunId.Value) && endpointIds.Contains(j.EndpointId))
            .OrderBy(j => j.CreatedAt)
            .Select(j => new
            {
                j.Id, j.TaskRunId, j.EndpointId, j.TaskStepId, j.Type, j.ScriptName, j.State, j.Result, j.ExitCode, j.ExitStatus, Problem = j.RefusalReason ?? j.Error,
                j.StartedAt, j.CompletedAt, j.ReceivedOutputBytes
            })
            .ToListAsync(cancellationToken);
        var jobIds = jobs.Select(j => j.Id).ToList();
        var firstChunks = previews
            ? await db.JobOutputChunks.AsNoTracking().Where(c => jobIds.Contains(c.JobId) && c.Stream == JobStream.Stdout && c.Sequence == 0)
                .ToDictionaryAsync(c => c.JobId, c => c.Data, cancellationToken)
            : [];
        var taskIds = rows.Select(r => r.TaskId).Distinct().ToList();
        var tasks = await db.AutomationTasks.IgnoreQueryFilters().AsNoTracking().Where(t => taskIds.Contains(t.Id)).ToListAsync(cancellationToken);
        var plans = await TaskPlan.LoadManyAsync(db, tasks, cancellationToken);

        return rows.Select(row =>
        {
            var plan = plans.GetValueOrDefault(row.TaskId);
            var steps = jobs.Where(j => j.TaskRunId == row.RunId && j.EndpointId == row.Target.EndpointId).Select((j, index) =>
            {
                var step = plan?.Find(j.TaskStepId);
                var kind = j.Type == JobType.Restart ? TaskStepKind.Restart : TaskStepKind.Script;
                var passed = step is not null ? AutomationTasks.Passed(step, j.State, j.Result, j.ExitCode, j.ExitStatus) : j.State == JobState.Succeeded;
                return new TaskRunStepItem(j.Id, step?.Position ?? index + 1, kind == TaskStepKind.Restart ? "Restart" : j.ScriptName, kind, j.State, j.Result,
                    j.ExitCode, passed, j.Problem, j.StartedAt, j.CompletedAt,
                    firstChunks.TryGetValue(j.Id, out var data) ? Preview(data) : string.Empty, j.ReceivedOutputBytes, j.ExitStatus);
            }).ToList();
            var current = steps.LastOrDefault();
            return row.Target with
            {
                Steps = steps,
                StepCount = plan?.Steps.Count ?? 1,
                OutputPreview = current?.OutputPreview ?? row.Target.OutputPreview,
                StartedAt = steps.FirstOrDefault()?.StartedAt ?? row.Target.StartedAt
            };
        }).ToList();
    }

    /// <summary>
    /// The run as CSV, one row per endpoint and step with its state and output (the first 64 KB of the output and 16 KB of the errors),
    /// for collecting inventory. Values that a spreadsheet would read as a formula are prefixed with an apostrophe.
    /// </summary>
    public async Task<(string FileName, string Csv)?> ExportRunAsync(Caller caller, Guid runId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksView);
        await using var db = _dbFactory.Create(caller.Scope);
        var run = await db.AutomationTaskRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var taskName = await db.AutomationTasks.AsNoTracking().Where(t => t.Id == run.AutomationTaskId).Select(t => t.Name).SingleOrDefaultAsync(cancellationToken)
                       ?? "task";
        var clientCode = await db.Clients.AsNoTracking().Where(c => c.Id == run.ClientId).Select(c => c.Code).SingleOrDefaultAsync(cancellationToken) ?? string.Empty;
        var rows = await WithStepsAsync(db, (await LoadTargetsAsync(db, runId, cancellationToken)).Select(r => (runId, run.AutomationTaskId, r)).ToList(),
            false, cancellationToken);
        var csv = new StringBuilder();
        csv.Append("Endpoint,Client,Run state,Step,Step name,Step state,Exit code,Started,Completed,Output,Errors\r\n");
        foreach (var row in rows)
        {
            if (row.Steps.Count == 0)
            {
                AppendRow(row, null, string.Empty, string.Empty);
                continue;
            }

            foreach (var step in row.Steps)
            {
                var stdout = (await JobService.ReadStreamAsync(db, step.JobId, JobStream.Stdout, cancellationToken, ExportStdoutBytes)).Text;
                var stderr = (await JobService.ReadStreamAsync(db, step.JobId, JobStream.Stderr, cancellationToken, ExportStderrBytes)).Text;
                AppendRow(row, step, stdout, stderr);
            }
        }

        var safeName = new string(taskName.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return ($"fleeto-task-{safeName}-{clientCode}-{run.CreatedAt:yyyyMMdd-HHmm}.csv", csv.ToString());

        void AppendRow(TaskRunTargetItem row, TaskRunStepItem? step, string stdout, string stderr)
        {
            csv.AppendJoin(',', [
                Field(row.Hostname), Field(clientCode), Field(StateText(row)), Field(step?.Position.ToString(CultureInfo.InvariantCulture)), Field(step?.Name),
                Field(step is null ? null : StepStateText(step)), Field(step?.ExitCode?.ToString(CultureInfo.InvariantCulture)),
                Field(step?.StartedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                Field(step?.CompletedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)), Field(stdout.TrimEnd()), Field(stderr.TrimEnd())
            ]);
            csv.Append("\r\n");
        }
    }

    /// <summary>
    /// The Tasks tab of an endpoint (0.6.0): every task that applies to it by <see cref="AutomationTaskTargets.Linked"/>, with the
    /// levels it is linked on, its next run (the run already waiting on this endpoint, else the next occurrence of its schedule)
    /// and its last run here; and the newest runs on the endpoint, of tasks that still exist. Null when the endpoint is not visible.
    /// </summary>
    public async Task<EndpointTasksView?> GetForEndpointAsync(Caller caller, Guid endpointId, int limit = 50, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksView);
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new { e.ClientId, e.SiteId, e.Hostname, e.Tier, e.OsPlatform, e.AgentVersion, Class = e.ClassOverride ?? e.DetectedClass })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var links = (await db.ClientAutomationTasks.Where(l => l.ClientId == endpoint.ClientId)
                .Select(l => new { l.AutomationTaskId, Level = LinkLevel.Client }).ToListAsync(cancellationToken))
            .Concat(await db.SiteAutomationTasks.Where(l => l.SiteId == endpoint.SiteId)
                .Select(l => new { l.AutomationTaskId, Level = LinkLevel.Site }).ToListAsync(cancellationToken))
            .Concat(await db.EndpointAutomationTasks.Where(l => l.EndpointId == endpointId)
                .Select(l => new { l.AutomationTaskId, Level = LinkLevel.Endpoint }).ToListAsync(cancellationToken))
            .GroupBy(l => l.AutomationTaskId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LinkLevel>)g.Select(l => l.Level).Distinct().Order().ToList());
        // Tasks of the monitoring templates the endpoint gets: linked to its client, site or itself, and for its class.
        var templateIds = db.ClientMonitoringTemplates.Where(m => m.ClientId == endpoint.ClientId).Select(m => m.MonitoringTemplateId)
            .Concat(db.SiteMonitoringTemplates.Where(m => m.SiteId == endpoint.SiteId).Select(m => m.MonitoringTemplateId))
            .Concat(db.EndpointMonitoringTemplates.Where(m => m.EndpointId == endpointId).Select(m => m.MonitoringTemplateId));
        var templateLinks = (await (
                from l in db.MonitoringTemplateAutomationTasks
                where templateIds.Contains(l.MonitoringTemplateId)
                join t in db.MonitoringTemplates on l.MonitoringTemplateId equals t.Id
                select new { l.AutomationTaskId, t.Name, t.AppliesTo })
            .ToListAsync(cancellationToken))
            .Where(t => EffectivePolicyRules.IsFor(t.AppliesTo, endpoint.Class))
            .GroupBy(t => t.AutomationTaskId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(t => t.Name).Distinct().Order().ToList());
        var candidateIds = links.Keys.Union(templateLinks.Keys).ToList();
        var tasks = await db.AutomationTasks.AsNoTracking().Where(t => candidateIds.Contains(t.Id)).OrderBy(t => t.Name).ToListAsync(cancellationToken);

        var plans = await TaskPlan.LoadManyAsync(db, tasks, cancellationToken);
        var states = await db.AutomationTaskStates.AsNoTracking().Where(s => candidateIds.Contains(s.AutomationTaskId))
            .ToDictionaryAsync(s => s.AutomationTaskId, s => s.NextOccurrenceAt, cancellationToken);
        var license = await _licenses.GetStatusAsync(db, cancellationToken);

        var items = new List<EndpointTaskItem>();
        foreach (var task in tasks)
        {
            // The one targeting rule decides, so the class and the client of the task count as they do for a run.
            if (!await AutomationTaskTargets.Linked(db, task).AnyAsync(e => e.Id == endpointId, cancellationToken))
            {
                continue;
            }

            var taskId = task.Id;
            var ofTask = db.AutomationTaskRunTargets.AsNoTracking().Where(t => t.EndpointId == endpointId && t.AutomationTaskId == taskId);
            var waiting = await Runs(db, ofTask.Where(t => t.State == TaskTargetState.Pending).OrderBy(t => t.DueAt).Take(1), endpoint.Hostname)
                .FirstOrDefaultAsync(cancellationToken);
            var last = await Runs(db, ofTask.Where(t => t.State != TaskTargetState.Pending).OrderByDescending(t => t.DueAt).Take(1), endpoint.Hostname)
                .FirstOrDefaultAsync(cancellationToken);
            var plan = plans[task.Id];
            var problem = AutomationTaskTargets.Problem(plan, license, endpoint.ClientId, endpoint.Tier, endpoint.OsPlatform, endpoint.AgentVersion);
            items.Add(new EndpointTaskItem(task.Id, task.Name, StepSummary(plan), task.Enabled, links.GetValueOrDefault(task.Id, []),
                templateLinks.GetValueOrDefault(task.Id, []), AutomationTasks.Describe(task), Triggers(task),
                task.Enabled ? waiting?.Target.DueAt ?? states.GetValueOrDefault(task.Id) : null,
                waiting is null ? task.RandomDelayMinutes : 0, problem, last is null ? null : (await WithRunStepsAsync(db, [last], cancellationToken))[0]));
        }

        var newest = db.AutomationTaskRunTargets.AsNoTracking().Where(t => t.EndpointId == endpointId)
            .OrderByDescending(t => t.DueAt).Take(Math.Clamp(limit, 1, 200));
        var history = await WithRunStepsAsync(db, await Runs(db, newest, endpoint.Hostname).ToListAsync(cancellationToken), cancellationToken);
        return new EndpointTasksView(items, history);
    }

    /// <summary>The runs of tasks on one endpoint with their steps.</summary>
    private static async Task<IReadOnlyList<EndpointTaskRunItem>> WithRunStepsAsync(FleetoDbContext db, IReadOnlyList<EndpointTaskRunItem> runs,
        CancellationToken cancellationToken)
    {
        var targets = await WithStepsAsync(db, runs.Select(r => (r.RunId, r.TaskId, r.Target)).ToList(), false, cancellationToken);
        return runs.Select((r, i) => r with { Target = targets[i] }).ToList();
    }

    /// <summary>These targets of runs on one endpoint, newest first, each with its run, task and job.</summary>
    private static IQueryable<EndpointTaskRunItem> Runs(FleetoDbContext db, IQueryable<AutomationTaskRunTarget> targets, string hostname) =>
        from t in targets
        join r in db.AutomationTaskRuns on t.RunId equals r.Id
        join task in db.AutomationTasks.IgnoreQueryFilters() on r.AutomationTaskId equals task.Id
        join j in db.Jobs on t.JobId equals j.Id into jobs
        from j in jobs.DefaultIfEmpty()
        orderby t.DueAt descending
        select new EndpointTaskRunItem(r.Id, task.Id, task.Name, r.Trigger, r.InitiatedByName, r.CheckName, r.CreatedAt,
            new TaskRunTargetItem(t.EndpointId, hostname, t.State, t.DueAt, t.Reason, t.JobId, (JobState?)j.State, j.ExitCode,
                j.RefusalReason ?? j.Error, j.StartedAt, j.CompletedAt, string.Empty, (long?)j.ReceivedOutputBytes ?? 0, j.Result, t.Outcome, t.FinishedAt,
                t.HasWarning));

    /// <summary>The tasks that can be linked to an endpoint from its Tasks tab: global ones and those of its client, not linked to it yet.</summary>
    public async Task<IReadOnlyList<TaskOption>> GetEndpointTaskChoicesAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksEdit);
        await using var db = _dbFactory.Create(caller.Scope);
        var clientId = await db.Endpoints.Where(e => e.Id == endpointId).Select(e => (Guid?)e.ClientId).SingleOrDefaultAsync(cancellationToken);
        if (clientId is null)
        {
            return [];
        }

        var linked = db.EndpointAutomationTasks.Where(l => l.EndpointId == endpointId).Select(l => l.AutomationTaskId);
        return await OptionsAsync(db, db.AutomationTasks.AsNoTracking().Where(t => (t.ClientId == null || t.ClientId == clientId) && !linked.Contains(t.Id)),
            cancellationToken);
    }

    /// <summary>
    /// The state of an endpoint of a run in a few words, for lists and the export: waiting, could not start, the step it is at
    /// ("Running step 2 of 5"), and how it ended ("Succeeded", "Succeeded with warnings", "Failed at step 3", or the job's own words
    /// for a task of one step).
    /// </summary>
    public static string StateText(TaskRunTargetItem item)
    {
        if (item.State == TaskTargetState.Pending)
        {
            return "Waiting";
        }

        if (item.State == TaskTargetState.Skipped)
        {
            return "Could not start";
        }

        var multi = item.StepCount > 1;
        var current = item.Steps.LastOrDefault();
        switch (item.Outcome)
        {
            case TaskTargetOutcome.Succeeded:
                return item.HasWarning ? "Succeeded with warnings" : "Succeeded";
            case TaskTargetOutcome.Cancelled:
                return "Cancelled";
            case TaskTargetOutcome.Failed when multi && item.Steps.LastOrDefault(s => !s.Passed) is { } failed:
                return $"Failed at step {failed.Position}";
            case TaskTargetOutcome.Failed when multi:
                return current is null ? "Failed" : $"Stopped after step {current.Position}";
        }

        if (item.Outcome is null && multi && current is not null && current.State is JobState.PendingSignature or JobState.Queued or JobState.Running)
        {
            return $"{(current.State == JobState.Running ? "Running" : "Queued")} step {current.Position} of {item.StepCount}";
        }

        return JobText(item.JobState, item.JobResult);
    }

    /// <summary>
    /// The state of one step of a run, in the words of its job; a script that ended with an exit code its script lists (0.6.0) names
    /// what the code meant ("Warning (exit code 1)").
    /// </summary>
    public static string StepStateText(TaskRunStepItem step) => step switch
    {
        { State: JobState.Succeeded, ExitStatus: ExitStatus.Info } => $"Info (exit code {step.ExitCode})",
        { State: JobState.Succeeded, ExitStatus: ExitStatus.Warning } => $"Warning (exit code {step.ExitCode})",
        { Passed: true, ExitCode: { } code } when code != 0 => $"Succeeded (exit code {code})",
        { State: JobState.Failed, Result: JobResult.Exited, ExitCode: { } code } => $"Failed (exit code {code})",
        _ => JobText(step.State, step.Result)
    };

    private static string JobText(JobState? state, JobResult? result) => state switch
    {
        JobState.PendingSignature or JobState.Queued => "Queued",
        JobState.Running => "Running",
        JobState.Succeeded => "Succeeded",
        JobState.Failed => result == JobResult.TimedOut ? "Timed out" : result == JobResult.FailedToStart ? "Could not start" : "Failed",
        JobState.Expired => "Missed",
        JobState.Refused => "Refused",
        JobState.Lost => "Lost",
        JobState.Cancelled => "Cancelled",
        _ => "Started"
    };

    private static async Task<List<TaskRunTargetItem>> LoadTargetsAsync(FleetoDbContext db, Guid runId, CancellationToken cancellationToken) =>
        (await (
                from t in db.AutomationTaskRunTargets.AsNoTracking()
                where t.RunId == runId
                join e in db.Endpoints on t.EndpointId equals e.Id
                join j in db.Jobs on t.JobId equals j.Id into jobs
                from j in jobs.DefaultIfEmpty()
                select new TaskRunTargetItem(t.EndpointId, e.Hostname, t.State, t.DueAt, t.Reason, t.JobId, (JobState?)j.State, j.ExitCode,
                    j.RefusalReason ?? j.Error, j.StartedAt, j.CompletedAt, string.Empty, (long?)j.ReceivedOutputBytes ?? 0, j.Result, t.Outcome, t.FinishedAt,
                    t.HasWarning))
            .ToListAsync(cancellationToken))
        .OrderBy(t => t.Hostname, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Preview(byte[] data)
    {
        var text = new UTF8Encoding(false, false).GetString(data, 0, Math.Min(data.Length, PreviewLength * 4)).Trim();
        return text.Length <= PreviewLength ? text : text[..PreviewLength] + "…";
    }

    private static string Field(string? value)
    {
        value ??= string.Empty;
        // A value starting like a formula would run as one in a spreadsheet: it is kept as text.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>"Schedule, check alerts, managed" and the like, for lists and the audit log.</summary>
    public static string Triggers(AutomationTask task)
    {
        var parts = new List<string>();
        if (task.ScheduleKind != TaskScheduleKind.None)
        {
            parts.Add("schedule");
        }

        if (task.TriggerCheckIds.Count > 0)
        {
            parts.Add(task.TriggerCheckIds.Count == 1 ? "alert of 1 check" : $"alerts of {task.TriggerCheckIds.Count} checks");
        }

        if (task.OnManaged)
        {
            parts.Add("endpoint managed");
        }

        parts.Add("by hand");
        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    public static TaskInput ToInput(AutomationTask t, TaskPlan plan) => new(t.Name, t.Description, t.ClientId,
        plan.Steps.Select(s => new TaskStepInput(s.Id == Guid.Empty ? null : s.Id, s.Kind, s.ScriptId, s.RunAs, s.ContinueOnFailure,
            s.RestartWaitMinutes)).ToList(), t.AppliesTo, t.Enabled,
        t.ScheduleKind, t.TimeZone, t.StartDate, t.StartMinute, t.EndDate, t.Every, t.WeekDays, t.MonthDay, t.RepeatMinutes, t.TriggerCheckIds,
        t.OnManaged, t.RandomDelayMinutes, t.CatchUp, t.Overlap, t.AlertOnFailure, t.AlertSeverity);

    private static void Apply(AutomationTask task, TaskInput input, List<Guid> checkIds)
    {
        task.Name = input.Name?.Trim() ?? string.Empty;
        task.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        task.AppliesTo = input.AppliesTo;
        task.Enabled = input.Enabled;
        task.ScheduleKind = input.ScheduleKind;
        task.TimeZone = string.IsNullOrWhiteSpace(input.TimeZone) ? "UTC" : input.TimeZone.Trim();
        task.StartDate = input.StartDate;
        task.StartMinute = input.StartMinute;
        task.EndDate = input.EndDate;
        task.Every = input.ScheduleKind == TaskScheduleKind.Once ? 1 : input.Every;
        task.WeekDays = input.ScheduleKind == TaskScheduleKind.Weekly ? input.WeekDays : WeekDays.None;
        task.MonthDay = input.ScheduleKind == TaskScheduleKind.Monthly ? input.MonthDay : 1;
        task.RepeatMinutes = input.ScheduleKind is TaskScheduleKind.None or TaskScheduleKind.Once ? null : input.RepeatMinutes;
        task.TriggerCheckIds = checkIds;
        task.OnManaged = input.OnManaged;
        task.RandomDelayMinutes = input.RandomDelayMinutes;
        task.CatchUp = input.CatchUp;
        task.Overlap = input.Overlap;
        task.AlertOnFailure = input.AlertOnFailure;
        task.AlertSeverity = input.AlertSeverity;
    }

    /// <summary>The settings of a task before a change, for the audit entry.</summary>
    private static object Snapshot(AutomationTask task, string? steps) => new
    {
        task.Name, Steps = steps, AppliesTo = task.AppliesTo.ToString(), task.Enabled,
        Schedule = AutomationTasks.Describe(task), Triggers = Triggers(task), task.RandomDelayMinutes, task.CatchUp, Overlap = task.Overlap.ToString(),
        task.AlertOnFailure
    };

    private async Task PublishAsync(Guid taskId, CancellationToken cancellationToken) =>
        await PublishQuietlyAsync(NotificationChannels.Tasks, taskId.ToString(), cancellationToken);

    private async Task PublishQuietlyAsync(string channel, string payload, CancellationToken cancellationToken)
    {
        try
        {
            await _bus.PublishAsync(channel, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The workers find the change on their next pass; pages refresh on their next load.
        }
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>The script of a task of one step, or "3 steps: Update, Restart, Start containers".</summary>
    public static string StepSummary(TaskPlan plan)
    {
        var names = plan.Steps.Select(s => s.Kind == TaskStepKind.Restart ? "Restart" : plan.ScriptOf(s)?.Name ?? "Script not available").ToList();
        return names.Count == 1 ? names[0] : Cut($"{names.Count} steps: {string.Join(", ", names)}", 300);
    }

    private static IReadOnlyList<TaskStepView> StepViews(TaskPlan plan) => plan.Steps.Select(s =>
    {
        var script = plan.ScriptOf(s);
        return new TaskStepView(s.Id, s.Position, s.Kind, s.ScriptId, script?.Name, script?.Language, s.RunAs, s.ContinueOnFailure,
            s.RestartWaitMinutes);
    }).ToList();
}
