using System.Text.Json.Nodes;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Policies, patch policies and monitoring templates on client, site and endpoint level (0.6.0): the most specific link
/// wins in C#, SQL and EF Core alike, monitoring templates add up, and a link never reaches another client's data.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PolicyLevelRuleTests
{
    private readonly TestDatabase _db;

    public PolicyLevelRuleTests(DatabaseFixture fixture) => _db = fixture.Database;

    private static DateTime Now => DateTime.UtcNow;

    private async Task<Policy> PolicyAsync(Guid? clientId = null)
    {
        await using var db = _db.DbFactory.CreateSystem();
        var policy = new Policy { Id = Guid.NewGuid(), ClientId = clientId, Name = "Level " + Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
        db.Policies.Add(policy);
        await db.SaveChangesAsync();
        return policy;
    }

    private async Task<PatchPolicy> PatchPolicyAsync(Guid? clientId = null)
    {
        await using var db = _db.DbFactory.CreateSystem();
        var policy = new PatchPolicy { Id = Guid.NewGuid(), ClientId = clientId, Name = "Patch " + Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
        db.PatchPolicies.Add(policy);
        await db.SaveChangesAsync();
        return policy;
    }

    private async Task AddAsync(params object[] rows)
    {
        await using var db = _db.DbFactory.CreateSystem();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<PostgresException> ViolationAsync(params object[] rows)
    {
        await using var db = _db.DbFactory.CreateSystem();
        db.AddRange(rows);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        return Assert.IsType<PostgresException>(error.InnerException);
    }

    private async Task<(Guid? Ef, Guid? Sql, Guid? PatchEf, Guid? PatchSql)> ResolveAsync(Guid endpointId)
    {
        await using var db = _db.DbFactory.CreateSystem();
        var row = await EffectivePolicies.Query(db).SingleAsync(r => r.EndpointId == endpointId);
        var sql = await db.Database.SqlQueryRaw<Guid?>(
                """SELECT """ + EffectivePolicyRules.PolicyIdSql + """ AS "Value" FROM "Endpoints" e WHERE e."Id" = @id""",
                new NpgsqlParameter("id", endpointId))
            .SingleAsync();
        var patchSql = await db.Database.SqlQueryRaw<Guid?>(
                """SELECT """ + EffectivePolicyRules.PatchPolicyIdSql + """ AS "Value" FROM "Endpoints" e WHERE e."Id" = @id""",
                new NpgsqlParameter("id", endpointId))
            .SingleAsync();
        return (row.PolicyId, sql, row.PatchPolicyId, patchSql);
    }

    [Fact]
    public async Task The_endpoint_wins_over_the_site_and_the_site_over_the_client_in_every_form_of_the_rule()
    {
        var client = await _db.CreateClientAsync();
        var siteA = await _db.CreateSiteAsync(client.Id, "Servers");
        var siteB = await _db.CreateSiteAsync(client.Id, "Workstations");
        var onClient = await _db.CreateEndpointAsync(siteB, EndpointTier.Managed, "WS-CLIENT");
        var onSite = await _db.CreateEndpointAsync(siteA, EndpointTier.Managed, "SRV-SITE");
        var onEndpoint = await _db.CreateEndpointAsync(siteA, EndpointTier.Managed, "SRV-OWN");
        var bare = await _db.CreateClientAsync();
        var bareEndpoint = await _db.CreateEndpointAsync(await _db.CreateSiteAsync(bare.Id), EndpointTier.Managed, "WS-BARE");

        var (clientPolicy, sitePolicy, endpointPolicy) = (await PolicyAsync(), await PolicyAsync(client.Id), await PolicyAsync());
        var (clientPatch, sitePatch, endpointPatch) = (await PatchPolicyAsync(client.Id), await PatchPolicyAsync(), await PatchPolicyAsync());
        await AddAsync(
            new ClientPolicy { ClientId = client.Id, PolicyId = clientPolicy.Id, CreatedAt = Now },
            new SitePolicy { SiteId = siteA.Id, ClientId = client.Id, PolicyId = sitePolicy.Id, CreatedAt = Now },
            new EndpointPolicy { EndpointId = onEndpoint.Id, ClientId = client.Id, PolicyId = endpointPolicy.Id, CreatedAt = Now },
            new ClientPatchPolicy { ClientId = client.Id, PatchPolicyId = clientPatch.Id, CreatedAt = Now },
            new SitePatchPolicy { SiteId = siteA.Id, ClientId = client.Id, PatchPolicyId = sitePatch.Id, CreatedAt = Now },
            new EndpointPatchPolicy { EndpointId = onEndpoint.Id, ClientId = client.Id, PatchPolicyId = endpointPatch.Id, CreatedAt = Now });

        Guid defaultPolicy;
        await using (var db = _db.DbFactory.CreateSystem())
        {
            defaultPolicy = await db.Policies.Where(p => p.IsDefault).Select(p => p.Id).SingleAsync();
        }

        var cases = new (Endpoint Endpoint, Guid Policy, Guid? Patch)[]
        {
            (onClient, clientPolicy.Id, clientPatch.Id),
            (onSite, sitePolicy.Id, sitePatch.Id),
            (onEndpoint, endpointPolicy.Id, endpointPatch.Id),
            (bareEndpoint, defaultPolicy, null)
        };
        foreach (var (endpoint, policy, patch) in cases)
        {
            var resolved = await ResolveAsync(endpoint.Id);
            Assert.Equal(policy, resolved.Ef);
            Assert.Equal(policy, resolved.Sql);
            Assert.Equal(patch, resolved.PatchEf);
            Assert.Equal(patch, resolved.PatchSql);
        }

        Assert.Equal(endpointPolicy.Id, EffectivePolicyRules.Resolve(endpointPolicy.Id, sitePolicy.Id, clientPolicy.Id, defaultPolicy));
        Assert.Equal(clientPolicy.Id, EffectivePolicyRules.Resolve(null, null, clientPolicy.Id, defaultPolicy));
        Assert.Equal(LinkLevel.Site, EffectivePolicyRules.Level(null, sitePolicy.Id, clientPolicy.Id));
        Assert.Equal(LinkLevel.None, EffectivePolicyRules.Level(null, null, null));

        await using (var db = _db.DbFactory.CreateSystem())
        {
            Assert.Equal(endpointPolicy.Id, (await EffectivePolicies.LoadAsync(db, onEndpoint.Id))!.Id);
            var sites = await EffectivePolicies.Sites(db).Where(s => s.ClientId == client.Id).ToDictionaryAsync(s => s.SiteId);
            Assert.Equal(sitePolicy.Id, sites[siteA.Id].PolicyId);
            Assert.Equal(clientPolicy.Id, sites[siteB.Id].PolicyId);
            Assert.Equal(clientPatch.Id, sites[siteB.Id].PatchPolicyId);
        }
    }

    [Fact]
    public async Task A_link_on_client_or_endpoint_level_cannot_reach_another_client()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-FOREIGN");
        var other = await _db.CreateClientAsync();
        var foreignPolicy = await PolicyAsync(other.Id);
        var foreignPatch = await PatchPolicyAsync(other.Id);
        MonitoringTemplate foreignTemplate;
        await using (var db = _db.DbFactory.CreateSystem())
        {
            foreignTemplate = new MonitoringTemplate { Id = Guid.NewGuid(), ClientId = other.Id, Name = "Foreign " + Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
            db.MonitoringTemplates.Add(foreignTemplate);
            await db.SaveChangesAsync();
        }

        object[] links =
        [
            new ClientPolicy { ClientId = client.Id, PolicyId = foreignPolicy.Id, CreatedAt = Now },
            new EndpointPolicy { EndpointId = endpoint.Id, ClientId = client.Id, PolicyId = foreignPolicy.Id, CreatedAt = Now },
            new ClientMonitoringTemplate { ClientId = client.Id, MonitoringTemplateId = foreignTemplate.Id, CreatedAt = Now },
            new ClientPatchPolicy { ClientId = client.Id, PatchPolicyId = foreignPatch.Id, CreatedAt = Now },
            new SitePatchPolicy { SiteId = site.Id, ClientId = client.Id, PatchPolicyId = foreignPatch.Id, CreatedAt = Now },
            new EndpointPatchPolicy { EndpointId = endpoint.Id, ClientId = client.Id, PatchPolicyId = foreignPatch.Id, CreatedAt = Now }
        ];
        foreach (var link in links)
        {
            Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, (await ViolationAsync(link)).SqlState);
        }
    }

    [Fact]
    public async Task A_client_template_uses_only_global_policies_patch_policies_and_monitoring_templates()
    {
        var client = await _db.CreateClientAsync();
        var clientPolicy = await PolicyAsync(client.Id);
        var clientPatch = await PatchPolicyAsync(client.Id);
        var template = new ClientTemplate { Id = Guid.NewGuid(), Name = "Levels " + Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
        await AddAsync(template);

        await using (var db = _db.DbFactory.CreateSystem())
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.ClientTemplates.Where(t => t.Id == template.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.PolicyId, clientPolicy.Id)));
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.ClientTemplates.Where(t => t.Id == template.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.PatchPolicyId, clientPatch.Id)));
        }

        var error = await ViolationAsync(new ClientTemplateSite
        {
            Id = Guid.NewGuid(), ClientTemplateId = template.Id, Name = "Site", PatchPolicyId = clientPatch.Id
        });
        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, error.SqlState);

        // A global patch policy is fine.
        var global = await PatchPolicyAsync();
        await using (var db = _db.DbFactory.CreateSystem())
        {
            await db.ClientTemplates.Where(t => t.Id == template.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.PatchPolicyId, global.Id));
        }
    }

    [Fact]
    public async Task Monitoring_templates_of_the_client_add_up_with_those_of_the_site_and_the_sql_twin_agrees()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-ADDUP");
        var clientCheck = new CheckDefinition
        {
            Id = Guid.NewGuid(), Name = "Client CPU", Type = CheckType.CpuUsage, WarningThreshold = 80, CreatedAt = Now, UpdatedAt = Now
        };
        var siteCheck = new CheckDefinition
        {
            Id = Guid.NewGuid(), Name = "Site CPU", Type = CheckType.CpuUsage, WarningThreshold = 90, CreatedAt = Now, UpdatedAt = Now
        };
        var clientTemplate = new MonitoringTemplate { Id = Guid.NewGuid(), Name = "Client " + Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now, Checks = { clientCheck } };
        var siteTemplate = new MonitoringTemplate { Id = Guid.NewGuid(), Name = "Site " + Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now, Checks = { siteCheck } };
        await AddAsync(clientTemplate, siteTemplate);
        await AddAsync(
            new ClientMonitoringTemplate { ClientId = client.Id, MonitoringTemplateId = clientTemplate.Id, CreatedAt = Now },
            new SiteMonitoringTemplate { SiteId = site.Id, ClientId = client.Id, MonitoringTemplateId = siteTemplate.Id, CreatedAt = Now },
            // Linked on both levels: it runs once and reports the client as its source.
            new SiteMonitoringTemplate { SiteId = site.Id, ClientId = client.Id, MonitoringTemplateId = clientTemplate.Id, CreatedAt = Now });

        await using var db = _db.DbFactory.CreateSystem();
        var resolved = await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: false, CancellationToken.None);
        Assert.Equal(2, resolved.Count);
        Assert.Equal(CheckSource.ClientTemplate, resolved.Single(c => c.Id == clientCheck.Id).Source);
        Assert.Equal(CheckSource.SiteTemplate, resolved.Single(c => c.Id == siteCheck.Id).Source);

        var applies = await db.Database.SqlQueryRaw<Guid>(
                """SELECT d."Id" AS "Value" FROM "CheckDefinitions" d JOIN "Endpoints" e ON e."Id" = @endpointId WHERE """ +
                EffectiveCheckResolver.AppliesSql,
                new NpgsqlParameter("endpointId", endpoint.Id))
            .ToListAsync();
        Assert.Contains(clientCheck.Id, applies);
        Assert.Contains(siteCheck.Id, applies);
    }

    [Fact]
    public void A_patch_policy_becomes_the_automation_action1_documents()
    {
        var policy = new PatchPolicy
        {
            Name = "Servers weekly",
            ScheduleKind = PatchScheduleKind.Weekly,
            WeekDays = WeekDays.Tuesday | WeekDays.Saturday,
            StartMinute = 22 * 60 + 30,
            EndpointLocalTime = false,
            Scope = PatchUpdateScope.Filtered,
            UpdateSources = ["Windows - Mandatory", "Applications"],
            Severities = ["Critical"],
            ExcludedNames = ["Mozilla*"],
            RequireApproval = false,
            InstallDelayDays = 3,
            AutoReboot = true,
            RebootMessage = true,
            RebootTimeoutMinutes = 45,
            RetryHours = 12
        };

        var json = new Action1Automation(policy, ["endpoint-1", "endpoint-2"]).ToJson();
        Assert.Equal("Fleeto: Servers weekly", json["name"]!.GetValue<string>());
        Assert.Equal("ENABLED WEEKLY:Tue,Sat AT:22-30-00", json["settings"]!.GetValue<string>());
        Assert.Equal("UTC", json["settings_timezone"]!.GetValue<string>());
        Assert.Equal("720", json["retry_minutes"]!.GetValue<string>());
        Assert.Equal(["endpoint-1", "endpoint-2"], json["endpoints"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.All(json["endpoints"]!.AsArray(), e => Assert.Equal("Endpoint", e!["type"]!.GetValue<string>()));

        var action = json["actions"]![0]!;
        Assert.Equal("deploy_update", action["template_id"]!.GetValue<string>());
        var parameters = action["params"]!;
        Assert.Equal("MatchingFilters", parameters["scope"]!.GetValue<string>());
        Assert.Equal("no", parameters["require_update_approval"]!.GetValue<string>());
        Assert.Equal(3, parameters["automatic_install_delay_days"]!.GetValue<int>());
        var filters = parameters["filters"]!.AsArray().ToDictionary(f => f!["name"]!.GetValue<string>(), f => f!);
        Assert.Equal(["update_sources", "update_security_severities", "update_names"], filters.Keys);
        Assert.Equal("exclude", filters["update_names"]["operator"]!.GetValue<string>());
        Assert.Equal("include", filters["update_sources"]["operator"]!.GetValue<string>());
        var reboot = parameters["reboot_options"]!;
        Assert.Equal("yes", reboot["auto_reboot"]!.GetValue<string>());
        Assert.Equal(PatchPolicyRules.DefaultRebootMessage, reboot["message_text"]!.GetValue<string>());
        // Minutes, as Action1's RebootOptions schema says.
        Assert.Equal(45, reboot["timeout"]!.GetValue<int>());

        policy.Enabled = false;
        policy.ScheduleKind = PatchScheduleKind.MonthlyWeekday;
        policy.MonthWeek = 2;
        policy.MonthWeekday = DayOfWeek.Tuesday;
        policy.Scope = PatchUpdateScope.All;
        policy.RequireApproval = true;
        policy.AutoReboot = false;
        policy.EndpointLocalTime = true;
        json = new Action1Automation(policy, ["endpoint-1"]).ToJson();
        Assert.Equal("DISABLED MONTHLYWEEK:2:Tue AT:22-30-00", json["settings"]!.GetValue<string>());
        Assert.Equal("LOCALTIME", json["settings_timezone"]!.GetValue<string>());
        parameters = json["actions"]![0]!["params"]!;
        Assert.Equal("All", parameters["scope"]!.GetValue<string>());
        Assert.Equal("yes", parameters["require_update_approval"]!.GetValue<string>());
        Assert.Null(parameters["automatic_install_delay_days"]);
        Assert.Null(parameters["filters"]);
        Assert.Equal(new JsonObject { ["auto_reboot"] = "no" }.ToJsonString(), parameters["reboot_options"]!.ToJsonString());

        policy.ScheduleKind = PatchScheduleKind.MonthlyDay;
        policy.MonthDay = 15;
        Assert.Equal("DISABLED MONTHLY:15 AT:22-30-00", new Action1Automation(policy, ["endpoint-1"]).Settings);
    }

    [Fact]
    public void Patch_policy_validation_only_accepts_what_action1_offers()
    {
        var valid = new PatchPolicy { Name = "Valid" };
        Assert.Null(PatchPolicyRules.Validate(valid));
        Assert.NotNull(PatchPolicyRules.Validate(new PatchPolicy { Name = "No days", WeekDays = WeekDays.None }));
        Assert.NotNull(PatchPolicyRules.Validate(new PatchPolicy { Name = "Fifth week", ScheduleKind = PatchScheduleKind.MonthlyWeekday, MonthWeek = 5 }));
        Assert.NotNull(PatchPolicyRules.Validate(new PatchPolicy { Name = "Filters", Scope = PatchUpdateScope.Filtered }));
        Assert.NotNull(PatchPolicyRules.Validate(new PatchPolicy { Name = "Unknown", Scope = PatchUpdateScope.Filtered, Severities = ["Urgent"] }));
        Assert.Null(PatchPolicyRules.Validate(new PatchPolicy { Name = "Known", Scope = PatchUpdateScope.Filtered, Severities = ["Critical"] }));
        Assert.NotNull(PatchPolicyRules.Validate(new PatchPolicy { Name = "Late", StartMinute = 1440 }));
        Assert.NotNull(PatchPolicyRules.Validate(new PatchPolicy { Name = "Retry", RetryHours = 0 }));
        Assert.Equal("Every Tuesday, Saturday at 06:05 (UTC)", PatchPolicyRules.DescribeSchedule(new PatchPolicy
        {
            WeekDays = WeekDays.Saturday | WeekDays.Tuesday, StartMinute = 6 * 60 + 5, EndpointLocalTime = false
        }));
        Assert.StartsWith("Every day", PatchPolicyRules.DescribeSchedule(new PatchPolicy { WeekDays = WeekDays.All }));
    }
}
