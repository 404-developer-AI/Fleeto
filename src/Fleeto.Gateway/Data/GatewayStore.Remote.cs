using System.Text.Json;
using Fleeto.Core.Entities;
using Npgsql;
using NpgsqlTypes;

namespace Fleeto.Gateway.Data;

/// <summary>A participant the gateway accepted a token for (single use): what the relay needs to pair it with its endpoint.</summary>
public sealed record ClaimedParticipant(Guid ParticipantId, Guid SessionId, Guid ClientId, Guid EndpointId, RemoteSessionKind Kind,
    AgentComponent Component, Guid UserId, string UserName, EndpointTier Tier);

/// <summary>The instance facts the relay checks browser tokens against.</summary>
public sealed record RelayTrust(Guid InstanceId, string WebBaseUrl, IReadOnlyDictionary<string, byte[]> SigningKeys);

/// <summary>Remote sessions (0.3.0): single-use claims of tokens, who joined and left, and the audit entries of both.</summary>
public sealed partial class GatewayStore
{
    /// <summary>The instance id, the public URL of web and the public signing keys that are not retired.</summary>
    public async Task<RelayTrust> ReadRelayTrustAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        Guid instanceId;
        string webBaseUrl;
        await using (var command = new NpgsqlCommand("""SELECT "InstanceId", "WebBaseUrl" FROM "InstanceSettings" WHERE "Id" = 1""", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The instance is not initialised: InstanceSettings has no row.");
            }

            instanceId = reader.GetGuid(0);
            webBaseUrl = reader.GetString(1);
        }

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("""SELECT "Id", "PublicKey" FROM "InstanceSigningKeys" WHERE "RetiredAt" IS NULL""", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                keys[reader.GetString(0)] = reader.GetFieldValue<byte[]>(1);
            }
        }

        return new RelayTrust(instanceId, webBaseUrl, keys);
    }

    /// <summary>
    /// Claims a signed participant for the relay, once: only while it is Signed, still valid, and its stored token is byte for byte the
    /// token the browser presented. Returns null otherwise (used before, expired, refused, unknown).
    /// </summary>
    public async Task<ClaimedParticipant?> ClaimParticipantAsync(Guid participantId, byte[] tokenPayload, string? ipAddress, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE "RemoteSessionParticipants" p
            SET "State" = 'Connecting', "ConnectingAt" = $3, "IpAddress" = $4
            FROM "RemoteSessions" s, "Endpoints" e
            WHERE p."Id" = $1 AND p."State" = 'Signed' AND p."ValidUntil" > $3 AND p."TokenPayload" = $2
              AND s."Id" = p."SessionId" AND s."ClientId" = p."ClientId" AND s."EndedAt" IS NULL
              AND e."Id" = p."EndpointId" AND e."ClientId" = p."ClientId"
            RETURNING p."SessionId", p."ClientId", p."EndpointId", s."Kind", s."Component", p."UserId", p."UserName", e."Tier"
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = participantId });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = tokenPayload });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = ipAddress is null ? DBNull.Value : DbText.Clean(ipAddress, 64) });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClaimedParticipant(participantId, reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
            Enum.Parse<RemoteSessionKind>(reader.GetString(3)), Enum.Parse<AgentComponent>(reader.GetString(4)), reader.GetGuid(5), reader.GetString(6),
            Enum.Parse<EndpointTier>(reader.GetString(7)));
    }

    /// <summary>Browser and endpoint are paired: the participant is connected, the session started, and the join is audited.</summary>
    public async Task MarkParticipantConnectedAsync(ClaimedParticipant participant, string? ipAddress, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE "RemoteSessionParticipants" SET "State" = 'Connected', "ConnectedAt" = $2 WHERE "Id" = $1 AND "State" = 'Connecting';
            """, connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = participant.ParticipantId });
            update.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE "RemoteSessions" SET "StartedAt" = COALESCE("StartedAt", $2) WHERE "Id" = $1
            """, connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = participant.SessionId });
            update.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertRemoteAuditAsync(connection, transaction, "remote_session.joined", participant, now, ipAddress,
            new { participant.EndpointId, ParticipantId = participant.ParticipantId, Kind = participant.Kind.ToString(), Technician = participant.UserName },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Ends a participant that was claimed: Ended after it was connected, Refused when the endpoint or gateway refused it before. When no
    /// participant of the session is active any more, the session ends too. Audited as left or refused.
    /// </summary>
    public async Task EndParticipantAsync(ClaimedParticipant participant, bool connected, string reason, string? ipAddress, DateTime now,
        CancellationToken cancellationToken)
    {
        var text = DbText.Clean(reason, 500) ?? string.Empty;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE "RemoteSessionParticipants" SET "State" = $2, "EndedAt" = $3, "EndReason" = $4
            WHERE "Id" = $1 AND "State" IN ('Connecting', 'Connected')
            """, connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = participant.ParticipantId });
            update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = connected ? nameof(RemoteParticipantState.Ended) : nameof(RemoteParticipantState.Refused) });
            update.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
            update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = text });
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                // Ended already (a gateway restart reset it); nothing to add.
                return;
            }
        }

        await EndSessionIfIdleAsync(connection, transaction, participant.SessionId, text, now, cancellationToken);
        await InsertRemoteAuditAsync(connection, transaction, connected ? "remote_session.left" : "remote_session.refused", participant, now, ipAddress,
            new { participant.EndpointId, ParticipantId = participant.ParticipantId, Kind = participant.Kind.ToString(), Technician = participant.UserName, Reason = text },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>At start no relay can be live: every claimed participant ends, and so does every session without an active participant.</summary>
    public async Task<int> ResetRemoteParticipantsAsync(DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sessions = new List<Guid>();
        await using (var update = new NpgsqlCommand("""
            UPDATE "RemoteSessionParticipants" SET "State" = 'Ended', "EndedAt" = $1, "EndReason" = 'The gateway restarted.'
            WHERE "State" IN ('Connecting', 'Connected')
            RETURNING "SessionId"
            """, connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sessions.Add(reader.GetGuid(0));
            }
        }

        foreach (var sessionId in sessions.Distinct())
        {
            await EndSessionIfIdleAsync(connection, transaction, sessionId, "The gateway restarted.", now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return sessions.Count;
    }

    /// <summary>
    /// Records an action a technician took in a remote background session (0.3.0 step 2), reported by the endpoint over its control
    /// session. The participant must belong to the reporting endpoint, so an endpoint cannot write actions for another's session. Terminal
    /// content is never stored. Returns false when the participant is unknown for this endpoint.
    /// </summary>
    public async Task<bool> RecordRemoteActionAsync(Guid endpointId, Guid participantId, string action, string target, string? detail, DateTime time,
        DateTime now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO "RemoteSessionActions" ("SessionId", "ParticipantId", "ClientId", "EndpointId", "Time", "Action", "Target", "Detail")
            SELECT p."SessionId", p."Id", p."ClientId", p."EndpointId", $3, $4, $5, $6
            FROM "RemoteSessionParticipants" p
            WHERE p."Id" = $1 AND p."EndpointId" = $2
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = participantId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = time <= DateTime.MinValue || time > now.AddMinutes(5) ? now : time });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(action, 50) });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(target, 1000) });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = string.IsNullOrEmpty(detail) ? DBNull.Value : DbText.Clean(detail, 1000) });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task EndSessionIfIdleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId, string reason, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand("""
            UPDATE "RemoteSessions" SET "EndedAt" = $2, "EndReason" = $3
            WHERE "Id" = $1 AND "EndedAt" IS NULL
              AND NOT EXISTS (SELECT 1 FROM "RemoteSessionParticipants" p
                              WHERE p."SessionId" = $1 AND p."State" IN ('Requested', 'Signed', 'Connecting', 'Connected'))
            """, connection, transaction);
        update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
        update.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = reason });
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRemoteAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string action, ClaimedParticipant participant,
        DateTime now, string? ipAddress, object details, CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO "AuditEntries" ("Time", "ClientId", "ActorType", "ActorId", "ActorName", "Action", "TargetType", "TargetId", "DetailsJson", "IpAddress")
            VALUES ($1, $2, 'User', $7, $8, $3, 'RemoteSession', $4, $5, $6)
            """, connection, transaction);
        audit.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        audit.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = participant.ClientId });
        audit.Parameters.Add(new NpgsqlParameter<string> { TypedValue = action });
        audit.Parameters.Add(new NpgsqlParameter<string> { TypedValue = participant.SessionId.ToString() });
        audit.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(details) });
        audit.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = ipAddress is null ? DBNull.Value : DbText.Clean(ipAddress, 64) });
        // The technician is the actor of a join or a leave; the gateway only witnessed it.
        audit.Parameters.Add(new NpgsqlParameter<string> { TypedValue = participant.UserId.ToString() });
        audit.Parameters.Add(new NpgsqlParameter<string> { TypedValue = participant.UserName });
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }
}
