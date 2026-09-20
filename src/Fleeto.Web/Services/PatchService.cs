using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>One update an endpoint is missing, as the patch management product reports it.</summary>
public sealed record MissingUpdateView(string Id, string Name, string Vendor, string Version, string KbNumber, PatchSeverity Severity,
    bool RebootNeeded);

/// <summary>
/// The patch state of one endpoint (0.4.0).
/// </summary>
/// <param name="Managed">False for an agent-only endpoint: patch management is a managed feature.</param>
/// <param name="Configured">False while no patch management integration is set up for this client at all.</param>
/// <param name="State">Null when the product does not know this endpoint, even though the integration is configured.</param>
public sealed record EndpointPatchView(bool Managed, bool Configured, EndpointPatchState? State, IReadOnlyList<MissingUpdateView> Missing,
    DateTime? DetailUpdatedAt);

/// <summary>Compliance of a set of endpoints: how many have patch state, and how many of those miss nothing.</summary>
/// <param name="Covered">Endpoints the product reports on.</param>
/// <param name="Compliant">Of those, the ones missing nothing.</param>
/// <param name="MissingCritical">Endpoints missing at least one update the vendor calls critical.</param>
/// <param name="NotPatched">Endpoints the product no longer patches (over the quota of the subscription).</param>
public sealed record PatchCompliance(int Covered, int Compliant, int MissingCritical, int NotPatched)
{
    public static readonly PatchCompliance None = new(0, 0, 0, 0);

    /// <summary>Percentage of covered endpoints that miss nothing, or null when nothing is covered.</summary>
    public int? Percentage => Covered == 0 ? null : (int)Math.Round(Compliant * 100d / Covered);
}

/// <summary>
/// What Fleeto shows about patch management (0.4.0). Action1 does the patching; this reads the state the workers stored,
/// with the same client scope and tier rules as everything else. Nothing here calls Action1: web has no outbound access.
/// </summary>
public sealed class PatchService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;

    public PatchService(IFleetoDbContextFactory dbFactory, LicenseService licenses)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
    }

    /// <summary>The patch state of one endpoint. Null when the endpoint does not exist or is not visible to the caller.</summary>
    public async Task<EndpointPatchView?> GetAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new { e.Tier, e.ClientId }).SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var managed = TierRules.EffectiveTier(endpoint.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) == EndpointTier.Managed;
        var configured = await db.IntegrationMappings.AsNoTracking().AnyAsync(m => m.ClientId == endpoint.ClientId, cancellationToken);
        if (!managed || !configured)
        {
            return new EndpointPatchView(managed, configured, null, [], null);
        }

        var state = await db.EndpointPatchStates.AsNoTracking().SingleOrDefaultAsync(p => p.EndpointId == endpointId, cancellationToken);
        if (state is null)
        {
            return new EndpointPatchView(true, true, null, [], null);
        }

        var missing = await db.EndpointMissingUpdates.AsNoTracking()
            .Where(u => u.EndpointId == endpointId)
            .Select(u => new MissingUpdateView(u.ExternalUpdateId, u.Name, u.Vendor, u.Version, u.KbNumber, u.Severity, u.RebootNeeded))
            .ToListAsync(cancellationToken);
        // Sorted here, not in the database: the severity is stored by name, so the database would sort it alphabetically
        // and put "Low" above "Critical". One endpoint has tens of missing updates, not thousands.
        missing = [.. missing.OrderByDescending(u => u.Severity).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)];
        var detailAt = await db.EndpointMissingUpdates.AsNoTracking()
            .Where(u => u.EndpointId == endpointId)
            .MaxAsync(u => (DateTime?)u.UpdatedAt, cancellationToken);

        return new EndpointPatchView(true, true, state, missing, detailAt);
    }

    /// <summary>
    /// Compliance over the endpoints the caller may see, optionally of one client or one site. Counts rows of patch state,
    /// so an endpoint without it (no Action1 agent, or not managed) is not counted as compliant.
    /// </summary>
    public async Task<PatchCompliance> GetComplianceAsync(Caller caller, Guid? clientId = null, Guid? siteId = null,
        CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var states = db.EndpointPatchStates.AsNoTracking();
        if (clientId is { } client)
        {
            states = states.Where(p => p.ClientId == client);
        }

        if (siteId is { } site)
        {
            states = states.Where(p => db.Endpoints.Any(e => e.Id == p.EndpointId && e.SiteId == site));
        }

        var counts = await states
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Covered = g.Count(),
                Compliant = g.Count(p => p.MissingCritical == 0 && p.MissingOther == 0),
                MissingCritical = g.Count(p => p.MissingCritical > 0),
                NotPatched = g.Count(p => p.Coverage == PatchCoverage.Inactive)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return counts is null
            ? PatchCompliance.None
            : new PatchCompliance(counts.Covered, counts.Compliant, counts.MissingCritical, counts.NotPatched);
    }
}
