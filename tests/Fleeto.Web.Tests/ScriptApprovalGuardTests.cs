using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Only somebody who may approve scripts takes an endpoint out from under a policy that requires approval of scripts: not by switching
/// the flag off, and neither by linking another policy, unlinking, deleting the policy, changing the endpoints it is for, moving the
/// endpoint, overriding its class or changing the client template its client follows (<see cref="ScriptApprovalGuard"/>).
/// </summary>
[Collection(WebCollection.Name)]
public class ScriptApprovalGuardTests
{
    private readonly WebFixture _fixture;

    public ScriptApprovalGuardTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private PolicyService Policies => _fixture.Services.GetRequiredService<PolicyService>();
    private LinkService Links => _fixture.Services.GetRequiredService<LinkService>();

    private async Task<Caller> CallerWithAsync(params string[] permissions)
    {
        var role = await _fixture.Database.CreateRoleAsync(permissions);
        var userId = (await _fixture.Database.CreateUserAsync(role)).Id;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var (roles, granted) = await UserPermissions.LoadAsync(db, userId, CancellationToken.None);
        var scope = await UserAccessResolver.ScopeOfAsync(db, userId, CancellationToken.None);
        return new Caller(userId, "Policy editor", "policies@example.com", roles, scope, "127.0.0.1", Granted: granted);
    }

    private static readonly string[] Editor =
        [Permissions.ClientsView, Permissions.LinksEdit, Permissions.PoliciesView, Permissions.PoliciesEdit, Permissions.EndpointsEdit, Permissions.EndpointsMove];

    private static PolicyInput Input(string name, bool approval, CheckAppliesTo? appliesTo = null) =>
        new(name + " " + Guid.NewGuid().ToString("N")[..6], null, 60, 3600, 10, AlertSeverity.Critical, ScriptApprovalRequired: approval, AppliesTo: appliesTo);

    private async Task<Guid> PolicyAsync(bool approval, CheckAppliesTo? appliesTo = null)
    {
        var created = await Policies.CreateAsync(WebFixtureBase.Admin(), null, Input(approval ? "Approval" : "No approval", approval, appliesTo));
        Assert.True(created.Success, created.Problem);
        return created.Value;
    }

    private async Task<bool?> ApprovalOnAsync(Guid endpointId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return (await EffectivePolicies.LoadAsync(db, endpointId))?.ScriptApprovalRequired;
    }

    private async Task<(Site Site, Endpoint Endpoint, Guid ApprovalPolicy, Caller Caller)> SetupAsync()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "APPROVAL-WS");
        var approval = await PolicyAsync(true);
        Assert.True((await Links.SetAsync(WebFixtureBase.Admin(), LinkLevel.Site, site.Id, new LinksInput(approval, null, []))).Success);
        Assert.True(await ApprovalOnAsync(endpoint.Id));

        var caller = await CallerWithAsync(Editor);
        Assert.False(caller.Has(Permissions.ScriptsApprove));
        return (site, endpoint, approval, caller);
    }

    [Fact]
    public async Task Switching_approval_off_inside_the_policy_is_refused()
    {
        var (_, endpoint, policy, caller) = await SetupAsync();
        Assert.Equal(PolicyService.ScriptApprovalProblem, (await Policies.UpdateAsync(caller, policy, Input("Off", false))).Problem);
        Assert.True(await ApprovalOnAsync(endpoint.Id));
    }

    [Fact]
    public async Task Linking_a_policy_without_approval_on_a_more_specific_level_is_refused()
    {
        var (_, endpoint, _, caller) = await SetupAsync();
        var noApproval = await Policies.CreateAsync(caller, null, Input("No approval", false));
        Assert.True(noApproval.Success, noApproval.Problem);

        Assert.Equal(ScriptApprovalGuard.Problem,
            (await Links.SetAsync(caller, LinkLevel.Endpoint, endpoint.Id, new LinksInput(noApproval.Value, null, []))).Problem);
        Assert.True(await ApprovalOnAsync(endpoint.Id));

        // Another policy that requires approval is fine, and so is the same change by somebody who may approve scripts.
        var otherApproval = await PolicyAsync(true);
        Assert.True((await Links.SetAsync(caller, LinkLevel.Endpoint, endpoint.Id, new LinksInput(otherApproval, null, []))).Success);
        Assert.True((await Links.SetAsync(WebFixtureBase.Admin(), LinkLevel.Endpoint, endpoint.Id, new LinksInput(noApproval.Value, null, []))).Success);
        Assert.False(await ApprovalOnAsync(endpoint.Id));
    }

    [Fact]
    public async Task Unlinking_the_policy_is_refused()
    {
        var (site, endpoint, _, caller) = await SetupAsync();
        Assert.Equal(ScriptApprovalGuard.Problem, (await Links.SetAsync(caller, LinkLevel.Site, site.Id, new LinksInput(null, null, []))).Problem);
        Assert.True(await ApprovalOnAsync(endpoint.Id));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await db.SitePolicies.AnyAsync(l => l.SiteId == site.Id));
    }

    [Fact]
    public async Task Linking_without_approval_on_the_client_counts_only_where_it_wins()
    {
        var (site, endpoint, _, caller) = await SetupAsync();
        var noApproval = await PolicyAsync(false);
        // The site link still wins for the endpoint, so the client link takes approval away from nothing.
        Assert.True((await Links.SetAsync(caller, LinkLevel.Client, site.ClientId, new LinksInput(noApproval, null, []))).Success);
        Assert.True(await ApprovalOnAsync(endpoint.Id));
    }

    [Fact]
    public async Task Deleting_the_policy_is_refused()
    {
        var (_, endpoint, policy, caller) = await SetupAsync();
        Assert.Equal(ScriptApprovalGuard.Problem, (await Policies.DeleteAsync(caller, policy)).Problem);
        Assert.True(await ApprovalOnAsync(endpoint.Id));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await db.Policies.AnyAsync(p => p.Id == policy));
    }

    [Fact]
    public async Task Narrowing_the_endpoints_the_policy_is_for_is_refused()
    {
        var (_, endpoint, policy, caller) = await SetupAsync();
        Assert.Equal(ScriptApprovalGuard.Problem, (await Policies.UpdateAsync(caller, policy, Input("Servers only", true, CheckAppliesTo.Server))).Problem);
        Assert.True(await ApprovalOnAsync(endpoint.Id));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(CheckAppliesTo.All, (await db.Policies.SingleAsync(p => p.Id == policy)).AppliesTo);
    }

    [Fact]
    public async Task Moving_the_endpoint_to_a_site_without_approval_is_refused()
    {
        var (site, endpoint, _, caller) = await SetupAsync();
        var other = await _fixture.Database.CreateSiteAsync(site.ClientId, "Elsewhere");
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();

        Assert.Equal(ScriptApprovalGuard.Problem, (await endpoints.MoveAsync(caller, endpoint.Id, other.Id)).Problem);
        Assert.True(await ApprovalOnAsync(endpoint.Id));
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(site.Id, (await db.Endpoints.SingleAsync(e => e.Id == endpoint.Id)).SiteId);
    }

    [Fact]
    public async Task Overriding_the_class_into_a_policy_without_approval_is_refused()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "APPROVAL-CLASS");
        var forServers = await PolicyAsync(false, CheckAppliesTo.Server);
        var forWorkstations = await PolicyAsync(true, CheckAppliesTo.Workstation);
        Assert.True((await Links.SetAsync(WebFixtureBase.Admin(), LinkLevel.Site, site.Id,
            new LinksInput(new ClassChoice(null, forServers, forWorkstations), null, []))).Success);
        Assert.True(await ApprovalOnAsync(endpoint.Id));
        var caller = await CallerWithAsync(Editor);
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();

        Assert.Equal(ScriptApprovalGuard.Problem, (await endpoints.SetClassOverrideAsync(caller, endpoint.Id, EndpointClass.Server)).Problem);
        Assert.True(await ApprovalOnAsync(endpoint.Id));
        Assert.True((await endpoints.SetClassOverrideAsync(WebFixtureBase.Admin(), endpoint.Id, EndpointClass.Server)).Success);
        Assert.False(await ApprovalOnAsync(endpoint.Id));
    }

    [Fact]
    public async Task Changing_the_client_template_into_no_approval_is_refused()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var admin = WebFixtureBase.Admin();
        var templates = _fixture.Services.GetRequiredService<ClientTemplateService>();
        var approval = await PolicyAsync(true);
        var name = "Approval template " + Guid.NewGuid().ToString("N")[..6];
        var template = await templates.SaveAsync(admin, null, new ClientTemplateInput(name, null,
            [new ClientTemplateSiteInput(null, "Office", null, null, [])], new ClientTemplateClientLinks(approval, ClassChoice.None, [])));
        Assert.True(template.Success, template.Problem);
        var code = "A" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var client = await _fixture.Services.GetRequiredService<ClientService>().CreateAsync(admin, code, "Approval", template.Value);
        Assert.True(client.Success, client.Problem);
        Site site;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            site = await db.Sites.AsNoTracking().SingleAsync(s => s.ClientId == client.Value);
        }

        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "APPROVAL-TPL");
        Assert.True(await ApprovalOnAsync(endpoint.Id));
        var caller = await CallerWithAsync(Permissions.ClientsView, Permissions.ClientTemplatesView, Permissions.ClientTemplatesEdit);
        var detail = (await templates.GetAsync(admin, template.Value))!;

        var refused = await templates.SaveAsync(caller, template.Value,
            new ClientTemplateInput(name, null, detail.Sites, detail.Client with { Policy = ClassChoice.None }));
        Assert.Equal(ScriptApprovalGuard.Problem, refused.Problem);
        Assert.True(await ApprovalOnAsync(endpoint.Id));
        Assert.Equal(approval, (await templates.GetAsync(admin, template.Value))!.Client.Policy.All);

        // A change that keeps approval goes through, and the clients follow it.
        var kept = await templates.SaveAsync(caller, template.Value, new ClientTemplateInput(name, "Still approved", detail.Sites, detail.Client));
        Assert.True(kept.Success, kept.Problem);
        Assert.True(await ApprovalOnAsync(endpoint.Id));
    }
}
