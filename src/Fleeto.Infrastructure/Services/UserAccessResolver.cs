using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>
/// Reads the groups of a user and resolves what they give (0.6.0), for web (every page and service) and for fleeto-signer,
/// which checks again that the user behind a job or remote session token may reach the endpoint. Needs a context with the
/// system scope: the groups are what defines the scope.
/// </summary>
public static class UserAccessResolver
{
    /// <summary>
    /// The scope of a user: everything for an admin, otherwise what its groups give together; nothing without a group.
    /// </summary>
    public static async Task<IClientScope> ScopeOfAsync(FleetoDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var isAdmin = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, r) => r.NormalizedName)
            .AnyAsync(name => name == ScriptCheckResolver.AdminRole, cancellationToken);
        return isAdmin ? AccessScope.Everything : await ResolveAsync(db, userId, cancellationToken);
    }

    /// <summary>
    /// Whether a user may reach an endpoint (0.6.0): an admin always, anyone else when one of its groups gives it. Cheaper than
    /// resolving the whole scope, for fleeto-signer, which asks it for every job and remote session token.
    /// </summary>
    public static async Task<bool> MayReachAsync(FleetoDbContext db, Guid userId, Guid clientId, Guid siteId, Guid endpointId,
        CancellationToken cancellationToken)
    {
        var isAdmin = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, r) => r.NormalizedName)
            .AnyAsync(name => name == ScriptCheckResolver.AdminRole, cancellationToken);
        if (isAdmin)
        {
            return true;
        }

        var groups = await LoadAsync(db, db.UserGroups.Where(g => g.Members.Any(m => m.UserId == userId)), cancellationToken);
        return groups.Any(g => g.Reaches(clientId, siteId, endpointId));
    }

    /// <summary>What the groups of a user give together, without looking at its role.</summary>
    public static async Task<AccessScope> ResolveAsync(FleetoDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var groups = await LoadAsync(db, db.UserGroups.Where(g => g.Members.Any(m => m.UserId == userId)), cancellationToken);
        return await ResolveAsync(db, groups, cancellationToken);
    }

    /// <summary>What these groups give together.</summary>
    public static async Task<AccessScope> ResolveAsync(FleetoDbContext db, IReadOnlyCollection<GroupAccess> groups, CancellationToken cancellationToken)
    {
        if (groups.Count == 0)
        {
            return AccessScope.Nothing;
        }

        if (UserAccessRules.SeesEverything(groups))
        {
            return AccessScope.Everything;
        }

        IReadOnlyCollection<Guid> allClients = groups.Any(g => g.AllClients)
            ? await db.Clients.AsNoTracking().Select(c => c.Id).ToListAsync(cancellationToken)
            : [];
        var expand = UserAccessRules.ClientsToExpand(groups).ToArray();
        var sites = expand.Length == 0
            ? []
            : await db.Sites.AsNoTracking().Where(s => expand.Contains(s.ClientId))
                .Select(s => new AccessSite(s.Id, s.ClientId)).ToListAsync(cancellationToken);
        var endpoints = expand.Length == 0
            ? []
            : await db.Endpoints.AsNoTracking().Where(e => expand.Contains(e.ClientId))
                .Select(e => new AccessEndpoint(e.Id, e.SiteId, e.ClientId)).ToListAsync(cancellationToken);
        return UserAccessRules.Resolve(groups, new AccessCatalog(allClients, sites, endpoints));
    }

    /// <summary>
    /// The groups with their rules, each target with the client and site it is in today. A rule whose target no longer exists
    /// is gone with it (foreign key cascade).
    /// </summary>
    public static async Task<IReadOnlyList<GroupAccess>> LoadAsync(FleetoDbContext db, IQueryable<UserGroup> query, CancellationToken cancellationToken)
    {
        var groups = await query.AsNoTracking().Select(g => new { g.Id, g.Name, g.AllClients }).ToListAsync(cancellationToken);
        if (groups.Count == 0)
        {
            return [];
        }

        var groupIds = groups.Select(g => g.Id).ToArray();
        var rules = await db.UserGroupRules.AsNoTracking().Where(r => groupIds.Contains(r.GroupId)).ToListAsync(cancellationToken);
        var siteIds = rules.Where(r => r.SiteId is not null).Select(r => r.SiteId!.Value).Distinct().ToArray();
        var endpointIds = rules.Where(r => r.EndpointId is not null).Select(r => r.EndpointId!.Value).Distinct().ToArray();
        var siteClients = siteIds.Length == 0
            ? new Dictionary<Guid, Guid>()
            : await db.Sites.AsNoTracking().Where(s => siteIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.ClientId, cancellationToken);
        var endpointPlaces = endpointIds.Length == 0
            ? new Dictionary<Guid, (Guid SiteId, Guid ClientId)>()
            : (await db.Endpoints.AsNoTracking().Where(e => endpointIds.Contains(e.Id)).Select(e => new { e.Id, e.SiteId, e.ClientId })
                .ToListAsync(cancellationToken)).ToDictionary(e => e.Id, e => (e.SiteId, e.ClientId));

        AccessTarget? Target(UserGroupRule rule)
        {
            if (rule.ClientId is { } clientId)
            {
                return new AccessTarget(clientId);
            }

            if (rule.SiteId is { } siteId)
            {
                return siteClients.TryGetValue(siteId, out var client) ? new AccessTarget(client, siteId) : null;
            }

            return rule.EndpointId is { } endpointId && endpointPlaces.TryGetValue(endpointId, out var place)
                ? new AccessTarget(place.ClientId, place.SiteId, endpointId)
                : null;
        }

        return groups.Select(g =>
        {
            var own = rules.Where(r => r.GroupId == g.Id).ToList();
            return new GroupAccess(g.Id, g.Name, g.AllClients,
                own.Where(r => r.Kind == UserGroupRuleKind.Include).Select(Target).OfType<AccessTarget>().ToList(),
                own.Where(r => r.Kind == UserGroupRuleKind.Exclude).Select(Target).OfType<AccessTarget>().ToList());
        }).ToList();
    }
}
