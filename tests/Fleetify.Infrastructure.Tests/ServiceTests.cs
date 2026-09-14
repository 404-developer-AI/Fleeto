using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Security;
using Fleetify.Infrastructure.Services;
using Fleetify.Testing;
using Tier = Fleetify.Protocol.Agent.V1.Tier;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Infrastructure.Tests;

/// <summary>
/// Shared services (0.1.0): serialized license allocation that never overspends the pool, envelope encryption bound to
/// purpose and row, client template sync that keeps manual links, configurations that give agent-only endpoints no
/// checks, and grants that cover every table.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ServiceTests
{
    private readonly TestDatabase _db;

    public ServiceTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Fact]
    public async Task Concurrent_switches_cannot_both_take_the_last_license()
    {
        // The license pool is instance-wide, so this test owns the only managed endpoints it creates and sizes the pool
        // to what is already managed plus one.
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var first = await _db.CreateEndpointAsync(site, hostname: "WS-A");
        var second = await _db.CreateEndpointAsync(site, hostname: "WS-B");
        await using (var db = _db.DbFactory.CreateSystem())
        {
            var managed = await db.Endpoints.CountAsync(e => e.Tier == EndpointTier.Managed);
            await _db.LoadTestLicenseAsync(managed + 1);
        }

        var service = new EndpointTierService(_db.DbFactory, _db.Licenses, _db.Bus, _db.Time);
        var actor = new Actor(Guid.NewGuid(), "tester", SystemClientScope.Instance);

        var results = await Task.WhenAll(
            Task.Run(() => service.SetTierAsync([first.Id], EndpointTier.Managed, actor)),
            Task.Run(() => service.SetTierAsync([second.Id], EndpointTier.Managed, actor)));

        Assert.Single(results, r => r.Success);
        var refused = Assert.Single(results, r => !r.Success);
        Assert.Contains("Not enough licenses", refused.Problem);
        await using var check = _db.DbFactory.CreateSystem();
        Assert.Equal(1, await check.Endpoints.CountAsync(e => (e.Id == first.Id || e.Id == second.Id) && e.Tier == EndpointTier.Managed));
    }

    [Fact]
    public async Task Bulk_switch_is_all_or_nothing()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoints = new[]
        {
            await _db.CreateEndpointAsync(site, hostname: "BULK-1"),
            await _db.CreateEndpointAsync(site, hostname: "BULK-2"),
            await _db.CreateEndpointAsync(site, hostname: "BULK-3")
        };
        await using (var db = _db.DbFactory.CreateSystem())
        {
            await _db.LoadTestLicenseAsync(await db.Endpoints.CountAsync(e => e.Tier == EndpointTier.Managed) + 2);
        }

        var service = new EndpointTierService(_db.DbFactory, _db.Licenses, _db.Bus, _db.Time);
        var result = await service.SetTierAsync(endpoints.Select(e => e.Id).ToList(), EndpointTier.Managed,
            new Actor(Guid.NewGuid(), "tester", SystemClientScope.Instance));

        Assert.False(result.Success);
        Assert.Contains("Add 1 license", result.Problem);
        await using var check = _db.DbFactory.CreateSystem();
        Assert.False(await check.Endpoints.AnyAsync(e => endpoints.Select(x => x.Id).Contains(e.Id) && e.Tier == EndpointTier.Managed));
    }

    [Fact]
    public async Task Switch_to_managed_is_refused_without_a_license_and_audited_when_allowed()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, hostname: "AUDIT-1");
        var service = new EndpointTierService(_db.DbFactory, _db.Licenses, _db.Bus, _db.Time);
        var actor = new Actor(Guid.NewGuid(), "tester", SystemClientScope.Instance);

        await using (var db = _db.DbFactory.CreateSystem())
        {
            await db.Licenses.Where(l => l.IsActive).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, false));
        }

        var refused = await service.SetTierAsync([endpoint.Id], EndpointTier.Managed, actor);
        Assert.False(refused.Success);
        Assert.Contains("No license is loaded", refused.Problem);

        await using (var db = _db.DbFactory.CreateSystem())
        {
            await _db.LoadTestLicenseAsync(await db.Endpoints.CountAsync(e => e.Tier == EndpointTier.Managed) + 1);
        }

        var allowed = await service.SetTierAsync([endpoint.Id], EndpointTier.Managed, actor);
        Assert.True(allowed.Success);
        await using var check = _db.DbFactory.CreateSystem();
        Assert.True(await check.AuditEntries.AnyAsync(a => a.Action == AuditActions.EndpointTierChanged && a.TargetId == endpoint.Id.ToString()));
        Assert.True(await check.ConfigChangeEvents.AnyAsync(c => c.Scope == ConfigChangeScope.Endpoint && c.ScopeId == endpoint.Id));
    }

    [Fact]
    public async Task Restricted_caller_cannot_switch_another_clients_endpoint()
    {
        var clientA = await _db.CreateClientAsync();
        var clientB = await _db.CreateClientAsync();
        var siteB = await _db.CreateSiteAsync(clientB.Id);
        var endpointB = await _db.CreateEndpointAsync(siteB);
        var service = new EndpointTierService(_db.DbFactory, _db.Licenses, _db.Bus, _db.Time);

        var result = await service.SetTierAsync([endpointB.Id], EndpointTier.Managed,
            new Actor(Guid.NewGuid(), "tester", new RestrictedClientScope([clientA.Id])));

        Assert.False(result.Success);
    }

    [Fact]
    public void Secret_ciphertext_is_bound_to_purpose_and_associated_data()
    {
        var ciphertext = _db.SecretProtector.Protect(SecretPurposes.Settings, "smtp password", "Settings|email.smtp");

        Assert.Equal("smtp password", _db.SecretProtector.Unprotect(SecretPurposes.Settings, ciphertext, "Settings|email.smtp"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            _db.SecretProtector.Unprotect(SecretPurposes.Settings, ciphertext, "Settings|backup.settings"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            _db.SecretProtector.Unprotect(SecretPurposes.License, ciphertext, "Settings|email.smtp"));
    }

    [Fact]
    public void Another_root_key_cannot_decrypt_the_data_keys()
    {
        var ciphertext = _db.SecretProtector.Protect(SecretPurposes.Identity, "totp seed", "AuthenticatorKey|1");
        var wrongRoot = new EnvelopeSecretProtector(new RootKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), _db.DbFactory);

        var error = Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            wrongRoot.Unprotect(SecretPurposes.Identity, ciphertext, "AuthenticatorKey|1"));
        Assert.Contains("root key", error.Message);
    }

    [Fact]
    public async Task Client_template_creates_sites_and_links_and_keeps_manual_links_on_change()
    {
        var now = DateTime.UtcNow;
        await using var db = _db.DbFactory.CreateSystem();
        var monitoring = await db.MonitoringTemplates.FirstAsync(t => t.ClientId == null);
        var extra = new MonitoringTemplate { Id = Guid.NewGuid(), Name = "Extra " + Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        var template = new ClientTemplate { Id = Guid.NewGuid(), Name = "Standard " + Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        var monitoringSite = new ClientTemplateSite { Id = Guid.NewGuid(), ClientTemplateId = template.Id, Name = "Monitoring", SortOrder = 1 };
        monitoringSite.MonitoringTemplates.Add(new ClientTemplateSiteMonitoringTemplate { ClientTemplateSiteId = monitoringSite.Id, MonitoringTemplateId = monitoring.Id });
        template.Sites.Add(monitoringSite);
        template.Sites.Add(new ClientTemplateSite { Id = Guid.NewGuid(), ClientTemplateId = template.Id, Name = "Agent Only", SortOrder = 2 });
        db.MonitoringTemplates.Add(extra);
        db.ClientTemplates.Add(template);
        var client = new Client { Id = Guid.NewGuid(), Code = "TPL" + Random.Shared.Next(10000, 99999), Name = "Templated", ClientTemplateId = template.Id, CreatedAt = now, UpdatedAt = now };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        await ClientTemplateSync.ApplyAsync(db, client, now);
        await db.SaveChangesAsync();

        var sites = await db.Sites.Include(s => s.MonitoringTemplates).Where(s => s.ClientId == client.Id).ToListAsync();
        Assert.Equal(["Agent Only", "Monitoring"], sites.Select(s => s.Name).Order());
        var created = sites.Single(s => s.Name == "Monitoring");
        Assert.Contains(created.MonitoringTemplates, l => l.MonitoringTemplateId == monitoring.Id && l.Source == LinkSource.ClientTemplate);

        // A technician adds a manual link; the template later drops its monitoring template.
        db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate { SiteId = created.Id, ClientId = client.Id, MonitoringTemplateId = extra.Id, Source = LinkSource.Manual, CreatedAt = now });
        await db.SaveChangesAsync();
        await db.ClientTemplateSiteMonitoringTemplates.Where(l => l.ClientTemplateSiteId == monitoringSite.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        var reloaded = await db.Clients.SingleAsync(c => c.Id == client.Id);
        await ClientTemplateSync.ApplyAsync(db, reloaded, now);
        await db.SaveChangesAsync();

        var links = await db.SiteMonitoringTemplates.Where(l => l.SiteId == created.Id).ToListAsync();
        Assert.DoesNotContain(links, l => l.MonitoringTemplateId == monitoring.Id);
        Assert.Contains(links, l => l.MonitoringTemplateId == extra.Id && l.Source == LinkSource.Manual);
    }

    [Fact]
    public async Task Agent_only_endpoint_gets_a_configuration_without_checks()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var agentOnly = await _db.CreateEndpointAsync(site, EndpointTier.AgentOnly, "AO-1");
        var managed = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "MG-1");
        await using var db = _db.DbFactory.CreateSystem();
        var template = await db.MonitoringTemplates.Include(t => t.Checks).FirstAsync(t => t.ClientId == null && t.Checks.Count > 0);
        db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate { SiteId = site.Id, ClientId = client.Id, MonitoringTemplateId = template.Id, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await _db.LoadTestLicenseAsync(100_000);

        var builder = new AgentConfigBuilder(_db.Licenses);
        var agentOnlyConfig = (await builder.BuildAsync(db, agentOnly.Id, _db.InstanceId))!;
        var managedConfig = (await builder.BuildAsync(db, managed.Id, _db.InstanceId))!;

        Assert.Equal(Tier.AgentOnly, agentOnlyConfig.Config.Tier);
        Assert.Empty(agentOnlyConfig.Config.Checks);
        Assert.Equal(Tier.Managed, managedConfig.Config.Tier);
        Assert.Equal(template.Checks.Count(c => c.Enabled), managedConfig.Config.Checks.Count);
        Assert.Equal(managedConfig.ContentHash, (await builder.BuildAsync(db, managed.Id, _db.InstanceId))!.ContentHash);
        Assert.NotEqual(agentOnlyConfig.ContentHash, managedConfig.ContentHash);
    }

    [Fact]
    public async Task Server_only_checks_are_left_out_for_workstations()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var workstation = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-CLS", EndpointClass.Workstation);
        var now = DateTime.UtcNow;
        await using var db = _db.DbFactory.CreateSystem();
        var template = new MonitoringTemplate { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Class " + Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        template.Checks.Add(new CheckDefinition { Id = Guid.NewGuid(), ClientId = client.Id, MonitoringTemplateId = template.Id, Name = "Spooler", Type = CheckType.ServiceRunning, ParametersJson = """{"service":"Spooler"}""", AppliesTo = CheckAppliesTo.Server, CreatedAt = now, UpdatedAt = now });
        template.Checks.Add(new CheckDefinition { Id = Guid.NewGuid(), ClientId = client.Id, MonitoringTemplateId = template.Id, Name = "Memory", Type = CheckType.MemoryUsage, WarningThreshold = 90, AppliesTo = CheckAppliesTo.All, CreatedAt = now, UpdatedAt = now });
        db.MonitoringTemplates.Add(template);
        db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate { SiteId = site.Id, ClientId = client.Id, MonitoringTemplateId = template.Id, CreatedAt = now });
        await db.SaveChangesAsync();
        await _db.LoadTestLicenseAsync(100_000);

        var built = (await new AgentConfigBuilder(_db.Licenses).BuildAsync(db, workstation.Id, _db.InstanceId))!;

        var spec = Assert.Single(built.Config.Checks);
        Assert.Equal(Protocol.Agent.V1.CheckType.MemoryUsage, spec.Type);
    }

    [Fact]
    public async Task Every_table_has_an_explicit_grant_entry()
    {
        await using var db = _db.DbFactory.CreateSystem();
        var modelTables = db.Model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().Distinct().ToList();

        var missing = modelTables.Where(t => !DatabaseGrants.Tables.ContainsKey(t)).ToList();
        var stale = DatabaseGrants.Tables.Keys.Where(t => !modelTables.Contains(t)).ToList();

        Assert.True(missing.Count == 0, "Tables without a DatabaseGrants entry: " + string.Join(", ", missing));
        Assert.True(stale.Count == 0, "DatabaseGrants entries without a table: " + string.Join(", ", stale));
    }

    [Fact]
    public async Task Only_the_signer_role_can_read_encrypted_key_material()
    {
        foreach (var table in new[] { "InstanceSigningKeys", "CertificateAuthorities" })
        {
            var grants = DatabaseGrants.Tables[table];
            Assert.Equal([Hosting.DatabaseRoles.Signer], grants.Keys);
            Assert.DoesNotContain(DatabaseGrants.ColumnGrants, g => g.Table == table && g.Columns.Contains("EncryptedPrivateKey"));
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Grant_script_applies_cleanly_when_the_roles_exist()
    {
        await using (var command = _db.DataSource.CreateCommand(string.Join('\n', Hosting.DatabaseRoles.Application.Select(r =>
                         $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{r}') THEN CREATE ROLE {r} NOLOGIN; END IF; END $$;"))))
        {
            await command.ExecuteNonQueryAsync();
        }

        await using var db = _db.DbFactory.CreateSystem();
        await db.Database.ExecuteSqlRawAsync(DatabaseGrants.BuildSql());

        await using var check = _db.DataSource.CreateCommand(
            """SELECT has_column_privilege('fleetify_web', '"InstanceSigningKeys"', 'EncryptedPrivateKey', 'SELECT'), has_column_privilege('fleetify_web', '"InstanceSigningKeys"', 'PublicKey', 'SELECT'), has_table_privilege('fleetify_gateway', '"Clients"', 'SELECT')""");
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
    }
}
