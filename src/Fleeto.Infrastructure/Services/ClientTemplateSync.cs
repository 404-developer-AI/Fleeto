using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>
/// Applies client templates to the clients that follow them. Templates are linked, not copied: a change to a
/// client template changes every client using it.
/// <list type="bullet">
/// <item>Every template site exists as a site of the client (an existing site with the same name is adopted).</item>
/// <item>Template-sourced links (policy, patch policy, monitoring templates) mirror the template, on the client itself (0.6.0)
/// and on its sites; manual links are untouched.</item>
/// <item>A site whose template site was removed is detached: it keeps its endpoints and its links become manual.</item>
/// </list>
/// Callers run this inside their own unit of work and save; the client and every affected site get a configuration change event.
/// </summary>
public static class ClientTemplateSync
{
    public static async Task ApplyAsync(FleetoDbContext db, Client client, DateTime now, CancellationToken cancellationToken = default)
    {
        var sites = await db.Sites
            .Include(s => s.MonitoringTemplates)
            .Include(s => s.Policy)
            .Include(s => s.PatchPolicy)
            .Where(s => s.ClientId == client.Id)
            .ToListAsync(cancellationToken);
        var clientPolicy = await db.ClientPolicies.SingleOrDefaultAsync(l => l.ClientId == client.Id, cancellationToken);
        var clientPatchPolicy = await db.ClientPatchPolicies.SingleOrDefaultAsync(l => l.ClientId == client.Id, cancellationToken);
        var clientTemplates = await db.ClientMonitoringTemplates.Where(l => l.ClientId == client.Id).ToListAsync(cancellationToken);

        if (client.ClientTemplateId is null)
        {
            // Detached: the client keeps what the template linked, as links of its own.
            if (clientPolicy is { Source: LinkSource.ClientTemplate } || clientPatchPolicy is { Source: LinkSource.ClientTemplate } ||
                clientTemplates.Any(l => l.Source == LinkSource.ClientTemplate))
            {
                if (clientPolicy is not null)
                {
                    clientPolicy.Source = LinkSource.Manual;
                }

                if (clientPatchPolicy is not null)
                {
                    clientPatchPolicy.Source = LinkSource.Manual;
                }

                clientTemplates.ForEach(l => l.Source = LinkSource.Manual);
                AddClientChange(db, client.Id, now);
            }

            foreach (var site in sites.Where(s => s.ClientTemplateSiteId is not null))
            {
                Detach(site, now);
                AddChange(db, site.Id, now);
            }

            return;
        }

        var template = await db.ClientTemplates.AsNoTracking()
            .Include(t => t.Sites).ThenInclude(s => s.MonitoringTemplates)
            .Include(t => t.MonitoringTemplates)
            .SingleAsync(t => t.Id == client.ClientTemplateId, cancellationToken);

        // The client itself (0.6.0): the same rules as for a site below.
        if (template.PolicyId is { } templatePolicyId)
        {
            if (clientPolicy is null)
            {
                db.ClientPolicies.Add(new ClientPolicy { ClientId = client.Id, PolicyId = templatePolicyId, Source = LinkSource.ClientTemplate, CreatedAt = now });
            }
            else if (clientPolicy.Source == LinkSource.ClientTemplate && clientPolicy.PolicyId != templatePolicyId)
            {
                db.ClientPolicies.Remove(clientPolicy);
                db.ClientPolicies.Add(new ClientPolicy { ClientId = client.Id, PolicyId = templatePolicyId, Source = LinkSource.ClientTemplate, CreatedAt = now });
            }
        }
        else if (clientPolicy is { Source: LinkSource.ClientTemplate })
        {
            db.ClientPolicies.Remove(clientPolicy);
        }

        if (template.PatchPolicyId is { } templatePatchPolicyId)
        {
            if (clientPatchPolicy is null)
            {
                db.ClientPatchPolicies.Add(new ClientPatchPolicy
                {
                    ClientId = client.Id, PatchPolicyId = templatePatchPolicyId, Source = LinkSource.ClientTemplate, CreatedAt = now
                });
            }
            else if (clientPatchPolicy.Source == LinkSource.ClientTemplate && clientPatchPolicy.PatchPolicyId != templatePatchPolicyId)
            {
                db.ClientPatchPolicies.Remove(clientPatchPolicy);
                db.ClientPatchPolicies.Add(new ClientPatchPolicy
                {
                    ClientId = client.Id, PatchPolicyId = templatePatchPolicyId, Source = LinkSource.ClientTemplate, CreatedAt = now
                });
            }
        }
        else if (clientPatchPolicy is { Source: LinkSource.ClientTemplate })
        {
            db.ClientPatchPolicies.Remove(clientPatchPolicy);
        }

        var wantedForClient = template.MonitoringTemplates.Select(m => m.MonitoringTemplateId).ToHashSet();
        db.ClientMonitoringTemplates.RemoveRange(clientTemplates.Where(l => l.Source == LinkSource.ClientTemplate && !wantedForClient.Contains(l.MonitoringTemplateId)));
        foreach (var monitoringTemplateId in wantedForClient.Where(id => clientTemplates.All(l => l.MonitoringTemplateId != id)))
        {
            db.ClientMonitoringTemplates.Add(new ClientMonitoringTemplate
            {
                ClientId = client.Id, MonitoringTemplateId = monitoringTemplateId, Source = LinkSource.ClientTemplate, CreatedAt = now
            });
        }

        AddClientChange(db, client.Id, now);

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

            if (templateSite.PatchPolicyId is { } patchPolicyId)
            {
                if (site.PatchPolicy is null)
                {
                    db.SitePatchPolicies.Add(new SitePatchPolicy
                    {
                        SiteId = site.Id, ClientId = client.Id, PatchPolicyId = patchPolicyId, Source = LinkSource.ClientTemplate, CreatedAt = now
                    });
                }
                else if (site.PatchPolicy.Source == LinkSource.ClientTemplate && site.PatchPolicy.PatchPolicyId != patchPolicyId)
                {
                    db.SitePatchPolicies.Remove(site.PatchPolicy);
                    db.SitePatchPolicies.Add(new SitePatchPolicy
                    {
                        SiteId = site.Id, ClientId = client.Id, PatchPolicyId = patchPolicyId, Source = LinkSource.ClientTemplate, CreatedAt = now
                    });
                }
            }
            else if (site.PatchPolicy is { Source: LinkSource.ClientTemplate })
            {
                db.SitePatchPolicies.Remove(site.PatchPolicy);
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

        if (site.PatchPolicy is { Source: LinkSource.ClientTemplate })
        {
            site.PatchPolicy.Source = LinkSource.Manual;
        }

        foreach (var link in site.MonitoringTemplates.Where(l => l.Source == LinkSource.ClientTemplate))
        {
            link.Source = LinkSource.Manual;
        }
    }

    private static void AddClientChange(FleetoDbContext db, Guid clientId, DateTime now) =>
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Client, ScopeId = clientId, CreatedAt = now });

    private static void AddChange(FleetoDbContext db, Guid siteId, DateTime now) =>
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Site, ScopeId = siteId, CreatedAt = now });
}
