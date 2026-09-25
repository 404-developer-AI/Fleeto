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
    public static IQueryable<EffectivePolicyRow> Query(FleetoDbContext db) =>
        db.Endpoints.Select(e => new EffectivePolicyRow
        {
            EndpointId = e.Id,
            ClientId = e.ClientId,
            SiteId = e.SiteId,
            PolicyId = db.EndpointPolicies.Where(l => l.EndpointId == e.Id).Select(l => (Guid?)l.PolicyId).FirstOrDefault()
                       ?? db.SitePolicies.Where(l => l.SiteId == e.SiteId).Select(l => (Guid?)l.PolicyId).FirstOrDefault()
                       ?? db.ClientPolicies.Where(l => l.ClientId == e.ClientId).Select(l => (Guid?)l.PolicyId).FirstOrDefault()
                       ?? db.Policies.Where(p => p.IsDefault).Select(p => (Guid?)p.Id).FirstOrDefault(),
            PatchPolicyId = db.EndpointPatchPolicies.Where(l => l.EndpointId == e.Id).Select(l => (Guid?)l.PatchPolicyId).FirstOrDefault()
                            ?? db.SitePatchPolicies.Where(l => l.SiteId == e.SiteId).Select(l => (Guid?)l.PatchPolicyId).FirstOrDefault()
                            ?? db.ClientPatchPolicies.Where(l => l.ClientId == e.ClientId).Select(l => (Guid?)l.PatchPolicyId).FirstOrDefault()
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

    /// <summary>The policy of a site's endpoints that have no link of their own: the site's, else the client's, else the default.</summary>
    public static IQueryable<SitePolicyRow> Sites(FleetoDbContext db) =>
        db.Sites.Select(s => new SitePolicyRow
        {
            SiteId = s.Id,
            ClientId = s.ClientId,
            PolicyId = db.SitePolicies.Where(l => l.SiteId == s.Id).Select(l => (Guid?)l.PolicyId).FirstOrDefault()
                       ?? db.ClientPolicies.Where(l => l.ClientId == s.ClientId).Select(l => (Guid?)l.PolicyId).FirstOrDefault()
                       ?? db.Policies.Where(p => p.IsDefault).Select(p => (Guid?)p.Id).FirstOrDefault(),
            PatchPolicyId = db.SitePatchPolicies.Where(l => l.SiteId == s.Id).Select(l => (Guid?)l.PatchPolicyId).FirstOrDefault()
                            ?? db.ClientPatchPolicies.Where(l => l.ClientId == s.ClientId).Select(l => (Guid?)l.PatchPolicyId).FirstOrDefault()
        });
}

/// <summary>The policy and patch policy a site passes on to its endpoints.</summary>
public sealed class SitePolicyRow
{
    public Guid SiteId { get; init; }
    public Guid ClientId { get; init; }
    public Guid? PolicyId { get; init; }
    public Guid? PatchPolicyId { get; init; }
}
