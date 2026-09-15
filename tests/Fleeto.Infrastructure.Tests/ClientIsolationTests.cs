using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Cross-client isolation (0.1.0, CLAUDE.md Multi-tenancy discipline): a client-restricted caller cannot read or change
/// another client's rows, and the database itself refuses rows whose denormalized ClientId disagrees with the parent,
/// for required ClientIds (composite foreign keys) and nullable ones (constraint triggers).
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ClientIsolationTests
{
    private readonly TestDatabase _db;

    public ClientIsolationTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Fact]
    public async Task Restricted_scope_cannot_read_another_clients_sites_or_endpoints()
    {
        var clientA = await _db.CreateClientAsync();
        var clientB = await _db.CreateClientAsync();
        var siteB = await _db.CreateSiteAsync(clientB.Id);
        var endpointB = await _db.CreateEndpointAsync(siteB);

        await using var db = _db.DbFactory.Create(new RestrictedClientScope([clientA.Id]));

        Assert.Null(await db.Clients.SingleOrDefaultAsync(c => c.Id == clientB.Id));
        Assert.Null(await db.Sites.SingleOrDefaultAsync(s => s.Id == siteB.Id));
        Assert.Null(await db.Endpoints.SingleOrDefaultAsync(e => e.Id == endpointB.Id));
        Assert.False(await db.Endpoints.AnyAsync(e => e.ClientId == clientB.Id));
    }

    [Fact]
    public async Task Restricted_scope_sees_global_templates_but_not_other_clients_templates()
    {
        var clientA = await _db.CreateClientAsync();
        var clientB = await _db.CreateClientAsync();
        var now = DateTime.UtcNow;
        var templateB = new MonitoringTemplate { Id = Guid.NewGuid(), ClientId = clientB.Id, Name = "B only " + Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        await using (var system = _db.DbFactory.CreateSystem())
        {
            system.MonitoringTemplates.Add(templateB);
            await system.SaveChangesAsync();
        }

        await using var db = _db.DbFactory.Create(new RestrictedClientScope([clientA.Id]));

        Assert.True(await db.MonitoringTemplates.AnyAsync(t => t.ClientId == null));
        Assert.False(await db.MonitoringTemplates.AnyAsync(t => t.Id == templateB.Id));
    }

    [Fact]
    public async Task Restricted_scope_cannot_write_into_another_client()
    {
        var clientA = await _db.CreateClientAsync();
        var clientB = await _db.CreateClientAsync();
        var now = DateTime.UtcNow;

        await using var db = _db.DbFactory.Create(new RestrictedClientScope([clientA.Id]));
        db.Sites.Add(new Site { Id = Guid.NewGuid(), ClientId = clientB.Id, Name = "Sneaky", CreatedAt = now, UpdatedAt = now });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Restricted_scope_cannot_change_global_templates()
    {
        var clientA = await _db.CreateClientAsync();
        await using var db = _db.DbFactory.Create(new RestrictedClientScope([clientA.Id]));
        var global = await db.Policies.FirstAsync(p => p.ClientId == null);
        global.Name = "Changed by a restricted caller";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Endpoint_with_a_client_id_different_from_its_site_is_rejected_by_the_database()
    {
        var clientA = await _db.CreateClientAsync();
        var clientB = await _db.CreateClientAsync();
        var siteA = await _db.CreateSiteAsync(clientA.Id);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """
            INSERT INTO "Endpoints" ("Id","ClientId","SiteId","Hostname","DetectedClass","Tier","Source","OsPlatform","OsName","OsVersion",
              "Architecture","AgentVersion","IsOnline","EnrolledAt","ConfigVersion","AppliedConfigVersion","CreatedAt","UpdatedAt")
            VALUES (gen_random_uuid(), @clientB, @siteA, 'X', 'Workstation', 'AgentOnly', 'Agent', '', '', '', '', '', false, now(), 0, 0, now(), now())
            """,
            ("clientB", clientB.Id), ("siteA", siteA.Id)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task Check_definition_must_carry_the_client_id_of_its_template_null_included()
    {
        var client = await _db.CreateClientAsync();
        await using var system = _db.DbFactory.CreateSystem();
        var globalTemplate = await system.MonitoringTemplates.FirstAsync(t => t.ClientId == null);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """
            INSERT INTO "CheckDefinitions" ("Id","ClientId","MonitoringTemplateId","Name","Type","IntervalSeconds","ParametersJson",
              "FailuresBeforeAlert","AppliesTo","Enabled","CreatedAt","UpdatedAt")
            VALUES (gen_random_uuid(), @client, @template, 'Wrong', 'CpuUsage', 60, '{}', 1, 'All', true, now(), now())
            """,
            ("client", client.Id), ("template", globalTemplate.Id)));

        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, error.SqlState);
    }

    [Fact]
    public async Task Site_cannot_link_a_monitoring_template_of_another_client()
    {
        var clientA = await _db.CreateClientAsync();
        var clientB = await _db.CreateClientAsync();
        var siteA = await _db.CreateSiteAsync(clientA.Id);
        var now = DateTime.UtcNow;
        var templateB = new MonitoringTemplate { Id = Guid.NewGuid(), ClientId = clientB.Id, Name = "B " + Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        await using (var system = _db.DbFactory.CreateSystem())
        {
            system.MonitoringTemplates.Add(templateB);
            await system.SaveChangesAsync();
        }

        await using var db = _db.DbFactory.CreateSystem();
        db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate { SiteId = siteA.Id, ClientId = clientA.Id, MonitoringTemplateId = templateB.Id, CreatedAt = now });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("another client", error.InnerException?.Message);
    }

    [Fact]
    public async Task Client_id_of_an_endpoint_cannot_be_changed()
    {
        var clientA = await _db.CreateClientAsync();
        var clientB = await _db.CreateClientAsync();
        var siteA = await _db.CreateSiteAsync(clientA.Id);
        var endpoint = await _db.CreateEndpointAsync(siteA);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """UPDATE "Endpoints" SET "ClientId" = @clientB WHERE "Id" = @id""", ("clientB", clientB.Id), ("id", endpoint.Id)));

        Assert.Contains("cannot change", error.MessageText);
    }

    [Fact]
    public async Task Audit_entries_cannot_be_updated_or_deleted()
    {
        await _db.AuditLog.WriteAsync(new Core.Interfaces.AuditRecord("test.action", "Test", "1", null, AuditActorType.System, "test", "test"));

        var update = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("""UPDATE "AuditEntries" SET "Action" = 'tampered'"""));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("""DELETE FROM "AuditEntries" """));

        Assert.Contains("append-only", update.MessageText);
        Assert.Contains("append-only", delete.MessageText);
    }

    [Fact]
    public async Task Deleting_a_client_removes_its_sites_endpoints_and_certificates()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site);
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.AgentCertificates.Add(new AgentCertificate
            {
                Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Fingerprint = Guid.NewGuid().ToString("N") + "00000000000000000000000000000000"[..32],
                PublicKeyFingerprint = "x", SerialNumber = "1", IssuedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(90)
            });
            await db.SaveChangesAsync();
            db.Clients.Remove(await db.Clients.SingleAsync(c => c.Id == client.Id));
            await db.SaveChangesAsync();
        }

        await using var check = _db.DbFactory.CreateSystem();
        Assert.False(await check.Sites.AnyAsync(s => s.ClientId == client.Id));
        Assert.False(await check.Endpoints.AnyAsync(e => e.Id == endpoint.Id));
        Assert.False(await check.AgentCertificates.AnyAsync(c => c.EndpointId == endpoint.Id));
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = _db.DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
