using System.Security.Cryptography;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees of opening a remote background session in web (0.3.0): only admins and technicians, only a visible managed endpoint whose
/// watchdog is online and runs 0.3.0 or later; a valid request writes the session, the participant with the browser key, the signing request
/// and the audit entry, and returns the signed token with the public key fingerprints of the watchdog's valid certificates; a refusal by the
/// signer reaches the technician; a request the signer does not answer in time is given up, so a late signature is never used.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class RemoteSessionServiceTests
{
    private readonly WebFixture _fixture;

    public RemoteSessionServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private RemoteSessionService Service => _fixture.Services.GetRequiredService<RemoteSessionService>();

    private async Task<Endpoint> EndpointAsync(EndpointTier tier = EndpointTier.Managed, string watchdogVersion = "0.3.0", bool watchdogOnline = true)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, tier, "SRV-REMOTE", EndpointClass.Server);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.OsPlatform, "windows")
            .SetProperty(e => e.WatchdogVersion, watchdogVersion)
            .SetProperty(e => e.WatchdogOnline, watchdogOnline));
        db.AgentCertificates.Add(new AgentCertificate
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Fingerprint = Guid.NewGuid().ToString("N"),
            PublicKeyFingerprint = "a1".PadRight(64, '0'), SerialNumber = "1", Role = AgentComponent.Watchdog,
            IssuedAt = _fixture.Database.Time.GetUtcNow().UtcDateTime, ExpiresAt = _fixture.Database.Time.GetUtcNow().UtcDateTime.AddDays(90)
        });
        await db.SaveChangesAsync();
        return endpoint;
    }

    /// <summary>A managed Windows endpoint whose agent serves remote control, with one user signed in on the console and one over RDP.</summary>
    private async Task<Endpoint> ControlEndpointAsync(string agentVersion = RemoteSessionRules.MinimumAgentVersion, bool online = true, string platform = "windows")
    {
        var endpoint = await EndpointAsync();
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.OsPlatform, platform)
            .SetProperty(e => e.AgentVersion, agentVersion)
            .SetProperty(e => e.IsOnline, online)
            .SetProperty(e => e.SignedInUsersJson,
                "[{\"id\":\"S-1-5-21-1-1001\",\"account\":\"ACME\\\\anna\",\"sessions\":[{\"id\":\"1\",\"console\":true}]}," +
                "{\"id\":\"S-1-5-21-1-1002\",\"account\":\"ACME\\\\bert\",\"sessions\":[{\"id\":\"3\",\"console\":false}]}]"));
        db.AgentCertificates.Add(new AgentCertificate
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Fingerprint = Guid.NewGuid().ToString("N"),
            PublicKeyFingerprint = "b2".PadRight(64, '0'), SerialNumber = "2", Role = AgentComponent.Agent,
            IssuedAt = _fixture.Database.Time.GetUtcNow().UtcDateTime, ExpiresAt = _fixture.Database.Time.GetUtcNow().UtcDateTime.AddDays(90)
        });
        await db.SaveChangesAsync();
        return endpoint;
    }

    /// <summary>Plays fleeto-signer: signs (or refuses) the participant of the next request as soon as it appears.</summary>
    private async Task SignNextAsync(Guid endpointId, string? refusal = null)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var db = _fixture.Database.DbFactory.CreateSystem();
            var participant = await db.RemoteSessionParticipants.FirstOrDefaultAsync(p => p.EndpointId == endpointId && p.State == RemoteParticipantState.Requested);
            if (participant is not null)
            {
                if (refusal is null)
                {
                    participant.State = RemoteParticipantState.Signed;
                    participant.TokenPayload = [1, 2, 3];
                    participant.TokenSignature = [4, 5, 6];
                    participant.SigningKeyId = "key";
                }
                else
                {
                    participant.State = RemoteParticipantState.Refused;
                    participant.EndReason = refusal;
                }

                await db.SaveChangesAsync();
                var request = await db.SigningRequests.FirstAsync(r => r.SubjectId == participant.Id);
                await _fixture.Database.Bus.PublishAsync(NotificationChannels.SigningResults, request.Id.ToString());
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("No remote session request appeared.");
    }

    [Fact]
    public async Task A_technician_gets_the_signed_token_and_the_watchdog_key_fingerprints()
    {
        var endpoint = await EndpointAsync();
        var key = RandomNumberGenerator.GetBytes(32);
        var signer = SignNextAsync(endpoint.Id);

        var result = await Service.OpenBackgroundAsync(WebFixtureBase.Technician(), endpoint.Id, key, "Printer queue stuck");
        await signer;

        Assert.True(result.Success, result.Problem);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), result.Value!.Token);
        Assert.Equal(["a1".PadRight(64, '0')], result.Value.CertificateFingerprints);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var participant = await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == result.Value.ParticipantId);
        Assert.Equal(key, participant.BrowserPublicKey);
        var session = await db.RemoteSessions.AsNoTracking().SingleAsync(s => s.Id == participant.SessionId);
        Assert.Equal(RemoteSessionKind.RemoteBackground, session.Kind);
        Assert.Equal(AgentComponent.Watchdog, session.Component);
        Assert.Equal("Printer queue stuck", session.Reason);
        Assert.True(await db.SigningRequests.AnyAsync(r => r.SubjectId == participant.Id && r.Kind == SigningRequestKind.RemoteSessionToken));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.RemoteSessionRequested && a.TargetId == session.Id.ToString()));
    }

    [Fact]
    public async Task Read_only_users_other_clients_and_agent_only_endpoints_get_no_session()
    {
        var endpoint = await EndpointAsync();
        var key = RandomNumberGenerator.GetBytes(32);

        var readOnly = WebFixtureBase.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);
        Assert.Equal(ServiceResult.ForbiddenProblem, (await Service.OpenBackgroundAsync(readOnly, endpoint.Id, key, null)).Problem);
        Assert.Null(await Service.GetBackgroundTargetAsync(readOnly, endpoint.Id));

        var otherClient = await _fixture.Database.CreateClientAsync();
        var restricted = WebFixtureBase.CallerWith(new RestrictedClientScope([otherClient.Id]), FleetoRoles.Technician);
        Assert.Null(await Service.GetBackgroundTargetAsync(restricted, endpoint.Id));
        Assert.False((await Service.OpenBackgroundAsync(restricted, endpoint.Id, key, null)).Success);

        var agentOnly = await EndpointAsync(EndpointTier.AgentOnly);
        var refused = await Service.OpenBackgroundAsync(WebFixtureBase.Technician(), agentOnly.Id, key, null);
        Assert.Contains("only available on managed endpoints", refused.Problem);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.RemoteSessions.AnyAsync(s => s.EndpointId == endpoint.Id || s.EndpointId == agentOnly.Id));
    }

    [Fact]
    public async Task An_old_or_offline_watchdog_and_an_invalid_browser_key_are_refused_with_the_reason()
    {
        var old = await EndpointAsync(watchdogVersion: "0.2.2");
        Assert.Contains("needs Fleeto 0.3.0", (await Service.OpenBackgroundAsync(WebFixtureBase.Technician(), old.Id, RandomNumberGenerator.GetBytes(32), null)).Problem);

        var offline = await EndpointAsync(watchdogOnline: false);
        Assert.Contains("offline", (await Service.OpenBackgroundAsync(WebFixtureBase.Technician(), offline.Id, RandomNumberGenerator.GetBytes(32), null)).Problem);

        var endpoint = await EndpointAsync();
        Assert.Contains("valid session key", (await Service.OpenBackgroundAsync(WebFixtureBase.Technician(), endpoint.Id, new byte[32], null)).Problem);
        Assert.Contains("valid session key", (await Service.OpenBackgroundAsync(WebFixtureBase.Technician(), endpoint.Id, new byte[16], null)).Problem);
    }

    [Fact]
    public async Task A_signer_refusal_reaches_the_technician()
    {
        var endpoint = await EndpointAsync();
        var signer = SignNextAsync(endpoint.Id, "The endpoint is not managed. Switch it to managed before opening a remote session.");
        var result = await Service.OpenBackgroundAsync(WebFixtureBase.Admin(), endpoint.Id, RandomNumberGenerator.GetBytes(32), null);
        await signer;
        Assert.Equal("The endpoint is not managed. Switch it to managed before opening a remote session.", result.Problem);
    }

    [Fact]
    public async Task A_request_the_signer_does_not_answer_is_given_up()
    {
        var endpoint = await EndpointAsync();
        var open = Service.OpenBackgroundAsync(WebFixtureBase.Technician(), endpoint.Id, RandomNumberGenerator.GetBytes(32), null);
        // Move the fake clock past the wait while the service polls.
        for (var i = 0; i < 100 && !open.IsCompleted; i++)
        {
            _fixture.Database.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }

        var result = await open.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("did not answer in time", result.Problem);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await db.RemoteSessionParticipants.AnyAsync(p => p.EndpointId == endpoint.Id && p.State == RemoteParticipantState.Failed));
    }

    [Fact]
    public async Task Remote_control_is_served_by_the_agent_on_the_chosen_windows_session()
    {
        var endpoint = await ControlEndpointAsync();
        var target = await Service.GetControlTargetAsync(WebFixtureBase.Technician(), endpoint.Id);
        Assert.Null(target!.Problem);
        Assert.Equal([0, 3], target.Sessions.Select(s => s.Id));
        Assert.Equal("ACME\\bert (session 3)", target.Sessions[1].Label);

        var signer = SignNextAsync(endpoint.Id);
        var result = await Service.OpenControlAsync(WebFixtureBase.Technician(), endpoint.Id, RandomNumberGenerator.GetBytes(32), null, 3);
        await signer;

        Assert.True(result.Success, result.Problem);
        Assert.Equal(["b2".PadRight(64, '0')], result.Value!.CertificateFingerprints);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var session = await db.RemoteSessions.AsNoTracking().SingleAsync(s => s.Id == result.Value.SessionId);
        Assert.Equal(RemoteSessionKind.RemoteControl, session.Kind);
        Assert.Equal(AgentComponent.Agent, session.Component);
        Assert.Equal(3, session.WindowsSessionId);
    }

    [Fact]
    public async Task Remote_control_is_refused_off_windows_on_an_old_or_offline_agent_and_for_an_unknown_windows_session()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var technician = WebFixtureBase.Technician();
        Assert.Contains("Windows and Linux endpoints",
            (await Service.OpenControlAsync(technician, (await ControlEndpointAsync(platform: "macos")).Id, key, null, 0)).Problem);
        Assert.Contains("on Linux needs Fleeto " + RemoteSessionRules.MinimumLinuxAgentVersion,
            (await Service.OpenControlAsync(technician, (await ControlEndpointAsync(platform: "linux")).Id, key, null, 0)).Problem);
        Assert.Contains("needs Fleeto 0.3.0", (await Service.OpenControlAsync(technician, (await ControlEndpointAsync("0.3.0-alpha.4")).Id, key, null, 0)).Problem);
        Assert.Contains("offline", (await Service.OpenControlAsync(technician, (await ControlEndpointAsync(online: false)).Id, key, null, 0)).Problem);

        var endpoint = await ControlEndpointAsync();
        Assert.Contains("no longer signed in", (await Service.OpenControlAsync(technician, endpoint.Id, key, null, 7)).Problem);
        // The console session of a signed-in user is no separate choice: the console stands for it.
        Assert.Contains("no longer signed in", (await Service.OpenControlAsync(technician, endpoint.Id, key, null, 1)).Problem);
        Assert.Equal(ServiceResult.ForbiddenProblem,
            (await Service.OpenControlAsync(WebFixtureBase.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly), endpoint.Id, key, null, 0)).Problem);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.RemoteSessions.AnyAsync(s => s.EndpointId == endpoint.Id));
    }

    [Fact]
    public async Task Remote_control_on_linux_shows_the_screen_only()
    {
        var endpoint = await ControlEndpointAsync(RemoteSessionRules.MinimumLinuxAgentVersion, platform: "linux");
        var target = await Service.GetControlTargetAsync(WebFixtureBase.Technician(), endpoint.Id);
        Assert.NotNull(target);
        Assert.Null(target.Problem);
        Assert.Equal("linux", target.OsPlatform);
        var only = Assert.Single(target.Sessions);
        Assert.Equal(RemoteSessionRules.ConsoleWindowsSession, only.Id);

        // A signed-in user's session is not a choice on Linux, even when the agent reported one.
        Assert.Contains("screen of the endpoint only",
            (await Service.OpenControlAsync(WebFixtureBase.Technician(), endpoint.Id, RandomNumberGenerator.GetBytes(32), null, 3)).Problem);
    }

    [Fact]
    public async Task Opening_remote_control_on_a_windows_session_with_a_running_session_joins_it()
    {
        var endpoint = await ControlEndpointAsync();
        var anna = WebFixtureBase.Technician() with { Name = "Anna Admin" };
        var signer = SignNextAsync(endpoint.Id);
        var first = await Service.OpenControlAsync(anna, endpoint.Id, RandomNumberGenerator.GetBytes(32), "Printer", 0);
        await signer;
        Assert.True(first.Success, first.Problem);

        var target = await Service.GetControlTargetAsync(WebFixtureBase.Technician(), endpoint.Id);
        var running = Assert.Single(target!.Running);
        Assert.Equal(first.Value!.SessionId, running.SessionId);
        Assert.Equal(0, running.WindowsSessionId);
        Assert.Equal(["Anna Admin"], running.Technicians);

        // The same Windows session: a second participant on the running session, with its own token.
        signer = SignNextAsync(endpoint.Id);
        var joined = await Service.OpenControlAsync(WebFixtureBase.Technician(), endpoint.Id, RandomNumberGenerator.GetBytes(32), null, 0);
        await signer;
        Assert.True(joined.Success, joined.Problem);
        Assert.Equal(first.Value.SessionId, joined.Value!.SessionId);
        Assert.NotEqual(first.Value.ParticipantId, joined.Value.ParticipantId);

        // Another Windows session gets its own session.
        signer = SignNextAsync(endpoint.Id);
        var other = await Service.OpenControlAsync(WebFixtureBase.Technician(), endpoint.Id, RandomNumberGenerator.GetBytes(32), null, 3);
        await signer;
        Assert.NotEqual(first.Value.SessionId, other.Value!.SessionId);

        // A session whose participants all ended is not joined.
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.RemoteSessionParticipants.Where(p => p.SessionId == first.Value.SessionId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.State, RemoteParticipantState.Ended));
            Assert.Equal(2, await db.RemoteSessionParticipants.CountAsync(p => p.SessionId == first.Value.SessionId));
        }

        Assert.DoesNotContain((await Service.GetControlTargetAsync(WebFixtureBase.Technician(), endpoint.Id))!.Running, r => r.SessionId == first.Value.SessionId);
        signer = SignNextAsync(endpoint.Id);
        var fresh = await Service.OpenControlAsync(WebFixtureBase.Technician(), endpoint.Id, RandomNumberGenerator.GetBytes(32), null, 0);
        await signer;
        Assert.NotEqual(first.Value.SessionId, fresh.Value!.SessionId);
    }

    [Theory]
    [InlineData(9, null, "consent timeout")]
    [InlineData(301, null, "consent timeout")]
    [InlineData(30, 512L * 1024, "file size cap")]
    [InlineData(30, 11L * 1024 * 1024 * 1024, "file size cap")]
    public void Policy_remote_control_settings_are_validated(int consentSeconds, long? fileBytes, string problem)
    {
        var input = new PolicyInput("Remote", null, 30, 3600, 10, AlertSeverity.Critical, RemoteConsentTimeoutSeconds: consentSeconds,
            RemoteMaxFileBytes: fileBytes);
        Assert.Contains(problem, PolicyService.Validate(input));
        Assert.Null(PolicyService.Validate(input with { RemoteConsentTimeoutSeconds = 30, RemoteMaxFileBytes = 64L * 1024 * 1024 }));
    }
}
