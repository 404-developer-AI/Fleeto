using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Library scripts in a remote background terminal (0.6.0): only the technician of a connected participant gets a script, with the checks
/// of a job (managed, platform, client, approval where the policy requires it), only reported users can be chosen, and every run is in the
/// audit log by name, version and hash, never its body.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class RemoteScriptServiceTests
{
    private const string Users = """[{"id":"S-1-5-21-1-1001","account":"CONTOSO\\jan","sessions":[{"id":"1","console":true}]}]""";

    private readonly WebFixture _fixture;

    public RemoteScriptServiceTests(WebFixture fixture) => _fixture = fixture;

    private RemoteScriptService Service => _fixture.Services.GetRequiredService<RemoteScriptService>();

    private static Caller As(ApplicationUser user, string role) =>
        new(user.Id, user.DisplayName, user.Email!, [role], SystemClientScope.Instance, "198.51.100.51");

    private async Task<(Endpoint Endpoint, Site Site, ApplicationUser Technician)> EndpointAsync(EndpointTier tier = EndpointTier.Managed)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, tier, "SRV-SCRIPT", EndpointClass.Server);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.OsPlatform, "windows").SetProperty(e => e.AgentVersion, "0.6.0")
            .SetProperty(e => e.SignedInUsersJson, Users).SetProperty(e => e.SignedInUsersAt, DateTime.UtcNow));
        return (endpoint, site, technician);
    }

    /// <summary>A remote background participant of the user on the endpoint, in the given state.</summary>
    private async Task<Guid> ParticipantAsync(Endpoint endpoint, Guid userId, RemoteParticipantState state = RemoteParticipantState.Connected,
        RemoteSessionKind kind = RemoteSessionKind.RemoteBackground)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var session = new RemoteSession
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = kind,
            Component = kind == RemoteSessionKind.RemoteBackground ? AgentComponent.Watchdog : AgentComponent.Agent, StartedByUserId = userId,
            StartedByName = "Tech", CreatedAt = now
        };
        var participant = new RemoteSessionParticipant
        {
            Id = Guid.NewGuid(), SessionId = session.Id, ClientId = endpoint.ClientId, EndpointId = endpoint.Id, UserId = userId, UserName = "Tech",
            BrowserPublicKey = new byte[32], State = state, CreatedAt = now
        };
        db.RemoteSessions.Add(session);
        db.RemoteSessionParticipants.Add(participant);
        await db.SaveChangesAsync();
        return participant.Id;
    }

    [Fact]
    public async Task A_connected_technician_gets_the_current_version_and_the_run_is_audited_without_its_body()
    {
        var (endpoint, _, technician) = await EndpointAsync();
        var (script, version) = await _fixture.Database.CreateScriptAsync(null, technician.Id, ScriptLanguage.PowerShell, "Read-Host 'Name'");
        var caller = As(technician, FleetoRoles.Technician);
        var participant = await ParticipantAsync(endpoint, technician.Id);

        var choices = await Service.ListAsync(caller, endpoint.Id);
        Assert.NotNull(choices);
        Assert.Contains(choices.Scripts, s => s.Id == script.Id);
        Assert.False(choices.ApprovalRequired);
        Assert.Equal(@"CONTOSO\jan", Assert.Single(choices.SignedInUsers!.Users).Account);

        var prepared = await Service.PrepareAsync(caller, participant, script.Id, runAsUser: true, "S-1-5-21-1-1001");
        Assert.True(prepared.Success, prepared.Problem);
        Assert.Equal(new TerminalScript(script.Name, 1, "powershell", "Read-Host 'Name'", version.Sha256, true, "S-1-5-21-1-1001"), prepared.Value);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var audit = (await db.AuditEntries.AsNoTracking().Where(a => a.Action == AuditActions.RemoteSessionScript).ToListAsync())
            .Single(a => a.DetailsJson!.Contains(participant.ToString()));
        Assert.Contains(script.Name, audit.DetailsJson);
        Assert.Contains(version.Sha256, audit.DetailsJson);
        Assert.Contains("jan", audit.DetailsJson);
        Assert.DoesNotContain("Read-Host", audit.DetailsJson);
    }

    [Fact]
    public async Task Only_the_technicians_own_connected_background_participant_gets_a_script()
    {
        var (endpoint, _, technician) = await EndpointAsync();
        var (script, _) = await _fixture.Database.CreateScriptAsync(null, technician.Id);
        var caller = As(technician, FleetoRoles.Technician);
        var other = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);

        Assert.False((await Service.PrepareAsync(caller, await ParticipantAsync(endpoint, other.Id), script.Id, false, null)).Success);
        Assert.False((await Service.PrepareAsync(caller, await ParticipantAsync(endpoint, technician.Id, RemoteParticipantState.Ended), script.Id, false, null)).Success);
        Assert.False((await Service.PrepareAsync(caller, await ParticipantAsync(endpoint, technician.Id, kind: RemoteSessionKind.RemoteControl), script.Id, false, null)).Success);

        var readOnly = await _fixture.Database.CreateUserAsync(FleetoRoles.ReadOnly);
        var readOnlyCaller = As(readOnly, FleetoRoles.ReadOnly);
        Assert.Null(await Service.ListAsync(readOnlyCaller, endpoint.Id));
        Assert.False((await Service.PrepareAsync(readOnlyCaller, await ParticipantAsync(endpoint, readOnly.Id), script.Id, false, null)).Success);
    }

    [Fact]
    public async Task The_checks_of_a_job_apply_and_only_a_reported_user_can_be_chosen()
    {
        var (endpoint, site, technician) = await EndpointAsync();
        var caller = As(technician, FleetoRoles.Technician);
        var participant = await ParticipantAsync(endpoint, technician.Id);
        var (bash, _) = await _fixture.Database.CreateScriptAsync(null, technician.Id, ScriptLanguage.Bash, "read name");
        var otherClient = await _fixture.Database.CreateClientAsync();
        var (foreign, _) = await _fixture.Database.CreateScriptAsync(otherClient.Id, technician.Id);
        var (script, _) = await _fixture.Database.CreateScriptAsync(null, technician.Id);

        Assert.Contains("does not run", (await Service.PrepareAsync(caller, participant, bash.Id, false, null)).Problem);
        Assert.Contains("another client", (await Service.PrepareAsync(caller, participant, foreign.Id, false, null)).Problem);
        Assert.Contains("no longer signed in", (await Service.PrepareAsync(caller, participant, script.Id, true, "S-1-5-21-1-1009")).Problem);
        Assert.False((await Service.PrepareAsync(caller, participant, script.Id, false, "S-1-5-21-1-1001")).Success);
        Assert.True((await Service.PrepareAsync(caller, participant, script.Id, true, null)).Success);

        // Under an approval policy only an approved current version is offered and sent.
        var admin = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin);
        var (approved, _) = await _fixture.Database.CreateScriptAsync(null, technician.Id, approverId: admin.Id);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var policy = new Policy { Id = Guid.NewGuid(), ClientId = site.ClientId, Name = "Approval " + Guid.NewGuid().ToString("N")[..6], ScriptApprovalRequired = true };
            db.Policies.Add(policy);
            db.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = site.ClientId, PolicyId = policy.Id });
            await db.SaveChangesAsync();
        }

        var choices = await Service.ListAsync(caller, endpoint.Id);
        Assert.True(choices!.ApprovalRequired);
        Assert.Contains(choices.Scripts, s => s.Id == approved.Id);
        Assert.DoesNotContain(choices.Scripts, s => s.Id == script.Id);
        Assert.Contains("approved", (await Service.PrepareAsync(caller, participant, script.Id, false, null)).Problem);
        Assert.True((await Service.PrepareAsync(caller, participant, approved.Id, false, null)).Success);
    }

    [Fact]
    public async Task An_agent_only_endpoint_runs_no_script()
    {
        var (endpoint, _, technician) = await EndpointAsync(EndpointTier.AgentOnly);
        var (script, _) = await _fixture.Database.CreateScriptAsync(null, technician.Id);
        var prepared = await Service.PrepareAsync(As(technician, FleetoRoles.Technician), await ParticipantAsync(endpoint, technician.Id), script.Id, false, null);
        Assert.False(prepared.Success);
        Assert.Contains("managed", prepared.Problem);
    }
}
