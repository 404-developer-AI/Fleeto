using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>
/// The EF Core form of <see cref="EffectivePolicyRules"/> (0.6.0): the policy and patch policy of every endpoint in the scope
/// of the context. Compose it like any query (join on <see cref="EffectivePolicyRow.EndpointId"/>); EF Core inlines it as
/// correlated subqueries, the same as <see cref="EffectivePolicyRules.PolicyIdSql"/>.
/// </summary>
public static class EffectivePolicies
{
    // A link or policy counts for an endpoint when it is for every endpoint, or for the endpoint's class:
    // x == All || ((x == Server) == (class == Server)).
    public static IQueryable<EffectivePolicyRow> Query(FleetoDbContext db) =>
        db.Endpoints.Select(e => new EffectivePolicyRow
        {
            EndpointId = e.Id,
            ClientId = e.ClientId,
            SiteId = e.SiteId,
            PolicyId = db.EndpointPolicies
                           .Where(l => l.EndpointId == e.Id &&
                                       (l.Policy!.AppliesTo == CheckAppliesTo.All ||
                                        (l.Policy.AppliesTo == CheckAppliesTo.Server) == ((e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server)))
                           .Select(l => (Guid?)l.PolicyId).FirstOrDefault()
                       ?? db.SitePolicies
                           .Where(l => l.SiteId == e.SiteId &&
                                       (l.AppliesTo == CheckAppliesTo.All ||
                                        (l.AppliesTo == CheckAppliesTo.Server) == ((e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server)) &&
                                       (l.Policy!.AppliesTo == CheckAppliesTo.All ||
                                        (l.Policy.AppliesTo == CheckAppliesTo.Server) == ((e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server)))
                           .OrderBy(l => l.AppliesTo == CheckAppliesTo.All)
                           .Select(l => (Guid?)l.PolicyId).FirstOrDefault()
                       ?? db.ClientPolicies
                           .Where(l => l.ClientId == e.ClientId &&
                                       (l.AppliesTo == CheckAppliesTo.All ||
                                        (l.AppliesTo == CheckAppliesTo.Server) == ((e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server)) &&
                                       (l.Policy!.AppliesTo == CheckAppliesTo.All ||
                                        (l.Policy.AppliesTo == CheckAppliesTo.Server) == ((e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server)))
                           .OrderBy(l => l.AppliesTo == CheckAppliesTo.All)
                           .Select(l => (Guid?)l.PolicyId).FirstOrDefault()
                       ?? db.Policies.Where(p => p.IsDefault).Select(p => (Guid?)p.Id).FirstOrDefault(),
            PatchPolicyId = db.EndpointPatchPolicies
                                .Where(l => l.EndpointId == e.Id &&
                                            (l.PatchPolicy!.AppliesTo == CheckAppliesTo.All ||
                                             (l.PatchPolicy.AppliesTo == CheckAppliesTo.Server) == ((e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server)))
                                .Select(l => (Guid?)l.PatchPolicyId).FirstOrDefault()
                            ?? db.SitePatchPolicies
                                .Where(l => l.SiteId == e.SiteId &&
                                            (l.AppliesTo == CheckAppliesTo.All ||
                                             (l.AppliesTo == CheckAppliesTo.Server) == ((e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server)) &&
                                            (l.PatchPolicy!.AppliesTo == CheckAppliesTo.All ||
                                             (l.PatchPolicy.AppliesTo == CheckAppliesTo.Server) == ((e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server)))
                                .OrderBy(l => l.AppliesTo == CheckAppliesTo.All)
                                .Select(l => (Guid?)l.PatchPolicyId).FirstOrDefault()
                            ?? db.ClientPatchPolicies
                                .Where(l => l.ClientId == e.ClientId &&
                                            (l.AppliesTo == CheckAppliesTo.All ||
                                             (l.AppliesTo == CheckAppliesTo.Server) == ((e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server)) &&
                                            (l.PatchPolicy!.AppliesTo == CheckAppliesTo.All ||
                                             (l.PatchPolicy.AppliesTo == CheckAppliesTo.Server) == ((e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server)))
                                .OrderBy(l => l.AppliesTo == CheckAppliesTo.All)
                                .Select(l => (Guid?)l.PatchPolicyId).FirstOrDefault()
        });

    /// <summary>
    /// The policy of one endpoint, or null when neither the endpoint nor a default policy exists. The caller has already
    /// decided the endpoint may be read, so the client scope is not applied again: a global default policy must be found.
    /// </summary>
    public static async Task<Policy?> LoadAsync(FleetoDbContext db, Guid endpointId, CancellationToken cancellationToken = default)
    {
        var policyId = await Query(db).IgnoreQueryFilters().Where(p => p.EndpointId == endpointId).Select(p => p.PolicyId)
            .FirstOrDefaultAsync(cancellationToken);
        return policyId is null
            ? null
            : await db.Policies.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);
    }
}
