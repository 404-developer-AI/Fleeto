using System.Security.Cryptography;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Identity;
using Fleetify.Web.Security;
using Fleetify.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleetify.Web.Tests;

/// <summary>
/// Guarantees the script library and jobs in web (0.2.0): a changed body makes a new version and an unchanged save does not;
/// approval needs an admin who did not write the version and a valid, unused two-factor code; runs create one pending job and
/// signing request per endpoint that can run the script and skip the others with the reason; read-only users and other clients
/// get nothing; a job can be cancelled only before delivery; output is shown from the stored chunks.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class ScriptAndJobServiceTests
{
    private readonly WebFixture _fixture;

    public ScriptAndJobServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private ScriptService Scripts => _fixture.Services.GetRequiredService<ScriptService>();
    private JobService Jobs => _fixture.Services.GetRequiredService<JobService>();

    private static Caller As(ApplicationUser user, string role) =>
        new(user.Id, user.DisplayName, user.Email!, [role], SystemClientScope.Instance, "198.51.100.50");

    private async Task<string> CodeForAsync(ApplicationUser user)
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

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
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

    [Fact]
    public async Task Versions_follow_the_body_and_approval_needs_a_second_admin_with_a_fresh_code()
    {
        var author = await _fixture.Database.CreateUserAsync(FleetifyRoles.Admin, displayName: "Author");
        var approver = await _fixture.Database.CreateUserAsync(FleetifyRoles.Admin, displayName: "Approver");
        var technician = await _fixture.Database.CreateUserAsync(FleetifyRoles.Technician);
        var name = "Restart spooler " + Guid.NewGuid().ToString("N")[..6];

        var created = await Scripts.CreateAsync(As(author, FleetifyRoles.Admin),
            new ScriptInput(name, null, ScriptLanguage.Batch, null, "net stop spooler\nnet start spooler", 300));
        Assert.True(created.Success, created.Problem);
        var detail = (await Scripts.GetAsync(As(author, FleetifyRoles.Admin), created.Value))!;
        Assert.Equal("net stop spooler\r\nnet start spooler", detail.Body);
        var first = Assert.Single(detail.Versions);

        var unchanged = await Scripts.SaveVersionAsync(As(author, FleetifyRoles.Admin), created.Value, "net stop spooler\r\nnet start spooler", 300);
        Assert.Equal(1, unchanged.Value);
        Assert.Equal(2, (await Scripts.SaveVersionAsync(As(technician, FleetifyRoles.Technician), created.Value, "net stop spooler", 300)).Value);
        Assert.False((await Scripts.CreateAsync(As(technician, FleetifyRoles.Technician),
            new ScriptInput(name, null, ScriptLanguage.Batch, null, "echo", 300))).Success);

        var current = (await Scripts.GetAsync(As(author, FleetifyRoles.Admin), created.Value))!.Versions.Single(v => v.IsCurrent);
        Assert.False((await Scripts.ApproveAsync(As(technician, FleetifyRoles.Technician), current.Id, "123456")).Success);
        Assert.False((await Scripts.ApproveAsync(As(approver, FleetifyRoles.Admin), current.Id, "000000")).Success);

        // The technician wrote version 2; another admin approves it.
        var code = await CodeForAsync(approver);
        var approved = await Scripts.ApproveAsync(As(approver, FleetifyRoles.Admin), current.Id, code);
        Assert.True(approved.Success, approved.Problem);
        Assert.True((await Scripts.GetAsync(As(author, FleetifyRoles.Admin), created.Value))!.Versions.Single(v => v.IsCurrent).IsApproved);

        // The same code cannot approve another version.
        await Scripts.SaveVersionAsync(As(technician, FleetifyRoles.Technician), created.Value, "net start spooler", 300);
        var third = (await Scripts.GetAsync(As(author, FleetifyRoles.Admin), created.Value))!.Versions.Single(v => v.IsCurrent);
        var reused = await Scripts.ApproveAsync(As(approver, FleetifyRoles.Admin), third.Id, code);
        Assert.False(reused.Success);

        // An author never approves their own version.
        var own = await Scripts.CreateAsync(As(author, FleetifyRoles.Admin), new ScriptInput(name + " own", null, ScriptLanguage.PowerShell, null, "Get-Date", 300));
        var ownVersion = (await Scripts.GetAsync(As(author, FleetifyRoles.Admin), own.Value))!.Versions.Single();
        Assert.Contains("second admin", (await Scripts.ApproveAsync(As(author, FleetifyRoles.Admin), ownVersion.Id, await CodeForAsync(author))).Problem);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.Action == AuditActions.ScriptVersionApproved && a.TargetId == current.Id.ToString()).ToListAsync();
        Assert.Contains(audit, a => a.DetailsJson.Contains("\"approved\""));
        Assert.Contains(audit, a => a.DetailsJson.Contains("wrong two-factor code"));
        Assert.NotEqual(first.Id, current.Id);
    }

    [Fact]
    public async Task Runs_create_jobs_only_where_the_script_can_run_and_cancel_before_delivery()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var technician = await _fixture.Database.CreateUserAsync(FleetifyRoles.Technician);
        var readOnly = await _fixture.Database.CreateUserAsync(FleetifyRoles.ReadOnly);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var managed = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-RUN-1", EndpointClass.Server);
        var agentOnly = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-RUN-2");
        var (script, _) = await _fixture.Database.CreateScriptAsync(null, technician.Id);
        var caller = As(technician, FleetifyRoles.Technician);

        Assert.False((await Jobs.RunAsync(As(readOnly, FleetifyRoles.ReadOnly), script.Id, [managed.Id], TimeSpan.FromHours(24))).Success);
        Assert.False((await Jobs.RunAsync(caller, script.Id, [managed.Id], TimeSpan.FromHours(3))).Success);
        Assert.Contains(await Jobs.ListRunnableScriptsAsync(caller, [managed.Id]), s => s.Id == script.Id);

        var run = await Jobs.RunAsync(caller, script.Id, [managed.Id, agentOnly.Id], TimeSpan.FromHours(24));
        Assert.True(run.Success, run.Problem);
        Assert.Equal(1, run.Value!.Created);
        Assert.Equal("WS-RUN-2", Assert.Single(run.Value.Skipped).Hostname);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.BatchId == run.Value.BatchId);
            Assert.Equal(JobState.PendingSignature, job.State);
            Assert.True(await db.SigningRequests.AnyAsync(r => r.Kind == SigningRequestKind.Job && r.SubjectId == job.Id));
        }

        var listed = Assert.Single(await Jobs.ListForEndpointAsync(caller, managed.Id));
        Assert.True(listed.CanCancel);
        var otherClient = WebFixture.CallerWith(new RestrictedClientScope([Guid.NewGuid()]), FleetifyRoles.Technician);
        Assert.Empty(await Jobs.ListForEndpointAsync(otherClient, managed.Id));
        Assert.Null(await Jobs.GetOutputAsync(otherClient, listed.Id));
        Assert.False((await Jobs.CancelAsync(otherClient, listed.Id)).Success);

        Assert.True((await Jobs.CancelAsync(caller, listed.Id)).Success);
        Assert.Equal(JobState.Cancelled, Assert.Single(await Jobs.ListForEndpointAsync(caller, managed.Id)).State);
        Assert.False((await Jobs.CancelAsync(caller, listed.Id)).Success);

        // Under an approval policy an unapproved version is skipped.
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var policy = new Policy { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Servers " + Guid.NewGuid().ToString("N")[..6], ScriptApprovalRequired = true };
            db.Policies.Add(policy);
            db.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = client.Id, PolicyId = policy.Id });
            await db.SaveChangesAsync();
        }

        var refused = await Jobs.RunAsync(caller, script.Id, [managed.Id], TimeSpan.FromHours(1));
        Assert.False(refused.Success);
        Assert.Contains("approved", refused.Problem);
    }

    [Fact]
    public async Task Script_checks_use_scripts_of_their_own_scope_and_a_used_script_cannot_be_deleted()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var technician = await _fixture.Database.CreateUserAsync(FleetifyRoles.Technician);
        var caller = As(technician, FleetifyRoles.Technician);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-SCRIPTCHECK", EndpointClass.Server);
        var (global, _) = await _fixture.Database.CreateScriptAsync(null, technician.Id, ScriptLanguage.Batch, "@echo off\r\nexit /b 0");
        var (clientScript, _) = await _fixture.Database.CreateScriptAsync(client.Id, technician.Id);
        var (bash, _) = await _fixture.Database.CreateScriptAsync(null, technician.Id, ScriptLanguage.Bash, "exit 0");
        var templates = _fixture.Services.GetRequiredService<MonitoringTemplateService>();
        var endpointChecks = _fixture.Services.GetRequiredService<EndpointCheckService>();
        CheckDefinitionInput Input(Guid scriptId, string? language = null)
        {
            var parameters = new Dictionary<string, string> { ["script"] = scriptId.ToString("D") };
            if (language is not null)
            {
                parameters["language"] = language;
            }

            return new CheckDefinitionInput("Script check", CheckType.Script, 300, parameters, null, null, 1, CheckAppliesTo.All, true);
        }

        var globalTemplate = await templates.CreateAsync(caller, "Scripts " + Guid.NewGuid().ToString("N")[..6], null, null);
        Assert.Contains("only use global scripts", (await templates.SaveCheckAsync(caller, globalTemplate.Value, null, Input(clientScript.Id))).Problem);
        // The language comes from the script, whatever the input says.
        var saved = await templates.SaveCheckAsync(caller, globalTemplate.Value, null, Input(global.Id, "Bash"));
        Assert.True(saved.Success, saved.Problem);
        Assert.False((await endpointChecks.SaveEndpointCheckAsync(caller, endpoint.Id, null, Input(bash.Id))).Success);
        var onEndpoint = await endpointChecks.SaveEndpointCheckAsync(caller, endpoint.Id, null, Input(clientScript.Id));
        Assert.True(onEndpoint.Success, onEndpoint.Problem);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var parameters = await db.CheckDefinitions.AsNoTracking().Where(c => c.Id == saved.Value).Select(c => c.ParametersJson).SingleAsync();
            Assert.Equal("Batch", Core.Domain.CheckParameters.Parse(parameters)["language"]);
        }

        Assert.Contains("One check uses this script", (await Scripts.DeleteAsync(caller, clientScript.Id)).Problem);
        Assert.True((await endpointChecks.DeleteEndpointCheckAsync(caller, endpoint.Id, onEndpoint.Value)).Success);
        Assert.True((await Scripts.DeleteAsync(caller, clientScript.Id)).Success);

        // A new version re-signs the configurations of the checks that use the script.
        Assert.Equal(2, (await Scripts.SaveVersionAsync(caller, global.Id, "@echo off\r\nexit /b 1", 300)).Value);
        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await check.ConfigChangeEvents.AnyAsync(e => e.Scope == ConfigChangeScope.Script && e.ScopeId == global.Id));
    }

    [Fact]
    public async Task Output_is_shown_from_the_stored_chunks_in_order()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var technician = await _fixture.Database.CreateUserAsync(FleetifyRoles.Technician);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-OUT");
        var (script, version) = await _fixture.Database.CreateScriptAsync(null, technician.Id);
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var jobId = Guid.NewGuid();
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.Jobs.Add(new Job
            {
                Id = jobId, ClientId = client.Id, EndpointId = endpoint.Id, BatchId = Guid.NewGuid(), ScriptId = script.Id, ScriptVersionId = version.Id,
                ScriptName = script.Name, ScriptVersionNumber = 1, Language = script.Language, ScriptSha256 = version.Sha256, TimeoutSeconds = 600,
                MaxOutputBytes = ScriptRules.MaxOutputBytes, CreatedAt = now, ValidUntil = now.AddHours(1), InitiatedByUserId = technician.Id, InitiatedByName = "Tech",
                State = JobState.Succeeded, Signature = new byte[64], Payload = [1], DeliveredAt = now, CompletedAt = now, ExitCode = 0, OutputState = JobOutputState.Complete
            });
            db.JobOutputChunks.AddRange(
                new JobOutputChunk { JobId = jobId, ClientId = client.Id, Stream = JobStream.Stdout, Sequence = 1, Data = "world\n"u8.ToArray(), ReceivedAt = now },
                new JobOutputChunk { JobId = jobId, ClientId = client.Id, Stream = JobStream.Stdout, Sequence = 0, Data = "hello "u8.ToArray(), ReceivedAt = now },
                new JobOutputChunk { JobId = jobId, ClientId = client.Id, Stream = JobStream.Stderr, Sequence = 0, Data = "warning"u8.ToArray(), ReceivedAt = now });
            await db.SaveChangesAsync();
        }

        var output = await Jobs.GetOutputAsync(WebFixture.Technician(), jobId);

        Assert.Equal("hello world\n", output!.Stdout);
        Assert.Equal("warning", output.Stderr);
        Assert.Equal(12, output.StdoutBytes);
        Assert.False(output.ShortenedInView);
        Assert.Equal("SRV-OUT", output.Job.Hostname);
    }
}
