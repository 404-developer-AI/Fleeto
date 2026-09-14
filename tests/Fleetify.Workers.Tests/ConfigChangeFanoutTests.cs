using Fleetify.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Workers.Tests;

/// <summary>
/// Guarantees that a configuration change requests exactly one pending AgentConfig signature for every affected endpoint
/// and none for unaffected ones, for every scope including the default policy, without duplicates.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class ConfigChangeFanoutTests
{
    private readonly WorkersFixture _fixture;

    public ConfigChangeFanoutTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Topology(Client ClientA, Site SiteA1, Site SiteA2, Site SiteB1, Endpoint E1, Endpoint E2, Endpoint E3,
        Policy LinkedPolicy, Guid DefaultPolicyId, MonitoringTemplate Template)
    {
        public Guid[] All => [E1.Id, E2.Id, E3.Id];
    }

    private async Task<Topology> CreateTopologyAsync()
    {
        var db = _fixture.Db;
        var clientA = await db.CreateClientAsync();
        var siteA1 = await db.CreateSiteAsync(clientA.Id, "A1");
        var siteA2 = await db.CreateSiteAsync(clientA.Id, "A2");
        var clientB = await db.CreateClientAsync();
        var siteB1 = await db.CreateSiteAsync(clientB.Id, "B1");
        var e1 = await db.CreateEndpointAsync(siteA1, EndpointTier.Managed, "E1");
        var e2 = await db.CreateEndpointAsync(siteA2, EndpointTier.Managed, "E2");
        var e3 = await db.CreateEndpointAsync(siteB1, EndpointTier.Managed, "E3");

        await using var context = db.DbFactory.CreateSystem();
        var now = _fixture.Now;
        var policy = new Policy { Id = Guid.NewGuid(), Name = "Policy " + Guid.NewGuid().ToString("N")[..10], CreatedAt = now, UpdatedAt = now };
        var template = new MonitoringTemplate { Id = Guid.NewGuid(), Name = "Template " + Guid.NewGuid().ToString("N")[..10], CreatedAt = now, UpdatedAt = now };
        context.Policies.Add(policy);
        context.MonitoringTemplates.Add(template);
        context.SitePolicies.Add(new SitePolicy { SiteId = siteA1.Id, ClientId = clientA.Id, PolicyId = policy.Id, Source = LinkSource.Manual, CreatedAt = now });
        context.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate
        {
            SiteId = siteB1.Id, ClientId = clientB.Id, MonitoringTemplateId = template.Id, Source = LinkSource.Manual, CreatedAt = now
        });
        await context.SaveChangesAsync();
        var defaultPolicyId = await context.Policies.Where(p => p.IsDefault).Select(p => p.Id).SingleAsync();

        return new Topology(clientA, siteA1, siteA2, siteB1, e1, e2, e3, policy, defaultPolicyId, template);
    }

    /// <summary>Processes leftovers of other tests and completes every pending request, so a scenario starts clean.</summary>
    private async Task ResetAsync()
    {
        await _fixture.Fanout().ProcessPendingAsync(CancellationToken.None);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.SigningRequests.Where(r => r.State == SigningRequestState.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, SigningRequestState.Completed));
    }

    private async Task AddEventAsync(ConfigChangeScope scope, Guid? scopeId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = scope, ScopeId = scopeId, CreatedAt = _fixture.Now });
        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<Guid, int>> PendingCountsAsync(IEnumerable<Guid> endpointIds)
    {
        var ids = endpointIds.ToList();
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var counts = await db.SigningRequests
            .Where(r => r.Kind == SigningRequestKind.AgentConfig && r.State == SigningRequestState.Pending && r.SubjectId != null && ids.Contains(r.SubjectId.Value))
            .GroupBy(r => r.SubjectId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count);
        return ids.ToDictionary(id => id, id => counts.GetValueOrDefault(id));
    }

    private async Task AssertScopeAsync(Topology topology, ConfigChangeScope scope, Guid? scopeId, params Guid[] expected)
    {
        await ResetAsync();
        await AddEventAsync(scope, scopeId);
        await _fixture.Fanout().ProcessPendingAsync(CancellationToken.None);

        var counts = await PendingCountsAsync(topology.All);
        foreach (var (endpointId, count) in counts)
        {
            Assert.Equal(expected.Contains(endpointId) ? 1 : 0, count);
        }
    }

    [Fact]
    public async Task Instance_scope_requests_one_config_for_every_endpoint()
    {
        var t = await CreateTopologyAsync();
        await AssertScopeAsync(t, ConfigChangeScope.Instance, null, t.E1.Id, t.E2.Id, t.E3.Id);
    }

    [Fact]
    public async Task Client_site_and_endpoint_scopes_request_configs_only_for_their_endpoints()
    {
        var t = await CreateTopologyAsync();
        await AssertScopeAsync(t, ConfigChangeScope.Client, t.ClientA.Id, t.E1.Id, t.E2.Id);
        await AssertScopeAsync(t, ConfigChangeScope.Site, t.SiteA2.Id, t.E2.Id);
        await AssertScopeAsync(t, ConfigChangeScope.Endpoint, t.E3.Id, t.E3.Id);
    }

    [Fact]
    public async Task Policy_scope_requests_configs_for_sites_linked_to_the_policy()
    {
        var t = await CreateTopologyAsync();
        await AssertScopeAsync(t, ConfigChangeScope.Policy, t.LinkedPolicy.Id, t.E1.Id);
    }

    [Fact]
    public async Task Default_policy_scope_requests_configs_for_sites_without_a_linked_policy()
    {
        var t = await CreateTopologyAsync();
        await AssertScopeAsync(t, ConfigChangeScope.Policy, t.DefaultPolicyId, t.E2.Id, t.E3.Id);
    }

    [Fact]
    public async Task Monitoring_template_scope_requests_configs_for_sites_linking_the_template()
    {
        var t = await CreateTopologyAsync();
        await AssertScopeAsync(t, ConfigChangeScope.MonitoringTemplate, t.Template.Id, t.E3.Id);
    }

    [Fact]
    public async Task Overlapping_events_do_not_create_duplicate_pending_requests()
    {
        var t = await CreateTopologyAsync();
        await ResetAsync();
        await AddEventAsync(ConfigChangeScope.Client, t.ClientA.Id);
        await AddEventAsync(ConfigChangeScope.Site, t.SiteA2.Id);
        await AddEventAsync(ConfigChangeScope.Instance, null);

        var processed = await _fixture.Fanout().ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(3, processed);
        Assert.Equal(0, await _fixture.Fanout().ProcessPendingAsync(CancellationToken.None));

        var counts = await PendingCountsAsync(t.All);
        Assert.All(counts.Values, count => Assert.Equal(1, count));

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await db.ConfigChangeEvents.AnyAsync(e => e.ProcessedAt == null));
        var request = await db.SigningRequests.FirstAsync(r => r.SubjectId == t.E1.Id && r.State == SigningRequestState.Pending);
        Assert.Equal("workers", request.RequestedBy);
        Assert.Equal(t.ClientA.Id, request.ClientId);
        Assert.Empty(request.Payload);
    }
}
