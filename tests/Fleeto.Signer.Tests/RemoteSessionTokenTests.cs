using System.Security.Cryptography;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Handlers;
using Microsoft.EntityFrameworkCore;
using RemoteSessionKind = Fleeto.Core.Entities.RemoteSessionKind;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees the signer's remote session rules (0.3.0): a token is signed for exactly one participant, endpoint and instance, carries the
/// browser key and the policy's idle timeout, and is valid for 60 seconds; a read-only, locked-out, unknown or two-factor-less user, an
/// agent-only endpoint, an expired license, an old request, a request for another client and an invalid browser key are refused and recorded
/// on the participant; a participant that web gave up on is never signed.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class RemoteSessionTokenTests
{
    private readonly SignerFixture _fixture;

    public RemoteSessionTokenTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Endpoint Endpoint, Guid TechnicianId)> ScopeAsync(EndpointTier tier = EndpointTier.Managed, int idleMinutes = 45)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, tier, "SRV-REMOTE", EndpointClass.Server);
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, displayName: "Tess Tech");
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var policy = new Policy
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = "Remote " + Guid.NewGuid().ToString("N")[..6], RemoteIdleTimeoutMinutes = idleMinutes,
            CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
        };
        db.Policies.Add(policy);
        db.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = client.Id, PolicyId = policy.Id, CreatedAt = _fixture.Now });
        await db.SaveChangesAsync();
        return (endpoint, technician.Id);
    }

    private async Task<RemoteSessionParticipant> CreateParticipantAsync(Endpoint endpoint, Guid userId, byte[]? browserKey = null, DateTime? createdAt = null,
        RemoteParticipantState state = RemoteParticipantState.Requested)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var session = new RemoteSession
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = RemoteSessionKind.RemoteBackground,
            Component = AgentComponent.Watchdog, StartedByUserId = userId, StartedByName = "Tess Tech", CreatedAt = createdAt ?? _fixture.Now
        };
        var participant = new RemoteSessionParticipant
        {
            Id = Guid.NewGuid(), SessionId = session.Id, ClientId = endpoint.ClientId, EndpointId = endpoint.Id, UserId = userId, UserName = "Tess Tech",
            BrowserPublicKey = browserKey ?? RandomNumberGenerator.GetBytes(32), State = state, CreatedAt = createdAt ?? _fixture.Now
        };
        db.RemoteSessions.Add(session);
        db.RemoteSessionParticipants.Add(participant);
        await db.SaveChangesAsync();
        return participant;
    }

    private async Task<(SigningRequest Request, RemoteSessionParticipant Participant)> SignAsync(RemoteSessionParticipant participant, Guid? clientId = null)
    {
        var request = await _fixture.ProcessAsync(SigningRequestKind.RemoteSessionToken, clientId ?? participant.ClientId, participant.Id, [],
            "web:198.51.100.40");
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return (request, await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == participant.Id));
    }

    [Fact]
    public async Task A_token_is_signed_for_one_participant_endpoint_and_browser_key()
    {
        var (endpoint, technicianId) = await ScopeAsync(idleMinutes: 45);
        var participant = await CreateParticipantAsync(endpoint, technicianId);

        var (request, signed) = await SignAsync(participant);

        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.Equal(RemoteParticipantState.Signed, signed.State);
        Assert.True(Ed25519.Verify(_fixture.KeyRing.SigningPublicKey, SignatureContexts.RemoteSession, signed.TokenPayload, signed.TokenSignature!));
        Assert.Equal(_fixture.KeyRing.SigningKeyId, signed.SigningKeyId);
        var token = RemoteSessionToken.Parser.ParseFrom(signed.TokenPayload);
        Assert.Equal(participant.Id.ToString("D"), token.ParticipantId);
        Assert.Equal(participant.SessionId.ToString("D"), token.SessionId);
        Assert.Equal(endpoint.Id.ToString("D"), token.EndpointId);
        Assert.Equal(_fixture.KeyRing.InstanceId.ToString("D"), token.InstanceId);
        Assert.Equal(Component.Watchdog, token.Component);
        Assert.Equal(Protocol.Agent.V1.RemoteSessionKind.RemoteBackground, token.Kind);
        Assert.Equal(participant.BrowserPublicKey, token.BrowserPublicKey.ToByteArray());
        Assert.Equal(45u * 60, token.IdleTimeoutSeconds);
        Assert.Equal((ulong)RemoteSessionRules.DefaultMaxFileBytes, token.MaxFileBytes);
        Assert.Equal(_fixture.Now + RemoteSessionRules.TokenValidity, token.ValidUntil.ToDateTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(signed.ValidUntil!.Value, token.ValidUntil.ToDateTime(), TimeSpan.FromSeconds(1));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.RemoteSessionSigned && a.TargetId == participant.SessionId.ToString()));
    }

    [Fact]
    public async Task Refused_for_an_agent_only_endpoint_and_after_the_license_grace_period()
    {
        var (agentOnly, technicianId) = await ScopeAsync(EndpointTier.AgentOnly);
        var (request, refused) = await SignAsync(await CreateParticipantAsync(agentOnly, technicianId));
        Assert.Equal(SigningRequestState.Refused, request.State);
        Assert.Equal(RemoteParticipantState.Refused, refused.State);
        Assert.Equal(RemoteSessionTokenHandler.NotManagedReason, refused.EndReason);
        Assert.Null(refused.TokenSignature);

        var (managed, managedTechnician) = await ScopeAsync();
        await _fixture.Database.LoadTestLicenseAsync(1000, _fixture.Now.AddDays(-30));
        try
        {
            var (_, expired) = await SignAsync(await CreateParticipantAsync(managed, managedTechnician));
            Assert.Equal(RemoteSessionTokenHandler.NotManagedReason, expired.EndReason);
        }
        finally
        {
            await _fixture.Database.LoadTestLicenseAsync(1000);
        }
    }

    [Fact]
    public async Task Refused_for_a_read_only_locked_out_unknown_or_two_factor_less_user()
    {
        var (endpoint, _) = await ScopeAsync();
        var readOnly = await _fixture.Database.CreateUserAsync(FleetoRoles.ReadOnly);
        var locked = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin, lockoutEnd: _fixture.Database.Time.GetUtcNow().AddDays(1));
        var withoutTwoFactor = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, twoFactor: false);

        foreach (var userId in new[] { readOnly.Id, locked.Id, withoutTwoFactor.Id, Guid.NewGuid() })
        {
            var (_, refused) = await SignAsync(await CreateParticipantAsync(endpoint, userId));
            Assert.Equal(RemoteParticipantState.Refused, refused.State);
            Assert.Equal(RemoteSessionTokenHandler.UserReason, refused.EndReason);
        }
    }

    [Fact]
    public async Task Refused_for_an_old_request_an_invalid_browser_key_or_another_client()
    {
        var (endpoint, technicianId) = await ScopeAsync();

        var (_, old) = await SignAsync(await CreateParticipantAsync(endpoint, technicianId, createdAt: _fixture.Now.AddMinutes(-2)));
        Assert.Equal(RemoteSessionTokenHandler.TooOldReason, old.EndReason);

        var (_, zeroKey) = await SignAsync(await CreateParticipantAsync(endpoint, technicianId, browserKey: new byte[32]));
        Assert.Equal(RemoteSessionTokenHandler.BrowserKeyReason, zeroKey.EndReason);

        // A request that names another client never touches the participant.
        var other = await _fixture.Database.CreateClientAsync();
        var participant = await CreateParticipantAsync(endpoint, technicianId);
        var (request, untouched) = await SignAsync(participant, other.Id);
        Assert.Equal(SigningRequestState.Refused, request.State);
        Assert.Equal(RemoteSessionTokenHandler.MissingReason, request.RefusalReason);
        Assert.Equal(RemoteParticipantState.Requested, untouched.State);
        Assert.Null(untouched.TokenSignature);
    }

    [Fact]
    public async Task A_participant_web_gave_up_on_is_not_signed()
    {
        var (endpoint, technicianId) = await ScopeAsync();
        var (request, failed) = await SignAsync(await CreateParticipantAsync(endpoint, technicianId, state: RemoteParticipantState.Failed));

        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.Equal(RemoteParticipantState.Failed, failed.State);
        Assert.Null(failed.TokenSignature);
    }
}
