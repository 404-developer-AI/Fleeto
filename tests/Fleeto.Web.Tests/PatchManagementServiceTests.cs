using System.Security.Cryptography;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>A database of its own: choosing Fleeto converts every patch policy and removes every Action1 agent of the instance.</summary>
public sealed class PatchManagementFixture : WebFixtureBase
{
    public PatchManagementFixture() : base("web_patching")
    {
    }
}

[CollectionDefinition(Name)]
public sealed class PatchManagementCollection : ICollectionFixture<PatchManagementFixture>
{
    public const string Name = "web-patching-database";
}

/// <summary>
/// Guarantees the instance-wide choice of patch management (0.9.0, <c>PATCHING.md</c> §16): only an admin chooses, with a fresh code
/// of the Fleeto authenticator; choosing Fleeto converts the patch policies of engine Action1, stops Action1 following clients, and
/// removes the Action1 agents of the managed endpoints (through Action1 where it knows the endpoint, by a signed job on Windows
/// otherwise, by hand elsewhere), and of the agent-only ones only on request; afterwards nothing installs the Action1 agent or offers to.
/// Choosing Action1 again changes nothing else.
/// </summary>
[Collection(PatchManagementCollection.Name)]
public sealed class PatchManagementServiceTests
{
    private readonly PatchManagementFixture _fixture;

    public PatchManagementServiceTests(PatchManagementFixture fixture)
    {
        _fixture = fixture;
    }

    private PatchManagementService Service => _fixture.Services.GetRequiredService<PatchManagementService>();

    private static Caller As(ApplicationUser user, string role) =>
        new(user.Id, user.DisplayName, user.Email!, [role], SystemClientScope.Instance, "198.51.100.60");

    private sealed record Scene(Guid PolicyId, Endpoint ReportedByAction1, Endpoint AgentInMappedClient, Endpoint AgentInUnmappedClient,
        Endpoint LinuxUnmapped, Endpoint AgentOnly, Endpoint WithoutAgent);

    /// <summary>An instance on Action1 with one Action1 policy and an Action1 agent in every situation that decides how it is removed.</summary>
    private async Task<Scene> SceneAsync()
    {
        await _fixture.Database.SetPatchManagerAsync(PatchManager.Action1);
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var mapped = await _fixture.Database.CreateClientAsync();
        var mappedSite = await _fixture.Database.CreateSiteAsync(mapped.Id);
        var unmapped = await _fixture.Database.CreateClientAsync();
        var unmappedSite = await _fixture.Database.CreateSiteAsync(unmapped.Id);
        var reported = await _fixture.Database.CreateEndpointAsync(mappedSite, EndpointTier.Managed, "WS-REPORTED");
        var inMapped = await _fixture.Database.CreateEndpointAsync(mappedSite, EndpointTier.Managed, "SRV-MAPPED", EndpointClass.Server);
        var inUnmapped = await _fixture.Database.CreateEndpointAsync(unmappedSite, EndpointTier.Managed, "WS-UNMAPPED");
        var linux = await _fixture.Database.CreateEndpointAsync(unmappedSite, EndpointTier.Managed, "LNX-UNMAPPED", EndpointClass.Server);
        var agentOnly = await _fixture.Database.CreateEndpointAsync(mappedSite, EndpointTier.AgentOnly, "WS-FREE");
        var without = await _fixture.Database.CreateEndpointAsync(mappedSite, EndpointTier.Managed, "WS-CLEAN");

        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == linux.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.OsPlatform, "linux").SetProperty(e => e.OsName, "Ubuntu 24.04.1 LTS"));
        var integration = await db.Integrations.FirstOrDefaultAsync(i => i.Type == IntegrationType.Action1);
        if (integration is null)
        {
            integration = new Integration
            {
                Id = Guid.NewGuid(), Type = IntegrationType.Action1, Region = Action1Region.Europe, EncryptedCredentials = "x",
                CredentialName = "api-key@action1.com", Enabled = true, CreatedAt = now, UpdatedAt = now
            };
            db.Integrations.Add(integration);
        }

        integration.FollowClients = true;
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = mapped.Id, ExternalTenantId = "org-" + mapped.Code,
            ExternalTenantName = mapped.Code, CreatedAt = now
        });
        foreach (var (endpoint, agentId) in new[]
                 {
                     (reported, "a1-" + reported.Id), (inMapped, "a1-" + inMapped.Id), (inUnmapped, "a1-" + inUnmapped.Id),
                     (linux, "a1-" + linux.Id), (agentOnly, "a1-" + agentOnly.Id), (without, "")
                 })
        {
            db.InventorySnapshots.Add(new InventorySnapshot
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ReceivedAt = now, Hash = "h", Action1AgentId = agentId
            });
        }

        foreach (var endpoint in new[] { reported, agentOnly })
        {
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ExternalEndpointId = "a1-" + endpoint.Id, ExternalTenantId = "org-" + mapped.Code,
                Coverage = PatchCoverage.Active, ProductLastSeenAt = now, ProductAgentVersion = "2.0.33", UpdatedAt = now
            });
        }

        var policy = new PatchPolicy
        {
            Id = Guid.NewGuid(), Name = "Action1 Monday " + mapped.Code, WeekDays = WeekDays.Monday, Scope = PatchUpdateScope.Filtered,
            UpdateSources = ["Applications"], UpdateTypes = ["Security Updates", "Feature Updates"], RequireApproval = true, AutoInstallAgent = true,
            CreatedAt = now, UpdatedAt = now
        };
        db.PatchPolicies.Add(policy);
        await db.SaveChangesAsync();
        return new Scene(policy.Id, reported, inMapped, inUnmapped, linux, agentOnly, without);
    }

    [Fact]
    public async Task Only_an_admin_with_a_valid_code_chooses_and_a_refusal_changes_nothing()
    {
        await SceneAsync();
        var admin = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin);
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);

        Assert.False((await Service.PreviewAsync(As(technician, FleetoRoles.Technician), PatchManager.Fleeto)).Success);
        Assert.False((await Service.ChooseAsync(As(technician, FleetoRoles.Technician), PatchManager.Fleeto, false,
            await CodeForAsync(technician))).Success);
        var wrong = await Service.ChooseAsync(As(admin, FleetoRoles.Admin), PatchManager.Fleeto, false, "000000");
        Assert.False(wrong.Success);
        Assert.False((await Service.ChooseAsync(As(admin, FleetoRoles.Admin), PatchManager.Action1, false, await CodeForAsync(admin))).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(PatchManager.Action1, await Infrastructure.Services.PatchManagers.GetAsync(db, default));
        Assert.True(await db.PatchPolicies.AnyAsync(p => p.Engine == PatchEngine.Action1));
    }

    [Fact]
    public async Task The_preview_lists_every_policy_it_converts_and_every_endpoint_with_its_way_of_removal()
    {
        var scene = await SceneAsync();
        var admin = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin);

        var preview = await Service.PreviewAsync(As(admin, FleetoRoles.Admin), PatchManager.Fleeto);

        Assert.True(preview.Success, preview.Problem);
        var policy = Assert.Single(preview.Value!.Policies, p => p.Id == scene.PolicyId);
        Assert.Contains(policy.Dropped, d => d.Contains("Applications", StringComparison.Ordinal));
        Assert.Contains(policy.Dropped, d => d.Contains("Feature Updates", StringComparison.Ordinal));
        Assert.True(preview.Value.FollowClients);
        var ways = preview.Value.Agents.ToDictionary(a => a.EndpointId);
        Assert.Equal(Action1RemovalWay.Action1, ways[scene.ReportedByAction1.Id].Way);
        Assert.Equal(Action1RemovalWay.Action1, ways[scene.AgentInMappedClient.Id].Way);
        Assert.Equal(Action1RemovalWay.Job, ways[scene.AgentInUnmappedClient.Id].Way);
        Assert.Equal((Action1RemovalWay.ByHand, Action1RemovalStatus.ByHand), (ways[scene.LinuxUnmapped.Id].Way, ways[scene.LinuxUnmapped.Id].Status));
        Assert.False(ways[scene.AgentOnly.Id].Managed);
        Assert.Equal(EndpointClass.Server, ways[scene.AgentInMappedClient.Id].Class);
        Assert.DoesNotContain(scene.WithoutAgent.Id, ways.Keys);

        // Nothing is saved by a preview.
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(PatchEngine.Action1, (await db.PatchPolicies.SingleAsync(p => p.Id == scene.PolicyId)).Engine);
    }

    [Fact]
    public async Task Choosing_Fleeto_converts_the_policies_and_removes_the_Action1_agents_of_the_managed_endpoints()
    {
        var scene = await SceneAsync();
        var admin = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin);
        var caller = As(admin, FleetoRoles.Admin);
        try
        {
            var chosen = await Service.ChooseAsync(caller, PatchManager.Fleeto, false, await CodeForAsync(admin));
            Assert.True(chosen.Success, chosen.Problem);

            await using (var db = _fixture.Database.DbFactory.CreateSystem())
            {
                Assert.Equal(PatchManager.Fleeto, await Infrastructure.Services.PatchManagers.GetAsync(db, default));
                var policy = await db.PatchPolicies.SingleAsync(p => p.Id == scene.PolicyId);
                Assert.Equal(PatchEngine.Fleeto, policy.Engine);
                Assert.False(policy.AutoInstallAgent);
                Assert.False(policy.RequireApproval);
                Assert.Empty(policy.UpdateSources);
                Assert.Equal(new[] { "Security Updates" }, policy.UpdateTypes);
                Assert.Null(PatchPolicyRules.Validate(policy));
                Assert.False(await db.Integrations.AnyAsync(i => i.Type == IntegrationType.Action1 && i.FollowClients));

                var operations = await db.IntegrationOperations.Where(o => o.Kind == IntegrationOperationKind.RemoveEndpoint).ToListAsync();
                Assert.Contains(operations, o => o.ExternalEndpointId == "a1-" + scene.ReportedByAction1.Id);
                Assert.Contains(operations, o => o.ExternalEndpointId == "a1-" + scene.AgentInMappedClient.Id);
                Assert.DoesNotContain(operations, o => o.ExternalEndpointId == "a1-" + scene.AgentOnly.Id);
                var job = await db.Jobs.SingleAsync(j => j.EndpointId == scene.AgentInUnmappedClient.Id && j.Type == JobType.Action1AgentRemoval);
                Assert.Equal(JobState.PendingSignature, job.State);
                Assert.True(await db.SigningRequests.AnyAsync(r => r.SubjectId == job.Id));
                Assert.False(await db.Jobs.AnyAsync(j => j.EndpointId == scene.LinuxUnmapped.Id));
                Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.PatchManagerChanged && a.ActorId == admin.Id.ToString()));
            }

            // Every removal is on its way; asking again starts nothing and says so.
            var view = await Service.GetAsync(caller);
            var statuses = view.Agents.ToDictionary(a => a.EndpointId, a => a.Status);
            Assert.Equal(Action1RemovalStatus.WaitingForAction1, statuses[scene.ReportedByAction1.Id]);
            Assert.Equal(Action1RemovalStatus.JobRunning, statuses[scene.AgentInUnmappedClient.Id]);
            Assert.Equal(Action1RemovalStatus.ByHand, statuses[scene.LinuxUnmapped.Id]);
            Assert.False((await Service.RemoveAgentsAsync(caller, false, await CodeForAsync(admin, nextStep: true))).Success);

            // The agent-only endpoint goes only when asked.
            var agentOnly = await Service.RemoveAgentsAsync(caller, true, await CodeForAsync(admin, nextStep: true, steps: 2));
            Assert.True(agentOnly.Success, agentOnly.Problem);
            Assert.True(agentOnly.Value!.ThroughAction1 >= 1);
            await using var after = _fixture.Database.DbFactory.CreateSystem();
            Assert.True(await after.IntegrationOperations.AnyAsync(o => o.ExternalEndpointId == "a1-" + scene.AgentOnly.Id));
        }
        finally
        {
            await _fixture.Database.SetPatchManagerAsync(PatchManager.Action1);
        }
    }

    [Fact]
    public async Task While_the_instance_patches_with_Fleeto_nothing_installs_or_checks_the_Action1_agent()
    {
        var scene = await SceneAsync();
        var admin = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin);
        await _fixture.Database.SetPatchManagerAsync(PatchManager.Fleeto);
        try
        {
            var patches = _fixture.Services.GetRequiredService<PatchService>();
            var install = await patches.InstallAgentAsync(WebFixture.Admin(), scene.WithoutAgent.Id);
            Assert.Equal(PatchManagement.InstallRefusal, install.Problem);
            var view = await patches.GetAsync(WebFixture.Admin(), scene.ReportedByAction1.Id);
            Assert.Equal(PatchManager.Fleeto, view!.Manager);
            Assert.False(view.CanInstallAgent);

            var policies = _fixture.Services.GetRequiredService<PatchPolicyService>();
            var input = new PatchPolicyInput("Action1 again " + Guid.NewGuid().ToString("N")[..6], null, true, PatchScheduleKind.Weekly, WeekDays.All, 1, 2,
                DayOfWeek.Tuesday, 20 * 60, false, PatchUpdateScope.All, [], [], [], [], [], false, 3, true, true, null, 30, 12);
            Assert.Equal(PatchManagement.Action1PolicyRefusal, (await policies.CreateAsync(WebFixture.Admin(), null, input)).Problem);
            Assert.True((await policies.CreateAsync(WebFixture.Admin(), null, input with { Engine = PatchEngine.Fleeto })).Success);

            var template = await _fixture.Database.CreateMonitoringTemplateAsync();
            var templates = _fixture.Services.GetRequiredService<MonitoringTemplateService>();
            var check = new CheckDefinitionInput("Action1 agent", CheckType.Action1Agent, CheckCatalog.FleetoEvaluatedIntervalSeconds,
                new Dictionary<string, string>(), null, null, 1, CheckAppliesTo.All, true);
            Assert.Equal(PatchManagement.Action1CheckRefusal, (await templates.SaveCheckAsync(WebFixture.Admin(), template.Id, null, check)).Problem);

            await using (var db = _fixture.Database.DbFactory.CreateSystem())
            {
                await db.Integrations.Where(i => i.Type == IntegrationType.Action1).ExecuteUpdateAsync(s => s.SetProperty(i => i.FollowClients, false));
            }

            var integrations = _fixture.Services.GetRequiredService<IntegrationService>();
            var follow = await integrations.SaveAction1Async(As(admin, FleetoRoles.Admin),
                new Action1Input("api-key@action1.com", "secret", Action1Region.Europe, true, FollowClients: true));
            Assert.False(follow.Success);
        }
        finally
        {
            await _fixture.Database.SetPatchManagerAsync(PatchManager.Action1);
        }
    }

    [Fact]
    public async Task Choosing_Action1_again_converts_nothing_back_and_installs_nothing()
    {
        var scene = await SceneAsync();
        var admin = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin);
        var caller = As(admin, FleetoRoles.Admin);
        try
        {
            Assert.True((await Service.ChooseAsync(caller, PatchManager.Fleeto, false, await CodeForAsync(admin))).Success);
            var back = await Service.ChooseAsync(caller, PatchManager.Action1, false, await CodeForAsync(admin, nextStep: true));
            Assert.True(back.Success, back.Problem);

            await using var db = _fixture.Database.DbFactory.CreateSystem();
            Assert.Equal(PatchManager.Action1, await Infrastructure.Services.PatchManagers.GetAsync(db, default));
            Assert.Equal(PatchEngine.Fleeto, (await db.PatchPolicies.SingleAsync(p => p.Id == scene.PolicyId)).Engine);
            Assert.False(await db.Jobs.AnyAsync(j => j.Type == JobType.Action1Agent));
            Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.PatchManagerChanged && a.ActorId == admin.Id.ToString()));
        }
        finally
        {
            await _fixture.Database.SetPatchManagerAsync(PatchManager.Action1);
        }
    }

    /// <summary>
    /// The current code of the user's authenticator, or one of the next steps: a code is used once per purpose, so a second action in
    /// the same test needs the code of a later step, which the authenticator accepts within its window of drift.
    /// </summary>
    private async Task<string> CodeForAsync(ApplicationUser user, bool nextStep = false, int steps = 1)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var stored = await users.FindByIdAsync(user.Id.ToString());
        var key = await users.GetAuthenticatorKeyAsync(stored!);
        if (key is null)
        {
            await users.ResetAuthenticatorKeyAsync(stored!);
            key = await users.GetAuthenticatorKeyAsync(stored!);
        }

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + (nextStep ? steps : 0);
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(Base32Decode(key!), counter);
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var buffer = 0;
        var output = new List<byte>();
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
