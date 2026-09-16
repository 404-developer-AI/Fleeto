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
}
