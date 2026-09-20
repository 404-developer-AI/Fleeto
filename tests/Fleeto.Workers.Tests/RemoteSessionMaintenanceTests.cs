using Fleeto.Core.Entities;
using Fleeto.Workers.Remote;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees remote session maintenance (0.3.0): a participant that never connected ends after 5 minutes and its session ends with it; a
/// connected participant and its session are left alone, whatever their age.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class RemoteSessionMaintenanceTests
{
    private readonly WorkersFixture _fixture;

    public RemoteSessionMaintenanceTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(RemoteSession Session, RemoteSessionParticipant Participant)> SessionAsync(Endpoint endpoint, RemoteParticipantState state, TimeSpan age)
    {
        var createdAt = _fixture.Now - age;
        var session = new RemoteSession
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = RemoteSessionKind.RemoteBackground,
            Component = AgentComponent.Watchdog, StartedByUserId = Guid.NewGuid(), StartedByName = "Tech", CreatedAt = createdAt
        };
        var participant = new RemoteSessionParticipant
        {
            Id = Guid.NewGuid(), SessionId = session.Id, ClientId = endpoint.ClientId, EndpointId = endpoint.Id, UserId = session.StartedByUserId, UserName = "Tech",
            BrowserPublicKey = Enumerable.Repeat((byte)7, 32).ToArray(), State = state, CreatedAt = createdAt,
            TokenPayload = state == RemoteParticipantState.Requested ? null : [1], TokenSignature = state == RemoteParticipantState.Requested ? null : [2],
            ConnectingAt = state is RemoteParticipantState.Connected or RemoteParticipantState.Connecting ? createdAt : null, ConnectedAt = state == RemoteParticipantState.Connected ? createdAt : null
        };
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.RemoteSessions.Add(session);
        db.RemoteSessionParticipants.Add(participant);
        await db.SaveChangesAsync();
        return (session, participant);
    }

    [Fact]
    public async Task Sessions_that_never_connected_end_and_connected_ones_stay()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-REMOTE");
        var stale = await SessionAsync(endpoint, RemoteParticipantState.Signed, TimeSpan.FromMinutes(6));
        var fresh = await SessionAsync(endpoint, RemoteParticipantState.Requested, TimeSpan.FromMinutes(1));
        var connected = await SessionAsync(endpoint, RemoteParticipantState.Connected, TimeSpan.FromHours(3));
        // Claimed by a gateway that never paired it (it lost its database or stopped): it ends too (security review of 0.3.0 step 7).
        var stuck = await SessionAsync(endpoint, RemoteParticipantState.Connecting, TimeSpan.FromMinutes(6));

        var service = new RemoteSessionMaintenanceService(_fixture.Db.DbFactory, _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<RemoteSessionMaintenanceService>.Instance);
        Assert.True(await service.RunAsync(CancellationToken.None) >= 2);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var staleParticipant = await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == stale.Participant.Id);
        Assert.Equal(RemoteParticipantState.Ended, staleParticipant.State);
        Assert.Equal(RemoteSessionMaintenanceService.NotConnectedReason, staleParticipant.EndReason);
        Assert.NotNull((await db.RemoteSessions.AsNoTracking().SingleAsync(s => s.Id == stale.Session.Id)).EndedAt);

        Assert.Equal(RemoteParticipantState.Requested, (await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == fresh.Participant.Id)).State);
        Assert.Null((await db.RemoteSessions.AsNoTracking().SingleAsync(s => s.Id == fresh.Session.Id)).EndedAt);
        Assert.Equal(RemoteParticipantState.Connected, (await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == connected.Participant.Id)).State);
        Assert.Null((await db.RemoteSessions.AsNoTracking().SingleAsync(s => s.Id == connected.Session.Id)).EndedAt);
        Assert.Equal(RemoteParticipantState.Ended, (await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == stuck.Participant.Id)).State);
        Assert.NotNull((await db.RemoteSessions.AsNoTracking().SingleAsync(s => s.Id == stuck.Session.Id)).EndedAt);
    }
}
