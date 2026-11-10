using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Signed configurations carry the body of a script check approved by somebody who may approve scripts with two factors. Every change
/// that takes that away from the approver (the permissions of its role, its roles, its Entra ID link, its second factor, its account)
/// writes a Script configuration change, so the configurations are signed again without the body.
/// </summary>
[Collection(WebCollection.Name)]
public class ScriptApproverChangeTests
{
    private readonly WebFixture _fixture;

    public ScriptApproverChangeTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private UserAdminService Users => _fixture.Services.GetRequiredService<UserAdminService>();

    private async Task<int> ScriptEventsAsync(Guid scriptId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.ConfigChangeEvents.CountAsync(e => e.Scope == ConfigChangeScope.Script && e.ScopeId == scriptId);
    }

    private async Task<string?> ResolveAsync(Guid scriptId, Guid clientId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return (await ScriptCheckResolver.ResolveAsync(db, scriptId.ToString("D"), clientId, "windows", true, CancellationToken.None)).UnavailableReason;
    }

    /// <summary>An approver with its own role, and a script of a new client it approved, whose approved body runs.</summary>
    private async Task<(Guid ApproverId, string RoleName, Guid RoleId, Guid ScriptId, Guid ClientId)> ApprovedScriptAsync(bool linkedToEntra = false)
    {
        var client = await _fixture.Database.CreateClientAsync();
        var roleName = await _fixture.Database.CreateRoleAsync(Permissions.ScriptsView, Permissions.ScriptsApprove);
        var approver = await _fixture.Database.CreateUserAsync(roleName, twoFactor: !linkedToEntra);
        Guid roleId;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            roleId = await db.Roles.Where(r => r.Name == roleName).Select(r => r.Id).SingleAsync();
            if (linkedToEntra)
            {
                // No authenticator: the link counts as its second factor.
                await db.Users.Where(u => u.Id == approver.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.EntraObjectId, Guid.NewGuid())
                    .SetProperty(u => u.EntraTenantId, Guid.NewGuid().ToString())
                    .SetProperty(u => u.EntraLinkedAt, DateTime.UtcNow));
            }
        }

        var (script, _) = await _fixture.Database.CreateScriptAsync(client.Id, Guid.NewGuid(), approverId: approver.Id);
        Assert.Null(await ResolveAsync(script.Id, client.Id));
        return (approver.Id, roleName, roleId, script.Id, client.Id);
    }

    [Fact]
    public async Task Taking_approve_scripts_from_the_role_of_an_approver_signs_its_script_checks_again()
    {
        var (_, roleName, roleId, scriptId, clientId) = await ApprovedScriptAsync();
        var roles = _fixture.Services.GetRequiredService<RoleService>();
        var before = await ScriptEventsAsync(scriptId);

        // A change that leaves Approve scripts alone asks for nothing.
        var unrelated = await roles.UpdateAsync(WebFixtureBase.Admin(), roleId,
            new RoleInput(roleName, "Another description", [Permissions.ScriptsView, Permissions.ScriptsApprove]));
        Assert.True(unrelated.Success, unrelated.Problem);
        Assert.Equal(before, await ScriptEventsAsync(scriptId));

        var updated = await roles.UpdateAsync(WebFixtureBase.Admin(), roleId, new RoleInput(roleName, null, [Permissions.ScriptsView]));
        Assert.True(updated.Success, updated.Problem);
        Assert.Equal(ScriptCheckResolver.NotApprovedReason, await ResolveAsync(scriptId, clientId));
        Assert.Equal(before + 1, await ScriptEventsAsync(scriptId));
    }

    [Fact]
    public async Task Changing_the_roles_of_an_approver_signs_its_script_checks_again()
    {
        var (approverId, _, _, scriptId, clientId) = await ApprovedScriptAsync();
        var before = await ScriptEventsAsync(scriptId);

        var result = await Users.SetRolesAsync(WebFixtureBase.Admin(), approverId, [FleetoRoles.ReadOnly]);
        Assert.True(result.Success, result.Problem);
        Assert.Equal(ScriptCheckResolver.NotApprovedReason, await ResolveAsync(scriptId, clientId));
        Assert.Equal(before + 1, await ScriptEventsAsync(scriptId));
    }

    [Fact]
    public async Task Unlinking_an_approver_without_an_authenticator_from_Entra_signs_its_script_checks_again()
    {
        var (approverId, _, _, scriptId, clientId) = await ApprovedScriptAsync(linkedToEntra: true);
        var before = await ScriptEventsAsync(scriptId);

        var result = await Users.UnlinkEntraAsync(WebFixtureBase.Admin(), approverId);
        Assert.True(result.Success, result.Problem);
        Assert.Equal(ScriptCheckResolver.NotApprovedReason, await ResolveAsync(scriptId, clientId));
        Assert.Equal(before + 1, await ScriptEventsAsync(scriptId));
    }

    [Fact]
    public async Task Resetting_two_factor_or_deleting_an_approver_signs_its_script_checks_again()
    {
        var (resetId, _, _, resetScript, resetClient) = await ApprovedScriptAsync();
        var before = await ScriptEventsAsync(resetScript);
        Assert.True((await Users.ResetTwoFactorAsync(WebFixtureBase.Admin(), resetId)).Success);
        Assert.Equal(ScriptCheckResolver.NotApprovedReason, await ResolveAsync(resetScript, resetClient));
        Assert.Equal(before + 1, await ScriptEventsAsync(resetScript));

        var (deletedId, _, _, deletedScript, deletedClient) = await ApprovedScriptAsync();
        before = await ScriptEventsAsync(deletedScript);
        Assert.True((await Users.DeleteAsync(WebFixtureBase.Admin(), deletedId)).Success);
        Assert.Equal(ScriptCheckResolver.NotApprovedReason, await ResolveAsync(deletedScript, deletedClient));
        Assert.Equal(before + 1, await ScriptEventsAsync(deletedScript));
    }
}
