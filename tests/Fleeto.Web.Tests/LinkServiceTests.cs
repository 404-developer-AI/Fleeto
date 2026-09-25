using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Policies, patch policies and monitoring templates on client, site and endpoint level (0.6.0): what a dialog shows as
/// inherited, what saving writes, what the client template keeps, and that nothing reaches another client.
/// </summary>
[Collection(WebCollection.Name)]
public class LinkServiceTests
{
    private readonly WebFixture _fixture;

    public LinkServiceTests(WebFixture fixture) => _fixture = fixture;

    private LinkService Links => _fixture.Services.GetRequiredService<LinkService>();
    private PatchPolicyService PatchPolicies => _fixture.Services.GetRequiredService<PatchPolicyService>();
    private PolicyService Policies => _fixture.Services.GetRequiredService<PolicyService>();

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    private static PatchPolicyInput PatchInput(string name) => new(name, null, true, PatchScheduleKind.Weekly, WeekDays.Wednesday, 1, 2,
        DayOfWeek.Tuesday, 20 * 60, true, PatchUpdateScope.All, [], [], [], [], [], false, 0, false, true, null, 30, 24);

    private async Task<Guid> PolicyAsync(Guid? clientId = null)
    {
        var created = await Policies.CreateAsync(WebFixtureBase.Admin(), clientId,
            new PolicyInput(Unique("Level policy"), null, 30, 3600, 10, AlertSeverity.Critical));
        Assert.True(created.Success, created.Problem);
        return created.Value;
    }

    private async Task<Guid> PatchPolicyAsync(Guid? clientId = null)
    {
        var created = await PatchPolicies.CreateAsync(WebFixtureBase.Admin(), clientId, PatchInput(Unique("Level patch")));
        Assert.True(created.Success, created.Problem);
        return created.Value;
    }

    [Fact]
    public async Task Each_level_shows_what_it_inherits_and_the_most_specific_link_wins()
    {
        var caller = WebFixtureBase.Technician();
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-LEVELS");
        var clientPolicy = await PolicyAsync();
        var sitePolicy = await PolicyAsync(client.Id);
        var clientPatch = await PatchPolicyAsync();
        Guid template;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            template = await db.MonitoringTemplates.Where(t => t.ClientId == null).Select(t => t.Id).FirstAsync();
        }

        Assert.True((await Links.SetAsync(caller, LinkLevel.Client, client.Id, new LinksInput(clientPolicy, clientPatch, [template]))).Success);
        Assert.True((await Links.SetAsync(caller, LinkLevel.Site, site.Id, new LinksInput(sitePolicy, null, []))).Success);

        var onSite = (await Links.GetAsync(caller, LinkLevel.Site, site.Id))!;
        Assert.Equal(sitePolicy, onSite.PolicyId);
        Assert.Equal(clientPolicy, onSite.InheritedPolicy!.Id);
        Assert.Equal(LinkLevel.Client, onSite.InheritedPolicy.Level);
        Assert.Null(onSite.PatchPolicyId);
        Assert.Equal(clientPatch, onSite.InheritedPatchPolicy!.Id);
        Assert.Contains(onSite.InheritedTemplates, t => t.Id == template && t.Level == LinkLevel.Client);
        Assert.Contains(onSite.Choices.Policies, p => p.Id == sitePolicy);

        var onEndpoint = (await Links.GetAsync(caller, LinkLevel.Endpoint, endpoint.Id))!;
        Assert.Null(onEndpoint.PolicyId);
        Assert.Equal(sitePolicy, onEndpoint.InheritedPolicy!.Id);
        Assert.Equal(LinkLevel.Site, onEndpoint.InheritedPolicy.Level);
        Assert.True(onEndpoint.TemplatesAllowed);

        var bare = await _fixture.Database.CreateClientAsync();
        var onBare = (await Links.GetAsync(caller, LinkLevel.Client, bare.Id))!;
        Assert.Equal(LinkLevel.None, onBare.InheritedPolicy!.Level);
        Assert.Null(onBare.InheritedPatchPolicy);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await check.ConfigChangeEvents.AnyAsync(e => e.Scope == ConfigChangeScope.Client && e.ScopeId == client.Id));
        Assert.True(await check.AuditEntries.AnyAsync(a => a.Action == AuditActions.ClientLinksChanged && a.TargetId == client.Id.ToString()));
        Assert.Equal(sitePolicy, await Infrastructure.Services.EffectivePolicies.Query(check).Where(r => r.EndpointId == endpoint.Id)
            .Select(r => r.PolicyId).SingleAsync());
    }

    [Fact]
    public async Task An_endpoint_gets_its_own_policy_and_patch_policy_and_an_agent_only_endpoint_no_monitoring_templates()
    {
        var caller = WebFixtureBase.Technician();
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var agentOnly = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-AGENTONLY");
        var policy = await PolicyAsync();
        var patch = await PatchPolicyAsync(client.Id);
        Guid template;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            template = await db.MonitoringTemplates.Where(t => t.ClientId == null).Select(t => t.Id).FirstAsync();
        }

        Assert.True((await Links.SetAsync(caller, LinkLevel.Endpoint, agentOnly.Id, new LinksInput(policy, patch, []))).Success);
        var refused = await Links.SetAsync(caller, LinkLevel.Endpoint, agentOnly.Id, new LinksInput(policy, patch, [template]));
        Assert.False(refused.Success);
        Assert.False((await Links.GetAsync(caller, LinkLevel.Endpoint, agentOnly.Id))!.TemplatesAllowed);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(policy, (await check.EndpointPolicies.SingleAsync(l => l.EndpointId == agentOnly.Id)).PolicyId);
        Assert.Equal(patch, (await check.EndpointPatchPolicies.SingleAsync(l => l.EndpointId == agentOnly.Id)).PatchPolicyId);
        Assert.True(await check.ConfigChangeEvents.AnyAsync(e => e.Scope == ConfigChangeScope.Endpoint && e.ScopeId == agentOnly.Id));

        // Back to inheriting: the links are gone.
        Assert.True((await Links.SetAsync(caller, LinkLevel.Endpoint, agentOnly.Id, new LinksInput(null, null, []))).Success);
        Assert.False(await check.EndpointPolicies.AnyAsync(l => l.EndpointId == agentOnly.Id));
        Assert.False(await check.EndpointPatchPolicies.AnyAsync(l => l.EndpointId == agentOnly.Id));
    }

    [Fact]
    public async Task Links_never_reach_another_client_and_a_read_only_user_or_limited_user_cannot_set_them()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var other = await _fixture.Database.CreateClientAsync();
        var foreignPolicy = await PolicyAsync(other.Id);
        var foreignPatch = await PatchPolicyAsync(other.Id);

        var technician = WebFixtureBase.Technician();
        Assert.False((await Links.SetAsync(technician, LinkLevel.Client, client.Id, new LinksInput(foreignPolicy, null, []))).Success);
        Assert.False((await Links.SetAsync(technician, LinkLevel.Client, client.Id, new LinksInput(null, foreignPatch, []))).Success);
        Assert.DoesNotContain((await Links.GetChoicesAsync(technician, client.Id)).PatchPolicies, p => p.Id == foreignPatch);

        var readOnly = WebFixtureBase.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);
        Assert.False((await Links.SetAsync(readOnly, LinkLevel.Client, client.Id, new LinksInput(null, null, []))).Success);

        var limited = WebFixtureBase.CallerWith(new RestrictedClientScope([other.Id]), FleetoRoles.Technician);
        Assert.Null(await Links.GetAsync(limited, LinkLevel.Client, client.Id));
        Assert.False((await Links.SetAsync(limited, LinkLevel.Client, client.Id, new LinksInput(null, null, []))).Success);
    }

    [Fact]
    public async Task A_client_template_sets_the_links_of_the_client_and_its_sites_and_they_can_be_replaced_not_removed()
    {
        var admin = WebFixtureBase.Admin();
        var templates = _fixture.Services.GetRequiredService<ClientTemplateService>();
        var clients = _fixture.Services.GetRequiredService<ClientService>();
        var clientPolicy = await PolicyAsync();
        var clientPatch = await PatchPolicyAsync();
        var sitePatch = await PatchPolicyAsync();
        Guid monitoring;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            monitoring = await db.MonitoringTemplates.Where(t => t.ClientId == null).Select(t => t.Id).FirstAsync();
        }

        var template = await templates.SaveAsync(admin, null, new ClientTemplateInput(Unique("Levels"), null,
            [new ClientTemplateSiteInput(null, "Servers", null, null, [], sitePatch)],
            new ClientTemplateClientLinks(clientPolicy, clientPatch, [monitoring])));
        Assert.True(template.Success, template.Problem);
        var detail = (await templates.GetAsync(admin, template.Value))!;
        Assert.Equal(clientPatch, detail.Client.PatchPolicyId);
        Assert.Equal(sitePatch, detail.Sites.Single().PatchPolicyId);

        var code = "L" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await clients.CreateAsync(admin, code, "Levels", template.Value);
        Assert.True(created.Success, created.Problem);

        var links = (await Links.GetAsync(admin, LinkLevel.Client, created.Value))!;
        Assert.Equal(clientPolicy, links.PolicyId);
        Assert.True(links.PolicyFromClientTemplate);
        Assert.Equal(clientPatch, links.PatchPolicyId);
        Assert.Contains(links.MonitoringTemplates, t => t.Id == monitoring && t.Source == LinkSource.ClientTemplate);

        Guid siteId;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            siteId = await db.Sites.Where(s => s.ClientId == created.Value).Select(s => s.Id).SingleAsync();
            var sitePatchLink = await db.SitePatchPolicies.SingleAsync(l => l.SiteId == siteId);
            Assert.Equal(sitePatch, sitePatchLink.PatchPolicyId);
            Assert.Equal(LinkSource.ClientTemplate, sitePatchLink.Source);
        }

        // Removing what the template linked is refused; replacing it makes it the technician's own.
        Assert.False((await Links.SetAsync(admin, LinkLevel.Client, created.Value, new LinksInput(null, clientPatch, [monitoring]))).Success);
        Assert.False((await Links.SetAsync(admin, LinkLevel.Client, created.Value, new LinksInput(clientPolicy, clientPatch, []))).Success);
        var other = await PolicyAsync();
        Assert.True((await Links.SetAsync(admin, LinkLevel.Client, created.Value, new LinksInput(other, clientPatch, [monitoring]))).Success);
        links = (await Links.GetAsync(admin, LinkLevel.Client, created.Value))!;
        Assert.Equal(other, links.PolicyId);
        Assert.False(links.PolicyFromClientTemplate);

        // Detaching keeps every link, as the client's own.
        Assert.True((await clients.DetachFromTemplateAsync(admin, created.Value)).Success);
        links = (await Links.GetAsync(admin, LinkLevel.Client, created.Value))!;
        Assert.False(links.PatchPolicyFromClientTemplate);
        Assert.All(links.MonitoringTemplates, t => Assert.Equal(LinkSource.Manual, t.Source));
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.Equal(LinkSource.Manual, (await db.SitePatchPolicies.SingleAsync(l => l.SiteId == siteId)).Source);
        }
    }

    [Fact]
    public async Task A_patch_policy_is_validated_named_once_per_scope_and_its_links_go_when_it_is_deleted()
    {
        var admin = WebFixtureBase.Admin();
        var name = Unique("Patch nights");
        var created = await PatchPolicies.CreateAsync(admin, null, PatchInput(name));
        Assert.True(created.Success, created.Problem);
        Assert.False((await PatchPolicies.CreateAsync(admin, null, PatchInput(name.ToUpperInvariant()))).Success);
        Assert.False((await PatchPolicies.CreateAsync(admin, null, PatchInput(Unique("No days")) with { WeekDays = WeekDays.None })).Success);
        Assert.False((await PatchPolicies.CreateAsync(admin, null,
            PatchInput(Unique("Unknown")) with { Scope = PatchUpdateScope.Filtered, Severities = ["Urgent"] })).Success);
        Assert.False((await PatchPolicies.CreateAsync(WebFixtureBase.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly), null,
            PatchInput(Unique("Read only")))).Success);

        // Filters are dropped for all updates, and the delay for updates that need approval.
        var updated = await PatchPolicies.UpdateAsync(admin, created.Value, PatchInput(name) with
        {
            Severities = ["Critical"], RequireApproval = true, InstallDelayDays = 5
        });
        Assert.True(updated.Success, updated.Problem);
        var stored = (await PatchPolicies.GetAsync(admin, created.Value))!;
        Assert.Empty(stored.Severities);
        Assert.Equal(0, stored.InstallDelayDays);

        var client = await _fixture.Database.CreateClientAsync();
        Assert.True((await Links.SetAsync(admin, LinkLevel.Client, client.Id, new LinksInput(null, created.Value, []))).Success);
        var listed = (await PatchPolicies.ListAsync(admin)).Single(p => p.Id == created.Value);
        Assert.Equal(1, listed.Links);

        Assert.True((await PatchPolicies.DeleteAsync(admin, created.Value)).Success);
        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await check.ClientPatchPolicies.AnyAsync(l => l.ClientId == client.Id));
        Assert.True(await check.AuditEntries.AnyAsync(a => a.Action == AuditActions.PatchPolicyDeleted && a.TargetId == created.Value.ToString()));
    }
}
