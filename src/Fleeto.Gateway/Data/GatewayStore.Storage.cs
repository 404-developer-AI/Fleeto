using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Protocol.Agent.V1;
using Npgsql;
using NpgsqlTypes;

namespace Fleeto.Gateway.Data;

/// <summary>A storage scan request that may be delivered to its agent now (0.6.0).</summary>
public sealed record DeliverableStorageScanRequest(Guid Id, Guid EndpointId);

public enum StorageScanOutcome
{
    Stored,
    /// <summary>The scan id was stored before; nothing was written.</summary>
    Duplicate,
    /// <summary>The endpoint sent more scans in 24 hours than <see cref="StorageRules.MaxScansPerEndpointPerDay"/>; nothing was written.</summary>
    OverLimit
}

/// <summary>Storage analysis (0.6.0): StorageScans (select, insert) and StorageScanRequests (select, update).</summary>
public sealed partial class GatewayStore
{
    /// <summary>
    /// Stores one volume scan exactly once per scan id. Paths, volume and texts are cleaned and cut to their limits, and only the
    /// first <see cref="StorageRules.MaxFolders"/> folders and <see cref="StorageRules.MaxFiles"/> files are kept: the agent is
    /// authenticated, not trusted with the size of the database.
    /// </summary>
    public async Task<StorageScanOutcome> SaveStorageScanAsync(Guid endpointId, Guid clientId, Guid scanId, StorageScanReport report, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var existing = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE "AgentScanId" = $2), count(*) FILTER (WHERE "ReceivedAt" > $3)
            FROM "StorageScans" WHERE "EndpointId" = $1 AND ("AgentScanId" = $2 OR "ReceivedAt" > $3)
            """, connection, transaction))
        {
            existing.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
            existing.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = scanId });
            existing.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now.AddDays(-1) });
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt64(0) > 0)
            {
                return StorageScanOutcome.Duplicate;
            }

            if (reader.GetInt64(1) >= StorageRules.MaxScansPerEndpointPerDay)
            {
                return StorageScanOutcome.OverLimit;
            }
        }

        var folders = report.Folders.Take(StorageRules.MaxFolders)
            .Where(f => !string.IsNullOrEmpty(f.Path))
            .Select(f => new StorageFolderEntry(DbText.Clean(f.Path, StorageRules.MaxPathLength), DbText.ClampToLong(f.SizeBytes),
                DbText.ClampToLong(f.FileCount), DbText.ClampToLong(f.FolderCount)));
        var files = report.Files.Take(StorageRules.MaxFiles)
            .Where(f => !string.IsNullOrEmpty(f.Path))
            .Select(f => new StorageFileEntry(DbText.Clean(f.Path, StorageRules.MaxPathLength), DbText.ClampToLong(f.SizeBytes),
                f.ModifiedAt is null ? null : SafeToDateTime(f.ModifiedAt, now)));
        var method = report.Method == Protocol.Agent.V1.StorageScanMethod.Mft ? Core.Entities.StorageScanMethod.Mft : Core.Entities.StorageScanMethod.Walk;

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO "StorageScans" ("Id", "ClientId", "EndpointId", "AgentScanId", "Volume", "Filesystem", "TotalBytes", "FreeBytes",
              "ReceivedAt", "AgentStartedAt", "DurationMs", "Method", "Complete", "Error", "FileCount", "FolderCount", "RequestId",
              "FoldersJson", "FilesJson")
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)
            ON CONFLICT ("EndpointId", "AgentScanId") DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7() });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = clientId });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = scanId });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(report.Volume, StorageRules.MaxVolumeLength) });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(report.Filesystem, 50) });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = DbText.ClampToLong(report.TotalBytes) });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = DbText.ClampToLong(report.FreeBytes) });
            insert.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
            insert.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.TimestampTz,
                Value = report.StartedAt is null ? DBNull.Value : SafeToDateTime(report.StartedAt, now)
            });
            insert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = report.DurationMs > int.MaxValue ? int.MaxValue : (int)report.DurationMs });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = method.ToString() });
            insert.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = report.Complete });
            insert.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Varchar,
                Value = string.IsNullOrEmpty(report.Error) ? DBNull.Value : DbText.Clean(report.Error, 1000)
            });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = DbText.ClampToLong(report.FileCount) });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = DbText.ClampToLong(report.FolderCount) });
            insert.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Uuid,
                Value = Guid.TryParse(report.RequestId, out var requestId) ? requestId : DBNull.Value
            });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = StorageRules.SerializeFolders(folders) });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = StorageRules.SerializeFiles(files) });
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return StorageScanOutcome.Duplicate;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return StorageScanOutcome.Stored;
    }

    /// <summary>Pending storage scan requests of these endpoints, oldest first; at most one per endpoint is needed.</summary>
    public async Task<List<DeliverableStorageScanRequest>> ReadDeliverableStorageScanRequestsAsync(Guid[] endpointIds, DateTime now,
        CancellationToken cancellationToken)
    {
        var requests = new List<DeliverableStorageScanRequest>();
        if (endpointIds.Length == 0)
        {
            return requests;
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT "Id", "EndpointId" FROM "StorageScanRequests"
            WHERE "EndpointId" = ANY($1) AND "DeliveredAt" IS NULL AND "ExpiresAt" > $2
            ORDER BY "RequestedAt"
            LIMIT 1000
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = endpointIds });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new DeliverableStorageScanRequest(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return requests;
    }

    /// <summary>The endpoint of a storage scan request, or null when it does not exist.</summary>
    public async Task<Guid?> ReadStorageScanRequestEndpointAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""SELECT "EndpointId" FROM "StorageScanRequests" WHERE "Id" = $1""");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = requestId });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid endpointId ? endpointId : null;
    }

    /// <summary>
    /// Marks every pending request of the endpoint delivered, since one scan answers them all. Returns the oldest, or null when
    /// none was pending, so a request is sent once.
    /// </summary>
    public async Task<Guid?> MarkStorageScanRequestsDeliveredAsync(Guid endpointId, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            WITH delivered AS (
              UPDATE "StorageScanRequests" SET "DeliveredAt" = $2
              WHERE "EndpointId" = $1 AND "DeliveredAt" IS NULL AND "ExpiresAt" > $2
              RETURNING "Id", "RequestedAt")
            SELECT "Id" FROM delivered ORDER BY "RequestedAt" LIMIT 1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : null;
    }
}
