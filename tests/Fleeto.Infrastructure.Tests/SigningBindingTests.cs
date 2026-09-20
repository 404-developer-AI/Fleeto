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
    private async Task<PostgresException?> AsGatewayAsync(string sql, params object[] values)
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

        await using (var role = new NpgsqlCommand("SET LOCAL ROLE fleeto_gateway", connection, transaction))
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
}
