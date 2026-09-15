using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>
/// Applies client templates to the clients that follow them. Templates are linked, not copied: a change to a
/// client template changes every client using it.
/// <list type="bullet">
/// <item>Every template site exists as a site of the client (an existing site with the same name is adopted).</item>
/// <item>Template-sourced links (policy, monitoring templates) mirror the template; manual links are untouched.</item>
/// <item>A site whose template site was removed is detached: it keeps its endpoints and its links become manual.</item>
/// </list>
/// Callers run this inside their own unit of work and save; every affected site gets a configuration change event.
/// </summary>
public static class ClientTemplateSync
{
    public static async Task ApplyAsync(FleetoDbContext db, Client client, DateTime now, CancellationToken cancellationToken = default)
    {
        var sites = await db.Sites
            .Include(s => s.MonitoringTemplates)
            .Include(s => s.Policy)
            .Where(s => s.ClientId == client.Id)
            .ToListAsync(cancellationToken);

        if (client.ClientTemplateId is null)
        {
            foreach (var site in sites.Where(s => s.ClientTemplateSiteId is not null))
            {
                Detach(site, now);
                AddChange(db, site.Id, now);
            }

            return;
        }

        var template = await db.ClientTemplates.AsNoTracking()
            .Include(t => t.Sites).ThenInclude(s => s.MonitoringTemplates)
            .SingleAsync(t => t.Id == client.ClientTemplateId, cancellationToken);

        var templateSiteIds = template.Sites.Select(s => s.Id).ToHashSet();
        foreach (var site in sites.Where(s => s.ClientTemplateSiteId is { } id && !templateSiteIds.Contains(id)))
        {
            Detach(site, now);
            AddChange(db, site.Id, now);
        }

        foreach (var templateSite in template.Sites.OrderBy(s => s.SortOrder))
        {
            var site = sites.FirstOrDefault(s => s.ClientTemplateSiteId == templateSite.Id)
                       ?? sites.FirstOrDefault(s => s.ClientTemplateSiteId is null &&
                                                    string.Equals(s.Name, templateSite.Name, StringComparison.OrdinalIgnoreCase));
            if (site is null)
            {
                site = new Site
                {
                    Id = Guid.NewGuid(),
                    ClientId = client.Id,
                    Name = templateSite.Name,
                    Description = templateSite.Description,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.Sites.Add(site);
                sites.Add(site);
            }

            site.ClientTemplateSiteId = templateSite.Id;
            site.UpdatedAt = now;

            // Policy: the template decides when it sets one; a manual policy on the site is never overwritten.
            if (templateSite.PolicyId is { } policyId)
            {
                if (site.Policy is null)
                {
                    db.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = client.Id, PolicyId = policyId, Source = LinkSource.ClientTemplate, CreatedAt = now });
                }
                else if (site.Policy.Source == LinkSource.ClientTemplate && site.Policy.PolicyId != policyId)
                {
                    db.SitePolicies.Remove(site.Policy);
                    db.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = client.Id, PolicyId = policyId, Source = LinkSource.ClientTemplate, CreatedAt = now });
                }
            }
            else if (site.Policy is { Source: LinkSource.ClientTemplate })
            {
                db.SitePolicies.Remove(site.Policy);
            }

            var wanted = templateSite.MonitoringTemplates.Select(m => m.MonitoringTemplateId).ToHashSet();
            foreach (var link in site.MonitoringTemplates.Where(l => l.Source == LinkSource.ClientTemplate && !wanted.Contains(l.MonitoringTemplateId)).ToList())
            {
                db.SiteMonitoringTemplates.Remove(link);
            }

            foreach (var monitoringTemplateId in wanted.Where(id => site.MonitoringTemplates.All(l => l.MonitoringTemplateId != id)))
            {
                db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate
                {
                    SiteId = site.Id,
                    ClientId = client.Id,
                    MonitoringTemplateId = monitoringTemplateId,
                    Source = LinkSource.ClientTemplate,
                    CreatedAt = now
                });
            }

            AddChange(db, site.Id, now);
        }
    }

    private static void Detach(Site site, DateTime now)
    {
        site.ClientTemplateSiteId = null;
        site.UpdatedAt = now;
        if (site.Policy is { Source: LinkSource.ClientTemplate })
        {
            site.Policy.Source = LinkSource.Manual;
        }

        foreach (var link in site.MonitoringTemplates.Where(l => l.Source == LinkSource.ClientTemplate))
        {
            link.Source = LinkSource.Manual;
        }
    }

    private static void AddChange(FleetoDbContext db, Guid siteId, DateTime now) =>
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Site, ScopeId = siteId, CreatedAt = now });
}
