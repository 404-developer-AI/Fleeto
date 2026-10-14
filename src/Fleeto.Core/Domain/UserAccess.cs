using Fleeto.Core.Interfaces;

namespace Fleeto.Core.Domain;

/// <summary>How much of a client or site a group or scope reaches.</summary>
public enum AccessLevel
{
    None,

    /// <summary>Some of its sites or endpoints.</summary>
    Partial,

    /// <summary>All of it, also what is added later.</summary>
    Full
}

/// <summary>
/// A client, site or endpoint a group rule names, with the client and site it is in today. <see cref="SiteId"/> is set for a
/// site and for an endpoint, <see cref="EndpointId"/> only for an endpoint.
/// </summary>
public sealed record AccessTarget(Guid ClientId, Guid? SiteId = null, Guid? EndpointId = null)
{
    public bool IsClient => SiteId is null && EndpointId is null;
    public bool IsSite => SiteId is not null && EndpointId is null;
    public bool IsEndpoint => EndpointId is not null;
}

/// <summary>
/// What one group gives (0.6.0): every client or the clients, sites and endpoints it includes, minus what it excludes. An
/// exclude counts only inside its own group: another group can still give the same endpoint.
/// <para>
/// <see cref="Reaches"/> and a whole site or client agree exactly with <see cref="UserAccessRules.Resolve"/>. Part of a site or
/// client is an upper bound: without the catalog the group cannot know that every endpoint left was excluded one by one.
/// </para>
/// </summary>
public sealed record GroupAccess(Guid Id, string Name, bool AllClients, IReadOnlyList<AccessTarget> Includes, IReadOnlyList<AccessTarget> Excludes)
{
    private bool ClientExcluded(Guid clientId) => Excludes.Any(x => x.IsClient && x.ClientId == clientId);
    private bool SiteExcluded(Guid siteId) => Excludes.Any(x => x.IsSite && x.SiteId == siteId);
    private bool EndpointExcluded(Guid endpointId) => Excludes.Any(x => x.EndpointId == endpointId);

    private bool CoversClient(Guid clientId) => AllClients || Includes.Any(i => i.IsClient && i.ClientId == clientId);
    private bool CoversSite(Guid clientId, Guid siteId) => CoversClient(clientId) || Includes.Any(i => i.IsSite && i.SiteId == siteId);

    /// <summary>Whether the group gives this endpoint.</summary>
    public bool Reaches(Guid clientId, Guid siteId, Guid endpointId) =>
        !ClientExcluded(clientId) && !SiteExcluded(siteId) && !EndpointExcluded(endpointId) &&
        (CoversSite(clientId, siteId) || Includes.Any(i => i.EndpointId == endpointId));

    /// <summary>How much of this site the group gives.</summary>
    public AccessLevel ReachesSite(Guid clientId, Guid siteId)
    {
        if (ClientExcluded(clientId) || SiteExcluded(siteId))
        {
            return AccessLevel.None;
        }

        if (CoversSite(clientId, siteId))
        {
            return Excludes.Any(x => x.IsEndpoint && x.SiteId == siteId) ? AccessLevel.Partial : AccessLevel.Full;
        }

        return Includes.Any(i => i.IsEndpoint && i.SiteId == siteId && !EndpointExcluded(i.EndpointId!.Value))
            ? AccessLevel.Partial
            : AccessLevel.None;
    }

    /// <summary>How much of this client the group gives.</summary>
    public AccessLevel ReachesClient(Guid clientId)
    {
        if (ClientExcluded(clientId))
        {
            return AccessLevel.None;
        }

        if (CoversClient(clientId))
        {
            return Excludes.Any(x => !x.IsClient && x.ClientId == clientId) ? AccessLevel.Partial : AccessLevel.Full;
        }

        return Includes.Any(i => !i.IsClient && i.ClientId == clientId && !SiteExcluded(i.SiteId!.Value) &&
                                 (i.EndpointId is not { } endpointId || !EndpointExcluded(endpointId)))
            ? AccessLevel.Partial
            : AccessLevel.None;
    }
}

/// <summary>A site or endpoint as <see cref="UserAccessRules.Resolve"/> needs to know it.</summary>
public readonly record struct AccessSite(Guid Id, Guid ClientId);

/// <inheritdoc cref="AccessSite"/>
public readonly record struct AccessEndpoint(Guid Id, Guid SiteId, Guid ClientId);

/// <summary>
/// What <see cref="UserAccessRules.Resolve"/> needs from the database: every client id (only when a group is for all
/// clients), and the sites and endpoints of the clients in <see cref="UserAccessRules.ClientsToExpand"/>.
/// </summary>
public sealed record AccessCatalog(IReadOnlyCollection<Guid> AllClientIds, IReadOnlyCollection<AccessSite> Sites, IReadOnlyCollection<AccessEndpoint> Endpoints);

/// <summary>
/// Turns the groups of a user into the scope every query is filtered on (0.6.0). Computed when the user is resolved, so a new
/// client, site or endpoint is part of it at once, and nothing has to be kept in step when one is added, moved or deleted.
/// </summary>
public static class UserAccessRules
{
    /// <summary>Longest group name.</summary>
    public const int MaxNameLength = 100;

    public const int MaxDescriptionLength = 500;

    /// <summary>Clients whose sites and endpoints <see cref="Resolve"/> needs: the ones a site or endpoint rule is in.</summary>
    public static IReadOnlyCollection<Guid> ClientsToExpand(IEnumerable<GroupAccess> groups) =>
        groups.SelectMany(g => g.Includes.Concat(g.Excludes)).Where(t => !t.IsClient).Select(t => t.ClientId).ToHashSet();

    /// <summary>True when one of the groups is for all clients without exceptions: the user sees everything.</summary>
    public static bool SeesEverything(IEnumerable<GroupAccess> groups) => groups.Any(g => g.AllClients && g.Excludes.Count == 0);

    public static AccessScope Resolve(IReadOnlyCollection<GroupAccess> groups, AccessCatalog catalog)
    {
        if (SeesEverything(groups))
        {
            return AccessScope.Everything;
        }

        var sitesByClient = catalog.Sites.ToLookup(s => s.ClientId, s => s.Id);
        var siteClient = catalog.Sites.ToDictionary(s => s.Id, s => s.ClientId);
        var endpointsBySite = catalog.Endpoints.ToLookup(e => e.SiteId, e => e.Id);
        var endpointsById = catalog.Endpoints.ToDictionary(e => e.Id);

        var fullClients = new HashSet<Guid>();
        var fullSites = new HashSet<Guid>();
        var endpoints = new HashSet<Guid>();

        foreach (var group in groups)
        {
            var excludedClients = group.Excludes.Where(x => x.IsClient).Select(x => x.ClientId).ToHashSet();
            var excludedSites = group.Excludes.Where(x => x.IsSite).Select(x => x.SiteId!.Value).ToHashSet();
            var excludedEndpoints = group.Excludes.Where(x => x.IsEndpoint).Select(x => x.EndpointId!.Value).ToHashSet();
            var clientsWithExceptions = group.Excludes.Where(x => !x.IsClient).Select(x => x.ClientId).ToHashSet();
            var sitesWithExceptions = group.Excludes.Where(x => x.IsEndpoint).Select(x => x.SiteId!.Value).ToHashSet();

            void AddSite(Guid siteId)
            {
                if (!sitesWithExceptions.Contains(siteId))
                {
                    fullSites.Add(siteId);
                    return;
                }

                foreach (var endpointId in endpointsBySite[siteId])
                {
                    if (!excludedEndpoints.Contains(endpointId))
                    {
                        endpoints.Add(endpointId);
                    }
                }
            }

            var covered = group.AllClients ? catalog.AllClientIds : group.Includes.Where(i => i.IsClient).Select(i => i.ClientId);
            foreach (var clientId in covered)
            {
                if (excludedClients.Contains(clientId))
                {
                    continue;
                }

                if (!clientsWithExceptions.Contains(clientId))
                {
                    fullClients.Add(clientId);
                    continue;
                }

                foreach (var siteId in sitesByClient[clientId])
                {
                    if (!excludedSites.Contains(siteId))
                    {
                        AddSite(siteId);
                    }
                }
            }

            foreach (var include in group.Includes.Where(i => i.IsSite))
            {
                var siteId = include.SiteId!.Value;
                // The site as it is today: one that no longer exists, or moved under an excluded client, gives nothing.
                if (siteClient.TryGetValue(siteId, out var clientId) && !excludedClients.Contains(clientId) && !excludedSites.Contains(siteId))
                {
                    AddSite(siteId);
                }
            }

            foreach (var include in group.Includes.Where(i => i.IsEndpoint))
            {
                if (endpointsById.TryGetValue(include.EndpointId!.Value, out var endpoint) && !excludedClients.Contains(endpoint.ClientId) &&
                    !excludedSites.Contains(endpoint.SiteId) && !excludedEndpoints.Contains(endpoint.Id))
                {
                    endpoints.Add(endpoint.Id);
                }
            }
        }

        // What a full client holds needs no list of its own; what is left are the parts of partial clients.
        fullSites.RemoveWhere(s => !siteClient.TryGetValue(s, out var c) || fullClients.Contains(c));
        endpoints.RemoveWhere(e => !endpointsById.TryGetValue(e, out var endpoint) || fullClients.Contains(endpoint.ClientId));
        foreach (var siteId in fullSites)
        {
            endpoints.UnionWith(endpointsBySite[siteId]);
        }

        var sites = new HashSet<Guid>(fullSites);
        sites.UnionWith(endpoints.Select(e => endpointsById[e].SiteId));
        var clients = new HashSet<Guid>(fullClients);
        clients.UnionWith(sites.Select(s => siteClient[s]));

        return new AccessScope(false, groups.Any(g => g.AllClients), [.. clients], [.. fullClients], [.. fullSites], [.. sites], [.. endpoints]);
    }
}

/// <summary>
/// A resolved scope (0.6.0): the arrays are what the DbContext passes to PostgreSQL, the sets answer lookups in code.
/// </summary>
public sealed class AccessScope : IClientScope
{
    /// <summary>Every client, site and endpoint.</summary>
    public static readonly AccessScope Everything = new(true, true, [], [], [], [], []);

    /// <summary>Nothing: a user without a group, or whose groups give nothing.</summary>
    public static readonly AccessScope Nothing = new(false, false, [], [], [], [], []);

    private readonly HashSet<Guid> _clients;
    private readonly HashSet<Guid> _fullClients;
    private readonly HashSet<Guid> _fullSites;
    private readonly HashSet<Guid> _sites;
    private readonly HashSet<Guid> _endpoints;

    public AccessScope(bool allClients, bool includesNewClients, Guid[] clientIds, Guid[] fullClientIds, Guid[] fullSiteIds, Guid[] siteIds,
        Guid[] endpointIds)
    {
        AllClients = allClients;
        IncludesNewClients = includesNewClients;
        ClientIds = clientIds;
        FullClientIds = fullClientIds;
        FullSiteIds = fullSiteIds;
        SiteIds = siteIds;
        EndpointIds = endpointIds;
        _clients = [.. clientIds];
        _fullClients = [.. fullClientIds];
        _fullSites = [.. fullSiteIds];
        _sites = [.. siteIds];
        _endpoints = [.. endpointIds];
    }

    public bool AllClients { get; }
    public bool IncludesNewClients { get; }
    public IReadOnlyCollection<Guid> ClientIds { get; }
    public IReadOnlyCollection<Guid> FullClientIds { get; }
    public IReadOnlyCollection<Guid> FullSiteIds { get; }
    public IReadOnlyCollection<Guid> SiteIds { get; }
    public IReadOnlyCollection<Guid> EndpointIds { get; }

    /// <summary>
    /// This scope with a client it creates itself as a whole (a scope that <see cref="IncludesNewClients"/>), so the rows of the
    /// new client can be written in the same unit of work.
    /// </summary>
    public AccessScope WithNewClient(Guid clientId) => AllClients
        ? this
        : new AccessScope(false, IncludesNewClients, [.. ClientIds, clientId], [.. FullClientIds, clientId], [.. FullSiteIds], [.. SiteIds], [.. EndpointIds]);

    internal bool HasClient(Guid id) => _clients.Contains(id);
    internal bool HasFullClient(Guid id) => _fullClients.Contains(id);
    internal bool HasFullSite(Guid id) => _fullSites.Contains(id);
    internal bool HasSite(Guid id) => _sites.Contains(id);
    internal bool HasEndpoint(Guid id) => _endpoints.Contains(id);
}

/// <summary>
/// Questions every service asks a scope (0.6.0). Seeing a client, site or endpoint is enough to read it; changing what belongs
/// to a client or site as a whole (its name, links, tags, maintenance, a new site) needs all of it.
/// </summary>
public static class ClientScopeExtensions
{
    public static bool SeesClient(this IClientScope scope, Guid clientId) =>
        scope.AllClients || (scope is AccessScope a ? a.HasClient(clientId) : scope.ClientIds.Contains(clientId));

    public static bool HasWholeClient(this IClientScope scope, Guid clientId) =>
        scope.AllClients || (scope is AccessScope a ? a.HasFullClient(clientId) : scope.FullClientIds.Contains(clientId));

    public static bool SeesSite(this IClientScope scope, Guid clientId, Guid siteId) =>
        scope.HasWholeClient(clientId) || (scope is AccessScope a ? a.HasSite(siteId) : scope.SiteIds.Contains(siteId));

    public static bool HasWholeSite(this IClientScope scope, Guid clientId, Guid siteId) =>
        scope.HasWholeClient(clientId) || (scope is AccessScope a ? a.HasFullSite(siteId) : scope.FullSiteIds.Contains(siteId));

    public static bool SeesEndpoint(this IClientScope scope, Guid clientId, Guid endpointId) =>
        scope.HasWholeClient(clientId) || (scope is AccessScope a ? a.HasEndpoint(endpointId) : scope.EndpointIds.Contains(endpointId));

    /// <summary>How much of a client the scope sees.</summary>
    public static AccessLevel ClientLevel(this IClientScope scope, Guid clientId) =>
        scope.HasWholeClient(clientId) ? AccessLevel.Full : scope.SeesClient(clientId) ? AccessLevel.Partial : AccessLevel.None;

    /// <summary>True when the scope sees only part of some client, so a list of a client's content can be incomplete.</summary>
    public static bool HasPartialClients(this IClientScope scope) => !scope.AllClients && scope.ClientIds.Count != scope.FullClientIds.Count;
}
