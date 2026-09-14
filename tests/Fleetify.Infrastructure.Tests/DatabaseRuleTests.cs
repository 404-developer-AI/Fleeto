using Fleetify.Core.Entities;
using Fleetify.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleetify.Infrastructure.Tests;

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
    [InlineData("fleetify_web", "GatewayCertificate", false)]
    [InlineData("fleetify_workers", "GatewayCertificate", false)]
    [InlineData("fleetify_signer", "AgentEnrollment", false)]
    [InlineData("fleetify_workers", "AgentRenewal", false)]
    [InlineData("fleetify_gateway", "GatewayCertificate", true)]
    [InlineData("fleetify_gateway", "AgentConfig", false)]
    [InlineData("fleetify_web", "AgentConfig", false)]
    [InlineData("fleetify_workers", "AgentConfig", true)]
    public async Task Signing_request_kind_is_restricted_to_the_right_role(string role, string kind, bool allowed)
    {
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, "GRANT SELECT, INSERT ON \"SigningRequests\" TO fleetify_web, fleetify_gateway, fleetify_signer, fleetify_workers");
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
