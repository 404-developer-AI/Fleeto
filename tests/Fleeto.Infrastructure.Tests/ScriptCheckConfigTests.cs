using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using ProtoCheckType = Fleeto.Protocol.Agent.V1.CheckType;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Guarantees what a script check carries in the signed configuration (0.2.0): the current version when the policy does not require
/// approval; otherwise the newest version approved by a current admin who did not write it, so an unapproved change never runs; no
/// script, with the reason, for a deleted script, a script of another client or a policy without an approved version.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScriptCheckConfigTests
{
    private readonly TestDatabase _db;

    public ScriptCheckConfigTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Fact]
    public async Task A_script_check_runs_the_version_its_policy_allows_and_otherwise_says_why_it_cannot_run()
    {
        var client = await _db.CreateClientAsync();
        var otherClient = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-SCRIPT", EndpointClass.Server);
        await _db.LoadTestLicenseAsync(100_000);
        var author = await _db.CreateUserAsync(FleetoRoles.Admin);
        var approver = await _db.CreateUserAsync(FleetoRoles.Admin);
        var (script, approvedVersion) = await _db.CreateScriptAsync(null, author.Id, body: "exit 0", approverId: approver.Id);
        var (foreignScript, _) = await _db.CreateScriptAsync(otherClient.Id, author.Id, approverId: approver.Id);
        var now = DateTime.UtcNow;

        CheckDefinition Check(Guid scriptId) => new()
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Name = "Script " + scriptId.ToString("N")[..6], Type = CheckType.Script,
            IntervalSeconds = 60, ParametersJson = CheckParameters.Serialize(new Dictionary<string, string> { ["script"] = scriptId.ToString("D"), ["language"] = "PowerShell" }),
            CreatedAt = now, UpdatedAt = now
        };
        var own = Check(script.Id);
        var foreign = Check(foreignScript.Id);
        var deleted = Check(Guid.NewGuid());
        var newBody = "Write-Output 'changed'\nexit 1";
        var changed = new ScriptVersion
        {
            Id = Guid.NewGuid(), ScriptId = script.Id, Number = 2, Body = newBody, Sha256 = ScriptLanguages.Sha256(newBody), TimeoutSeconds = 45,
            AuthorUserId = author.Id, AuthorName = "Author", CreatedAt = now
        };
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.CheckDefinitions.AddRange(own, foreign, deleted);
            db.ScriptVersions.Add(changed);
            await db.SaveChangesAsync();
            await db.Scripts.Where(s => s.Id == script.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentVersionId, changed.Id));
        }

        var builder = new AgentConfigBuilder(_db.Licenses);
        Protocol.Agent.V1.CheckSpec Spec(Protocol.Agent.V1.AgentConfig config, CheckDefinition check) => config.Checks.Single(c => c.Id == check.Id.ToString("D"));

        await using var context = _db.DbFactory.CreateSystem();
        var withoutApproval = (await builder.BuildAsync(context, endpoint.Id, _db.InstanceId))!.Config;
        var current = Spec(withoutApproval, own);
        Assert.Equal(ProtoCheckType.Script, current.Type);
        Assert.Equal(2u, current.Script.Version);
        Assert.Equal(newBody, current.Script.Body);
        Assert.Equal(changed.Sha256, current.Script.Sha256);
        Assert.Equal("45", current.Parameters["timeout_seconds"]);
        Assert.False(current.Parameters.ContainsKey("unavailable"));
        Assert.Null(Spec(withoutApproval, foreign).Script);
        Assert.Equal(ScriptCheckResolver.ClientReason, Spec(withoutApproval, foreign).Parameters["unavailable"]);
        Assert.Equal(ScriptCheckResolver.DeletedReason, Spec(withoutApproval, deleted).Parameters["unavailable"]);

        var policy = new Policy { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Approval " + Guid.NewGuid().ToString("N")[..8], ScriptApprovalRequired = true, CreatedAt = now, UpdatedAt = now };
        context.Policies.Add(policy);
        context.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = client.Id, PolicyId = policy.Id, Source = LinkSource.Manual, CreatedAt = now });
        await context.SaveChangesAsync();

        var withApproval = (await builder.BuildAsync(context, endpoint.Id, _db.InstanceId))!.Config;
        Assert.Equal(1u, Spec(withApproval, own).Script.Version);
        Assert.Equal(approvedVersion.Body, Spec(withApproval, own).Script.Body);

        // The approver is no longer an admin: nothing they approved runs where approval is required.
        var adminRole = await context.Roles.Where(r => r.NormalizedName == FleetoRoles.Admin.ToUpperInvariant()).Select(r => r.Id).SingleAsync();
        await context.UserRoles.Where(r => r.UserId == approver.Id && r.RoleId == adminRole).ExecuteDeleteAsync();
        var demoted = (await builder.BuildAsync(context, endpoint.Id, _db.InstanceId))!.Config;
        Assert.Null(Spec(demoted, own).Script);
        Assert.Equal(ScriptCheckResolver.NotApprovedReason, Spec(demoted, own).Parameters["unavailable"]);
    }
}
