using Fleeto.Core.Entities;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Web.Tests;

/// <summary>
/// A live remote session connection of a user, and the question the gateway asks about it: the relay ends a connection only
/// once web marked its participant revoked (GatewayStore.ReadRevokedParticipantsAsync, whose statement is run here).
/// </summary>
internal static class ConnectedParticipants
{
    public static async Task<Guid> AddAsync(TestDatabase database, Endpoint endpoint, Guid userId)
    {
        await using var db = database.DbFactory.CreateSystem();
        var now = DateTime.UtcNow;
        var session = new RemoteSession
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = RemoteSessionKind.RemoteBackground,
            Component = AgentComponent.Watchdog, StartedByUserId = userId, StartedByName = "Tess", CreatedAt = now, StartedAt = now
        };
        var participant = new RemoteSessionParticipant
        {
            Id = Guid.NewGuid(), SessionId = session.Id, ClientId = endpoint.ClientId, EndpointId = endpoint.Id, UserId = userId,
            UserName = "Tess", BrowserPublicKey = new byte[32], State = RemoteParticipantState.Connected, CreatedAt = now
        };
        db.RemoteSessions.Add(session);
        db.RemoteSessionParticipants.Add(participant);
        await db.SaveChangesAsync();
        return participant.Id;
    }

    /// <summary>A managed endpoint of a new client with a connection of this user.</summary>
    public static async Task<Guid> AddOnNewEndpointAsync(TestDatabase database, Guid userId, string hostname)
    {
        var client = await database.CreateClientAsync();
        var site = await database.CreateSiteAsync(client.Id);
        var endpoint = await database.CreateEndpointAsync(site, EndpointTier.Managed, hostname);
        return await AddAsync(database, endpoint, userId);
    }

    public static async Task<bool> GatewayClosesAsync(TestDatabase database, Guid participantId)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT "Id" FROM "RemoteSessionParticipants" WHERE "Id" = ANY($1) AND "RevokedAt" IS NOT NULL
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [participantId] });
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync();
    }

    public static async Task<RemoteSessionParticipant> GetAsync(TestDatabase database, Guid participantId)
    {
        await using var db = database.DbFactory.CreateSystem();
        return await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == participantId);
    }

    public static int AccessNotifications(TestDatabase database, Guid userId) =>
        database.Bus.PayloadsFor(Core.Interfaces.NotificationChannels.UserAccess).Count(p => p == userId.ToString());
}
