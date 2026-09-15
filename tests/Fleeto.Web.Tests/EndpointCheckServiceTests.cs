using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees the checks of one endpoint through the web: every effective check is listed before it ran, adjustments are
/// refused for agent-only endpoints and for another client's endpoint or template, changes fan out as configuration
/// events, and run requests are rate limited and never made up for checks that do not run.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class EndpointCheckServiceTests
{
    private readonly WebFixture _fixture;

    public EndpointCheckServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private EndpointCheckService Service => _fixture.Services.GetRequiredService<EndpointCheckService>();

    private DateTime Now => _fixture.Database.Time.GetUtcNow().UtcDateTime;

    private async Task<(Client Client, Site Site, Endpoint Endpoint, CheckDefinition Check)> ManagedWithTemplateAsync(EndpointTier tier = EndpointTier.Managed)
    {
        var db = _fixture.Database;
        await db.LoadTestLicenseAsync(1000);
        var client = await db.CreateClientAsync();
        var site = await db.CreateSiteAsync(client.Id);
        var endpoint = await db.CreateEndpointAsync(site, tier, "WS-CHECKS");
        await using var context = db.DbFactory.CreateSystem();
        var template = new MonitoringTemplate { Id = Guid.NewGuid(), Name = "Web checks " + Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
        var check = new CheckDefinition
        {
            Id = Guid.NewGuid(), MonitoringTemplateId = template.Id, Name = "CPU usage", Type = CheckType.CpuUsage, IntervalSeconds = 300,
            WarningThreshold = 80, CriticalThreshold = 90, CreatedAt = Now, UpdatedAt = Now
        };
        template.Checks.Add(check);
        context.MonitoringTemplates.Add(template);
        context.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate { SiteId = site.Id, ClientId = client.Id, MonitoringTemplateId = template.Id, CreatedAt = Now });
        await context.SaveChangesAsync();
        return (client, site, endpoint, check);
    }

    private static CheckDefinitionInput ServiceCheck(string service = "Spooler") =>
        new("Spooler", CheckType.ServiceRunning, 120, new Dictionary<string, string> { ["service"] = service }, null, null, 1, CheckAppliesTo.Server, true);

    [Fact]
    public async Task Checks_are_listed_before_they_ran_and_state_and_disable_show()
    {
        var (_, _, endpoint, check) = await ManagedWithTemplateAsync();
        var caller = WebFixture.Technician();

        var view = await Service.GetChecksAsync(caller, endpoint.Id);
        Assert.NotNull(view);
        Assert.True(view.Managed);
        var row = Assert.Single(view.Rows);
        Assert.Equal(check.Id, row.DefinitionId);
        Assert.Null(row.Status);
        Assert.Equal(CheckSource.SiteTemplate, row.Source);

        Assert.True((await Service.SetDisabledAsync(caller, endpoint.Id, check.Id, true)).Success);
        row = Assert.Single((await Service.GetChecksAsync(caller, endpoint.Id))!.Rows);
        Assert.True(row.DisabledOnEndpoint);

        Assert.True((await Service.SetDisabledAsync(caller, endpoint.Id, check.Id, false)).Success);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        // Enabling again leaves nothing to override, so the row is removed.
        Assert.False(await db.EndpointCheckOverrides.AnyAsync(o => o.EndpointId == endpoint.Id));
        Assert.Equal(2, await db.ConfigChangeEvents.CountAsync(e => e.Scope == ConfigChangeScope.Endpoint && e.ScopeId == endpoint.Id));
    }

    [Fact]
    public async Task Overrides_are_validated_saved_and_removed()
    {
        var (_, _, endpoint, check) = await ManagedWithTemplateAsync();
        var caller = WebFixture.Technician();

        var invalid = await Service.SaveOverrideAsync(caller, endpoint.Id, check.Id, new CheckOverrideInput(5, true, 95, 90, null));
        Assert.False(invalid.Success);

        Assert.True((await Service.SaveOverrideAsync(caller, endpoint.Id, check.Id, new CheckOverrideInput(60, true, 90, 97, 3))).Success);
        var view = await Service.GetOverrideAsync(caller, endpoint.Id, check.Id);
        Assert.NotNull(view);
        Assert.Equal(60, view.IntervalSeconds);
        Assert.Equal(97, view.CriticalThreshold);
        Assert.Equal(300, view.TemplateIntervalSeconds);
        Assert.True(Assert.Single((await Service.GetChecksAsync(caller, endpoint.Id))!.Rows).HasOverrides);

        Assert.True((await Service.SaveOverrideAsync(caller, endpoint.Id, check.Id, new CheckOverrideInput(null, false, null, null, null))).Success);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.EndpointCheckOverrides.AnyAsync(o => o.EndpointId == endpoint.Id));
    }

    [Fact]
    public async Task Endpoint_only_checks_and_template_links_can_be_managed()
    {
        var (client, _, endpoint, _) = await ManagedWithTemplateAsync();
        var caller = WebFixture.Technician();

        var added = await Service.SaveEndpointCheckAsync(caller, endpoint.Id, null, ServiceCheck());
        Assert.True(added.Success);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var stored = await db.CheckDefinitions.AsNoTracking().SingleAsync(d => d.Id == added.Value);
            Assert.Equal(endpoint.Id, stored.EndpointId);
            Assert.Equal(client.Id, stored.ClientId);
            Assert.Null(stored.MonitoringTemplateId);
            Assert.Equal(CheckAppliesTo.All, stored.AppliesTo);
        }

        Assert.False((await Service.SaveEndpointCheckAsync(caller, endpoint.Id, null, ServiceCheck(service: "bad\\name"))).Success);

        var template = await CreateClientTemplateAsync(client.Id);
        Assert.True((await Service.SetEndpointTemplatesAsync(caller, endpoint.Id, [template])).Success);
        var links = await Service.GetTemplateLinksAsync(caller, endpoint.Id);
        Assert.Contains(links!.EndpointTemplates, t => t.Id == template);
        Assert.Contains((await Service.GetChecksAsync(caller, endpoint.Id))!.Rows, r => r.Source == CheckSource.EndpointTemplate);

        Assert.True((await Service.DeleteEndpointCheckAsync(caller, endpoint.Id, added.Value)).Success);
        Assert.True((await Service.SetEndpointTemplatesAsync(caller, endpoint.Id, [])).Success);
        Assert.DoesNotContain((await Service.GetChecksAsync(caller, endpoint.Id))!.Rows, r => r.Source != CheckSource.SiteTemplate);

        async Task<Guid> CreateClientTemplateAsync(Guid clientId)
        {
            await using var db = _fixture.Database.DbFactory.CreateSystem();
            var t = new MonitoringTemplate { Id = Guid.NewGuid(), ClientId = clientId, Name = "Own " + Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
            t.Checks.Add(new CheckDefinition { Id = Guid.NewGuid(), ClientId = clientId, MonitoringTemplateId = t.Id, Name = "Uptime", Type = CheckType.Uptime, WarningThreshold = 30, CreatedAt = Now, UpdatedAt = Now });
            db.MonitoringTemplates.Add(t);
            await db.SaveChangesAsync();
            return t.Id;
        }
    }

    [Fact]
    public async Task Every_change_is_refused_for_an_agent_only_endpoint_and_after_the_license_expired()
    {
        var (_, _, agentOnly, check) = await ManagedWithTemplateAsync(EndpointTier.AgentOnly);
        var caller = WebFixture.Admin();

        Assert.False((await Service.GetChecksAsync(caller, agentOnly.Id))!.Managed);
        await AssertAllRefusedAsync(caller, agentOnly, check);

        var (_, _, managed, managedCheck) = await ManagedWithTemplateAsync();
        await _fixture.Database.LoadTestLicenseAsync(1000, expiresAt: Now.AddDays(-30));
        try
        {
            await AssertAllRefusedAsync(caller, managed, managedCheck);
        }
        finally
        {
            await _fixture.Database.LoadTestLicenseAsync(1000);
        }
    }

    private async Task AssertAllRefusedAsync(Fleeto.Web.Security.Caller caller, Endpoint endpoint, CheckDefinition check)
    {
        Assert.False((await Service.SetDisabledAsync(caller, endpoint.Id, check.Id, true)).Success);
        Assert.False((await Service.SaveOverrideAsync(caller, endpoint.Id, check.Id, new CheckOverrideInput(60, false, null, null, null))).Success);
        Assert.False((await Service.SaveEndpointCheckAsync(caller, endpoint.Id, null, ServiceCheck())).Success);
        Assert.False((await Service.SetEndpointTemplatesAsync(caller, endpoint.Id, [check.MonitoringTemplateId!.Value])).Success);
        Assert.False((await Service.RequestRunAsync(caller, endpoint.Id, check.Id, reset: true)).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.EndpointCheckOverrides.AnyAsync(o => o.EndpointId == endpoint.Id));
        Assert.False(await db.CheckDefinitions.AnyAsync(d => d.EndpointId == endpoint.Id));
        Assert.False(await db.EndpointMonitoringTemplates.AnyAsync(l => l.EndpointId == endpoint.Id));
        Assert.False(await db.CheckRunRequests.AnyAsync(r => r.EndpointId == endpoint.Id));
    }

    [Fact]
    public async Task A_restricted_caller_cannot_touch_checks_of_another_client_or_link_its_templates()
    {
        var (clientA, _, endpointA, _) = await ManagedWithTemplateAsync();
        var (clientB, _, endpointB, checkB) = await ManagedWithTemplateAsync();
        var caller = WebFixture.CallerWith(new RestrictedClientScope([clientA.Id]), FleetoRoles.Technician);

        Assert.Null(await Service.GetChecksAsync(caller, endpointB.Id));
        Assert.Null(await Service.GetOverrideAsync(caller, endpointB.Id, checkB.Id));
        Assert.Null(await Service.GetTemplateLinksAsync(caller, endpointB.Id));
        await AssertAllRefusedAsync(caller, endpointB, checkB);

        Guid foreignTemplate;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var template = new MonitoringTemplate { Id = Guid.NewGuid(), ClientId = clientB.Id, Name = "B only " + Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
            db.MonitoringTemplates.Add(template);
            await db.SaveChangesAsync();
            foreignTemplate = template.Id;
        }

        Assert.False((await Service.SetEndpointTemplatesAsync(caller, endpointA.Id, [foreignTemplate])).Success);
        // Also for an unrestricted caller: a template of another client never links.
        Assert.False((await Service.SetEndpointTemplatesAsync(WebFixture.Admin(), endpointA.Id, [foreignTemplate])).Success);
        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await check.EndpointMonitoringTemplates.AnyAsync(l => l.EndpointId == endpointA.Id));
    }

    [Fact]
    public async Task Run_requests_are_audited_rate_limited_and_only_for_running_checks()
    {
        var (_, _, endpoint, check) = await ManagedWithTemplateAsync();
        var caller = WebFixture.Technician();
        var readOnly = WebFixture.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);

        Assert.False((await Service.RequestRunAsync(readOnly, endpoint.Id, check.Id, reset: false)).Success);
        var first = await Service.RequestRunAsync(caller, endpoint.Id, check.Id, reset: true);
        Assert.True(first.Success);
        Assert.False(first.Value);
        Assert.False((await Service.RequestRunAsync(caller, endpoint.Id, check.Id, reset: false)).Success);

        _fixture.Database.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.True((await Service.RequestRunAsync(caller, endpoint.Id, check.Id, reset: false)).Success);

        Assert.True((await Service.SetDisabledAsync(caller, endpoint.Id, check.Id, true)).Success);
        _fixture.Database.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.False((await Service.RequestRunAsync(caller, endpoint.Id, check.Id, reset: false)).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var requests = await db.CheckRunRequests.AsNoTracking().Where(r => r.EndpointId == endpoint.Id).OrderBy(r => r.RequestedAt).ToListAsync();
        Assert.Equal([true, false], requests.Select(r => r.Reset).ToArray());
        Assert.All(requests, r => Assert.Equal(caller.UserId, r.RequestedByUserId));
        var id = endpoint.Id.ToString();
        Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.TargetId == id && a.Action == Core.Interfaces.AuditActions.CheckReset));
        Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.TargetId == id && a.Action == Core.Interfaces.AuditActions.CheckRunRequested));
    }
}
