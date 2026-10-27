using Fleeto.Core.Dashboards;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>The scope a tile reads in: the caller with a narrower client scope, and the class it counts.</summary>
public sealed record TileReadScope(Caller Caller, EndpointClass? Class, string Key);

/// <summary>
/// Turns the scope of a tile (DASHBOARDS.md §3) into the scope it reads in. A scope only narrows: the result is the intersection
/// of what the tile names and what the groups of the caller give, as an <see cref="AccessScope"/>, so every service and query
/// filter applies it as it applies the groups. Clients, sites and tags that no longer exist are left out; a scope that names
/// nothing that exists any more follows the scope of the dashboard.
/// </summary>
public sealed class DashboardScopes
{
    private readonly IFleetoDbContextFactory _dbFactory;

    public DashboardScopes(IFleetoDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>The scope of a tile: its own, or the dashboard's, with its own class when it sets one.</summary>
    public static TileScope Effective(TileScope dashboard, TileSettings settings) =>
        settings.Scope switch
        {
            null => dashboard,
            { Mode: TileScopeMode.Dashboard } own => dashboard with { Class = own.Class ?? dashboard.Class },
            var own => own
        };

    /// <summary>Resolves scopes for one render of a dashboard; equal scopes resolve once.</summary>
    public async Task<Func<TileScope, Task<TileReadScope>>> ResolverAsync(Caller caller, TileScope dashboard,
        CancellationToken cancellationToken = default)
    {
        var cache = new Dictionary<string, TileReadScope>(StringComparer.Ordinal);
        var dashboardScope = await NarrowAsync(caller, dashboard, null, cancellationToken);
        cache[dashboard.Key()] = dashboardScope;
        return async scope =>
        {
            var key = scope.Key();
            if (!cache.TryGetValue(key, out var resolved))
            {
                resolved = await NarrowAsync(caller, scope, dashboardScope, cancellationToken);
                cache[key] = resolved;
            }

            return resolved;
        };
    }

    /// <summary>
    /// The caller narrowed to <paramref name="scope"/>. <paramref name="fallback"/> is used when the scope names nothing that still
    /// exists; without one (the dashboard's own scope) that means every client the caller sees.
    /// </summary>
    public async Task<TileReadScope> NarrowAsync(Caller caller, TileScope scope, TileReadScope? fallback, CancellationToken cancellationToken = default)
    {
        scope = scope.Normalized();
        if (scope.Mode is TileScopeMode.All or TileScopeMode.Dashboard)
        {
            return new TileReadScope(caller, scope.Class, scope.Key());
        }

        // Ids only, in system context: what exists is decided here, what the caller may see by its own scope below.
        await using var db = _dbFactory.CreateSystem();
        var ids = scope.Ids.ToArray();
        List<Guid> clientIds = [];
        List<AccessSite> sites = [];
        switch (scope.Mode)
        {
            case TileScopeMode.Clients:
                clientIds = await db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken);
                break;
            case TileScopeMode.Tags:
                clientIds = await db.ClientTags.AsNoTracking().Where(l => ids.Contains(l.TagId)).Select(l => l.ClientId).Distinct()
                    .ToListAsync(cancellationToken);
                if (clientIds.Count == 0 && await db.Tags.AnyAsync(t => ids.Contains(t.Id), cancellationToken))
                {
                    // The tags exist but no client carries them: the tile shows nothing, not everything.
                    return new TileReadScope(caller with { Scope = AccessScope.Nothing }, scope.Class, scope.Key());
                }

                break;
            case TileScopeMode.Sites:
                sites = await db.Sites.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new AccessSite(s.Id, s.ClientId))
                    .ToListAsync(cancellationToken);
                break;
        }

        if (clientIds.Count == 0 && sites.Count == 0)
        {
            return fallback is null
                ? new TileReadScope(caller, scope.Class, TileScope.Everything.Key())
                : fallback with { Class = scope.Class ?? fallback.Class, Key = fallback.Key + "|" + scope.Class };
        }

        var narrowed = await IntersectAsync(db, caller.Scope, clientIds, sites, cancellationToken);
        return new TileReadScope(caller with { Scope = narrowed }, scope.Class, scope.Key());
    }

    /// <summary>
    /// The part of <paramref name="user"/> that lies within the clients and sites named. A client or site the user sees wholly is
    /// given wholly; one it sees in part only with the sites and endpoints it sees.
    /// </summary>
    internal static async Task<AccessScope> IntersectAsync(FleetoDbContext db, IClientScope user, IReadOnlyCollection<Guid> clientIds,
        IReadOnlyCollection<AccessSite> sites, CancellationToken cancellationToken)
    {
        HashSet<Guid> clients = [], fullClients = [], fullSites = [], siteIds = [], endpoints = [];
        var partialClients = new List<Guid>();
        foreach (var clientId in clientIds)
        {
            if (user.HasWholeClient(clientId))
            {
                clients.Add(clientId);
                fullClients.Add(clientId);
            }
            else if (user.SeesClient(clientId))
            {
                clients.Add(clientId);
                partialClients.Add(clientId);
            }
        }

        if (partialClients.Count > 0)
        {
            foreach (var site in await db.Sites.AsNoTracking().Where(s => partialClients.Contains(s.ClientId)).Select(s => s.Id)
                         .ToListAsync(cancellationToken))
            {
                if (user.FullSiteIds.Contains(site))
                {
                    fullSites.Add(site);
                }

                if (user.SiteIds.Contains(site))
                {
                    siteIds.Add(site);
                }
            }

            var userEndpoints = user.EndpointIds.ToHashSet();
            endpoints.UnionWith((await db.Endpoints.AsNoTracking().Where(e => partialClients.Contains(e.ClientId)).Select(e => e.Id)
                .ToListAsync(cancellationToken)).Where(userEndpoints.Contains));
        }

        var wholeSites = new List<Guid>();
        var partSites = new List<Guid>();
        foreach (var site in sites)
        {
            if (user.HasWholeSite(site.ClientId, site.Id))
            {
                clients.Add(site.ClientId);
                fullSites.Add(site.Id);
                siteIds.Add(site.Id);
                wholeSites.Add(site.Id);
            }
            else if (user.SeesSite(site.ClientId, site.Id))
            {
                clients.Add(site.ClientId);
                siteIds.Add(site.Id);
                partSites.Add(site.Id);
            }
        }

        if (wholeSites.Count > 0 || partSites.Count > 0)
        {
            var userEndpoints = user.EndpointIds.ToHashSet();
            var rows = await db.Endpoints.AsNoTracking().Where(e => wholeSites.Contains(e.SiteId) || partSites.Contains(e.SiteId))
                .Select(e => new { e.Id, e.SiteId }).ToListAsync(cancellationToken);
            endpoints.UnionWith(rows.Where(r => wholeSites.Contains(r.SiteId) || userEndpoints.Contains(r.Id)).Select(r => r.Id));
        }

        return new AccessScope(false, false, [.. clients], [.. fullClients], [.. fullSites], [.. siteIds], [.. endpoints]);
    }
}
