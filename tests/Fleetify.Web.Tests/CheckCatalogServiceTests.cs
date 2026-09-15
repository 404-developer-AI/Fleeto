using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;
using Fleetify.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleetify.Web.Tests;

/// <summary>
/// Guarantees the check catalog in web: services are suggested from the endpoint's inventory and, for a monitoring template, from
/// the endpoints that run it within the caller's clients; a check type that cannot run on an endpoint's platform is refused; only
/// the parameters of the type are stored; and the type of an existing check cannot change.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class CheckCatalogServiceTests
{
    private readonly WebFixture _fixture;

    public CheckCatalogServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private EndpointCheckService Checks => _fixture.Services.GetRequiredService<EndpointCheckService>();
    private MonitoringTemplateService Templates => _fixture.Services.GetRequiredService<MonitoringTemplateService>();

    private async Task InventoryAsync(Endpoint endpoint, string servicesJson)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.InventorySnapshots.Add(new InventorySnapshot
        {
            EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ReceivedAt = DateTime.UtcNow, Hash = "h", ServicesJson = servicesJson
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Services_are_suggested_from_the_inventory_of_the_endpoint_and_of_the_endpoints_running_a_template()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var first = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-SVC-01", EndpointClass.Server);
        var second = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-SVC-02", EndpointClass.Server);
        await InventoryAsync(first, """[{"name":"Spooler","displayName":"Print Spooler","startType":"automatic","state":"running"},{"name":"W32Time","displayName":"Windows Time"}]""");
        await InventoryAsync(second, """[{"name":"MSSQLSERVER","displayName":"SQL Server (MSSQLSERVER)"},{"name":"Spooler","displayName":"Print Spooler"},{"bad":1},"x"]""");

        var other = await _fixture.Database.CreateClientAsync();
        var otherSite = await _fixture.Database.CreateSiteAsync(other.Id);
        var otherEndpoint = await _fixture.Database.CreateEndpointAsync(otherSite, EndpointTier.Managed, "SRV-OTHER", EndpointClass.Server);
        await InventoryAsync(otherEndpoint, """[{"name":"SecretService","displayName":"Other client service"}]""");

        var technician = WebFixture.Technician();
        var own = await Checks.GetServicesAsync(technician, first.Id);
        Assert.Equal(["Spooler", "W32Time"], own.Select(s => s.Name).ToArray());
        Assert.Equal("running", own[0].State);

        var created = await Templates.CreateAsync(technician, "Services " + Guid.NewGuid().ToString("N")[..6], null, null);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate { SiteId = site.Id, ClientId = client.Id, MonitoringTemplateId = created.Value, CreatedAt = DateTime.UtcNow });
            db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate { SiteId = otherSite.Id, ClientId = other.Id, MonitoringTemplateId = created.Value, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var all = await Templates.GetServicesAsync(technician, created.Value);
        Assert.Equal(["Other client service", "Print Spooler", "SQL Server (MSSQLSERVER)", "Windows Time"], all.Select(s => s.DisplayName).ToArray());

        var restricted = WebFixture.CallerWith(new RestrictedClientScope([client.Id]), FleetifyRoles.ReadOnly);
        var scoped = await Templates.GetServicesAsync(restricted, created.Value);
        Assert.DoesNotContain(scoped, s => s.Name == "SecretService");
        Assert.Contains(scoped, s => s.Name == "MSSQLSERVER");
        Assert.Empty(await Checks.GetServicesAsync(restricted, otherEndpoint.Id));
    }

    [Fact]
    public async Task A_check_type_that_cannot_run_on_the_endpoint_is_refused_and_the_type_of_a_check_cannot_change()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var linux = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "LNX-CATALOG", EndpointClass.Server);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == linux.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.OsPlatform, "linux"));
        }

        var technician = WebFixture.Technician();
        var eventLog = new CheckDefinitionInput("Errors", CheckType.EventLog, 900, new Dictionary<string, string> { ["log"] = "System", ["level"] = "error" },
            1, null, 1, CheckAppliesTo.All, true);
        var refused = await Checks.SaveEndpointCheckAsync(technician, linux.Id, null, eventLog);
        Assert.False(refused.Success);
        Assert.Contains("Windows only", refused.Problem);

        var http = new CheckDefinitionInput("Intranet", CheckType.Http, 300,
            new Dictionary<string, string> { ["url"] = "https://intranet.example", ["contains"] = "OK", ["service"] = "ignored", ["timeout_seconds"] = " 15 " },
            null, 2000, 1, CheckAppliesTo.All, true);
        var saved = await Checks.SaveEndpointCheckAsync(technician, linux.Id, null, http);
        Assert.True(saved.Success, saved.Problem);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var stored = await db.CheckDefinitions.AsNoTracking().SingleAsync(c => c.Id == saved.Value);
            var parameters = Fleetify.Core.Domain.CheckParameters.Parse(stored.ParametersJson);
            Assert.Equal(new Dictionary<string, string> { ["url"] = "https://intranet.example", ["contains"] = "OK", ["timeout_seconds"] = "15" }, parameters);
        }

        var changed = await Checks.SaveEndpointCheckAsync(technician, linux.Id, saved.Value, http with { Type = CheckType.TcpPort });
        Assert.False(changed.Success);
        Assert.Contains("cannot change", changed.Problem);
    }
}
