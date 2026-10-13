using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Database-side rules added during 0.1.0 integration: only the gateway role can request certificates, only workers
/// and the signer can request configurations, and deleting a check definition resolves its open alerts without
/// colliding in the deduplication index.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DatabaseRuleTests
{
    private readonly TestDatabase _db;

    public DatabaseRuleTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Theory]
    [InlineData("fleeto_web", "GatewayCertificate", false)]
    [InlineData("fleeto_workers", "GatewayCertificate", false)]
    [InlineData("fleeto_signer", "AgentEnrollment", false)]
    [InlineData("fleeto_workers", "AgentRenewal", false)]
    [InlineData("fleeto_gateway", "GatewayCertificate", true)]
    [InlineData("fleeto_gateway", "AgentConfig", false)]
    [InlineData("fleeto_web", "AgentConfig", false)]
    [InlineData("fleeto_workers", "AgentConfig", true)]
    [InlineData("fleeto_web", "AgentRecovery", false)]
    [InlineData("fleeto_signer", "AgentRecovery", false)]
    [InlineData("fleeto_gateway", "AgentRecovery", true)]
    [InlineData("fleeto_gateway", "SomethingNew", false)]
    [InlineData("fleeto_web", "Job", true)]
    [InlineData("fleeto_gateway", "Job", false)]
    [InlineData("fleeto_workers", "Job", false)]
    [InlineData("fleeto_workers", "SomethingNew", false)]
    [InlineData("fleeto_gateway", "WatchdogCertificate", true)]
    [InlineData("fleeto_web", "WatchdogCertificate", false)]
    [InlineData("fleeto_signer", "WatchdogCertificate", false)]
    [InlineData("fleeto_workers", "WatchdogCertificate", false)]
    [InlineData("fleeto_web", "AgentUninstall", true)]
    [InlineData("fleeto_gateway", "AgentUninstall", false)]
    [InlineData("fleeto_workers", "AgentUninstall", false)]
    [InlineData("fleeto_signer", "AgentUninstall", false)]
    public async Task Signing_request_kind_is_restricted_to_the_right_role(string role, string kind, bool allowed)
    {
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, "GRANT SELECT, INSERT ON \"SigningRequests\" TO fleeto_web, fleeto_gateway, fleeto_signer, fleeto_workers");
#pragma warning disable CA2100 // role names come from the fixed InlineData above
        await Execute(connection, $"SET LOCAL ROLE {role}");
#pragma warning restore CA2100

        await using var insert = new NpgsqlCommand(
            """INSERT INTO "SigningRequests" ("Id","Kind","Payload","RequestedBy","State","CreatedAt") VALUES (gen_random_uuid(), @kind, ''::bytea, 'test', 'Pending', now())""",
            connection, transaction);
        insert.Parameters.AddWithValue("kind", kind);

        if (allowed)
        {
            await insert.ExecuteNonQueryAsync();
        }
        else
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("fleeto_web", "Job")]
    [InlineData("fleeto_gateway", "WatchdogCertificate")]
    [InlineData("fleeto_workers", "AgentConfig")]
    public async Task Every_container_that_requests_a_signature_may_insert_it_with_the_production_grants(string role, string kind)
    {
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // The grants of fleeto-tool migrate, not a test grant: a missing INSERT here stopped every script run on a VPS.
        await Execute(connection, DatabaseGrants.BuildSql());
#pragma warning disable CA2100 // role names come from the fixed InlineData above
        await Execute(connection, $"SET LOCAL ROLE {role}");
#pragma warning restore CA2100

        await using var insert = new NpgsqlCommand(
            """INSERT INTO "SigningRequests" ("Id","Kind","Payload","RequestedBy","State","CreatedAt") VALUES (gen_random_uuid(), @kind, ''::bytea, 'test', 'Pending', now())""",
            connection, transaction);
        insert.Parameters.AddWithValue("kind", kind);
        await insert.ExecuteNonQueryAsync();

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Deleting_check_definitions_resolves_their_alerts_without_index_collisions()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed);
        var now = DateTime.UtcNow;
        await using var db = _db.DbFactory.CreateSystem();
        var template = new MonitoringTemplate { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Alerts " + Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        var first = new CheckDefinition { Id = Guid.NewGuid(), ClientId = client.Id, MonitoringTemplateId = template.Id, Name = "A", Type = CheckType.CpuUsage, WarningThreshold = 80, CreatedAt = now, UpdatedAt = now };
        var second = new CheckDefinition { Id = Guid.NewGuid(), ClientId = client.Id, MonitoringTemplateId = template.Id, Name = "B", Type = CheckType.MemoryUsage, WarningThreshold = 80, CreatedAt = now, UpdatedAt = now };
        template.Checks.Add(first);
        template.Checks.Add(second);
        db.MonitoringTemplates.Add(template);
        foreach (var definition in new[] { first, second })
        {
            db.Alerts.Add(new Alert
            {
                Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Kind = AlertKind.Check, CheckDefinitionId = definition.Id,
                Severity = AlertSeverity.Warning, Title = "t", OpenedAt = now, UpdatedAt = now
            });
        }

        await db.SaveChangesAsync();

        await db.CheckDefinitions.Where(c => c.MonitoringTemplateId == template.Id).ExecuteDeleteAsync();

        var alerts = await db.Alerts.AsNoTracking().Where(a => a.EndpointId == endpoint.Id).ToListAsync();
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a =>
        {
            Assert.Null(a.CheckDefinitionId);
            Assert.Equal(AlertState.Resolved, a.State);
            Assert.NotNull(a.ResolvedAt);
        });
    }

    [Fact]
    public async Task Storage_analysis_works_with_the_production_grants_of_each_container()
    {
        // 0.6.0: the gateway stores scans and delivers requests, web and the workers ask for scans, the workers apply retention.
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-GRANTS");
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        var ids = $"'{Guid.NewGuid()}', '{client.Id}', '{endpoint.Id}'";
#pragma warning disable CA2100 // test-only statements built from constants and generated ids
        await Execute(connection, "SET LOCAL ROLE fleeto_web");
        await Execute(connection, $"""
            INSERT INTO "StorageScanRequests" ("Id", "ClientId", "EndpointId", "Reason", "RequestedByName", "RequestedAt", "ExpiresAt")
            VALUES ({ids}, 'Technician', 'Tech', now(), now() + interval '1 hour')
            """);
        await Execute(connection, """SELECT count(*) FROM "StorageScans" """);
        await Execute(connection, "RESET ROLE; SET LOCAL ROLE fleeto_gateway");
        await Execute(connection, $"""
            SELECT count(*) FROM "StorageScans" WHERE "EndpointId" = '{endpoint.Id}';
            INSERT INTO "StorageScans" ("Id", "ClientId", "EndpointId", "AgentScanId", "Volume", "Filesystem", "TotalBytes", "FreeBytes",
              "ReceivedAt", "DurationMs", "Method", "Complete", "FileCount", "FolderCount", "FoldersJson", "FilesJson")
            VALUES ('{Guid.NewGuid()}', '{client.Id}', '{endpoint.Id}', '{Guid.NewGuid()}', 'C:', 'NTFS', 1, 1, now(), 1, 'Mft', true, 1, 1, '[]', '[]')
            ON CONFLICT ("EndpointId", "AgentScanId") DO NOTHING;
            UPDATE "StorageScanRequests" SET "DeliveredAt" = now() WHERE "EndpointId" = '{endpoint.Id}' AND "DeliveredAt" IS NULL;
            """);
        await Execute(connection, "RESET ROLE; SET LOCAL ROLE fleeto_workers");
        await Execute(connection, $"""
            INSERT INTO "StorageScanRequests" ("Id", "ClientId", "EndpointId", "Reason", "RequestedByName", "RequestedAt", "ExpiresAt")
            VALUES ('{Guid.NewGuid()}', '{client.Id}', '{endpoint.Id}', 'DiskFree', 'Fleeto', now(), now() + interval '12 hours');
            DELETE FROM "StorageScans" WHERE "EndpointId" = '{endpoint.Id}';
            DELETE FROM "StorageScanRequests" WHERE "EndpointId" = '{endpoint.Id}';
            """);
#pragma warning restore CA2100

        await transaction.RollbackAsync();
    }

    [Theory]
    // 0.6.0: a job without a user is only the automatic installation of the Action1 agent, and only the workers write it.
    [InlineData("fleeto_workers", "Action1Agent", true, true)]
    [InlineData("fleeto_workers", "Script", true, false)]
    [InlineData("fleeto_workers", "Action1AgentRemoval", true, false)]
    [InlineData("fleeto_workers", "Action1Agent", false, false)]
    [InlineData("fleeto_web", "Action1Agent", true, false)]
    [InlineData("fleeto_web", "Action1Agent", false, true)]
    [InlineData("fleeto_web", "Script", false, true)]
    public async Task Only_the_workers_write_a_job_without_a_user_and_only_to_install_the_Action1_agent(string role, string type, bool automatic,
        bool allowed)
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-JOBORIGIN");
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
#pragma warning disable CA2100 // role names come from the fixed InlineData above
        await Execute(connection, $"SET LOCAL ROLE {role}");
#pragma warning restore CA2100
        var jobId = Guid.NewGuid();
        await using var insert = InsertJob(connection, transaction, jobId, client.Id, endpoint.Id, type, automatic ? Guid.Empty : Guid.NewGuid());

        if (allowed)
        {
            await insert.ExecuteNonQueryAsync();
            // The one who wrote the job asks for its signature: the workers only for their own installation.
            await using var request = new NpgsqlCommand(
                """INSERT INTO "SigningRequests" ("Id","ClientId","Kind","SubjectId","Payload","RequestedBy","State","CreatedAt") VALUES (gen_random_uuid(), @client, 'Job', @job, ''::bytea, 'test', 'Pending', now())""",
                connection, transaction);
            request.Parameters.AddWithValue("client", client.Id);
            request.Parameters.AddWithValue("job", jobId);
            await request.ExecuteNonQueryAsync();
        }
        else
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task The_workers_cannot_ask_for_the_signature_of_a_job_a_technician_started()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-SIGNORIGIN");
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        var jobId = Guid.NewGuid();
        await using (var insert = InsertJob(connection, transaction, jobId, client.Id, endpoint.Id, "Action1Agent", Guid.NewGuid()))
        {
            await insert.ExecuteNonQueryAsync();
        }

        await Execute(connection, "SET LOCAL ROLE fleeto_workers");
        await using var request = new NpgsqlCommand(
            """INSERT INTO "SigningRequests" ("Id","ClientId","Kind","SubjectId","Payload","RequestedBy","State","CreatedAt") VALUES (gen_random_uuid(), @client, 'Job', @job, ''::bytea, 'test', 'Pending', now())""",
            connection, transaction);
        request.Parameters.AddWithValue("client", client.Id);
        request.Parameters.AddWithValue("job", jobId);
        var error = await Assert.ThrowsAsync<PostgresException>(() => request.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task The_gateway_records_the_answer_of_an_uninstall_and_cannot_change_what_was_signed()
    {
        // 0.6.0: an uninstall request keeps what web asked and the signer signed; the gateway only writes the answer.
        var id = Guid.NewGuid();
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.AgentUninstalls.Add(new AgentUninstall
            {
                Id = id, ClientId = Guid.NewGuid(), EndpointId = Guid.NewGuid(), Hostname = "WS-BOUND", Fingerprints = [new string('b', 64)],
                RequestedByUserId = Guid.NewGuid(), RequestedByName = "Tech", RequestedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30),
                Payload = [1], Signature = new byte[64], SigningKeyId = "k", SignedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        await Execute(connection, "SET LOCAL ROLE fleeto_gateway");
#pragma warning disable CA2100 // test-only statements built from a generated id
        await Execute(connection, $"""UPDATE "AgentUninstalls" SET "AcceptedAt" = now(), "AcceptedBy" = 'Agent' WHERE "Id" = '{id}'""");
        await using var tamper = new NpgsqlCommand($"""UPDATE "AgentUninstalls" SET "Fingerprints" = ARRAY['{new string('c', 64)}'] WHERE "Id" = '{id}'""",
            connection, transaction);
#pragma warning restore CA2100
        var error = await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        await transaction.RollbackAsync();
    }

    private static NpgsqlCommand InsertJob(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid clientId, Guid endpointId,
        string type, Guid initiator)
    {
        var insert = new NpgsqlCommand(
            """
            INSERT INTO "Jobs" ("Id","ClientId","EndpointId","BatchId","Type","ScriptName","ScriptVersionNumber","Language","ScriptSha256",
              "TimeoutSeconds","MaxOutputBytes","CreatedAt","ValidUntil","InitiatedByUserId","InitiatedByName","State","ReceivedOutputBytes",
              "OutputTruncated","OutputState")
            VALUES (@id, @client, @endpoint, gen_random_uuid(), @type, 'Install Action1 agent', 0, 'PowerShell', repeat('a', 64), 1200, 1024,
              now(), now() + interval '1 day', @initiator, 'Fleeto', 'PendingSignature', 0, false, 'None')
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("client", clientId);
        insert.Parameters.AddWithValue("endpoint", endpointId);
        insert.Parameters.AddWithValue("type", type);
        insert.Parameters.AddWithValue("initiator", initiator);
        return insert;
    }

    private async Task EnsureRolesAsync()
    {
        await using var command = _db.DataSource.CreateCommand(string.Join('\n', Hosting.DatabaseRoles.Application.Select(r =>
            $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{r}') THEN CREATE ROLE {r} NOLOGIN; END IF; END $$;")));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // test-only statements built from constants
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }
}
