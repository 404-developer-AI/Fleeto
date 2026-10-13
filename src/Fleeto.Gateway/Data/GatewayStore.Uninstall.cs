using System.Text.Json;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Npgsql;
using NpgsqlTypes;

namespace Fleeto.Gateway.Data;

/// <summary>Uninstalling the agent of a deleted endpoint (0.6.0): the gateway records that the agent or the watchdog accepted it.</summary>
public sealed partial class GatewayStore
{
    /// <summary>
    /// Records that the agent or the watchdog of a deleted endpoint accepted its uninstall command, with an audit entry. Returns
    /// false when it was accepted already (the other service of the endpoint got there first) or the row is gone.
    /// </summary>
    public async Task<bool> RecordUninstallAcceptedAsync(Guid uninstallId, AgentComponent component, DateTime now, string? ipAddress,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = new NpgsqlCommand("""
            UPDATE "AgentUninstalls" SET "AcceptedAt" = $2, "AcceptedBy" = $3
            WHERE "Id" = $1 AND "AcceptedAt" IS NULL
            RETURNING "ClientId", "EndpointId", "Hostname", "RequestedByName"
            """, connection, transaction);
        update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = uninstallId });
        update.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = component.ToString() });
        Guid clientId, endpointId;
        string hostname, requestedBy;
        await using (var reader = await update.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            (clientId, endpointId, hostname, requestedBy) = (reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3));
        }

        await using var audit = new NpgsqlCommand("""
            INSERT INTO "AuditEntries" ("Time", "ClientId", "ActorType", "ActorId", "ActorName", "Action", "TargetType", "TargetId", "DetailsJson", "IpAddress")
            VALUES ($1, $2, 'System', 'fleeto-gateway', 'fleeto-gateway', $3, 'Endpoint', $4, $5, $6)
            """, connection, transaction);
        audit.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        audit.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = clientId });
        audit.Parameters.Add(new NpgsqlParameter<string> { TypedValue = AuditActions.AgentUninstallAccepted });
        audit.Parameters.Add(new NpgsqlParameter<string> { TypedValue = endpointId.ToString() });
        audit.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = JsonSerializer.Serialize(new { Hostname = hostname, Component = component.ToString(), RequestedBy = requestedBy })
        });
        audit.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = ipAddress is null ? DBNull.Value : DbText.Clean(ipAddress, 64) });
        await audit.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
