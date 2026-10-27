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
/// The Proxmox web interface in web (0.8.0, PROXMOX-WEB.md §3): only for a managed Proxmox VE host with a new enough agent and the permission;
/// opening it writes a claim and a fleeto-connect: link; Fleeto Connect claims it once, within 50 seconds, with its own key and a current
/// version, and only then are the session, the participant and the signing request written; ending it in Fleeto stops the link and marks the
/// connection revoked.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class ProxmoxWebServiceTests
{
    private const string Connect = "0.8.0-alpha.6";
    private readonly WebFixture _fixture;

    public ProxmoxWebServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private RemoteSessionService Service => _fixture.Services.GetRequiredService<RemoteSessionService>();

    private async Task<(Endpoint Endpoint, Caller Technician)> ProxmoxAsync(string osName = "Proxmox VE 8.4.1 (Debian GNU/Linux 12 (bookworm))",
        string agentVersion = RemoteSessionRules.MinimumProxmoxAgentVersion)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "PVE-01", EndpointClass.Server);
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.OsPlatform, "linux").SetProperty(e => e.OsName, osName)
                .SetProperty(e => e.AgentVersion, agentVersion).SetProperty(e => e.IsOnline, true));
            db.AgentCertificates.Add(new AgentCertificate
            {
                Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Fingerprint = Guid.NewGuid().ToString("N"),
                PublicKeyFingerprint = "c3".PadRight(64, '0'), SerialNumber = "3", Role = AgentComponent.Agent, IssuedAt = now, ExpiresAt = now.AddDays(90)
            });
            await db.SaveChangesAsync();
        }

        var user = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, displayName: "Tess Tech");
        return (endpoint, new Caller(user.Id, "Tess Tech", "tess@example.com", [FleetoRoles.Technician], SystemClientScope.Instance, "198.51.100.7"));
    }

    /// <summary>Plays fleeto-signer for the participant of a claim.</summary>
    private async Task SignAsync(Guid participantId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var db = _fixture.Database.DbFactory.CreateSystem();
            var participant = await db.RemoteSessionParticipants.FirstOrDefaultAsync(p => p.Id == participantId && p.State == RemoteParticipantState.Requested);
            if (participant is not null)
            {
                participant.State = RemoteParticipantState.Signed;
                participant.TokenPayload = [1, 2, 3];
                participant.TokenSignature = [4, 5, 6];
                participant.SigningKeyId = "key";
                await db.SaveChangesAsync();
                var request = await db.SigningRequests.FirstAsync(r => r.SubjectId == participant.Id);
                await _fixture.Database.Bus.PublishAsync(NotificationChannels.SigningResults, request.Id.ToString());
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("No signing request appeared for the claim.");
    }

    private static string ClaimOf(string link) => Uri.UnescapeDataString(link[(link.IndexOf("claim=", StringComparison.Ordinal) + 6)..]);

    [Fact]
    public async Task A_claim_of_fleeto_connect_becomes_the_session_with_its_key_once()
    {
        var (endpoint, technician) = await ProxmoxAsync();
        var start = await Service.StartProxmoxAsync(technician, endpoint.Id, "Storage of the cluster");
        Assert.True(start.Success, start.Problem);
        Assert.StartsWith($"fleeto-connect://open?instance=rmm.test.example&claim={start.Value!.ClaimId:D}.", start.Value.Link);
        Assert.Equal(ProxmoxProgressState.WaitingForConnect, (await Service.GetProxmoxProgressAsync(technician, start.Value.ClaimId))!.State);

        var claim = ClaimOf(start.Value.Link);
        var key = RandomNumberGenerator.GetBytes(32);
        var signer = SignAsync(start.Value.ClaimId);
        var ticket = await Service.ClaimProxmoxAsync(claim, key, Connect, "windows/amd64", "198.51.100.8");
        await signer;

        Assert.True(ticket.Success, ticket.Problem);
        Assert.Equal(start.Value.ClaimId, ticket.Value!.ParticipantId);
        Assert.Equal($"wss://rmm.test.example/relay/v1/sessions/{start.Value.ClaimId:D}", ticket.Value.RelayUrl);
        Assert.Equal(["c3".PadRight(64, '0')], ticket.Value.Fingerprints);
        Assert.Equal(("PVE-01", endpoint.Id), (ticket.Value.Endpoint, ticket.Value.EndpointId));
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var participant = await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == start.Value.ClaimId);
            Assert.Equal(key, participant.BrowserPublicKey);
            Assert.Equal(technician.UserId, participant.UserId);
            var session = await db.RemoteSessions.AsNoTracking().SingleAsync(s => s.Id == participant.SessionId);
            Assert.Equal((RemoteSessionKind.ProxmoxWeb, AgentComponent.Agent, (int?)null, "Storage of the cluster"),
                (session.Kind, session.Component, session.WindowsSessionId, session.Reason));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.RemoteSessionClaimed && a.TargetId == session.Id.ToString()));
            var stored = await db.RemoteSessionClaims.AsNoTracking().SingleAsync(c => c.Id == start.Value.ClaimId);
            Assert.Equal(("198.51.100.8", "Fleeto Connect 0.8.0-alpha.6 (windows/amd64)"), (stored.ClaimedFrom, stored.ClaimedBy));
        }

        Assert.Equal(ProxmoxProgressState.Connecting, (await Service.GetProxmoxProgressAsync(technician, start.Value.ClaimId))!.State);

        // A link opens one session only.
        var again = await Service.ClaimProxmoxAsync(claim, RandomNumberGenerator.GetBytes(32), Connect, "windows/amd64", "203.0.113.9");
        Assert.Equal(RemoteSessionService.ClaimProblem, again.Problem);
    }

    [Fact]
    public async Task A_wrong_secret_a_late_claim_and_an_old_fleeto_connect_are_refused()
    {
        var (endpoint, technician) = await ProxmoxAsync();
        var start = (await Service.StartProxmoxAsync(technician, endpoint.Id, null)).Value!;
        var claim = ClaimOf(start.Link);
        var key = RandomNumberGenerator.GetBytes(32);

        var wrong = claim[..^4] + (claim.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");
        Assert.Equal(RemoteSessionService.ClaimProblem, (await Service.ClaimProxmoxAsync(wrong, key, Connect, "linux/amd64", null)).Problem);
        Assert.Equal(RemoteSessionService.ClaimProblem, (await Service.ClaimProxmoxAsync("not-a-claim", key, Connect, "linux/amd64", null)).Problem);

        // Too old a Fleeto Connect: refused with the way out, and the link still works for the current one.
        var old = await Service.ClaimProxmoxAsync(claim, key, "0.8.0-alpha.5", "linux/amd64", null);
        Assert.Contains("too old", old.Problem);
        Assert.Contains("invalid session key", (await Service.ClaimProxmoxAsync(claim, new byte[32], Connect, "linux/amd64", null)).Problem);

        _fixture.Database.Time.Advance(RemoteSessionRules.ClaimValidity + TimeSpan.FromSeconds(1));
        Assert.Equal(RemoteSessionService.ClaimProblem, (await Service.ClaimProxmoxAsync(claim, key, Connect, "linux/amd64", null)).Problem);
        Assert.Equal(ProxmoxProgressState.NotClaimed, (await Service.GetProxmoxProgressAsync(technician, start.ClaimId))!.State);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.RemoteSessionParticipants.AnyAsync(p => p.Id == start.ClaimId));
    }

    [Fact]
    public async Task Only_a_managed_proxmox_host_with_a_current_agent_and_the_permission()
    {
        var (debian, technician) = await ProxmoxAsync(osName: "Debian GNU/Linux 12 (bookworm)");
        Assert.Contains("Proxmox VE host", (await Service.StartProxmoxAsync(technician, debian.Id, null)).Problem);

        var (old, oldTechnician) = await ProxmoxAsync(agentVersion: "0.8.0-alpha.5");
        Assert.Contains("needs Fleeto " + RemoteSessionRules.MinimumProxmoxAgentVersion, (await Service.StartProxmoxAsync(oldTechnician, old.Id, null)).Problem);

        var (proxmox, _) = await ProxmoxAsync();
        var readOnly = WebFixtureBase.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);
        Assert.Equal(ServiceResult.ForbiddenProblem, (await Service.StartProxmoxAsync(readOnly, proxmox.Id, null)).Problem);
        Assert.Null(await Service.GetProxmoxTargetAsync(readOnly, proxmox.Id));

        // A user whose groups do not give the client does not see the endpoint.
        var elsewhere = WebFixtureBase.CallerWith(new RestrictedClientScope([Guid.NewGuid()]), FleetoRoles.Technician);
        Assert.Null(await Service.GetProxmoxTargetAsync(elsewhere, proxmox.Id));
    }

    [Fact]
    public async Task Ending_in_fleeto_stops_the_link_and_revokes_the_connection()
    {
        var (endpoint, technician) = await ProxmoxAsync();
        var unclaimed = (await Service.StartProxmoxAsync(technician, endpoint.Id, null)).Value!;
        await Service.EndProxmoxAsync(technician, unclaimed.ClaimId);
        Assert.Equal(RemoteSessionService.ClaimProblem,
            (await Service.ClaimProxmoxAsync(ClaimOf(unclaimed.Link), RandomNumberGenerator.GetBytes(32), Connect, "linux/amd64", null)).Problem);

        var start = (await Service.StartProxmoxAsync(technician, endpoint.Id, null)).Value!;
        var signer = SignAsync(start.ClaimId);
        Assert.True((await Service.ClaimProxmoxAsync(ClaimOf(start.Link), RandomNumberGenerator.GetBytes(32), Connect, "linux/amd64", null)).Success);
        await signer;

        // Somebody else cannot end it.
        await Service.EndProxmoxAsync(WebFixtureBase.Technician(), start.ClaimId);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.Null((await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == start.ClaimId)).RevokedAt);
        }

        await Service.EndProxmoxAsync(technician, start.ClaimId);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.NotNull((await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == start.ClaimId)).RevokedAt);
        }
    }

    [Fact]
    public void The_link_names_the_host_of_the_instance()
    {
        var id = Guid.Parse("1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f");
        Assert.Equal($"fleeto-connect://open?instance=rmm.customer.example&claim={id:D}.secret",
            RemoteSessionService.ConnectLink("https://rmm.customer.example/", id, "secret"));
        Assert.Equal($"fleeto-connect://open?instance=localhost%3A7100&claim={id:D}.secret",
            RemoteSessionService.ConnectLink("https://localhost:7100", id, "secret"));
    }
}
