using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>An endpoint a task is linked to, and why it cannot run the task now (null when it can).</summary>
public sealed record TaskEndpoint(Guid Id, Guid ClientId, Guid SiteId, string Hostname, string OsPlatform, string? Problem);

/// <summary>
/// The steps of a task in order, with the scripts they run (0.6.0, steps). A task saved before steps existed, or by a build
/// without them, has no step rows: its script is then its one step, with an empty id (its job carries no step).
/// </summary>
public sealed class TaskPlan
{
    private TaskPlan(IReadOnlyList<AutomationTaskStep> steps, IReadOnlyDictionary<Guid, Script> scripts)
    {
        Steps = steps;
        Scripts = scripts;
    }

    public IReadOnlyList<AutomationTaskStep> Steps { get; }

    /// <summary>The scripts of the script steps that still exist, by id.</summary>
    public IReadOnlyDictionary<Guid, Script> Scripts { get; }

    public AutomationTaskStep First => Steps[0];

    public bool HasRestart => Steps.Any(s => s.Kind == TaskStepKind.Restart);

    /// <summary>The step of a job: by its id, or the first step for a job without one.</summary>
    public AutomationTaskStep? Find(Guid? stepId) => stepId is { } id ? Steps.FirstOrDefault(s => s.Id == id) : First;

    /// <summary>The step after this one, or null for the last.</summary>
    public AutomationTaskStep? After(AutomationTaskStep step) => Steps.FirstOrDefault(s => s.Position > step.Position);

    /// <summary>The script a step runs, or null for a restart or a script that no longer exists.</summary>
    public Script? ScriptOf(AutomationTaskStep step) => step.ScriptId is { } id ? Scripts.GetValueOrDefault(id) : null;

    /// <summary>"step 2 (Stop containers)" when the task has more than one step; null for a task of one step.</summary>
    public string? Label(AutomationTaskStep step) =>
        Steps.Count <= 1 ? null : AutomationTasks.StepLabel(step.Position, step.Kind, ScriptOf(step)?.Name);

    /// <summary>The plan of one task, from the database whatever the scope (a step's script is global or of the task's client).</summary>
    public static async Task<TaskPlan> LoadAsync(FleetoDbContext db, AutomationTask task, CancellationToken cancellationToken) =>
        (await LoadManyAsync(db, [task], cancellationToken))[task.Id];

    public static async Task<IReadOnlyDictionary<Guid, TaskPlan>> LoadManyAsync(FleetoDbContext db, IReadOnlyCollection<AutomationTask> tasks,
        CancellationToken cancellationToken)
    {
        var ids = tasks.Select(t => t.Id).ToList();
        var steps = (await db.AutomationTaskSteps.IgnoreQueryFilters().AsNoTracking().Where(s => ids.Contains(s.AutomationTaskId))
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.AutomationTaskId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Position).ToList());
        var plans = new Dictionary<Guid, IReadOnlyList<AutomationTaskStep>>();
        foreach (var task in tasks)
        {
            plans[task.Id] = steps.TryGetValue(task.Id, out var own) && own.Count > 0 ? own : [Legacy(task)];
        }

        var scriptIds = plans.Values.SelectMany(p => p).Where(s => s.ScriptId is not null).Select(s => s.ScriptId!.Value).Distinct().ToList();
        var scripts = await db.Scripts.IgnoreQueryFilters().AsNoTracking().Where(s => scriptIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        return plans.ToDictionary(p => p.Key, p => new TaskPlan(p.Value, scripts));
    }

    /// <summary>A plan from steps in memory, in their order (a task being saved).</summary>
    public static TaskPlan From(AutomationTask task, IReadOnlyList<AutomationTaskStep> steps, IReadOnlyDictionary<Guid, Script> scripts) =>
        new(steps.Count > 0 ? steps : [Legacy(task)], scripts);

    /// <summary>The one step of a task without step rows: its own script and account.</summary>
    private static AutomationTaskStep Legacy(AutomationTask task) => new()
    {
        Id = Guid.Empty, AutomationTaskId = task.Id, ClientId = task.ClientId, Position = 1, Kind = TaskStepKind.Script, ScriptId = task.ScriptId,
        RunAs = task.RunAs
    };
}

/// <summary>
/// The one rule for which endpoints a task runs on (0.6.0), used by web (Run now, the task page), the workers (starting runs) and
/// fleeto-signer (before it signs a job of a run Fleeto started):
/// <list type="bullet">
/// <item>the task is linked to the endpoint's client, its site or the endpoint itself (the links add up, like monitoring templates),
/// or to a monitoring template the endpoint gets: one linked to its client, site or itself and for its class;</item>
/// <item>a task of a client only reaches that client's endpoints, and its class (servers, workstations) must match the endpoint's;</item>
/// <item>only endpoints with an agent; of those, an endpoint that is not managed, runs another platform than a script of a step,
/// belongs to another client than a script, or cannot restart for a restart step is linked but cannot run it, with the reason.</item>
/// </list>
/// </summary>
public static class AutomationTaskTargets
{
    public const string NotManagedProblem = "Not managed. Switch it to managed to run tasks.";
    public const string ScriptClientProblem = "The script belongs to another client.";
    public const string ScriptGoneProblem = "The script is no longer available.";

    /// <summary>
    /// The endpoints with an agent the task is linked to, on any level or through a monitoring template, of its client and class.
    /// Scope filters apply.
    /// </summary>
    public static IQueryable<Endpoint> Linked(FleetoDbContext db, AutomationTask task)
    {
        var taskId = task.Id;
        var clientId = task.ClientId;
        EndpointClass? endpointClass = task.AppliesTo switch
        {
            CheckAppliesTo.Server => EndpointClass.Server,
            CheckAppliesTo.Workstation => EndpointClass.Workstation,
            _ => null
        };
        return db.Endpoints.Where(e => e.Source == EndpointSource.Agent &&
                                       (clientId == null || e.ClientId == clientId) &&
                                       (endpointClass == null || (e.ClassOverride ?? e.DetectedClass) == endpointClass) &&
                                       (db.ClientAutomationTasks.Any(l => l.AutomationTaskId == taskId && l.ClientId == e.ClientId) ||
                                        db.SiteAutomationTasks.Any(l => l.AutomationTaskId == taskId && l.SiteId == e.SiteId) ||
                                        db.EndpointAutomationTasks.Any(l => l.AutomationTaskId == taskId && l.EndpointId == e.Id) ||
                                        db.MonitoringTemplateAutomationTasks.Any(l => l.AutomationTaskId == taskId &&
                                            db.MonitoringTemplates.Any(t => t.Id == l.MonitoringTemplateId &&
                                                (t.AppliesTo == CheckAppliesTo.All ||
                                                 (t.AppliesTo == CheckAppliesTo.Server && (e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server) ||
                                                 (t.AppliesTo == CheckAppliesTo.Workstation && (e.ClassOverride ?? e.DetectedClass) == EndpointClass.Workstation))) &&
                                            (db.ClientMonitoringTemplates.Any(m => m.MonitoringTemplateId == l.MonitoringTemplateId && m.ClientId == e.ClientId) ||
                                             db.SiteMonitoringTemplates.Any(m => m.MonitoringTemplateId == l.MonitoringTemplateId && m.SiteId == e.SiteId) ||
                                             db.EndpointMonitoringTemplates.Any(m => m.MonitoringTemplateId == l.MonitoringTemplateId && m.EndpointId == e.Id)))));
    }

    /// <summary>
    /// The linked endpoints with their problem, sorted by host name. <paramref name="endpointIds"/> limits them (a run for one
    /// endpoint); null takes every linked endpoint.
    /// </summary>
    public static async Task<IReadOnlyList<TaskEndpoint>> LoadAsync(FleetoDbContext db, AutomationTask task, TaskPlan plan, LicenseStatus license,
        IReadOnlyCollection<Guid>? endpointIds, CancellationToken cancellationToken)
    {
        var query = Linked(db, task);
        if (endpointIds is not null)
        {
            var ids = endpointIds.ToList();
            query = query.Where(e => ids.Contains(e.Id));
        }

        var endpoints = await query.AsNoTracking()
            .OrderBy(e => e.Hostname)
            .Select(e => new { e.Id, e.ClientId, e.SiteId, e.Hostname, e.OsPlatform, e.Tier, e.AgentVersion })
            .ToListAsync(cancellationToken);
        return endpoints.Select(e => new TaskEndpoint(e.Id, e.ClientId, e.SiteId, e.Hostname, e.OsPlatform,
            Problem(plan, license, e.ClientId, e.Tier, e.OsPlatform, e.AgentVersion))).ToList();
    }

    /// <summary>
    /// Why a linked endpoint cannot run the task now, or null: it is not managed, a script of a step is gone, does not run on its
    /// operating system or belongs to another client, or a restart step cannot restart it. Checked for every step before the
    /// first starts, so a run never stops half way for something known beforehand.
    /// </summary>
    public static string? Problem(TaskPlan plan, LicenseStatus license, Guid endpointClientId, EndpointTier tier, string osPlatform, string? agentVersion)
    {
        if (TierRules.EffectiveTier(tier, license) != EndpointTier.Managed)
        {
            return NotManagedProblem;
        }

        foreach (var step in plan.Steps)
        {
            var prefix = plan.Label(step) is { } label ? char.ToUpperInvariant(label[0]) + label[1..] + ": " : string.Empty;
            if (step.Kind == TaskStepKind.Restart)
            {
                if (RestartRules.Problem(osPlatform, agentVersion) is { } restart)
                {
                    return prefix + restart;
                }

                continue;
            }

            if (plan.ScriptOf(step) is not { } script)
            {
                return prefix + ScriptGoneProblem;
            }

            if (!ScriptLanguages.RunsOn(script.Language, osPlatform))
            {
                return prefix + $"{ScriptLanguages.Label(script.Language)} does not run on this operating system.";
            }

            if (script.ClientId is { } scriptClient && scriptClient != endpointClientId)
            {
                return prefix + ScriptClientProblem;
            }
        }

        return null;
    }
}
