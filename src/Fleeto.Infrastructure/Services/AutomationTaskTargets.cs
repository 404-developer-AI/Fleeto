using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>An endpoint a task is linked to, and why it cannot run the task now (null when it can).</summary>
public sealed record TaskEndpoint(Guid Id, Guid ClientId, Guid SiteId, string Hostname, string OsPlatform, string? Problem);

/// <summary>
/// The one rule for which endpoints a task runs on (0.6.0), used by web (Run now, the task page), the workers (starting runs) and
/// fleeto-signer (before it signs a job of a run Fleeto started):
/// <list type="bullet">
/// <item>the task is linked to the endpoint's client, its site or the endpoint itself (the links add up, like monitoring templates);</item>
/// <item>a task of a client only reaches that client's endpoints, and its class (servers, workstations) must match the endpoint's;</item>
/// <item>only endpoints with an agent; of those, an endpoint that is not managed, runs another platform than the script's language
/// or belongs to another client than the script is linked but cannot run it, with the reason.</item>
/// </list>
/// </summary>
public static class AutomationTaskTargets
{
    public const string NotManagedProblem = "Not managed. Switch it to managed to run tasks.";
    public const string ScriptClientProblem = "The script belongs to another client.";

    /// <summary>The endpoints with an agent the task is linked to, on any level, of its client and class. Scope filters apply.</summary>
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
                                        db.EndpointAutomationTasks.Any(l => l.AutomationTaskId == taskId && l.EndpointId == e.Id)));
    }

    /// <summary>
    /// The linked endpoints with their problem, sorted by host name. <paramref name="endpointIds"/> limits them (a run for one
    /// endpoint); null takes every linked endpoint.
    /// </summary>
    public static async Task<IReadOnlyList<TaskEndpoint>> LoadAsync(FleetoDbContext db, AutomationTask task, Script script, LicenseStatus license,
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
            .Select(e => new { e.Id, e.ClientId, e.SiteId, e.Hostname, e.OsPlatform, e.Tier })
            .ToListAsync(cancellationToken);
        return endpoints.Select(e => new TaskEndpoint(e.Id, e.ClientId, e.SiteId, e.Hostname, e.OsPlatform,
            Problem(script, license, e.ClientId, e.Tier, e.OsPlatform))).ToList();
    }

    /// <summary>Why a linked endpoint cannot run the task's script now, or null.</summary>
    public static string? Problem(Script script, LicenseStatus license, Guid endpointClientId, EndpointTier tier, string osPlatform) =>
        TierRules.EffectiveTier(tier, license) != EndpointTier.Managed ? NotManagedProblem
        : !ScriptLanguages.RunsOn(script.Language, osPlatform) ? $"{ScriptLanguages.Label(script.Language)} does not run on this operating system."
        : script.ClientId is { } scriptClient && scriptClient != endpointClientId ? ScriptClientProblem
        : null;
}
