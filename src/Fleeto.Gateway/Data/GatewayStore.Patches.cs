using System.Text.RegularExpressions;
using Fleeto.Core.Domain;
using Fleeto.Protocol.Agent.V1;
using Npgsql;
using NpgsqlTypes;

namespace Fleeto.Gateway.Data;

/// <summary>A patch scan request that may be delivered to its agent now (0.9.0).</summary>
public sealed record DeliverablePatchScanRequest(Guid Id, Guid EndpointId);

public enum PatchScanOutcome
{
    Stored,
    /// <summary>The scan id was stored before; nothing was written.</summary>
    Duplicate,
    /// <summary>The endpoint sent more scans in 24 hours than <see cref="PatchScanRules.MaxScansPerEndpointPerDay"/>; nothing was written.</summary>
    OverLimit
}

/// <summary>Patch scans (0.9.0): PatchScans (select, insert) and PatchScanRequests (select, update).</summary>
public sealed partial class GatewayStore
{
    [GeneratedRegex(@"^CVE-\d{4}-\d{4,7}$")]
    private static partial Regex CvePattern();

    [GeneratedRegex(@"^KB\d{4,8}$")]
    private static partial Regex KbPattern();

    /// <summary>
    /// Stores one patch scan exactly once per scan id. Every text is cleaned and cut to its limit and only the first
    /// <see cref="PatchScanRules.MaxUpdates"/> updates are kept: the agent is authenticated, not trusted with the size of the database.
    /// The workers turn the row into patch state.
    /// </summary>
    public async Task<PatchScanOutcome> SavePatchScanAsync(Guid endpointId, Guid clientId, Guid scanId, PatchScanReport report, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var existing = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE "AgentScanId" = $2), count(*) FILTER (WHERE "ReceivedAt" > $3)
            FROM "PatchScans" WHERE "EndpointId" = $1 AND ("AgentScanId" = $2 OR "ReceivedAt" > $3)
            """, connection, transaction))
        {
            existing.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
            existing.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = scanId });
            existing.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now.AddDays(-1) });
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt64(0) > 0)
            {
                return PatchScanOutcome.Duplicate;
            }

            if (reader.GetInt64(1) >= PatchScanRules.MaxScansPerEndpointPerDay)
            {
                return PatchScanOutcome.OverLimit;
            }
        }

        var updates = ToEntries(report).ToList();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO "PatchScans" ("Id", "ClientId", "EndpointId", "AgentScanId", "ReceivedAt", "AgentStartedAt", "DurationMs", "Tool",
              "Problem", "RestartRequired", "UpdateCount", "Truncated", "RequestId", "AgentVersion", "UpdatesJson", "Source", "SourceServer")
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13,
              COALESCE((SELECT left("AgentVersion", 50) FROM "Endpoints" WHERE "Id" = $3), ''), $14, $15, $16)
            ON CONFLICT ("EndpointId", "AgentScanId") DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7() });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = clientId });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = scanId });
            insert.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
            insert.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.TimestampTz,
                Value = report.StartedAt is null ? DBNull.Value : SafeToDateTime(report.StartedAt, now)
            });
            insert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = report.DurationMs > int.MaxValue ? int.MaxValue : (int)report.DurationMs });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = report.Engine });
            insert.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Varchar,
                Value = string.IsNullOrWhiteSpace(report.Problem) ? DBNull.Value : DbText.Clean(report.Problem, PatchScanRules.MaxProblemLength)
            });
            insert.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = report.RestartRequired });
            insert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = updates.Count });
            insert.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = report.Truncated || report.Updates.Count > PatchScanRules.MaxUpdates });
            insert.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Uuid,
                Value = Guid.TryParse(report.RequestId, out var requestId) ? requestId : DBNull.Value
            });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = PatchScanRules.Serialize(updates) });
            // Windows (step 2): where the Windows Update Agent searched; an unknown value is stored as none.
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = PatchScanRules.Sources.Contains(report.Source) ? report.Source : string.Empty });
            insert.Parameters.Add(new NpgsqlParameter<string>
            {
                TypedValue = report.Source == "wsus" ? DbText.Clean(report.SourceServer, PatchScanRules.MaxServerLength) : string.Empty
            });
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return PatchScanOutcome.Duplicate;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return PatchScanOutcome.Stored;
    }

    /// <summary>The updates of a report as stored: cleaned, cut, without empty names, at most <see cref="PatchScanRules.MaxUpdates"/>.</summary>
    internal static IEnumerable<PatchScanUpdate> ToEntries(PatchScanReport report)
    {
        var windows = report.Engine == "windows_update";
        return report.Updates.Take(PatchScanRules.MaxUpdates)
            .Where(u => !string.IsNullOrWhiteSpace(u.Name))
            .Select(u => new PatchScanUpdate(
                DbText.Clean(string.IsNullOrWhiteSpace(u.Id) ? u.Name : u.Id, PatchScanRules.MaxNameLength),
                // A Windows update is named by its title, which is longer than a package name.
                DbText.Clean(u.Name, windows ? PatchScanRules.MaxTitleLength : PatchScanRules.MaxNameLength),
                DbText.Clean(u.Version, PatchScanRules.MaxVersionLength),
                DbText.Clean(u.InstalledVersion, PatchScanRules.MaxVersionLength),
                u.Classification == "security",
                DbText.Clean(u.Origin, PatchScanRules.MaxOriginLength),
                DbText.Clean(u.Advisory, PatchScanRules.MaxAdvisoryLength),
                DbText.Clean(u.Severity, 20),
                u.Held,
                u.Removal,
                windows ? DbText.Clean(u.Classification, 50) : string.Empty,
                [.. u.KbNumbers.Select(k => k.Trim().ToUpperInvariant()).Where(k => KbPattern().IsMatch(k)).Distinct().Take(10)],
                ReleaseDate(u.ReleasedAt),
                [.. u.Cves.Select(c => c.Trim().ToUpperInvariant()).Where(c => CvePattern().IsMatch(c)).Distinct().Take(200)],
                DbText.ClampToLong(u.SizeBytes),
                u.Downloaded,
                u.RestartRequired,
                u.Mandatory,
                u.IsDriver,
                u.IsFeatureUpdate));
    }

    /// <summary>A release date the agent reported, or null when it is missing, unreadable or not plausible (before 1990 or in the future).</summary>
    private static DateTime? ReleaseDate(Google.Protobuf.WellKnownTypes.Timestamp? timestamp)
    {
        if (timestamp is null)
        {
            return null;
        }

        var value = SafeToDateTime(timestamp, DateTime.MinValue);
        return value.Year >= 1990 && value <= DateTime.UtcNow.AddDays(1) ? value : null;
    }

    /// <summary>Pending patch scan requests of these endpoints, oldest first; at most one per endpoint is needed.</summary>
    public async Task<List<DeliverablePatchScanRequest>> ReadDeliverablePatchScanRequestsAsync(Guid[] endpointIds, DateTime now,
        CancellationToken cancellationToken)
    {
        var requests = new List<DeliverablePatchScanRequest>();
        if (endpointIds.Length == 0)
        {
            return requests;
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT "Id", "EndpointId" FROM "PatchScanRequests"
            WHERE "EndpointId" = ANY($1) AND "DeliveredAt" IS NULL AND "ExpiresAt" > $2
            ORDER BY "RequestedAt"
            LIMIT 1000
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = endpointIds });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new DeliverablePatchScanRequest(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return requests;
    }

    /// <summary>The endpoint of a patch scan request, or null when it does not exist.</summary>
    public async Task<Guid?> ReadPatchScanRequestEndpointAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""SELECT "EndpointId" FROM "PatchScanRequests" WHERE "Id" = $1""");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = requestId });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid endpointId ? endpointId : null;
    }

    /// <summary>
    /// Marks every pending patch scan request of the endpoint delivered, since one scan answers them all. Returns the oldest, or null
    /// when none was pending, so a request is sent once.
    /// </summary>
    public async Task<Guid?> MarkPatchScanRequestsDeliveredAsync(Guid endpointId, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            WITH delivered AS (
              UPDATE "PatchScanRequests" SET "DeliveredAt" = $2
              WHERE "EndpointId" = $1 AND "DeliveredAt" IS NULL AND "ExpiresAt" > $2
              RETURNING "Id", "RequestedAt")
            SELECT "Id" FROM delivered ORDER BY "RequestedAt" LIMIT 1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : null;
    }
}
