using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Web.Security;
using Fleetify.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleetify.Web.Tests;

/// <summary>
/// Client creation: with a client template, the template's sites and their template-sourced links are created in the same
/// unit of work; roles are enforced in the service, not only in the UI.
/// </summary>
[Collection(WebCollection.Name)]
public class ClientServiceTests
{
    private readonly WebFixture _fixture;

    public ClientServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private static string NewCode() => "C" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    public async Task Client_created_with_a_client_template_gets_its_sites_and_template_sourced_links()
    {
        var caller = WebFixtureBase.Technician();
        var clients = _fixture.Services.GetRequiredService<ClientService>();
        var templates = _fixture.Services.GetRequiredService<ClientTemplateService>();
        var policies = _fixture.Services.GetRequiredService<PolicyService>();

        Guid monitoringTemplateId;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            monitoringTemplateId = await db.MonitoringTemplates.Where(t => t.ClientId == null).Select(t => t.Id).FirstAsync();
        }

        var policy = await policies.CreateAsync(caller, null,
            new PolicyInput("Servers " + Guid.NewGuid().ToString("N")[..6], null, 30, 3600, 5, AlertSeverity.Critical));
        Assert.True(policy.Success, policy.Problem);

        var template = await templates.SaveAsync(caller, null, new ClientTemplateInput("Template " + Guid.NewGuid().ToString("N")[..6], null,
        [
            new ClientTemplateSiteInput(null, "Monitoring", null, policy.Value, [monitoringTemplateId]),
            new ClientTemplateSiteInput(null, "Agent Only", null, null, [])
        ]));
        Assert.True(template.Success, template.Problem);

        var code = NewCode().ToLowerInvariant();
        var created = await clients.CreateAsync(caller, " " + code + " ", "Acme Corporation", template.Value);
        Assert.True(created.Success, created.Problem);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        var client = await check.Clients.SingleAsync(c => c.Id == created.Value);
        Assert.Equal(code.ToUpperInvariant(), client.Code);
        Assert.Equal(template.Value, client.ClientTemplateId);

        var sites = await check.Sites.Include(s => s.Policy).Include(s => s.MonitoringTemplates)
            .Where(s => s.ClientId == client.Id).OrderBy(s => s.Name).ToListAsync();
        Assert.Equal(["Agent Only", "Monitoring"], sites.Select(s => s.Name).ToList());
        Assert.All(sites, s => Assert.NotNull(s.ClientTemplateSiteId));

        var monitoring = sites.Single(s => s.Name == "Monitoring");
        Assert.Equal(policy.Value, monitoring.Policy!.PolicyId);
        Assert.Equal(LinkSource.ClientTemplate, monitoring.Policy.Source);
        var link = Assert.Single(monitoring.MonitoringTemplates);
        Assert.Equal(monitoringTemplateId, link.MonitoringTemplateId);
        Assert.Equal(LinkSource.ClientTemplate, link.Source);
        Assert.Null(sites.Single(s => s.Name == "Agent Only").Policy);

        Assert.True(await check.AuditEntries.AnyAsync(a => a.Action == AuditActions.ClientCreated && a.TargetId == client.Id.ToString()));
        Assert.True(await check.ConfigChangeEvents.AnyAsync(e => e.Scope == ConfigChangeScope.Site && e.ScopeId == monitoring.Id));
    }

    [Fact]
    public async Task Client_without_a_template_gets_one_site_and_a_duplicate_code_is_refused()
    {
        var caller = WebFixtureBase.Technician();
        var clients = _fixture.Services.GetRequiredService<ClientService>();
        var code = NewCode();

        var created = await clients.CreateAsync(caller, code, "Contoso", null);
        Assert.True(created.Success, created.Problem);
        var detail = await clients.GetAsync(caller, created.Value);
        Assert.Equal(ClientService.DefaultSiteName, Assert.Single(detail!.Sites).Name);

        var duplicate = await clients.CreateAsync(caller, code, "Other", null);
        Assert.False(duplicate.Success);
        Assert.Contains(code, duplicate.Problem);

        var invalid = await clients.CreateAsync(caller, "not valid!", "Other", null);
        Assert.False(invalid.Success);
    }

    [Fact]
    public async Task Read_only_users_cannot_create_or_delete_clients()
    {
        var readOnly = WebFixtureBase.CallerWith(Infrastructure.Data.SystemClientScope.Instance, FleetifyRoles.ReadOnly);
        var clients = _fixture.Services.GetRequiredService<ClientService>();

        var created = await clients.CreateAsync(readOnly, NewCode(), "Nope", null);
        Assert.False(created.Success);
        Assert.Equal(ServiceResult.ForbiddenProblem, created.Problem);

        var existing = await _fixture.Database.CreateClientAsync();
        var deleted = await clients.DeleteAsync(readOnly, existing.Id, existing.Code);
        Assert.Equal(ServiceResult.ForbiddenProblem, deleted.Problem);
    }

    [Fact]
    public async Task Deleting_a_client_requires_the_typed_code_and_publishes_revocations()
    {
        var caller = WebFixtureBase.Technician();
        var clients = _fixture.Services.GetRequiredService<ClientService>();
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site);

        var wrong = await clients.DeleteAsync(caller, client.Id, "WRONG");
        Assert.False(wrong.Success);

        var deleted = await clients.DeleteAsync(caller, client.Id, client.Code.ToLowerInvariant());
        Assert.True(deleted.Success, deleted.Problem);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.Endpoints.AnyAsync(e => e.Id == endpoint.Id));
        Assert.Contains(endpoint.Id.ToString(), _fixture.Database.Bus.PayloadsFor(NotificationChannels.Revocations));
    }
}
