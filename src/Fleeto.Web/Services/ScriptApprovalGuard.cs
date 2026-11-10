using System.Linq.Expressions;
using Fleeto.Core.Domain;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>
/// The one rule for taking endpoints out from under the approval of scripts: an action by a caller without Approve scripts is refused
/// when an endpoint it affects moves from an effective policy that requires approval of scripts to one that does not. Switching the
/// flag off inside a policy is the obvious way; linking, unlinking, deleting a policy, narrowing or widening the endpoints a policy is
/// for, moving an endpoint, overriding its class and changing a client template do the same and are judged here.
/// <para>
/// Use: <see cref="BeforeAsync"/> before the change, then the change inside a transaction and saved, then
/// <see cref="LosesApprovalAsync"/> before the commit; when it is true, return <see cref="Problem"/> without committing. Both steps
/// are one set-based statement over <see cref="EffectivePolicies"/>, and cost nothing for a caller that may approve scripts.
/// </para>
/// </summary>
public static class ScriptApprovalGuard
{
    public const string Problem =
        "This change would take endpoints out from under a policy that requires approval of scripts, and only somebody who may approve scripts can do that. Ask an admin, or choose a policy that requires approval.";

    /// <summary>
    /// The endpoints among <paramref name="affected"/> whose policy requires approval of scripts now, or null when the caller may approve
    /// scripts (nothing to guard). Every client counts: the policy of an endpoint the caller cannot see is still taken away by the change.
    /// </summary>
    public static async Task<List<Guid>?> BeforeAsync(FleetoDbContext db, Caller caller, Expression<Func<EffectivePolicyRow, bool>> affected,
        CancellationToken cancellationToken)
    {
        if (caller.Has(Permissions.ScriptsApprove))
        {
            return null;
        }

        return await Requiring(db).Where(affected).Select(r => r.EndpointId).ToListAsync(cancellationToken);
    }

    /// <summary>True when one of the endpoints <see cref="BeforeAsync"/> found no longer has a policy that requires approval of scripts.</summary>
    public static async Task<bool> LosesApprovalAsync(FleetoDbContext db, List<Guid>? guarded, CancellationToken cancellationToken)
    {
        if (guarded is not { Count: > 0 })
        {
            return false;
        }

        // None of the guarded actions removes an endpoint, so every guarded endpoint must still require approval.
        return await Requiring(db).CountAsync(r => guarded.Contains(r.EndpointId), cancellationToken) < guarded.Count;
    }

    private static IQueryable<EffectivePolicyRow> Requiring(FleetoDbContext db) =>
        EffectivePolicies.Query(db).IgnoreQueryFilters()
            .Where(r => db.Policies.Any(p => p.Id == r.PolicyId && p.ScriptApprovalRequired));
}
