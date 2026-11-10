using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Testing;
using Npgsql;

// Every statement here is a constant of this test class, with values as parameters.
#pragma warning disable CA2100

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// What the signer signs cannot be changed after it was requested (security review of 0.3.0 step 7, migration ImmutableSigningBindings).
/// Acting as the gateway with the production grants: the gateway may move a remote session participant through its states and record
/// the endpoint's online state, but never put another browser key or endpoint into a token request, never make a participant or a job
/// wait for a signature again, never change what was signed, and never change an endpoint's tier, client or site.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SigningBindingTests
{
    private readonly TestDatabase _db;

    public SigningBindingTests(DatabaseFixture fixture) => _db = fixture.Database;

    private async Task<(Endpoint Endpoint, Guid SessionId, Guid ParticipantId, Guid JobId)> RowsAsync()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.AgentOnly);
        var user = await _db.CreateUserAsync(FleetoRoles.Technician);
        var (script, version) = await _db.CreateScriptAsync(client.Id, user.Id);
        var now = _db.Time.GetUtcNow().UtcDateTime;
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await using var db = _db.DbFactory.CreateSystem();
        db.RemoteSessions.Add(new RemoteSession
        {
            Id = sessionId, ClientId = client.Id, EndpointId = endpoint.Id, Kind = RemoteSessionKind.RemoteBackground, Component = AgentComponent.Watchdog,
            StartedByUserId = user.Id, StartedByName = "Tess", CreatedAt = now
        });
        db.RemoteSessionParticipants.Add(new RemoteSessionParticipant
        {
            Id = participantId, SessionId = sessionId, ClientId = client.Id, EndpointId = endpoint.Id, UserId = user.Id, UserName = "Tess",
            BrowserPublicKey = Enumerable.Repeat((byte)1, 32).ToArray(), State = RemoteParticipantState.Signed, CreatedAt = now,
            TokenPayload = [1, 2, 3], TokenSignature = new byte[64], SigningKeyId = "key", SignedAt = now, ValidUntil = now.AddMinutes(1)
        });
        db.Jobs.Add(new Job
        {
            Id = jobId, ClientId = client.Id, EndpointId = endpoint.Id, BatchId = Guid.NewGuid(), ScriptId = script.Id, ScriptVersionId = version.Id,
            ScriptName = script.Name, ScriptVersionNumber = 1, Language = script.Language, ScriptSha256 = version.Sha256, TimeoutSeconds = 600,
            MaxOutputBytes = 1024 * 1024, CreatedAt = now, ValidUntil = now.AddHours(1), InitiatedByUserId = user.Id, InitiatedByName = "Tess",
            State = JobState.Queued, Payload = [1, 2, 3], Signature = new byte[64], SigningKeyId = "key", SignedAt = now
        });
        await db.SaveChangesAsync();
        return (endpoint, sessionId, participantId, jobId);
    }

    /// <summary>Runs one statement as the gateway with the grants of production; returns the error, or null when it was allowed.</summary>
    private Task<PostgresException?> AsGatewayAsync(string sql, params object[] values) => AsRoleAsync(Hosting.DatabaseRoles.Gateway, sql, values);

    /// <summary>Runs one statement as a role with the grants of production, rolled back; returns the error, or null when it was allowed.</summary>
    private async Task<PostgresException?> AsRoleAsync(string databaseRole, string sql, params object[] values)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var roles = new NpgsqlCommand(string.Join('\n', Hosting.DatabaseRoles.Application.Select(r =>
                         $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{r}') THEN CREATE ROLE {r} NOLOGIN; END IF; END $$;")),
                         connection, transaction))
        {
            await roles.ExecuteNonQueryAsync();
        }

        await using (var grants = new NpgsqlCommand(DatabaseGrants.BuildSql(), connection, transaction))
        {
            await grants.ExecuteNonQueryAsync();
        }

        await using (var role = new NpgsqlCommand("SET LOCAL ROLE " + databaseRole, connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException error)
        {
            return error;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task The_gateway_cannot_put_its_own_key_or_another_endpoint_into_a_token_request()
    {
        var (_, sessionId, participantId, _) = await RowsAsync();
        var other = await _db.CreateEndpointAsync(await _db.CreateSiteAsync((await _db.CreateClientAsync()).Id), EndpointTier.Managed);

        var key = await AsGatewayAsync("""UPDATE "RemoteSessionParticipants" SET "BrowserPublicKey" = $1 WHERE "Id" = $2""",
            Enumerable.Repeat((byte)7, 32).ToArray(), participantId);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, key?.SqlState);

        var endpoint = await AsGatewayAsync("""UPDATE "RemoteSessions" SET "EndpointId" = $1 WHERE "Id" = $2""", other.Id, sessionId);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, endpoint?.SqlState);

        var kind = await AsGatewayAsync("""UPDATE "RemoteSessions" SET "Kind" = 'RemoteControl', "Component" = 'Agent' WHERE "Id" = $1""", sessionId);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, kind?.SqlState);

        // What the gateway does with a participant stays allowed.
        Assert.Null(await AsGatewayAsync("""UPDATE "RemoteSessionParticipants" SET "State" = 'Connecting', "ConnectingAt" = now() WHERE "Id" = $1""",
            participantId));
    }

    [Fact]
    public async Task Nothing_waits_for_a_signature_again_and_nothing_signed_changes()
    {
        var (_, _, participantId, jobId) = await RowsAsync();

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsGatewayAsync("""UPDATE "RemoteSessionParticipants" SET "State" = 'Requested' WHERE "Id" = $1""", participantId))?.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsGatewayAsync("""UPDATE "RemoteSessionParticipants" SET "TokenSignature" = $1 WHERE "Id" = $2""",
                Enumerable.Repeat((byte)9, 64).ToArray(), participantId))?.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsGatewayAsync("""UPDATE "Jobs" SET "State" = 'PendingSignature' WHERE "Id" = $1""", jobId))?.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsGatewayAsync("""UPDATE "Jobs" SET "Payload" = $1 WHERE "Id" = $2""", new byte[] { 3 }, jobId))?.SqlState);
    }

    [Fact]
    public async Task The_gateway_cannot_point_a_job_at_another_endpoint_or_change_an_endpoints_tier()
    {
        var (endpoint, _, _, jobId) = await RowsAsync();
        var other = await _db.CreateEndpointAsync(await _db.CreateSiteAsync(endpoint.ClientId, "Other"), EndpointTier.Managed);

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsGatewayAsync("""UPDATE "Jobs" SET "EndpointId" = $1 WHERE "Id" = $2""", other.Id, jobId))?.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsGatewayAsync("""UPDATE "Endpoints" SET "Tier" = 'Managed' WHERE "Id" = $1""", endpoint.Id))?.SqlState);

        // What the gateway does with a job and an endpoint stays allowed.
        Assert.Null(await AsGatewayAsync("""UPDATE "Jobs" SET "DeliveredAt" = now() WHERE "Id" = $1""", jobId));
        Assert.Null(await AsGatewayAsync("""UPDATE "Endpoints" SET "IsOnline" = true WHERE "Id" = $1""", endpoint.Id));
    }

    /// <summary>
    /// Every column of Endpoints with whether the gateway may change it and an expression that changes it. The gateway may change only
    /// what it writes itself (GatewayStore): the online state of the agent and the watchdog, the facts the agent reports and the config
    /// version the agent applied. A new column fails the test until it is decided here and in the trigger (fleeto_endpoint_gateway_columns).
    /// </summary>
    private static readonly Dictionary<string, (bool GatewayMay, string Change)> EndpointColumns = new()
    {
        ["AgentVersion"] = (true, "'9.9.9'"),
        ["AppliedConfigVersion"] = (true, """ "AppliedConfigVersion" + 1 """),
        ["Architecture"] = (true, "'arm64'"),
        ["DetectedClass"] = (true, """ CASE WHEN "DetectedClass" = 'Server' THEN 'Workstation' ELSE 'Server' END """),
        ["Hostname"] = (true, """ "Hostname" || '-renamed' """),
        ["IsOnline"] = (true, """ NOT "IsOnline" """),
        ["LastSeenAt"] = (true, "now()"),
        ["OsName"] = (true, "'Other OS'"),
        ["OsPlatform"] = (true, "'linux'"),
        ["OsVersion"] = (true, "'1.0'"),
        ["PublicIpAddress"] = (true, "'203.0.113.9'"),
        ["PublicIpSeenAt"] = (true, "now()"),
        ["SignedInUsersAt"] = (true, "now()"),
        ["SignedInUsersJson"] = (true, "'[]'::jsonb"),
        ["UpdatedAt"] = (true, "now()"),
        ["WatchdogLastSeenAt"] = (true, "now()"),
        ["WatchdogOnline"] = (true, """ NOT "WatchdogOnline" """),
        ["WatchdogVersion"] = (true, "'9.9.9'"),

        // The binding of the endpoint and its license: the signer and the gateway's allow list trust them.
        ["Id"] = (false, "gen_random_uuid()"),
        ["ClientId"] = (false, "gen_random_uuid()"),
        ["SiteId"] = (false, "gen_random_uuid()"),
        ["Tier"] = (false, """ CASE WHEN "Tier" = 'Managed' THEN 'AgentOnly' ELSE 'Managed' END """),
        // Starts the tasks "when an endpoint becomes managed" (security review of 0.9.0).
        ["ManagedSince"] = (false, """ COALESCE("ManagedSince", now()) + interval '1 minute' """),
        ["ClassOverride"] = (false, """ CASE WHEN "ClassOverride" IS NULL THEN 'Server' END """),
        ["Source"] = (false, """ CASE WHEN "Source" = 'Agent' THEN 'Proxmox' ELSE 'Agent' END """),
        ["EnrolledAt"] = (false, """ "EnrolledAt" + interval '1 minute' """),
        ["CreatedAt"] = (false, """ "CreatedAt" + interval '1 minute' """),
        // Written by web and the workers when the configuration changes; the gateway records only what the agent applied.
        ["ConfigVersion"] = (false, """ "ConfigVersion" + 1 """),
        // What users set: the description, the end-of-life extension, maintenance and the refresh request.
        ["Description"] = (false, """ COALESCE("Description", '') || 'x' """),
        ["EndOfLifeExtensionRelease"] = (false, """ COALESCE("EndOfLifeExtensionRelease", '') || 'x' """),
        ["InventoryRequestedAt"] = (false, """ COALESCE("InventoryRequestedAt", now()) + interval '1 minute' """),
        ["MaintenanceStartedAt"] = (false, """ COALESCE("MaintenanceStartedAt", now()) + interval '1 minute' """),
        ["MaintenanceEndsAt"] = (false, """ COALESCE("MaintenanceEndsAt", now()) + interval '1 minute' """),
        ["MaintenanceStartedByUserId"] = (false, "gen_random_uuid()"),
        ["MaintenanceStartedByName"] = (false, """ COALESCE("MaintenanceStartedByName", '') || 'x' """),
        ["MaintenanceReason"] = (false, """ COALESCE("MaintenanceReason", '') || 'x' """),
    };

    [Fact]
    public async Task The_gateway_can_change_only_the_endpoint_columns_it_writes()
    {
        var (endpoint, _, _, _) = await RowsAsync();
        var columns = new List<string>();
        await using (var command = _db.DataSource.CreateCommand("""
            SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Endpoints'
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }

        // A new column of Endpoints needs a decision: may the gateway change it?
        Assert.Equal(EndpointColumns.Keys.Order(StringComparer.Ordinal), columns.Order(StringComparer.Ordinal));
        foreach (var (column, (gatewayMay, change)) in EndpointColumns)
        {
            var error = await AsGatewayAsync($"""UPDATE "Endpoints" SET "{column}" = {change} WHERE "Id" = $1""", endpoint.Id);
            if (gatewayMay)
            {
                Assert.True(error is null, $"The gateway could not change {column}: {error?.MessageText}");
            }
            else
            {
                // ClientId cannot change for any role (fleeto_client_id_immutable, an earlier trigger); the rest is the gateway rule.
                Assert.True(error?.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.IntegrityConstraintViolation,
                    $"The gateway changed {column} ({error?.SqlState} {error?.MessageText})");
            }
        }

        // Every column the gateway may change at once, as GatewayStore does on a hello.
        var all = string.Join(", ", EndpointColumns.Where(c => c.Value.GatewayMay).Select(c => $"""
            "{c.Key}" = {c.Value.Change}
            """));
        Assert.Null(await AsGatewayAsync($"""UPDATE "Endpoints" SET {all} WHERE "Id" = $1""", endpoint.Id));
    }

    [Fact]
    public async Task The_workers_cannot_change_an_endpoints_tier()
    {
        var (endpoint, _, _, _) = await RowsAsync();
        const string workers = Hosting.DatabaseRoles.Workers;

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsRoleAsync(workers, """UPDATE "Endpoints" SET "Tier" = 'Managed' WHERE "Id" = $1""", endpoint.Id))?.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsRoleAsync(workers, """UPDATE "Endpoints" SET "ManagedSince" = now() WHERE "Id" = $1""", endpoint.Id))?.SqlState);

        // What the workers do with an endpoint stays allowed (EndpointHealthService marks it offline), and the tier service in web
        // still switches it.
        Assert.Null(await AsRoleAsync(workers, """UPDATE "Endpoints" SET "IsOnline" = false, "UpdatedAt" = now() WHERE "Id" = $1""", endpoint.Id));
        Assert.Null(await AsRoleAsync(Hosting.DatabaseRoles.Web,
            """UPDATE "Endpoints" SET "Tier" = 'Managed', "ManagedSince" = now() WHERE "Id" = $1""", endpoint.Id));
    }
}
