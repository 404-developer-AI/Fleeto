using System.Buffers;
using System.Text.Json;
using Fleetify.Core.Entities;
using Fleetify.Protocol.Agent.V1;
using Npgsql;
using NpgsqlTypes;

namespace Fleetify.Gateway.Data;

/// <summary>What the gateway needs to know about an endpoint when its session opens.</summary>
public sealed record SessionStart(Guid ClientId, EndpointTier Tier, StoredConfig? NewerConfig, string? InventoryHash);

/// <summary>An endpoint's latest signed configuration together with the endpoint's stored tier.</summary>
public sealed record StoredConfig(Guid EndpointId, long Version, byte[] Payload, byte[] Signature, string KeyId, EndpointTier Tier);

public enum IngestOutcome
{
    Stored,
    /// <summary>The sequence was stored before; nothing was written.</summary>
    Duplicate
}

/// <summary>
/// Hot-path SQL of the gateway, written directly against Npgsql. Touches only what the <c>fleetify_gateway</c> role is
/// granted (DatabaseGrants): Endpoints (select, update), InventorySnapshots, CheckResults (insert), IngestBatches,
/// EndpointEvents (insert), EndpointConfigs and AgentCertificates (select). Every statement is a constant.
/// </summary>
public sealed class GatewayStore
{
    private const int MaxJsonString = 1000;

    private readonly NpgsqlDataSource _dataSource;

    public GatewayStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>One gateway per instance: at start no endpoint can have a live connection.</summary>
    public async Task<int> MarkAllOfflineAsync(DateTime now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE "Endpoints" SET "IsOnline" = false, "UpdatedAt" = $1 WHERE "IsOnline"
            """);
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Marks the endpoint online with the facts from Hello, records a Connected event and reads what the session needs.
    /// Returns null when the endpoint no longer exists.
    /// </summary>
    public async Task<SessionStart?> OpenSessionAsync(Guid endpointId, Hello hello, ulong agentConfigVersion, string remoteAddress,
        DateTime now, CancellationToken cancellationToken)
    {
        var os = hello.Os ?? new OsInfo();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        Guid clientId;
        EndpointTier tier;
        await using (var update = new NpgsqlCommand("""
            UPDATE "Endpoints" SET
              "IsOnline" = true, "LastSeenAt" = @now, "AgentVersion" = @agentVersion, "Hostname" = @hostname,
              "OsPlatform" = @osPlatform, "OsName" = @osName, "OsVersion" = @osVersion, "Architecture" = @architecture,
              "DetectedClass" = @detectedClass, "UpdatedAt" = @now
            WHERE "Id" = @id
            RETURNING "ClientId", "Tier"
            """, connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter<Guid>("id", endpointId));
            update.Parameters.Add(new NpgsqlParameter<DateTime>("now", now));
            update.Parameters.Add(new NpgsqlParameter<string>("agentVersion", DbText.Clean(hello.AgentVersion, 50)));
            AddEndpointFacts(update, hello.Hostname, os);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            clientId = reader.GetGuid(0);
            tier = Enum.Parse<EndpointTier>(reader.GetString(1));
        }

        await using var batch = new NpgsqlBatch(connection, transaction)
        {
            BatchCommands =
            {
                new NpgsqlBatchCommand("""
                    INSERT INTO "EndpointEvents" ("ClientId", "EndpointId", "Kind", "Detail", "Time") VALUES ($1, $2, 'Connected', $3, $4)
                    """)
                {
                    Parameters =
                    {
                        new NpgsqlParameter<Guid> { TypedValue = clientId },
                        new NpgsqlParameter<Guid> { TypedValue = endpointId },
                        new NpgsqlParameter<string> { TypedValue = DbText.Clean($"Connected from {remoteAddress}, agent {hello.AgentVersion}.", 1000) },
                        new NpgsqlParameter<DateTime> { TypedValue = now }
                    }
                },
                new NpgsqlBatchCommand("""
                    SELECT "Version", "Payload", "Signature", "KeyId" FROM "EndpointConfigs" WHERE "EndpointId" = $1 AND "Version" > $2
                    """)
                {
                    Parameters =
                    {
                        new NpgsqlParameter<Guid> { TypedValue = endpointId },
                        new NpgsqlParameter<long> { TypedValue = DbText.ClampToLong(agentConfigVersion) }
                    }
                },
                new NpgsqlBatchCommand("""SELECT "Hash" FROM "InventorySnapshots" WHERE "EndpointId" = $1""")
                {
                    Parameters = { new NpgsqlParameter<Guid> { TypedValue = endpointId } }
                }
            }
        };

        StoredConfig? config = null;
        string? inventoryHash = null;
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken))
        {
            // The INSERT returns no result set, so the reader starts on the configuration query.
            if (await reader.ReadAsync(cancellationToken))
            {
                config = new StoredConfig(endpointId, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2),
                    reader.GetString(3), tier);
            }

            await reader.NextResultAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                inventoryHash = reader.GetString(0);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new SessionStart(clientId, tier, config, inventoryHash);
    }

    /// <summary>Marks one endpoint offline and records a Disconnected event.</summary>
    public async Task MarkOfflineAsync(Guid endpointId, DateTime lastSeen, string detail, DateTime now, CancellationToken cancellationToken)
    {
        await using var batch = _dataSource.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand("""
            UPDATE "Endpoints" SET "IsOnline" = false, "LastSeenAt" = GREATEST("LastSeenAt", $2), "UpdatedAt" = $3 WHERE "Id" = $1
            """)
        {
            Parameters =
            {
                new NpgsqlParameter<Guid> { TypedValue = endpointId },
                new NpgsqlParameter<DateTime> { TypedValue = lastSeen },
                new NpgsqlParameter<DateTime> { TypedValue = now }
            }
        });
        batch.BatchCommands.Add(new NpgsqlBatchCommand("""
            INSERT INTO "EndpointEvents" ("ClientId", "EndpointId", "Kind", "Detail", "Time")
            SELECT "ClientId", "Id", 'Disconnected', $2, $3 FROM "Endpoints" WHERE "Id" = $1
            """)
        {
            Parameters =
            {
                new NpgsqlParameter<Guid> { TypedValue = endpointId },
                new NpgsqlParameter<string> { TypedValue = DbText.Clean(detail, 1000) },
                new NpgsqlParameter<DateTime> { TypedValue = now }
            }
        });
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Marks many endpoints offline in one round trip (gateway shutdown).</summary>
    public async Task MarkOfflineAsync(Guid[] endpointIds, string detail, DateTime now, CancellationToken cancellationToken)
    {
        if (endpointIds.Length == 0)
        {
            return;
        }

        await using var batch = _dataSource.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand("""
            UPDATE "Endpoints" SET "IsOnline" = false, "UpdatedAt" = $2 WHERE "Id" = ANY($1)
            """)
        {
            Parameters = { new NpgsqlParameter<Guid[]> { TypedValue = endpointIds }, new NpgsqlParameter<DateTime> { TypedValue = now } }
        });
        batch.BatchCommands.Add(new NpgsqlBatchCommand("""
            INSERT INTO "EndpointEvents" ("ClientId", "EndpointId", "Kind", "Detail", "Time")
            SELECT "ClientId", "Id", 'Disconnected', $2, $3 FROM "Endpoints" WHERE "Id" = ANY($1)
            """)
        {
            Parameters =
            {
                new NpgsqlParameter<Guid[]> { TypedValue = endpointIds },
                new NpgsqlParameter<string> { TypedValue = DbText.Clean(detail, 1000) },
                new NpgsqlParameter<DateTime> { TypedValue = now }
            }
        });
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Records an endpoint event, taking ClientId from the endpoint row. No-op when the endpoint is gone.</summary>
    public async Task InsertEventAsync(Guid endpointId, EndpointEventKind kind, string detail, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO "EndpointEvents" ("ClientId", "EndpointId", "Kind", "Detail", "Time")
            SELECT "ClientId", "Id", $2, $3, $4 FROM "Endpoints" WHERE "Id" = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind.ToString() });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(detail, 1000) });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes LastSeenAt for many endpoints in one statement (heartbeats never cause a write each).</summary>
    public async Task FlushLastSeenAsync(Guid[] endpointIds, DateTime[] lastSeen, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE "Endpoints" AS e SET "LastSeenAt" = u.t
            FROM unnest($1::uuid[], $2::timestamptz[]) AS u(id, t)
            WHERE e."Id" = u.id AND (e."LastSeenAt" IS NULL OR e."LastSeenAt" < u.t)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = endpointIds });
        command.Parameters.Add(new NpgsqlParameter<DateTime[]> { TypedValue = lastSeen });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Stores a result batch exactly once: the batch row and the results commit together, so a batch is either fully
    /// stored or not at all, and a resent sequence writes nothing. Results use server ingest time for ordering.
    /// </summary>
    public async Task<(IngestOutcome Outcome, int Stored)> IngestAsync(Guid endpointId, Guid clientId, CheckResultBatch batch, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var insertBatch = new NpgsqlCommand("""
            INSERT INTO "IngestBatches" ("EndpointId", "Sequence", "ClientId", "ReceivedAt") VALUES ($1, $2, $3, $4)
            ON CONFLICT DO NOTHING RETURNING 1
            """, connection, transaction))
        {
            insertBatch.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
            insertBatch.Parameters.Add(new NpgsqlParameter<long> { TypedValue = (long)batch.Sequence });
            insertBatch.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = clientId });
            insertBatch.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
            if (await insertBatch.ExecuteScalarAsync(cancellationToken) is null)
            {
                return (IngestOutcome.Duplicate, 0);
            }
        }

        var stored = 0;
        if (batch.Results.Count > 0)
        {
            await using var importer = await connection.BeginBinaryImportAsync("""
                COPY "CheckResults" ("Time", "ClientId", "EndpointId", "CheckDefinitionId", "Target", "AgentTime", "Value", "Detail", "Error", "ConfigVersion")
                FROM STDIN (FORMAT BINARY)
                """, cancellationToken);
            foreach (var result in batch.Results)
            {
                if (!Guid.TryParse(result.CheckId, out var checkId))
                {
                    continue;
                }

                var agentTime = result.CollectedAt is null ? now : SafeToDateTime(result.CollectedAt, now);
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                await importer.WriteAsync(clientId, NpgsqlDbType.Uuid, cancellationToken);
                await importer.WriteAsync(endpointId, NpgsqlDbType.Uuid, cancellationToken);
                await importer.WriteAsync(checkId, NpgsqlDbType.Uuid, cancellationToken);
                await importer.WriteAsync(DbText.Clean(result.Target, 256), NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(agentTime, NpgsqlDbType.TimestampTz, cancellationToken);
                await importer.WriteAsync(result.Value, NpgsqlDbType.Double, cancellationToken);
                await importer.WriteAsync(DbText.Clean(result.Detail, 1000), NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(DbText.Clean(result.Error, 1000), NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(DbText.ClampToLong(result.ConfigVersion), NpgsqlDbType.Bigint, cancellationToken);
                stored++;
            }

            await importer.CompleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (IngestOutcome.Stored, stored);
    }

    /// <summary>Stores the latest inventory (one row per endpoint) and copies the OS facts to the endpoint.</summary>
    public async Task SaveInventoryAsync(Guid endpointId, Guid clientId, InventoryReport report, DateTime now, CancellationToken cancellationToken)
    {
        var inventory = report.Inventory ?? new Inventory();
        var os = inventory.Os ?? new OsInfo();

        await using var batch = _dataSource.CreateBatch();
        var upsert = new NpgsqlBatchCommand("""
            INSERT INTO "InventorySnapshots" ("EndpointId", "ClientId", "ReceivedAt", "Hash", "Manufacturer", "Model", "SerialNumber",
              "CpuModel", "CpuCores", "CpuLogicalProcessors", "MemoryTotalBytes", "BootTime", "Domain", "LoggedOnUser",
              "DisksJson", "NetworkInterfacesJson", "SoftwareJson")
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15::jsonb, $16::jsonb, $17::jsonb)
            ON CONFLICT ("EndpointId") DO UPDATE SET
              "ReceivedAt" = EXCLUDED."ReceivedAt", "Hash" = EXCLUDED."Hash", "Manufacturer" = EXCLUDED."Manufacturer",
              "Model" = EXCLUDED."Model", "SerialNumber" = EXCLUDED."SerialNumber", "CpuModel" = EXCLUDED."CpuModel",
              "CpuCores" = EXCLUDED."CpuCores", "CpuLogicalProcessors" = EXCLUDED."CpuLogicalProcessors",
              "MemoryTotalBytes" = EXCLUDED."MemoryTotalBytes", "BootTime" = EXCLUDED."BootTime", "Domain" = EXCLUDED."Domain",
              "LoggedOnUser" = EXCLUDED."LoggedOnUser", "DisksJson" = EXCLUDED."DisksJson",
              "NetworkInterfacesJson" = EXCLUDED."NetworkInterfacesJson", "SoftwareJson" = EXCLUDED."SoftwareJson"
            """);
        upsert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        upsert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = clientId });
        upsert.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        upsert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(report.Hash, 64) });
        upsert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(inventory.Manufacturer, 200) });
        upsert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(inventory.Model, 200) });
        upsert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(inventory.SerialNumber, 200) });
        upsert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(inventory.CpuModel, 200) });
        upsert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = DbText.ClampToInt(inventory.CpuCores) });
        upsert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = DbText.ClampToInt(inventory.CpuLogicalProcessors) });
        upsert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = DbText.ClampToLong(inventory.MemoryTotalBytes) });
        upsert.Parameters.Add(new NpgsqlParameter("", NpgsqlDbType.TimestampTz)
        {
            Value = inventory.BootTime is null ? DBNull.Value : SafeToDateTime(inventory.BootTime, now)
        });
        upsert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(inventory.Domain, 255) });
        upsert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(inventory.LoggedOnUser, 255) });
        upsert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DisksJson(inventory) });
        upsert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = NetworkJson(inventory) });
        upsert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = SoftwareJson(inventory) });
        batch.BatchCommands.Add(upsert);

        // Batch commands take positional parameters only.
        var update = new NpgsqlBatchCommand("""
            UPDATE "Endpoints" SET "Hostname" = $3, "OsPlatform" = $4, "OsName" = $5, "OsVersion" = $6,
              "Architecture" = $7, "DetectedClass" = $8, "UpdatedAt" = $2
            WHERE "Id" = $1
            """);
        update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        update.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(inventory.Hostname, 255) });
        update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(os.Platform, 20) });
        update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(os.Name, 200) });
        update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(os.Version, 100) });
        update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(os.Architecture, 20) });
        update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DetectedClass(os) });
        batch.BatchCommands.Add(update);

        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The endpoint's signed configuration and stored tier, or null when there is none.</summary>
    public async Task<StoredConfig?> ReadConfigAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT c."Version", c."Payload", c."Signature", c."KeyId", e."Tier"
            FROM "EndpointConfigs" c JOIN "Endpoints" e ON e."Id" = c."EndpointId"
            WHERE c."EndpointId" = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredConfig(endpointId, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2),
            reader.GetString(3), Enum.Parse<EndpointTier>(reader.GetString(4)));
    }

    /// <summary>Configuration versions of many endpoints (catch-up after a lost notification).</summary>
    public async Task<Dictionary<Guid, long>> ReadConfigVersionsAsync(Guid[] endpointIds, CancellationToken cancellationToken)
    {
        var versions = new Dictionary<Guid, long>(endpointIds.Length);
        await using var command = _dataSource.CreateCommand("""
            SELECT "EndpointId", "Version" FROM "EndpointConfigs" WHERE "EndpointId" = ANY($1)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = endpointIds });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions[reader.GetGuid(0)] = reader.GetInt64(1);
        }

        return versions;
    }

    /// <summary>Stored tiers of many endpoints. Missing ids were deleted.</summary>
    public async Task<Dictionary<Guid, EndpointTier>> ReadTiersAsync(Guid[] endpointIds, CancellationToken cancellationToken)
    {
        var tiers = new Dictionary<Guid, EndpointTier>(endpointIds.Length);
        await using var command = _dataSource.CreateCommand("""
            SELECT "Id", "Tier" FROM "Endpoints" WHERE "Id" = ANY($1)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = endpointIds });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tiers[reader.GetGuid(0)] = Enum.Parse<EndpointTier>(reader.GetString(1));
        }

        return tiers;
    }

    /// <summary>Records the configuration version the agent confirmed. Never moves backwards.</summary>
    public async Task UpdateAppliedConfigVersionAsync(Guid endpointId, long version, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE "Endpoints" SET "AppliedConfigVersion" = $2, "UpdatedAt" = $3 WHERE "Id" = $1 AND "AppliedConfigVersion" < $2
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = version });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>True when the database answers within a few seconds.</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("SELECT 1");
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static void AddEndpointFacts(NpgsqlCommand command, string hostname, OsInfo os)
    {
        command.Parameters.Add(new NpgsqlParameter<string>("hostname", DbText.Clean(hostname, 255)));
        command.Parameters.Add(new NpgsqlParameter<string>("osPlatform", DbText.Clean(os.Platform, 20)));
        command.Parameters.Add(new NpgsqlParameter<string>("osName", DbText.Clean(os.Name, 200)));
        command.Parameters.Add(new NpgsqlParameter<string>("osVersion", DbText.Clean(os.Version, 100)));
        command.Parameters.Add(new NpgsqlParameter<string>("architecture", DbText.Clean(os.Architecture, 20)));
        command.Parameters.Add(new NpgsqlParameter<string>("detectedClass", DetectedClass(os)));
    }

    private static string DetectedClass(OsInfo os) => (os.IsServer ? EndpointClass.Server : EndpointClass.Workstation).ToString();

    private static DateTime SafeToDateTime(Google.Protobuf.WellKnownTypes.Timestamp timestamp, DateTime fallback)
    {
        try
        {
            return timestamp.ToDateTime();
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
        catch (ArgumentOutOfRangeException)
        {
            return fallback;
        }
    }

    private static string DisksJson(Inventory inventory) => WriteArray(inventory.Disks, static (writer, disk) =>
    {
        writer.WriteString("mount", DbText.Clean(disk.Mount, MaxJsonString));
        writer.WriteString("filesystem", DbText.Clean(disk.Filesystem, MaxJsonString));
        writer.WriteNumber("totalBytes", disk.TotalBytes);
        writer.WriteNumber("freeBytes", disk.FreeBytes);
    });

    private static string NetworkJson(Inventory inventory) => WriteArray(inventory.NetworkInterfaces, static (writer, nic) =>
    {
        writer.WriteString("name", DbText.Clean(nic.Name, MaxJsonString));
        writer.WriteString("macAddress", DbText.Clean(nic.MacAddress, MaxJsonString));
        writer.WriteStartArray("ipAddresses");
        foreach (var address in nic.IpAddresses)
        {
            writer.WriteStringValue(DbText.Clean(address, 100));
        }

        writer.WriteEndArray();
    });

    private static string SoftwareJson(Inventory inventory) => WriteArray(inventory.Software, static (writer, item) =>
    {
        writer.WriteString("name", DbText.Clean(item.Name, MaxJsonString));
        writer.WriteString("version", DbText.Clean(item.Version, MaxJsonString));
        writer.WriteString("publisher", DbText.Clean(item.Publisher, MaxJsonString));
        writer.WriteString("installDate", DbText.Clean(item.InstallDate, MaxJsonString));
    });

    private static string WriteArray<T>(IEnumerable<T> items, Action<Utf8JsonWriter, T> writeItem)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writeItem(writer, item);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
