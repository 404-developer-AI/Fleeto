using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>
/// A choice per class slot (0.6.0): one for every endpoint, or one for servers and one for workstations. Used for the
/// policy and patch policy of a client template, its sites, and the links of a client or site.
/// </summary>
public readonly record struct ClassChoice(Guid? All, Guid? Server, Guid? Workstation)
{
    public static readonly ClassChoice None = default;

    /// <summary>One choice for every endpoint.</summary>
    public static implicit operator ClassChoice(Guid? all) => new(all, null, null);

    public static readonly CheckAppliesTo[] Slots = [CheckAppliesTo.All, CheckAppliesTo.Server, CheckAppliesTo.Workstation];

    public Guid? this[CheckAppliesTo slot] => slot switch
    {
        CheckAppliesTo.Server => Server,
        CheckAppliesTo.Workstation => Workstation,
        _ => All
    };

    /// <summary>True when servers and workstations have a choice of their own instead of one for every endpoint.</summary>
    public bool Split => All is null && (Server is not null || Workstation is not null);

    public IEnumerable<Guid> Ids => new[] { All, Server, Workstation }.OfType<Guid>();
}

/// <summary>
/// Applies client templates to the clients that follow them. Templates are linked, not copied: a change to a
/// client template changes every client using it.
/// <list type="bullet">
/// <item>Every template site exists as a site of the client (an existing site with the same name is adopted).</item>
/// <item>Template-sourced links (policy, patch policy, monitoring templates) mirror the template, on the client itself (0.6.0)
/// and on its sites, per class slot; manual links are untouched.</item>
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
            .Include(s => s.Policies)
            .Include(s => s.PatchPolicies)
            .Where(s => s.ClientId == client.Id)
            .ToListAsync(cancellationToken);
        var clientPolicies = await db.ClientPolicies.Where(l => l.ClientId == client.Id).ToListAsync(cancellationToken);
        var clientPatchPolicies = await db.ClientPatchPolicies.Where(l => l.ClientId == client.Id).ToListAsync(cancellationToken);
        var clientTemplates = await db.ClientMonitoringTemplates.Where(l => l.ClientId == client.Id).ToListAsync(cancellationToken);

        if (client.ClientTemplateId is null)
        {
            // Detached: the client keeps what the template linked, as links of its own.
            if (clientPolicies.Any(l => l.Source == LinkSource.ClientTemplate) || clientPatchPolicies.Any(l => l.Source == LinkSource.ClientTemplate) ||
                clientTemplates.Any(l => l.Source == LinkSource.ClientTemplate))
            {
                clientPolicies.ForEach(l => l.Source = LinkSource.Manual);
                clientPatchPolicies.ForEach(l => l.Source = LinkSource.Manual);
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
        SyncSlots(db.ClientPolicies, clientPolicies, l => l.AppliesTo, l => l.PolicyId, l => l.Source,
            new ClassChoice(template.PolicyId, template.ServerPolicyId, template.WorkstationPolicyId),
            (slot, id) => new ClientPolicy { ClientId = client.Id, AppliesTo = slot, PolicyId = id, Source = LinkSource.ClientTemplate, CreatedAt = now });
        SyncSlots(db.ClientPatchPolicies, clientPatchPolicies, l => l.AppliesTo, l => l.PatchPolicyId, l => l.Source,
            new ClassChoice(template.PatchPolicyId, template.ServerPatchPolicyId, template.WorkstationPatchPolicyId),
            (slot, id) => new ClientPatchPolicy
            {
                ClientId = client.Id, AppliesTo = slot, PatchPolicyId = id, Source = LinkSource.ClientTemplate, CreatedAt = now
            });

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

            // The template decides per slot when it sets one; a manual link on the site is never overwritten.
            var siteId = site.Id;
            SyncSlots(db.SitePolicies, site.Policies.ToList(), l => l.AppliesTo, l => l.PolicyId, l => l.Source,
                new ClassChoice(templateSite.PolicyId, templateSite.ServerPolicyId, templateSite.WorkstationPolicyId),
                (slot, id) => new SitePolicy
                {
                    SiteId = siteId, ClientId = client.Id, AppliesTo = slot, PolicyId = id, Source = LinkSource.ClientTemplate, CreatedAt = now
                });
            SyncSlots(db.SitePatchPolicies, site.PatchPolicies.ToList(), l => l.AppliesTo, l => l.PatchPolicyId, l => l.Source,
                new ClassChoice(templateSite.PatchPolicyId, templateSite.ServerPatchPolicyId, templateSite.WorkstationPatchPolicyId),
                (slot, id) => new SitePatchPolicy
                {
                    SiteId = siteId, ClientId = client.Id, AppliesTo = slot, PatchPolicyId = id, Source = LinkSource.ClientTemplate, CreatedAt = now
                });

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

    /// <summary>
    /// Mirrors the template's choice per class slot in the links of one client or site: a missing link is added, a link of
    /// the template that points elsewhere is replaced, one the template no longer sets is removed. Manual links stay.
    /// </summary>
    private static void SyncSlots<T>(DbSet<T> set, List<T> existing, Func<T, CheckAppliesTo> slotOf, Func<T, Guid> idOf, Func<T, LinkSource> sourceOf,
        ClassChoice wanted, Func<CheckAppliesTo, Guid, T> create) where T : class
    {
        foreach (var slot in ClassChoice.Slots)
        {
            var link = existing.FirstOrDefault(l => slotOf(l) == slot);
            if (wanted[slot] is { } id)
            {
                if (link is null)
                {
                    set.Add(create(slot, id));
                }
                else if (sourceOf(link) == LinkSource.ClientTemplate && idOf(link) != id)
                {
                    set.Remove(link);
                    set.Add(create(slot, id));
                }
            }
            else if (link is not null && sourceOf(link) == LinkSource.ClientTemplate)
            {
                set.Remove(link);
            }
        }
    }

    private static void Detach(Site site, DateTime now)
    {
        site.ClientTemplateSiteId = null;
        site.UpdatedAt = now;
        foreach (var link in site.Policies)
        {
            link.Source = LinkSource.Manual;
        }

        foreach (var link in site.PatchPolicies)
        {
            link.Source = LinkSource.Manual;
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
