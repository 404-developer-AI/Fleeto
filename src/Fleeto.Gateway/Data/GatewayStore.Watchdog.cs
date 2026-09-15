using Fleeto.Core.Entities;
using Fleeto.Protocol.Agent.V1;
using Npgsql;

namespace Fleeto.Gateway.Data;

/// <summary>What the gateway needs to know about an endpoint when its watchdog session opens (0.2.1).</summary>
public sealed record WatchdogSessionStart(Guid ClientId, EndpointTier Tier, UpdateRing Ring);

/// <summary>Tier and update ring of a live endpoint, refreshed periodically.</summary>
public sealed record SessionFacts(EndpointTier Tier, UpdateRing Ring);

/// <summary>The release row the gateway offers, with the controls an admin sets in web.</summary>
public sealed record ReleaseControl(string Version, DateTime InstalledAt, DateTime? PausedAt, DateTime? ReleasedToAllAt, bool Installed);

/// <summary>Watchdog sessions, update rings, component states and agent releases (0.2.1).</summary>
public sealed partial class GatewayStore
{
    /// <summary>The ring of the endpoint's site policy, else of the default policy. Aliases: <c>e</c> = "Endpoints".</summary>
    internal const string EffectiveRingSql = """
        COALESCE(
          (SELECT p."UpdateRing" FROM "SitePolicies" sp JOIN "Policies" p ON p."Id" = sp."PolicyId" WHERE sp."SiteId" = e."SiteId"),
          (SELECT p."UpdateRing" FROM "Policies" p WHERE p."IsDefault" LIMIT 1),
          'Standard')
        """;

    /// <summary>Marks the watchdog of an endpoint online. Returns null when the endpoint no longer exists.</summary>
    public async Task<WatchdogSessionStart?> OpenWatchdogSessionAsync(Guid endpointId, string watchdogVersion, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE "Endpoints" e SET "WatchdogOnline" = true, "WatchdogVersion" = $2, "WatchdogLastSeenAt" = $3, "UpdatedAt" = $3
            WHERE e."Id" = $1
            RETURNING e."ClientId", e."Tier",
            """ + " " + EffectiveRingSql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(watchdogVersion, 50) });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WatchdogSessionStart(reader.GetGuid(0), Enum.Parse<EndpointTier>(reader.GetString(1)), ParseRing(reader.GetString(2)));
    }

    /// <summary>Marks the watchdog of one endpoint offline.</summary>
    public async Task MarkWatchdogOfflineAsync(Guid endpointId, DateTime lastSeen, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE "Endpoints" SET "WatchdogOnline" = false, "WatchdogLastSeenAt" = GREATEST("WatchdogLastSeenAt", $2), "UpdatedAt" = $3 WHERE "Id" = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = lastSeen });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Marks the watchdogs of many endpoints offline in one round trip (gateway shutdown).</summary>
    public async Task MarkWatchdogsOfflineAsync(Guid[] endpointIds, DateTime now, CancellationToken cancellationToken)
    {
        if (endpointIds.Length == 0)
        {
            return;
        }

        await using var command = _dataSource.CreateCommand("""
            UPDATE "Endpoints" SET "WatchdogOnline" = false, "UpdatedAt" = $2 WHERE "Id" = ANY($1)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = endpointIds });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes WatchdogLastSeenAt for many endpoints in one statement.</summary>
    public async Task FlushWatchdogLastSeenAsync(Guid[] endpointIds, DateTime[] lastSeen, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE "Endpoints" AS e SET "WatchdogLastSeenAt" = u.t
            FROM unnest($1::uuid[], $2::timestamptz[]) AS u(id, t)
            WHERE e."Id" = u.id AND (e."WatchdogLastSeenAt" IS NULL OR e."WatchdogLastSeenAt" < u.t)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = endpointIds });
        command.Parameters.Add(new NpgsqlParameter<DateTime[]> { TypedValue = lastSeen });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Stored tier and effective update ring of many endpoints. Missing ids were deleted.</summary>
    public async Task<Dictionary<Guid, SessionFacts>> ReadSessionFactsAsync(Guid[] endpointIds, CancellationToken cancellationToken)
    {
        var facts = new Dictionary<Guid, SessionFacts>(endpointIds.Length);
        if (endpointIds.Length == 0)
        {
            return facts;
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT e."Id", e."Tier",
            """ + " " + EffectiveRingSql + " " + """
            FROM "Endpoints" e WHERE e."Id" = ANY($1)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = endpointIds });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            facts[reader.GetGuid(0)] = new SessionFacts(Enum.Parse<EndpointTier>(reader.GetString(1)), ParseRing(reader.GetString(2)));
        }

        return facts;
    }

    /// <summary>Stores how a service sees its peer: the state of <paramref name="component"/> on the endpoint.</summary>
    public async Task SavePeerStatusAsync(Guid endpointId, Guid clientId, AgentComponent component, PeerStatus status, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO "EndpointComponentStates" ("EndpointId", "Component", "ClientId", "InstalledVersion", "ServiceState", "ServiceDetail", "ServiceStateAt", "UpdateVersion", "UpdateDetail")
            VALUES ($1, $2, $3, $4, $5, $6, $7, '', '')
            ON CONFLICT ("EndpointId", "Component") DO UPDATE SET
              "InstalledVersion" = EXCLUDED."InstalledVersion", "ServiceState" = EXCLUDED."ServiceState",
              "ServiceDetail" = EXCLUDED."ServiceDetail", "ServiceStateAt" = EXCLUDED."ServiceStateAt"
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = component.ToString() });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = clientId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(status.Version, 50) });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = MapServiceState(status.State).ToString() });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(status.Detail, 500) });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Stores the progress of installing a component, as reported by the service that installs it.</summary>
    public async Task SaveUpdateStatusAsync(Guid endpointId, Guid clientId, AgentComponent component, string version, ComponentUpdateState state,
        string detail, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO "EndpointComponentStates" ("EndpointId", "Component", "ClientId", "InstalledVersion", "ServiceState", "ServiceDetail", "UpdateVersion", "UpdateState", "UpdateDetail", "UpdateAt")
            VALUES ($1, $2, $3, '', 'Unknown', '', $4, $5, $6, $7)
            ON CONFLICT ("EndpointId", "Component") DO UPDATE SET
              "UpdateVersion" = EXCLUDED."UpdateVersion", "UpdateState" = EXCLUDED."UpdateState",
              "UpdateDetail" = EXCLUDED."UpdateDetail", "UpdateAt" = EXCLUDED."UpdateAt"
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = component.ToString() });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = clientId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(version, 50) });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = state.ToString() });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(detail, 500) });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Records the release the gateway loaded as the current one: inserted with the install time when it is new, marked current, earlier rows
    /// unmarked, and an audit entry the first time. Returns the controls of the row.
    /// </summary>
    public async Task<ReleaseControl> RecordCurrentReleaseAsync(string version, string manifestSha256, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var unmark = new NpgsqlCommand("""
            UPDATE "AgentReleases" SET "IsCurrent" = false WHERE "IsCurrent" AND "Version" <> $1
            """, connection, transaction))
        {
            unmark.Parameters.Add(new NpgsqlParameter<string> { TypedValue = version });
            await unmark.ExecuteNonQueryAsync(cancellationToken);
        }

        bool inserted;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO "AgentReleases" ("Version", "ManifestSha256", "InstalledAt", "IsCurrent") VALUES ($1, $2, $3, true)
            ON CONFLICT ("Version") DO UPDATE SET "IsCurrent" = true, "ManifestSha256" = EXCLUDED."ManifestSha256"
            RETURNING (xmax = 0)
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = version });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = manifestSha256 });
            insert.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
            inserted = (bool)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        if (inserted)
        {
            await using var audit = new NpgsqlCommand("""
                INSERT INTO "AuditEntries" ("Time", "ActorType", "ActorId", "ActorName", "Action", "TargetType", "TargetId", "DetailsJson")
                VALUES ($1, 'System', 'fleeto-gateway', 'fleeto-gateway', 'agent_release.installed', 'AgentRelease', $2, jsonb_build_object('version', $2::text, 'manifestSha256', $3::text))
                """, connection, transaction);
            audit.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
            audit.Parameters.Add(new NpgsqlParameter<string> { TypedValue = version });
            audit.Parameters.Add(new NpgsqlParameter<string> { TypedValue = manifestSha256 });
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await ReadReleaseControlAsync(version, cancellationToken) ?? throw new InvalidOperationException("The release row just written was not found.");
    }

    public async Task<ReleaseControl?> ReadReleaseControlAsync(string version, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT "InstalledAt", "PausedAt", "ReleasedToAllAt" FROM "AgentReleases" WHERE "Version" = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = version });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReleaseControl(version, DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
            reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
            reader.IsDBNull(2) ? null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc), true);
    }

    private static UpdateRing ParseRing(string value) => Enum.TryParse<UpdateRing>(value, out var ring) ? ring : UpdateRing.Standard;

    internal static ComponentServiceState MapServiceState(ServiceState state) => state switch
    {
        ServiceState.Running => ComponentServiceState.Running,
        ServiceState.Stopped => ComponentServiceState.Stopped,
        ServiceState.Starting => ComponentServiceState.Starting,
        ServiceState.Stopping => ComponentServiceState.Stopping,
        ServiceState.Disabled => ComponentServiceState.Disabled,
        ServiceState.NotInstalled => ComponentServiceState.NotInstalled,
        _ => ComponentServiceState.Unknown
    };

    internal static ComponentUpdateState? MapUpdateState(UpdateState state) => state switch
    {
        UpdateState.Downloading => ComponentUpdateState.Downloading,
        UpdateState.Installing => ComponentUpdateState.Installing,
        UpdateState.Installed => ComponentUpdateState.Installed,
        UpdateState.Failed => ComponentUpdateState.Failed,
        UpdateState.RolledBack => ComponentUpdateState.RolledBack,
        _ => null
    };
}
