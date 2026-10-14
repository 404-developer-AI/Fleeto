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

/// <summary>What a user sets on a task (0.6.0). <see cref="ClientId"/> counts only when the task is created.</summary>
public sealed record TaskInput(
    string? Name,
    string? Description,
    Guid? ClientId,
    Guid ScriptId,
    JobRunAs RunAs,
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

public sealed record TaskListItem(Guid Id, string Name, Guid? ClientId, string? ClientCode, string ScriptName, string Schedule, string Triggers,
    bool Enabled, int Links, DateTime? NextRunAt, DateTime? LastRunAt);

/// <summary>Where a task is linked: a client, a site or an endpoint, with its name.</summary>
public sealed record TaskLinkView(LinkLevel Level, Guid Id, Guid ClientId, string Name);

/// <summary>A check of a monitoring template whose alert can start a task.</summary>
public sealed record TaskCheckOption(Guid Id, string Name, string TemplateName);

/// <summary>A script a task can run: global, or of the task's client.</summary>
public sealed record TaskScriptOption(Guid Id, string Name, ScriptLanguage Language, string? ClientCode);

public sealed record TaskDetail(Guid Id, TaskInput Input, string? ClientCode, string ScriptName, ScriptLanguage Language,
    IReadOnlyList<TaskLinkView> Links, IReadOnlyList<TaskCheckOption> TriggerChecks, DateTime? NextRunAt, string UpdatedByName, DateTime UpdatedAt);

public sealed record TaskRunItem(Guid Id, Guid ClientId, string? ClientCode, TaskTrigger Trigger, DateTime OccurrenceAt, DateTime CreatedAt,
    string InitiatedByName, string? CheckName, int Endpoints, int Waiting, int Running, int Succeeded, int Failed, int Skipped);

/// <summary>One endpoint of a run: the target, and the job once it was written, with the start of its output.</summary>
public sealed record TaskRunTargetItem(Guid EndpointId, string Hostname, TaskTargetState State, DateTime DueAt, string? Reason, Guid? JobId,
    JobState? JobState, int? ExitCode, string? Problem, DateTime? StartedAt, DateTime? CompletedAt, string OutputPreview, long OutputBytes);

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

    public async Task<IReadOnlyList<TaskListItem>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksView);
        await using var db = _dbFactory.Create(caller.Scope);
        var tasks = await db.AutomationTasks.AsNoTracking().OrderBy(t => t.Name).ToListAsync(cancellationToken);
        var ids = tasks.Select(t => t.Id).ToList();
        var clientCodes = await db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        var scriptIds = tasks.Select(t => t.ScriptId).Distinct().ToList();
        var scripts = await db.Scripts.IgnoreQueryFilters().AsNoTracking().Where(s => scriptIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var states = await db.AutomationTaskStates.AsNoTracking().Where(s => ids.Contains(s.AutomationTaskId))
            .ToDictionaryAsync(s => s.AutomationTaskId, s => s.NextOccurrenceAt, cancellationToken);
        var links = (await db.ClientAutomationTasks.Where(l => ids.Contains(l.AutomationTaskId)).Select(l => l.AutomationTaskId).ToListAsync(cancellationToken))
            .Concat(await db.SiteAutomationTasks.Where(l => ids.Contains(l.AutomationTaskId)).Select(l => l.AutomationTaskId).ToListAsync(cancellationToken))
            .Concat(await db.EndpointAutomationTasks.Where(l => ids.Contains(l.AutomationTaskId)).Select(l => l.AutomationTaskId).ToListAsync(cancellationToken))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var lastRuns = (await db.AutomationTaskRuns.AsNoTracking().Where(r => ids.Contains(r.AutomationTaskId))
                .GroupBy(r => r.AutomationTaskId).Select(g => new { g.Key, Last = g.Max(r => r.CreatedAt) }).ToListAsync(cancellationToken))
            .ToDictionary(r => r.Key, r => r.Last);
        return tasks.Select(t => new TaskListItem(t.Id, t.Name, t.ClientId, t.ClientId is { } c ? clientCodes.GetValueOrDefault(c) : null,
            scripts.GetValueOrDefault(t.ScriptId, "Script not available"), AutomationTasks.Describe(t), Triggers(t), t.Enabled,
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

        var script = await db.Scripts.IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == task.ScriptId)
            .Select(s => new { s.Name, s.Language }).SingleOrDefaultAsync(cancellationToken);
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
        return new TaskDetail(task.Id, ToInput(task), clientCode, script?.Name ?? "Script not available", script?.Language ?? ScriptLanguage.PowerShell,
            await LinksAsync(db, taskId, cancellationToken), checks.OrderBy(c => c.TemplateName).ThenBy(c => c.Name).ToList(),
            task.Enabled ? next : null, task.UpdatedByName, task.UpdatedAt);
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

    /// <summary>Creates a task (taskId null) or changes one. Problems come from <see cref="AutomationTasks.Validate"/> and the checks here.</summary>
    public async Task<ServiceResult<Guid>> SaveAsync(Caller caller, Guid? taskId, TaskInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.TasksEdit))
        {
            return ServiceResult<Guid>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime;
        AutomationTask task;
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

        var script = await db.Scripts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == input.ScriptId, cancellationToken);
        if (script is null || script.CurrentVersionId is null)
        {
            return ServiceResult<Guid>.Fail("Choose a script from the library that has a version.");
        }

        if (script.ClientId is { } scriptClient && scriptClient != task.ClientId)
        {
            return ServiceResult<Guid>.Fail(task.ClientId is null
                ? "A task for every client can only run a script for every client. Choose another script, or make the task for that client."
                : "The script belongs to another client. Choose a script for every client or one of this client.");
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

        var before = taskId is null ? null : Snapshot(task);
        Apply(task, input, checkIds);
        task.UpdatedByName = Cut(caller.Name, 200);
        task.UpdatedAt = now;
        var problems = AutomationTasks.Validate(task);
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

        if (taskId is null)
        {
            db.AutomationTasks.Add(task);
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(taskId is null ? AuditActions.TaskCreated : AuditActions.TaskUpdated, "Task",
            task.Id.ToString(), task.ClientId, new
            {
                task.Name, Script = script.Name, RunAs = task.RunAs.ToString(), AppliesTo = task.AppliesTo.ToString(), task.Enabled,
                Schedule = AutomationTasks.Describe(task), Triggers = Triggers(task), task.RandomDelayMinutes, task.CatchUp,
                Overlap = task.Overlap.ToString(), task.AlertOnFailure, Before = before
            }), now));
        await db.SaveChangesAsync(cancellationToken);
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
    /// Starts the task by hand on the endpoints it applies to within the caller's groups (or only <paramref name="endpointIds"/>), as
    /// the caller: one run per client, a job per endpoint that can run it now, signed by fleeto-signer as any job the caller
    /// starts. An endpoint where the previous job of the task still runs is skipped. Above the notice threshold every admin is told.
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

        var script = await db.Scripts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == task.ScriptId, cancellationToken);
        var version = script?.CurrentVersionId is { } versionId
            ? await db.ScriptVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            : null;
        if (script is null || version is null)
        {
            return ServiceResult<JobRunResult>.Fail("The script of the task is not available to you, or has no version.");
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var endpoints = (await AutomationTaskTargets.LoadAsync(db, task, script, license, endpointIds, cancellationToken))
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
        var policies = await (
                from ep in EffectivePolicies.Query(db).Where(p => ids.Contains(p.EndpointId))
                join p in db.Policies.IgnoreQueryFilters() on ep.PolicyId equals p.Id
                select new { ep.EndpointId, p.ScriptApprovalRequired, p.MaxOutputBytes })
            .ToDictionaryAsync(p => p.EndpointId, cancellationToken);
        var active = (await db.Jobs.AsNoTracking()
                .Where(j => j.TaskId == taskId && ids.Contains(j.EndpointId) &&
                            (j.State == JobState.PendingSignature || j.State == JobState.Queued || j.State == JobState.Running))
                .Select(j => j.EndpointId).ToListAsync(cancellationToken))
            .ToHashSet();

        var now = _time.GetUtcNow().UtcDateTime;
        var name = Cut(caller.Name, 200);
        var created = new List<Job>();
        var skipped = new List<JobRunTarget>();
        var runs = new List<AutomationTaskRun>();
        foreach (var client in endpoints.GroupBy(e => e.ClientId))
        {
            var run = new AutomationTaskRun
            {
                Id = Guid.NewGuid(), AutomationTaskId = taskId, ClientId = client.Key, Trigger = TaskTrigger.Manual, OccurrenceAt = now,
                InitiatedByName = name, CreatedAt = now
            };
            runs.Add(run);
            db.AutomationTaskRuns.Add(run);
            foreach (var endpoint in client)
            {
                var policy = policies.GetValueOrDefault(endpoint.Id);
                var problem = endpoint.Problem
                              ?? (active.Contains(endpoint.Id) ? "The previous run of this task is still running on this endpoint." : null)
                              ?? (policy?.ScriptApprovalRequired == true && !version.IsApproved
                                  ? "The policy requires an approved script. Ask somebody else who may approve scripts to approve this version."
                                  : null);
                var target = new AutomationTaskRunTarget
                {
                    RunId = run.Id, EndpointId = endpoint.Id, ClientId = endpoint.ClientId, AutomationTaskId = taskId, DueAt = now,
                    ValidUntil = now + AutomationTasks.EventValidity, UpdatedAt = now, OutcomeHandledAt = null
                };
                db.AutomationTaskRunTargets.Add(target);
                if (problem is not null)
                {
                    target.State = TaskTargetState.Skipped;
                    target.Reason = problem;
                    skipped.Add(new JobRunTarget(endpoint.Id, endpoint.Hostname, problem));
                    continue;
                }

                var job = new Job
                {
                    Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, BatchId = run.Id, Type = JobType.Script,
                    ScriptId = script.Id, ScriptVersionId = version.Id, ScriptName = script.Name, ScriptVersionNumber = version.Number,
                    Language = script.Language, ScriptSha256 = version.Sha256, TimeoutSeconds = version.TimeoutSeconds,
                    MaxOutputBytes = ScriptRules.OutputCap(policy?.MaxOutputBytes ?? ScriptRules.DefaultMaxOutputBytes), RunAs = task.RunAs,
                    TaskId = taskId, TaskRunId = run.Id, CreatedAt = now, ValidUntil = target.ValidUntil, InitiatedByUserId = caller.UserId,
                    InitiatedByName = name
                };
                target.State = TaskTargetState.Started;
                target.JobId = job.Id;
                created.Add(job);
                db.Jobs.Add(job);
                db.SigningRequests.Add(new SigningRequest
                {
                    Id = Guid.NewGuid(), ClientId = endpoint.ClientId, Kind = SigningRequestKind.Job, SubjectId = job.Id, Payload = [],
                    RequestedBy = "web:" + (caller.IpAddress ?? "unknown"), CreatedAt = now
                });
                db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.JobCreated, "Job", job.Id.ToString(), endpoint.ClientId, new
                {
                    endpoint.Hostname, EndpointId = endpoint.Id, BatchId = run.Id, Task = task.Name, TaskId = taskId, job.ScriptName,
                    job.ScriptVersionNumber, job.ScriptSha256, job.ValidUntil, RunAs = job.RunAs.ToString()
                }), now));
            }
        }

        if (created.Count == 0)
        {
            return ServiceResult<JobRunResult>.Fail(skipped.Count == 1
                ? $"No job was started: {skipped[0].Problem}"
                : $"No job was started. None of the endpoints can run the task now, for example {skipped[0].Hostname}: {skipped[0].Problem}");
        }

        var notified = await _jobs.AddAdminNoticeAsync(db, caller, created, skipped.Count, now, cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.TaskRunStarted, "Task", taskId.ToString(), task.ClientId, new
        {
            task.Name, Runs = runs.Select(r => r.Id).ToList(), Jobs = created.Count, Skipped = skipped.Count, AdminsNotified = notified
        }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(taskId, cancellationToken);
        foreach (var endpointId in created.Select(j => j.EndpointId).Distinct())
        {
            await PublishQuietlyAsync(NotificationChannels.Jobs, endpointId.ToString(), cancellationToken);
        }

        return ServiceResult<JobRunResult>.Ok(new JobRunResult(runs[0].Id, created.Count, skipped, notified));
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
        var targets = await (
                from t in db.AutomationTaskRunTargets.AsNoTracking()
                where runIds.Contains(t.RunId)
                join j in db.Jobs on t.JobId equals j.Id into jobs
                from j in jobs.DefaultIfEmpty()
                select new { t.RunId, t.State, JobState = (JobState?)j.State })
            .ToListAsync(cancellationToken);
        var codes = await db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        return runs.Select(r =>
        {
            var of = targets.Where(t => t.RunId == r.Id).ToList();
            return new TaskRunItem(r.Id, r.ClientId, codes.GetValueOrDefault(r.ClientId), r.Trigger, r.OccurrenceAt, r.CreatedAt, r.InitiatedByName,
                r.CheckName, of.Count,
                of.Count(t => t.State == TaskTargetState.Pending),
                of.Count(t => t.JobState is JobState.PendingSignature or JobState.Queued or JobState.Running),
                of.Count(t => t.JobState == JobState.Succeeded),
                of.Count(t => t.JobState is { } state && (AutomationTasks.IsFailure(state) || state == JobState.Expired)),
                of.Count(t => t.State == TaskTargetState.Skipped || t.JobState == JobState.Cancelled));
        }).ToList();
    }

    /// <summary>The endpoints of one run the caller can see, each with its state and the start of its output.</summary>
    public async Task<IReadOnlyList<TaskRunTargetItem>> GetRunAsync(Caller caller, Guid runId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.TasksView);
        await using var db = _dbFactory.Create(caller.Scope);
        var rows = await LoadTargetsAsync(db, runId, cancellationToken);
        var jobIds = rows.Where(r => r.JobId is not null).Select(r => r.JobId!.Value).ToList();
        var firstChunks = await db.JobOutputChunks.AsNoTracking()
            .Where(c => jobIds.Contains(c.JobId) && c.Stream == JobStream.Stdout && c.Sequence == 0)
            .ToDictionaryAsync(c => c.JobId, c => c.Data, cancellationToken);
        return rows.Select(r => r with
        {
            OutputPreview = r.JobId is { } jobId && firstChunks.TryGetValue(jobId, out var data) ? Preview(data) : string.Empty
        }).ToList();
    }

    /// <summary>
    /// The run as CSV, one row per endpoint with its state and output (the first 64 KB of the output and 16 KB of the errors), for
    /// collecting inventory. Values that a spreadsheet would read as a formula are prefixed with an apostrophe.
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
        var rows = await LoadTargetsAsync(db, runId, cancellationToken);
        var csv = new StringBuilder();
        csv.Append("Endpoint,Client,State,Exit code,Started,Completed,Output,Errors\r\n");
        foreach (var row in rows)
        {
            var (stdout, stderr) = row.JobId is { } jobId
                ? ((await JobService.ReadStreamAsync(db, jobId, JobStream.Stdout, cancellationToken, ExportStdoutBytes)).Text,
                   (await JobService.ReadStreamAsync(db, jobId, JobStream.Stderr, cancellationToken, ExportStderrBytes)).Text)
                : (string.Empty, string.Empty);
            csv.AppendJoin(',', [
                Field(row.Hostname), Field(clientCode), Field(StateText(row)), Field(row.ExitCode?.ToString(CultureInfo.InvariantCulture)),
                Field(row.StartedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                Field(row.CompletedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)), Field(stdout.TrimEnd()), Field(stderr.TrimEnd())
            ]);
            csv.Append("\r\n");
        }

        var safeName = new string(taskName.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return ($"fleeto-task-{safeName}-{clientCode}-{run.CreatedAt:yyyyMMdd-HHmm}.csv", csv.ToString());
    }

    /// <summary>The state of an endpoint of a run as one word, for lists and the export.</summary>
    public static string StateText(TaskRunTargetItem item) => item.State switch
    {
        TaskTargetState.Pending => "Waiting",
        TaskTargetState.Skipped => "Skipped",
        _ => item.JobState switch
        {
            JobState.PendingSignature or JobState.Queued => "Queued",
            JobState.Running => "Running",
            JobState.Succeeded => "Succeeded",
            JobState.Failed => "Failed",
            JobState.Expired => "Missed",
            JobState.Refused => "Refused",
            JobState.Lost => "Lost",
            JobState.Cancelled => "Cancelled",
            _ => "Started"
        }
    };

    private static async Task<List<TaskRunTargetItem>> LoadTargetsAsync(FleetoDbContext db, Guid runId, CancellationToken cancellationToken) =>
        (await (
                from t in db.AutomationTaskRunTargets.AsNoTracking()
                where t.RunId == runId
                join e in db.Endpoints on t.EndpointId equals e.Id
                join j in db.Jobs on t.JobId equals j.Id into jobs
                from j in jobs.DefaultIfEmpty()
                select new TaskRunTargetItem(t.EndpointId, e.Hostname, t.State, t.DueAt, t.Reason, t.JobId, (JobState?)j.State, j.ExitCode,
                    j.RefusalReason ?? j.Error, j.StartedAt, j.CompletedAt, string.Empty, (long?)j.ReceivedOutputBytes ?? 0))
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

    public static TaskInput ToInput(AutomationTask t) => new(t.Name, t.Description, t.ClientId, t.ScriptId, t.RunAs, t.AppliesTo, t.Enabled,
        t.ScheduleKind, t.TimeZone, t.StartDate, t.StartMinute, t.EndDate, t.Every, t.WeekDays, t.MonthDay, t.RepeatMinutes, t.TriggerCheckIds,
        t.OnManaged, t.RandomDelayMinutes, t.CatchUp, t.Overlap, t.AlertOnFailure, t.AlertSeverity);

    private static void Apply(AutomationTask task, TaskInput input, List<Guid> checkIds)
    {
        task.Name = input.Name?.Trim() ?? string.Empty;
        task.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        task.ScriptId = input.ScriptId;
        task.RunAs = input.RunAs;
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
    private static object Snapshot(AutomationTask task) => new
    {
        task.Name, task.ScriptId, RunAs = task.RunAs.ToString(), AppliesTo = task.AppliesTo.ToString(), task.Enabled,
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
}
