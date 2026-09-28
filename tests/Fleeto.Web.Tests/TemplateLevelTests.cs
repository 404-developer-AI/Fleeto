using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Where a monitoring template can be linked (0.6.0): only there on a client, site or endpoint and in a client template, the
/// choices say so, and a level it is linked on cannot be switched off.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class TemplateLevelTests
{
    private readonly WebFixture _fixture;

    public TemplateLevelTests(WebFixture fixture) => _fixture = fixture;

    private LinkService Links => _fixture.Services.GetRequiredService<LinkService>();
    private MonitoringTemplateService Templates => _fixture.Services.GetRequiredService<MonitoringTemplateService>();

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    private async Task<Guid> TemplateAsync(TemplateLevels levels)
    {
        var created = await Templates.CreateAsync(WebFixtureBase.Admin(), Unique("Levels"), null, null, CheckAppliesTo.All, levels);
        Assert.True(created.Success, created.Problem);
        return created.Value;
    }

    [Fact]
    public async Task A_template_is_linked_only_where_it_can_be_and_the_choices_say_where()
    {
        var admin = WebFixtureBase.Admin();
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-LEVELS");
        var sitesOnly = await TemplateAsync(new TemplateLevels(false, true, false));

        var onClient = await Links.SetAsync(admin, LinkLevel.Client, client.Id, new LinksInput(ClassChoice.None, ClassChoice.None, [sitesOnly]));
        Assert.False(onClient.Success);
        Assert.Contains("cannot be linked to a client", onClient.Problem);
        Assert.False((await Links.SetAsync(admin, LinkLevel.Endpoint, endpoint.Id, new LinksInput(ClassChoice.None, ClassChoice.None, [sitesOnly]))).Success);
        var checks = _fixture.Services.GetRequiredService<EndpointCheckService>();
        Assert.False((await checks.SetEndpointTemplatesAsync(admin, endpoint.Id, [sitesOnly])).Success);
        Assert.DoesNotContain((await checks.GetTemplateLinksAsync(admin, endpoint.Id))!.Available, t => t.Id == sitesOnly);

        Assert.True((await Links.SetAsync(admin, LinkLevel.Site, site.Id, new LinksInput(ClassChoice.None, ClassChoice.None, [sitesOnly]))).Success);
        var option = (await Links.GetAsync(admin, LinkLevel.Site, site.Id))!.Choices.MonitoringTemplates.Single(t => t.Id == sitesOnly);
        Assert.True(option.Allows(LinkLevel.Site));
        Assert.False(option.Allows(LinkLevel.Client));
        Assert.False(option.Allows(LinkLevel.Endpoint));
    }

    [Fact]
    public async Task A_level_the_template_is_linked_on_cannot_be_switched_off()
    {
        var admin = WebFixtureBase.Admin();
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id, Unique("Linked site"));
        var template = await TemplateAsync(TemplateLevels.All);
        Assert.True((await Links.SetAsync(admin, LinkLevel.Site, site.Id, new LinksInput(ClassChoice.None, ClassChoice.None, [template]))).Success);

        var refused = await Templates.UpdateAsync(admin, template, Unique("Levels"), null, null, new TemplateLevels(true, false, true));
        Assert.False(refused.Success);
        Assert.Contains(site.Name, refused.Problem);

        // Switching off a level where it is not linked is fine, and nothing is refused for no level at all.
        var narrowed = await Templates.UpdateAsync(admin, template, Unique("Levels"), null, null, new TemplateLevels(false, true, false));
        Assert.True(narrowed.Success, narrowed.Problem);
        Assert.Equal(new TemplateLevels(false, true, false), (await Templates.GetAsync(admin, template))!.Levels);
        Assert.False((await Templates.UpdateAsync(admin, template, Unique("Levels"), null, null, new TemplateLevels(false, false, false))).Success);
        Assert.False((await Templates.CreateAsync(admin, Unique("None"), null, null, CheckAppliesTo.All, new TemplateLevels(false, false, false))).Success);
    }

    [Fact]
    public async Task A_client_template_uses_a_template_only_where_it_can_be_linked()
    {
        var admin = WebFixtureBase.Admin();
        var clientTemplates = _fixture.Services.GetRequiredService<ClientTemplateService>();
        var endpointsOnly = await TemplateAsync(new TemplateLevels(false, false, true));
        var clientsOnly = await TemplateAsync(new TemplateLevels(true, false, false));

        var onSite = await clientTemplates.SaveAsync(admin, null, new ClientTemplateInput(Unique("Levels"), null,
            [new ClientTemplateSiteInput(null, "Main", null, ClassChoice.None, [endpointsOnly])]));
        Assert.False(onSite.Success);
        var onClient = await clientTemplates.SaveAsync(admin, null, new ClientTemplateInput(Unique("Levels"), null, [],
            new ClientTemplateClientLinks(ClassChoice.None, ClassChoice.None, [endpointsOnly])));
        Assert.False(onClient.Success);

        var saved = await clientTemplates.SaveAsync(admin, null, new ClientTemplateInput(Unique("Levels"), null,
            [new ClientTemplateSiteInput(null, "Main", null, ClassChoice.None, [])],
            new ClientTemplateClientLinks(ClassChoice.None, ClassChoice.None, [clientsOnly])));
        Assert.True(saved.Success, saved.Problem);

        // The client template holds it for its clients, so linking to clients stays on.
        var refused = await Templates.UpdateAsync(admin, clientsOnly, Unique("Levels"), null, null, new TemplateLevels(false, true, false));
        Assert.False(refused.Success);
        Assert.Contains("Client template", refused.Problem);
    }

    [Fact]
    public async Task The_database_refuses_a_template_that_can_be_linked_nowhere()
    {
        var now = DateTime.UtcNow;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.MonitoringTemplates.Add(new MonitoringTemplate
        {
            Id = Guid.NewGuid(), Name = Unique("Nowhere"), ForClients = false, ForSites = false, ForEndpoints = false, CreatedAt = now, UpdatedAt = now
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
