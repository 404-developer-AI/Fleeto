using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// A script check runs its script on every endpoint it applies to, so adding, changing, enabling or copying one needs View scripts and
/// Run scripts, not only the permission to edit monitoring templates or checks. The configuration the signer would sign must not
/// carry a script that such a caller put there.
/// </summary>
[Collection(WebCollection.Name)]
public class ScriptCheckPermissionTests
{
    private readonly WebFixture _fixture;

    public ScriptCheckPermissionTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private MonitoringTemplateService Templates => _fixture.Services.GetRequiredService<MonitoringTemplateService>();
    private EndpointCheckService Checks => _fixture.Services.GetRequiredService<EndpointCheckService>();

    private async Task<Caller> CallerWithAsync(params string[] permissions)
    {
        var role = await _fixture.Database.CreateRoleAsync(permissions);
        var userId = (await _fixture.Database.CreateUserAsync(role)).Id;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var (roles, granted) = await UserPermissions.LoadAsync(db, userId, CancellationToken.None);
        var scope = await UserAccessResolver.ScopeOfAsync(db, userId, CancellationToken.None);
        return new Caller(userId, "Checks only", "checks@example.com", roles, scope, "127.0.0.1", Granted: granted);
    }

    private static CheckDefinitionInput ScriptCheck(Guid scriptId, bool enabled = true, int interval = 300) =>
        new("Script check " + Guid.NewGuid().ToString("N")[..6], CheckType.Script, interval,
            new Dictionary<string, string> { [CheckCatalog.ScriptParameter] = scriptId.ToString("D") }, null, null, 1, CheckAppliesTo.All, enabled);

    private async Task<IReadOnlyList<string>> ConfiguredChecksAsync(Guid endpointId)
    {
        var builder = new AgentConfigBuilder(_fixture.Database.Licenses);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var built = await builder.BuildAsync(db, endpointId, _fixture.Database.InstanceId);
        return built!.Config.Checks.Select(c => c.Id).ToList();
    }

    private async Task<(Endpoint Endpoint, Site Site, Script Script)> SetupAsync(string hostname)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, hostname);
        var (script, _) = await _fixture.Database.CreateScriptAsync(null, Guid.NewGuid(), body: "Write-Output 'script check'");
        return (endpoint, site, script);
    }

    [Fact]
    public async Task Template_script_check_needs_run_scripts()
    {
        var (endpoint, site, script) = await SetupAsync("SCRIPTCHECK-TPL");
        var template = await _fixture.Database.CreateMonitoringTemplateAsync(linkSite: site);
        var caller = await CallerWithAsync(Permissions.ClientsView, Permissions.MonitoringTemplatesView, Permissions.MonitoringTemplatesEdit);

        var refused = await Templates.SaveCheckAsync(caller, template.Id, null, ScriptCheck(script.Id));
        Assert.False(refused.Success);
        Assert.Equal(ScriptChecks.PermissionProblem, refused.Problem);
        Assert.Empty(await ConfiguredChecksAsync(endpoint.Id));

        // An existing script check: changing it (its script included) and switching it on are refused as well.
        var existing = await Templates.SaveCheckAsync(WebFixtureBase.Admin(), template.Id, null, ScriptCheck(script.Id, enabled: false));
        Assert.True(existing.Success, existing.Problem);
        Assert.Equal(ScriptChecks.PermissionProblem,
            (await Templates.SaveCheckAsync(caller, template.Id, existing.Value, ScriptCheck(script.Id, enabled: true))).Problem);
        Assert.Equal(ScriptChecks.PermissionProblem, (await Templates.SetCheckEnabledAsync(caller, template.Id, existing.Value, true)).Problem);
        Assert.Empty(await ConfiguredChecksAsync(endpoint.Id));

        // A copy would hold new script checks of its own.
        Assert.Equal(ScriptChecks.PermissionProblem, (await Templates.CopyAsync(caller, template.Id, "Copy " + Guid.NewGuid().ToString("N")[..6], null)).Problem);

        // Control: with View scripts and Run scripts the same caller may.
        var runner = await CallerWithAsync(Permissions.ClientsView, Permissions.MonitoringTemplatesView, Permissions.MonitoringTemplatesEdit,
            Permissions.ScriptsView, Permissions.ScriptsRun, Permissions.JobsView);
        var saved = await Templates.SaveCheckAsync(runner, template.Id, null, ScriptCheck(script.Id));
        Assert.True(saved.Success, saved.Problem);
        Assert.Contains(saved.Value.ToString("D"), await ConfiguredChecksAsync(endpoint.Id));
    }

    [Fact]
    public async Task Template_check_of_another_type_and_disabling_a_script_check_need_no_script_permission()
    {
        var (_, site, script) = await SetupAsync("SCRIPTCHECK-OTHER");
        var template = await _fixture.Database.CreateMonitoringTemplateAsync(linkSite: site);
        var caller = await CallerWithAsync(Permissions.ClientsView, Permissions.MonitoringTemplatesView, Permissions.MonitoringTemplatesEdit);

        var existing = await Templates.SaveCheckAsync(WebFixtureBase.Admin(), template.Id, null, ScriptCheck(script.Id));
        Assert.True(existing.Success, existing.Problem);
        Assert.True((await Templates.SetCheckEnabledAsync(caller, template.Id, existing.Value, false)).Success);
        Assert.True((await Templates.DeleteCheckAsync(caller, template.Id, existing.Value)).Success);
    }

    [Fact]
    public async Task Endpoint_script_check_needs_run_scripts()
    {
        var (endpoint, _, script) = await SetupAsync("SCRIPTCHECK-EP");
        var caller = await CallerWithAsync(Permissions.ClientsView, Permissions.ChecksView, Permissions.ChecksEdit);

        var refused = await Checks.SaveEndpointCheckAsync(caller, endpoint.Id, null, ScriptCheck(script.Id));
        Assert.False(refused.Success);
        Assert.Equal(ScriptChecks.PermissionProblem, refused.Problem);
        Assert.Empty(await ConfiguredChecksAsync(endpoint.Id));

        var existing = await Checks.SaveEndpointCheckAsync(WebFixtureBase.Admin(), endpoint.Id, null, ScriptCheck(script.Id, enabled: false));
        Assert.True(existing.Success, existing.Problem);
        Assert.Equal(ScriptChecks.PermissionProblem,
            (await Checks.SaveEndpointCheckAsync(caller, endpoint.Id, existing.Value, ScriptCheck(script.Id, enabled: true))).Problem);
        Assert.Empty(await ConfiguredChecksAsync(endpoint.Id));

        var runner = await CallerWithAsync(Permissions.ClientsView, Permissions.ChecksView, Permissions.ChecksEdit,
            Permissions.ScriptsView, Permissions.ScriptsRun, Permissions.JobsView);
        var saved = await Checks.SaveEndpointCheckAsync(runner, endpoint.Id, null, ScriptCheck(script.Id));
        Assert.True(saved.Success, saved.Problem);
        Assert.Contains(saved.Value.ToString("D"), await ConfiguredChecksAsync(endpoint.Id));
    }

    [Fact]
    public async Task Template_script_check_on_an_endpoint_needs_run_scripts_to_switch_on_or_adjust()
    {
        var (endpoint, site, script) = await SetupAsync("SCRIPTCHECK-ADJ");
        var template = await _fixture.Database.CreateMonitoringTemplateAsync(linkSite: site);
        var check = await Templates.SaveCheckAsync(WebFixtureBase.Admin(), template.Id, null, ScriptCheck(script.Id));
        Assert.True(check.Success, check.Problem);
        Assert.True((await Checks.SetDisabledAsync(WebFixtureBase.Admin(), endpoint.Id, check.Value, true)).Success);
        var caller = await CallerWithAsync(Permissions.ClientsView, Permissions.ChecksView, Permissions.ChecksEdit);

        Assert.Equal(ScriptChecks.PermissionProblem, (await Checks.SetDisabledAsync(caller, endpoint.Id, check.Value, false)).Problem);
        Assert.Equal(ScriptChecks.PermissionProblem,
            (await Checks.SaveOverrideAsync(caller, endpoint.Id, check.Value, new CheckOverrideInput(60, false, null, null, null))).Problem);
        Assert.DoesNotContain(check.Value.ToString("D"), await ConfiguredChecksAsync(endpoint.Id));
    }
}
