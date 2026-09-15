using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Database rules and the shared resolution of per-endpoint checks: links, overrides and run requests can never reach
/// another client's data, a check has exactly one owner, deleting an endpoint takes everything of it along, and the C#
/// resolver and its SQL twin agree on which checks apply.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EndpointCheckRuleTests
{
    private readonly TestDatabase _db;

    public EndpointCheckRuleTests(DatabaseFixture fixture) => _db = fixture.Database;

    private static DateTime Now => DateTime.UtcNow;

    private async Task<(Client Client, Site Site, Endpoint Endpoint)> EndpointAsync(EndpointClass endpointClass = EndpointClass.Workstation)
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-RULES", endpointClass);
        return (client, site, endpoint);
    }

    private async Task<MonitoringTemplate> TemplateAsync(Guid? clientId, params CheckDefinition[] checks)
    {
        await using var db = _db.DbFactory.CreateSystem();
        var template = new MonitoringTemplate { Id = Guid.NewGuid(), ClientId = clientId, Name = "Rules " + Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
        foreach (var check in checks)
        {
            check.MonitoringTemplateId = template.Id;
            check.ClientId = clientId;
            template.Checks.Add(check);
        }

        db.MonitoringTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    private static CheckDefinition Check(string name, CheckAppliesTo appliesTo = CheckAppliesTo.All, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(), Name = name, Type = CheckType.CpuUsage, WarningThreshold = 80, AppliesTo = appliesTo, Enabled = enabled,
        CreatedAt = Now, UpdatedAt = Now
    };

    private async Task<PostgresException> ViolationAsync(params object[] rows)
    {
        await using var db = _db.DbFactory.CreateSystem();
        db.AddRange(rows);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        return Assert.IsType<PostgresException>(error.InnerException);
    }

    [Fact]
    public async Task An_endpoint_cannot_link_a_monitoring_template_of_another_client()
    {
        var (_, _, endpoint) = await EndpointAsync();
        var other = await _db.CreateClientAsync();
        var foreign = await TemplateAsync(other.Id, Check("Foreign"));

        var error = await ViolationAsync(new EndpointMonitoringTemplate
        {
            EndpointId = endpoint.Id, ClientId = endpoint.ClientId, MonitoringTemplateId = foreign.Id, CreatedAt = Now
        });
        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, error.SqlState);
    }

    [Fact]
    public async Task Overrides_and_run_requests_cannot_reference_checks_of_another_client_or_endpoint()
    {
        var (_, site, endpoint) = await EndpointAsync();
        var sibling = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-SIBLING");
        var other = await _db.CreateClientAsync();
        var foreignCheck = Check("Foreign");
        await TemplateAsync(other.Id, foreignCheck);
        var siblingCheck = new CheckDefinition
        {
            Id = Guid.NewGuid(), ClientId = sibling.ClientId, EndpointId = sibling.Id, Name = "Sibling only", Type = CheckType.Uptime,
            WarningThreshold = 30, CreatedAt = Now, UpdatedAt = Now
        };
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.CheckDefinitions.Add(siblingCheck);
            await db.SaveChangesAsync();
        }

        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, (await ViolationAsync(new EndpointCheckOverride
        {
            EndpointId = endpoint.Id, ClientId = endpoint.ClientId, CheckDefinitionId = foreignCheck.Id, Disabled = true, CreatedAt = Now, UpdatedAt = Now
        })).SqlState);
        // Overrides only adjust template checks.
        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, (await ViolationAsync(new EndpointCheckOverride
        {
            EndpointId = sibling.Id, ClientId = sibling.ClientId, CheckDefinitionId = siblingCheck.Id, Disabled = true, CreatedAt = Now, UpdatedAt = Now
        })).SqlState);
        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, (await ViolationAsync(RunRequest(endpoint, foreignCheck.Id))).SqlState);
        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, (await ViolationAsync(RunRequest(endpoint, siblingCheck.Id))).SqlState);
    }

    [Fact]
    public async Task A_check_has_exactly_one_owner_and_an_endpoint_check_carries_the_endpoint_client()
    {
        var (_, _, endpoint) = await EndpointAsync();
        var template = await TemplateAsync(null, Check("Owner"));
        var other = await _db.CreateClientAsync();

        Assert.Equal(PostgresErrorCodes.CheckViolation, (await ViolationAsync(new CheckDefinition
        {
            Id = Guid.NewGuid(), MonitoringTemplateId = template.Id, EndpointId = endpoint.Id, ClientId = endpoint.ClientId, Name = "Both",
            Type = CheckType.Uptime, CreatedAt = Now, UpdatedAt = Now
        })).SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await ViolationAsync(new CheckDefinition
        {
            Id = Guid.NewGuid(), Name = "None", Type = CheckType.Uptime, CreatedAt = Now, UpdatedAt = Now
        })).SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (await ViolationAsync(new CheckDefinition
        {
            Id = Guid.NewGuid(), EndpointId = endpoint.Id, ClientId = other.Id, Name = "Wrong client", Type = CheckType.Uptime, CreatedAt = Now,
            UpdatedAt = Now
        })).SqlState);
    }

    [Fact]
    public async Task Deleting_an_endpoint_removes_its_checks_overrides_links_requests_and_notes()
    {
        var (client, site, endpoint) = await EndpointAsync();
        var siteCheck = Check("Site check");
        var template = await TemplateAsync(null, siteCheck);
        var own = new CheckDefinition
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Name = "Own", Type = CheckType.Uptime, WarningThreshold = 30,
            CreatedAt = Now, UpdatedAt = Now
        };
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate { SiteId = site.Id, ClientId = client.Id, MonitoringTemplateId = template.Id, CreatedAt = Now });
            db.CheckDefinitions.Add(own);
            db.EndpointMonitoringTemplates.Add(new EndpointMonitoringTemplate { EndpointId = endpoint.Id, ClientId = client.Id, MonitoringTemplateId = template.Id, CreatedAt = Now });
            db.EndpointCheckOverrides.Add(new EndpointCheckOverride { EndpointId = endpoint.Id, ClientId = client.Id, CheckDefinitionId = siteCheck.Id, IntervalSeconds = 60, CreatedAt = Now, UpdatedAt = Now });
            db.CheckRunRequests.Add(RunRequest(endpoint, own.Id));
            db.Notes.Add(new Note { Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, AuthorUserId = Guid.NewGuid(), AuthorName = "Tech", Body = "Replaced the disk.", CreatedAt = Now, UpdatedAt = Now });
            db.CheckStates.Add(new CheckState { EndpointId = endpoint.Id, CheckDefinitionId = own.Id, ClientId = client.Id, Status = CheckStatus.Critical, LastResultAt = Now, UpdatedAt = Now });
            db.Alerts.Add(new Alert { Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Kind = AlertKind.Check, CheckDefinitionId = own.Id, Severity = AlertSeverity.Critical, Title = "t", OpenedAt = Now, UpdatedAt = Now });
            await db.SaveChangesAsync();
        }

        await using (var db = _db.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteDeleteAsync();
        }

        await using var check = _db.DbFactory.CreateSystem();
        Assert.False(await check.CheckDefinitions.AnyAsync(d => d.Id == own.Id));
        Assert.True(await check.CheckDefinitions.AnyAsync(d => d.Id == siteCheck.Id));
        Assert.False(await check.EndpointMonitoringTemplates.AnyAsync(l => l.EndpointId == endpoint.Id));
        Assert.False(await check.EndpointCheckOverrides.AnyAsync(o => o.EndpointId == endpoint.Id));
        Assert.False(await check.CheckRunRequests.AnyAsync(r => r.EndpointId == endpoint.Id));
        Assert.False(await check.Notes.AnyAsync(n => n.EndpointId == endpoint.Id));
        Assert.False(await check.Alerts.AnyAsync(a => a.EndpointId == endpoint.Id));
    }

    [Fact]
    public async Task Deleting_a_client_removes_its_own_templates_and_policies()
    {
        var client = await _db.CreateClientAsync();
        var template = await TemplateAsync(client.Id, Check("Client check"));
        var policyId = Guid.NewGuid();
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.Policies.Add(new Policy { Id = policyId, ClientId = client.Id, Name = "Client policy " + policyId, CreatedAt = Now, UpdatedAt = Now });
            await db.SaveChangesAsync();
            await db.Clients.Where(c => c.Id == client.Id).ExecuteDeleteAsync();
        }

        await using var check = _db.DbFactory.CreateSystem();
        Assert.False(await check.MonitoringTemplates.AnyAsync(t => t.Id == template.Id));
        Assert.False(await check.Policies.AnyAsync(p => p.Id == policyId));
    }

    [Fact]
    public async Task The_resolver_and_its_sql_twin_agree()
    {
        var (client, site, endpoint) = await EndpointAsync(EndpointClass.Workstation);
        var siteAll = Check("site all");
        var siteServer = Check("site server only", CheckAppliesTo.Server);
        var siteWorkstation = Check("site workstation only", CheckAppliesTo.Workstation);
        var siteDisabled = Check("site disabled", enabled: false);
        var siteDisabledOnEndpoint = Check("site disabled on endpoint");
        var siteOverridden = Check("site overridden");
        var siteTemplate = await TemplateAsync(null, siteAll, siteServer, siteWorkstation, siteDisabled, siteDisabledOnEndpoint, siteOverridden);
        var linkedCheck = Check("endpoint link");
        var linkedTemplate = await TemplateAsync(client.Id, linkedCheck);
        var unlinkedCheck = Check("unlinked");
        await TemplateAsync(null, unlinkedCheck);
        var own = new CheckDefinition
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Name = "own", Type = CheckType.Uptime, WarningThreshold = 30,
            AppliesTo = CheckAppliesTo.Server, CreatedAt = Now, UpdatedAt = Now
        };
        var ownDisabled = new CheckDefinition
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Name = "own disabled", Type = CheckType.Uptime, Enabled = false,
            WarningThreshold = 30, CreatedAt = Now, UpdatedAt = Now
        };

        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate { SiteId = site.Id, ClientId = client.Id, MonitoringTemplateId = siteTemplate.Id, CreatedAt = Now });
            db.EndpointMonitoringTemplates.Add(new EndpointMonitoringTemplate { EndpointId = endpoint.Id, ClientId = client.Id, MonitoringTemplateId = linkedTemplate.Id, CreatedAt = Now });
            db.CheckDefinitions.AddRange(own, ownDisabled);
            db.EndpointCheckOverrides.Add(new EndpointCheckOverride { EndpointId = endpoint.Id, ClientId = client.Id, CheckDefinitionId = siteDisabledOnEndpoint.Id, Disabled = true, CreatedAt = Now, UpdatedAt = Now });
            db.EndpointCheckOverrides.Add(new EndpointCheckOverride { EndpointId = endpoint.Id, ClientId = client.Id, CheckDefinitionId = siteOverridden.Id, IntervalSeconds = 45, OverrideThresholds = true, WarningThreshold = null, CriticalThreshold = 99, CreatedAt = Now, UpdatedAt = Now });
            await db.SaveChangesAsync();
        }

        await using var context = _db.DbFactory.CreateSystem();
        var resolved = await EffectiveCheckResolver.LoadAsync(context, endpoint, includeDisabledOnEndpoint: false, CancellationToken.None);
        var expected = new[] { siteAll.Id, siteWorkstation.Id, siteOverridden.Id, linkedCheck.Id, own.Id }.OrderBy(id => id).ToList();
        Assert.Equal(expected, resolved.Select(c => c.Id).ToList());

        var overridden = resolved.Single(c => c.Id == siteOverridden.Id);
        Assert.Equal(45, overridden.IntervalSeconds);
        Assert.Null(overridden.WarningThreshold);
        Assert.Equal(99, overridden.CriticalThreshold);
        Assert.Equal(CheckSource.EndpointTemplate, resolved.Single(c => c.Id == linkedCheck.Id).Source);
        Assert.Equal(CheckSource.Endpoint, resolved.Single(c => c.Id == own.Id).Source);

        var withDisabled = await EffectiveCheckResolver.LoadAsync(context, endpoint, includeDisabledOnEndpoint: true, CancellationToken.None);
        Assert.True(withDisabled.Single(c => c.Id == siteDisabledOnEndpoint.Id).DisabledOnEndpoint);

        var all = new[] { siteAll, siteServer, siteWorkstation, siteDisabled, siteDisabledOnEndpoint, siteOverridden, linkedCheck, unlinkedCheck, own, ownDisabled };
        foreach (var definition in all)
        {
            var applies = await context.Database.SqlQueryRaw<bool>(
                    """SELECT EXISTS (SELECT 1 FROM "CheckDefinitions" d JOIN "Endpoints" e ON e."Id" = @endpointId WHERE d."Id" = @checkId AND """ +
                    EffectiveCheckResolver.AppliesSql + """) AS "Value" """,
                    new NpgsqlParameter("endpointId", endpoint.Id), new NpgsqlParameter("checkId", definition.Id))
                .SingleAsync();
            Assert.True(applies == resolved.Any(c => c.Id == definition.Id), $"SQL and C# disagree on check '{definition.Name}'");
        }
    }

    [Fact]
    public async Task Each_role_can_run_its_own_statements_on_the_new_tables_with_the_real_grants()
    {
        var (client, _, endpoint) = await EndpointAsync();
        var own = new CheckDefinition
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Name = "Own", Type = CheckType.Uptime, WarningThreshold = 30,
            CreatedAt = Now, UpdatedAt = Now
        };
        var siteCheck = Check("Template check");
        await TemplateAsync(null, siteCheck);
        var request = RunRequest(endpoint, own.Id);
        request.Reset = true;
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.CheckDefinitions.Add(own);
            db.CheckRunRequests.Add(request);
            await db.SaveChangesAsync();
        }

        await using (var roles = _db.DataSource.CreateCommand(string.Join('\n', Hosting.DatabaseRoles.Application.Select(r =>
                         $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{r}') THEN CREATE ROLE {r} NOLOGIN; END IF; END $$;"))))
        {
            await roles.ExecuteNonQueryAsync();
        }

        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, DatabaseGrants.BuildSql());

        // Web: request a run, adjust a template check, write a note.
        await ExecuteAsync(connection, "SET LOCAL ROLE fleeto_web");
        await ExecuteAsync(connection, """
            INSERT INTO "CheckRunRequests" ("Id","ClientId","EndpointId","CheckDefinitionId","Reset","RequestedByUserId","RequestedByName","RequestedAt","ExpiresAt")
            VALUES (gen_random_uuid(), @client, @endpoint, @check, false, gen_random_uuid(), 'Tech', now(), now() + interval '10 minutes')
            """, ("client", client.Id), ("endpoint", endpoint.Id), ("check", own.Id));
        await ExecuteAsync(connection, """
            INSERT INTO "EndpointCheckOverrides" ("EndpointId","CheckDefinitionId","ClientId","Disabled","OverrideThresholds","CreatedAt","UpdatedAt")
            VALUES (@endpoint, @check, @client, true, false, now(), now())
            """, ("client", client.Id), ("endpoint", endpoint.Id), ("check", siteCheck.Id));
        await ExecuteAsync(connection, """
            INSERT INTO "Notes" ("Id","ClientId","EndpointId","AuthorUserId","AuthorName","Body","CreatedAt","UpdatedAt")
            VALUES (gen_random_uuid(), @client, @endpoint, gen_random_uuid(), 'Tech', 'Checked the fans.', now(), now())
            """, ("client", client.Id), ("endpoint", endpoint.Id));

        // Workers: apply the reset.
        await ExecuteAsync(connection, "RESET ROLE; SET LOCAL ROLE fleeto_workers");
        await ExecuteAsync(connection, """UPDATE "CheckRunRequests" SET "ResetAppliedAt" = now() WHERE "Id" = @id""", ("id", request.Id));

        // Gateway: store the public address and mark the request delivered.
        await ExecuteAsync(connection, "RESET ROLE; SET LOCAL ROLE fleeto_gateway");
        await ExecuteAsync(connection, """UPDATE "Endpoints" SET "PublicIpAddress" = '203.0.113.1', "PublicIpSeenAt" = now() WHERE "Id" = @id""", ("id", endpoint.Id));
        await ExecuteAsync(connection, """UPDATE "CheckRunRequests" SET "DeliveredAt" = now() WHERE "Id" = @id AND "DeliveredAt" IS NULL""", ("id", request.Id));

        // The gateway cannot read notes.
        var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, """SELECT count(*) FROM "Notes" """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);

        await transaction.RollbackAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
#pragma warning disable CA2100 // test-only statements built from constants
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static CheckRunRequest RunRequest(Endpoint endpoint, Guid checkId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = endpoint.ClientId,
        EndpointId = endpoint.Id,
        CheckDefinitionId = checkId,
        RequestedByUserId = Guid.NewGuid(),
        RequestedByName = "Tech",
        RequestedAt = Now,
        ExpiresAt = Now.AddMinutes(10)
    };
}
