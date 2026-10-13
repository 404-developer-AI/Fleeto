using Npgsql;

namespace Fleeto.Gateway.Data;

/// <summary>Refresh on an endpoint (0.6.0): Endpoints.InventoryRequestedAt (select).</summary>
public sealed partial class GatewayStore
{
    /// <summary>
    /// Of these endpoints, those a user asked a fresh inventory of since <paramref name="since"/> that did not arrive after the
    /// request yet.
    /// </summary>
    public async Task<List<Guid>> ReadInventoryRequestsAsync(Guid[] endpointIds, DateTime since, CancellationToken cancellationToken)
    {
        var endpoints = new List<Guid>();
        if (endpointIds.Length == 0)
        {
            return endpoints;
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT e."Id" FROM "Endpoints" e
            LEFT JOIN "InventorySnapshots" i ON i."EndpointId" = e."Id"
            WHERE e."Id" = ANY($1) AND e."InventoryRequestedAt" >= $2
              AND (i."ReceivedAt" IS NULL OR i."ReceivedAt" < e."InventoryRequestedAt")
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = endpointIds });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = since });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            endpoints.Add(reader.GetGuid(0));
        }

        return endpoints;
    }
}
