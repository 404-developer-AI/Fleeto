using Fleeto.Core.Domain;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>What a template is that <see cref="TemplateAccess"/> judges.</summary>
internal enum TemplateKind
{
    Policy,
    MonitoringTemplate,
    PatchPolicy,
    Task
}

/// <summary>
/// Who may change a policy, monitoring template or patch policy (0.6.0). They are linked, not copied, so a change applies to
/// every client, site and endpoint that uses one: a user whose groups give part of the instance may change one only when it
/// has access to everything it is used on. One of a client is of the client as a whole. Admins and users that see every
/// client may change every one.
/// </summary>
internal static class TemplateAccess
{
    public const string DefaultPolicyProblem =
        "The default policy applies to every endpoint without a policy of its own, also where your groups give you no access. Ask an admin to change it.";

    public static string UsedElsewhereProblem(string what) =>
        $"This {what} is also used where your groups give you no access, and a change would apply there too. Ask an admin to change it, or make a copy for your client.";

    public const string GlobalScriptProblem =
        "A script for every client can be run on clients your groups give you no access to. Ask an admin to change it, or make a script for your client.";

    /// <summary>
    /// Null when the caller may change the template, with <see cref="FleetoDbContext.AllowInstanceWideWrites"/> called on
    /// <paramref name="db"/> for an instance-wide one; otherwise why not.
    /// </summary>
    public static async Task<string?> ProblemAsync(FleetoDbContext db, Caller caller, TemplateKind kind, Guid id, Guid? ownerClientId,
        bool isDefault, CancellationToken cancellationToken)
    {
        if (caller.Scope.AllClients)
        {
            return null;
        }

        if (ownerClientId is { } clientId)
        {
            return caller.HasWholeClient(clientId) ? null : ServiceResult.WholeClientProblem;
        }

        if (isDefault)
        {
            return DefaultPolicyProblem;
        }

        var (clients, sites, endpoints) = await UsesAsync(db, kind, id, cancellationToken);
        var scope = caller.Scope;
        if (clients.All(scope.HasWholeClient) && sites.All(s => scope.HasWholeSite(s.ClientId, s.Id)) &&
            endpoints.All(e => scope.SeesEndpoint(e.ClientId, e.Id)))
        {
            db.AllowInstanceWideWrites();
            return null;
        }

        return UsedElsewhereProblem(kind switch
        {
            TemplateKind.Policy => "policy",
            TemplateKind.PatchPolicy => "patch policy",
            TemplateKind.Task => "task",
            _ => "monitoring template"
        });
    }

    /// <summary>
    /// Null when the caller may create an instance-wide template (it is used nowhere yet), with writes allowed on
    /// <paramref name="db"/>; a template of a client needs the whole client.
    /// </summary>
    public static string? CreateProblem(FleetoDbContext db, Caller caller, Guid? clientId)
    {
        if (caller.Scope.AllClients)
        {
            return null;
        }

        if (clientId is { } owner)
        {
            return caller.HasWholeClient(owner) ? null : ServiceResult.WholeClientProblem;
        }

        db.AllowInstanceWideWrites();
        return null;
    }

    /// <summary>
    /// Null when the caller may create or change a script of this owner: one for every client only for a caller that sees every
    /// client, as it can be run anywhere; one of a client with the whole client.
    /// </summary>
    public static string? ScriptProblem(Caller caller, Guid? clientId) =>
        caller.Scope.AllClients ? null
        : clientId is not { } owner ? GlobalScriptProblem
        : caller.HasWholeClient(owner) ? null
        : ServiceResult.WholeClientProblem;

    /// <summary>Every link of the template, whatever the scope of the context.</summary>
    private static async Task<(List<Guid> Clients, List<(Guid Id, Guid ClientId)> Sites, List<(Guid Id, Guid ClientId)> Endpoints)> UsesAsync(
        FleetoDbContext db, TemplateKind kind, Guid id, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case TemplateKind.Policy:
                return (await db.ClientPolicies.IgnoreQueryFilters().Where(l => l.PolicyId == id).Select(l => l.ClientId).ToListAsync(cancellationToken),
                    (await db.SitePolicies.IgnoreQueryFilters().Where(l => l.PolicyId == id).Select(l => new { l.SiteId, l.ClientId })
                        .ToListAsync(cancellationToken)).Select(l => (l.SiteId, l.ClientId)).ToList(),
                    (await db.EndpointPolicies.IgnoreQueryFilters().Where(l => l.PolicyId == id).Select(l => new { l.EndpointId, l.ClientId })
                        .ToListAsync(cancellationToken)).Select(l => (l.EndpointId, l.ClientId)).ToList());
            case TemplateKind.PatchPolicy:
                return (await db.ClientPatchPolicies.IgnoreQueryFilters().Where(l => l.PatchPolicyId == id).Select(l => l.ClientId).ToListAsync(cancellationToken),
                    (await db.SitePatchPolicies.IgnoreQueryFilters().Where(l => l.PatchPolicyId == id).Select(l => new { l.SiteId, l.ClientId })
                        .ToListAsync(cancellationToken)).Select(l => (l.SiteId, l.ClientId)).ToList(),
                    (await db.EndpointPatchPolicies.IgnoreQueryFilters().Where(l => l.PatchPolicyId == id).Select(l => new { l.EndpointId, l.ClientId })
                        .ToListAsync(cancellationToken)).Select(l => (l.EndpointId, l.ClientId)).ToList());
            case TemplateKind.Task:
                return (await db.ClientAutomationTasks.IgnoreQueryFilters().Where(l => l.AutomationTaskId == id).Select(l => l.ClientId)
                        .ToListAsync(cancellationToken),
                    (await db.SiteAutomationTasks.IgnoreQueryFilters().Where(l => l.AutomationTaskId == id).Select(l => new { l.SiteId, l.ClientId })
                        .ToListAsync(cancellationToken)).Select(l => (l.SiteId, l.ClientId)).ToList(),
                    (await db.EndpointAutomationTasks.IgnoreQueryFilters().Where(l => l.AutomationTaskId == id)
                        .Select(l => new { l.EndpointId, l.ClientId }).ToListAsync(cancellationToken)).Select(l => (l.EndpointId, l.ClientId)).ToList());
            default:
                return (await db.ClientMonitoringTemplates.IgnoreQueryFilters().Where(l => l.MonitoringTemplateId == id).Select(l => l.ClientId)
                        .ToListAsync(cancellationToken),
                    (await db.SiteMonitoringTemplates.IgnoreQueryFilters().Where(l => l.MonitoringTemplateId == id).Select(l => new { l.SiteId, l.ClientId })
                        .ToListAsync(cancellationToken)).Select(l => (l.SiteId, l.ClientId)).ToList(),
                    (await db.EndpointMonitoringTemplates.IgnoreQueryFilters().Where(l => l.MonitoringTemplateId == id)
                        .Select(l => new { l.EndpointId, l.ClientId }).ToListAsync(cancellationToken)).Select(l => (l.EndpointId, l.ClientId)).ToList());
        }
    }
}
