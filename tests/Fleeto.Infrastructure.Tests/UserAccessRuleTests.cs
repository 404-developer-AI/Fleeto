using Fleeto.Core.Domain;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// What groups give (0.6.0): every client with exceptions or what they include, an exclude only inside its own group, the union over
/// groups, and the same answer from the scope every query filters on and from the per-target question the signer asks.
/// </summary>
public class UserAccessRuleTests
{
    // Two clients: A with sites A1 (endpoints a1, a2) and A2 (endpoint a3); B with site B1 (endpoint b1).
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static readonly Guid A1 = Guid.NewGuid(), A2 = Guid.NewGuid(), B1 = Guid.NewGuid();
    private static readonly Guid Ea1 = Guid.NewGuid(), Ea2 = Guid.NewGuid(), Ea3 = Guid.NewGuid(), Eb1 = Guid.NewGuid();

    private static readonly AccessCatalog Catalog = new([A, B],
        [new AccessSite(A1, A), new AccessSite(A2, A), new AccessSite(B1, B)],
        [new AccessEndpoint(Ea1, A1, A), new AccessEndpoint(Ea2, A1, A), new AccessEndpoint(Ea3, A2, A), new AccessEndpoint(Eb1, B1, B)]);

    private static readonly AccessEndpoint[] Endpoints = [.. Catalog.Endpoints];

    private static AccessTarget Client(Guid client) => new(client);
    private static AccessTarget Site(Guid site) => new(site == B1 ? B : A, site);
    private static AccessTarget Endpoint(Guid endpoint) => Endpoints.Single(e => e.Id == endpoint) is var e ? new AccessTarget(e.ClientId, e.SiteId, e.Id) : null!;

    private static GroupAccess Group(bool all, AccessTarget[]? includes = null, AccessTarget[]? excludes = null) =>
        new(Guid.NewGuid(), "Group", all, includes ?? [], excludes ?? []);

    private static AccessScope Resolve(params GroupAccess[] groups) =>
        UserAccessRules.Resolve(groups, groups.Any(g => g.AllClients) ? Catalog : Catalog with { AllClientIds = [] });

    private static HashSet<Guid> Seen(AccessScope scope) =>
        Endpoints.Where(e => scope.SeesEndpoint(e.ClientId, e.Id)).Select(e => e.Id).ToHashSet();

    [Fact]
    public void Without_a_group_nothing_is_visible_and_a_group_for_all_clients_without_exceptions_is_everything()
    {
        var nothing = UserAccessRules.Resolve([], Catalog);
        Assert.False(nothing.AllClients);
        Assert.Empty(Seen(nothing));
        Assert.False(nothing.IncludesNewClients);

        var everything = Resolve(Group(true));
        Assert.True(everything.AllClients);
        Assert.True(everything.IncludesNewClients);
        Assert.True(everything.HasWholeClient(Guid.NewGuid()));
    }

    [Fact]
    public void All_clients_except_one_gives_every_other_client_whole_and_grows_with_new_clients()
    {
        var scope = Resolve(Group(true, excludes: [Client(B)]));

        Assert.False(scope.AllClients);
        Assert.True(scope.IncludesNewClients);
        Assert.True(scope.HasWholeClient(A));
        Assert.False(scope.SeesClient(B));
        Assert.Equal(new[] { Ea1, Ea2, Ea3 }.Order(), Seen(scope).Order());
    }

    [Fact]
    public void An_excluded_site_makes_its_client_partial_and_keeps_the_other_sites_whole()
    {
        var scope = Resolve(Group(true, excludes: [Site(A2)]));

        Assert.True(scope.SeesClient(A));
        Assert.False(scope.HasWholeClient(A));
        Assert.True(scope.HasWholeSite(A, A1));
        Assert.False(scope.SeesSite(A, A2));
        Assert.True(scope.HasWholeClient(B));
        Assert.Equal(new[] { Ea1, Ea2, Eb1 }.Order(), Seen(scope).Order());
    }

    [Fact]
    public void An_excluded_endpoint_makes_its_site_partial()
    {
        var scope = Resolve(Group(false, includes: [Client(A)], excludes: [Endpoint(Ea2)]));

        Assert.True(scope.SeesSite(A, A1));
        Assert.False(scope.HasWholeSite(A, A1));
        Assert.True(scope.HasWholeSite(A, A2));
        Assert.False(scope.SeesClient(B));
        Assert.Equal(new[] { Ea1, Ea3 }.Order(), Seen(scope).Order());
    }

    [Fact]
    public void A_site_and_a_single_endpoint_can_be_included_in_different_clients()
    {
        var scope = Resolve(Group(false, includes: [Site(A2), Endpoint(Eb1)]));

        Assert.False(scope.IncludesNewClients);
        Assert.True(scope.HasWholeSite(A, A2));
        Assert.False(scope.SeesSite(A, A1));
        Assert.True(scope.SeesSite(B, B1));
        Assert.False(scope.HasWholeSite(B, B1));
        Assert.Equal(new[] { Ea3, Eb1 }.Order(), Seen(scope).Order());
    }

    [Fact]
    public void An_exclude_counts_only_inside_its_own_group()
    {
        var excluded = Group(true, excludes: [Client(A)]);
        var included = Group(false, includes: [Site(A1)]);

        Assert.Equal(new[] { Ea1, Ea2, Eb1 }.Order(), Seen(Resolve(excluded, included)).Order());
        // Together with a group that gives all of client A, client A is whole again.
        Assert.True(Resolve(excluded, Group(false, includes: [Client(A)])).HasWholeClient(A));
    }

    [Fact]
    public void An_exclude_wins_over_an_include_of_the_same_group()
    {
        var scope = Resolve(Group(false, includes: [Site(A1), Endpoint(Ea3)], excludes: [Client(A)]));

        Assert.Empty(Seen(scope));
        Assert.False(scope.SeesClient(A));
    }

    [Fact]
    public void The_scope_and_the_per_target_question_always_agree()
    {
        AccessTarget[] targets = [Client(A), Client(B), Site(A1), Site(A2), Site(B1), Endpoint(Ea1), Endpoint(Ea2), Endpoint(Ea3), Endpoint(Eb1)];
        var groups = new List<GroupAccess>();
        foreach (var all in new[] { true, false })
        {
            foreach (var include in targets)
            {
                foreach (var exclude in targets)
                {
                    groups.Add(Group(all, all ? [] : [include], include == exclude ? [] : [exclude]));
                }
            }
        }

        foreach (var group in groups)
        {
            var scope = Resolve(group);
            foreach (var endpoint in Endpoints)
            {
                Assert.Equal(group.Reaches(endpoint.ClientId, endpoint.SiteId, endpoint.Id), scope.SeesEndpoint(endpoint.ClientId, endpoint.Id));
            }

            // A whole site or client is the same answer both ways. Part of one is an upper bound per target: when every
            // endpoint left is excluded one by one, the scope holds nothing of it.
            foreach (var site in Catalog.Sites)
            {
                var level = group.ReachesSite(site.ClientId, site.Id);
                Assert.Equal(level == AccessLevel.Full, scope.HasWholeSite(site.ClientId, site.Id));
                Assert.True(level != AccessLevel.None || !scope.SeesSite(site.ClientId, site.Id));
            }

            foreach (var client in new[] { A, B })
            {
                var level = group.ReachesClient(client);
                Assert.Equal(level == AccessLevel.Full, scope.HasWholeClient(client));
                Assert.True(level != AccessLevel.None || !scope.SeesClient(client));
            }
        }
    }
}
