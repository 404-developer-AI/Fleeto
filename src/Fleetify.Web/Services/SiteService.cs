using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;
using AuditActions = Fleetify.Core.Interfaces.AuditActions;

namespace Fleetify.Web.Services;

public sealed record EndpointListItem(Guid Id, string Hostname, bool IsOnline, EndpointTier Tier, EndpointClass EffectiveClass,
    string OsName, string OsVersion, string AgentVersion, DateTime? LastSeenAt, int OpenAlertCount);

public sealed record LinkedTemplate(Guid Id, string Name, bool IsGlobal, LinkSource Source);

public sealed record LinkOption(Guid Id, string Name, bool IsGlobal, bool IsDefault = false);

public sealed record SiteDetail(Guid Id, string Name, string? Description, Guid ClientId, string ClientCode, string ClientName,
    bool FromTemplate, Guid? PolicyId, string PolicyName, LinkSource? PolicySource, IReadOnlyList<LinkedTemplate> MonitoringTemplates,
    IReadOnlyList<EndpointListItem> Endpoints);

/// <summary>Site header data without its endpoints: cheap enough for every page load.</summary>
public sealed record SiteSummary(Guid Id, string Name, string? Description, Guid ClientId, string ClientCode, string ClientName, bool FromTemplate,
    string PolicyName, int MonitoringTemplateCount);

/// <summary>Sites of a client: detail with endpoints, create, edit, delete, and the policy and monitoring template links.</summary>
public sealed class SiteService
{
    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public SiteService(IFleetifyDbContextFactory dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task<SiteDetail?> GetAsync(Caller caller, Guid siteId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var site = await db.Sites.AsNoTracking()
            .Where(s => s.Id == siteId)
            .Select(s => new { s.Id, s.Name, s.Description, s.ClientId, s.Client!.Code, ClientName = s.Client.Name, s.ClientTemplateSiteId })
            .SingleOrDefaultAsync(cancellationToken);
        if (site is null)
        {
            return null;
        }

        var policy = await db.SitePolicies.AsNoTracking().Where(l => l.SiteId == siteId)
            .Select(l => new { l.PolicyId, l.Policy!.Name, l.Source })
            .FirstOrDefaultAsync(cancellationToken);
        var defaultPolicyName = policy is null
            ? await db.Policies.AsNoTracking().Where(p => p.IsDefault).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken) ?? "Default policy"
            : null;

        var templates = await db.SiteMonitoringTemplates.AsNoTracking().Where(l => l.SiteId == siteId)
            .OrderBy(l => l.MonitoringTemplate!.Name)
            .Select(l => new LinkedTemplate(l.MonitoringTemplateId, l.MonitoringTemplate!.Name, l.MonitoringTemplate.ClientId == null, l.Source))
            .ToListAsync(cancellationToken);

        var endpoints = await ListEndpointsQuery(db, siteId).ToListAsync(cancellationToken);

        return new SiteDetail(site.Id, site.Name, site.Description, site.ClientId, site.Code, site.ClientName, site.ClientTemplateSiteId is not null,
            policy?.PolicyId, policy?.Name ?? defaultPolicyName!, policy?.Source, templates, endpoints);
    }

    public async Task<SiteSummary?> GetSummaryAsync(Caller caller, Guid siteId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var site = await db.Sites.AsNoTracking()
            .Where(s => s.Id == siteId)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Description,
                s.ClientId,
                s.Client!.Code,
                ClientName = s.Client.Name,
                FromTemplate = s.ClientTemplateSiteId != null,
                PolicyName = db.SitePolicies.Where(l => l.SiteId == s.Id).Select(l => l.Policy!.Name).FirstOrDefault(),
                Templates = db.SiteMonitoringTemplates.Count(l => l.SiteId == s.Id)
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (site is null)
        {
            return null;
        }

        var policyName = site.PolicyName
                         ?? await db.Policies.AsNoTracking().Where(p => p.IsDefault).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken)
                         ?? "Default policy";
        return new SiteSummary(site.Id, site.Name, site.Description, site.ClientId, site.Code, site.ClientName, site.FromTemplate, policyName,
            site.Templates);
    }

    public async Task<IReadOnlyList<EndpointListItem>> ListEndpointsAsync(Caller caller, Guid siteId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await ListEndpointsQuery(db, siteId).ToListAsync(cancellationToken);
    }

    private static IQueryable<EndpointListItem> ListEndpointsQuery(FleetifyDbContext db, Guid siteId) =>
        db.Endpoints.AsNoTracking()
            .Where(e => e.SiteId == siteId)
            .OrderBy(e => e.Hostname)
            .Select(e => new EndpointListItem(e.Id, e.Hostname, e.IsOnline, e.Tier, e.ClassOverride ?? e.DetectedClass, e.OsName, e.OsVersion,
                e.AgentVersion, e.LastSeenAt, db.Alerts.Count(a => a.EndpointId == e.Id && a.State != AlertState.Resolved)));

    /// <summary>Policies and monitoring templates this site can link: global ones and those of its own client.</summary>
    public async Task<(IReadOnlyList<LinkOption> Policies, IReadOnlyList<LinkOption> MonitoringTemplates)> GetLinkOptionsAsync(Caller caller,
        Guid clientId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var policies = await db.Policies.AsNoTracking()
            .Where(p => p.ClientId == null || p.ClientId == clientId)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.ClientId != null).ThenBy(p => p.Name)
            .Select(p => new LinkOption(p.Id, p.Name, p.ClientId == null, p.IsDefault))
            .ToListAsync(cancellationToken);
        var templates = await db.MonitoringTemplates.AsNoTracking()
            .Where(t => t.ClientId == null || t.ClientId == clientId)
            .OrderBy(t => t.ClientId != null).ThenBy(t => t.Name)
            .Select(t => new LinkOption(t.Id, t.Name, t.ClientId == null))
            .ToListAsync(cancellationToken);
        return (policies, templates);
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, Guid clientId, string? name, string? description,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        if (Validate(name, description) is { } problem)
        {
            return ServiceResult<Guid>.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        if (!await db.Clients.AnyAsync(c => c.Id == clientId, cancellationToken))
        {
            return ServiceResult<Guid>.NotFound("client");
        }

        var cleanName = name!.Trim();
        if (await db.Sites.AnyAsync(s => s.ClientId == clientId && s.Name.ToLower() == cleanName.ToLower(), cancellationToken))
        {
            return ServiceResult<Guid>.Fail($"This client already has a site named {cleanName}. Choose another name.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var site = new Site
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = cleanName,
            Description = ServiceSupport.Clean(description),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Sites.Add(site);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.SiteCreated, "Site", site.Id.ToString(), clientId, new { site.Name }), now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return ServiceResult<Guid>.Fail($"This client already has a site named {cleanName}. Choose another name.");
        }

        return ServiceResult<Guid>.Ok(site.Id);
    }

    public async Task<ServiceResult> UpdateAsync(Caller caller, Guid siteId, string? name, string? description,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        if (Validate(name, description) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var site = await db.Sites.SingleOrDefaultAsync(s => s.Id == siteId, cancellationToken);
        if (site is null)
        {
            return ServiceResult.NotFound("site");
        }

        // A site from a client template can be renamed: the template follows it by id, not by name.
        var cleanName = name!.Trim();
        if (await db.Sites.AnyAsync(s => s.ClientId == site.ClientId && s.Id != siteId && s.Name.ToLower() == cleanName.ToLower(), cancellationToken))
        {
            return ServiceResult.Fail($"This client already has a site named {cleanName}. Choose another name.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var previous = site.Name;
        site.Name = cleanName;
        site.Description = ServiceSupport.Clean(description);
        site.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.SiteUpdated, "Site", site.Id.ToString(), site.ClientId,
            new { From = previous, To = cleanName }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid siteId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var site = await db.Sites.SingleOrDefaultAsync(s => s.Id == siteId, cancellationToken);
        if (site is null)
        {
            return ServiceResult.NotFound("site");
        }

        if (await db.Endpoints.AnyAsync(e => e.SiteId == siteId, cancellationToken))
        {
            return ServiceResult.Fail("Move or delete the endpoints of this site first.");
        }

        if (site.ClientTemplateSiteId is not null &&
            await db.Clients.AnyAsync(c => c.Id == site.ClientId && c.ClientTemplateId != null, cancellationToken))
        {
            return ServiceResult.Fail("This site comes from the client template and would be created again. Remove it from the client template, or detach the client first.");
        }

        if (await db.Sites.CountAsync(s => s.ClientId == site.ClientId, cancellationToken) <= 1)
        {
            return ServiceResult.Fail("A client needs at least one site. Create another site before deleting this one.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.Sites.Remove(site);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.SiteDeleted, "Site", site.Id.ToString(), site.ClientId, new { site.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Links a policy to the site, or removes the link (null) so the default policy applies.</summary>
    public async Task<ServiceResult> SetPolicyAsync(Caller caller, Guid siteId, Guid? policyId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var site = await db.Sites.Include(s => s.Policy).SingleOrDefaultAsync(s => s.Id == siteId, cancellationToken);
        if (site is null)
        {
            return ServiceResult.NotFound("site");
        }

        Policy? policy = null;
        if (policyId is { } id)
        {
            policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && (p.ClientId == null || p.ClientId == site.ClientId), cancellationToken);
            if (policy is null)
            {
                return ServiceResult.NotFound("policy");
            }

            if (policy.IsDefault)
            {
                // Linking the default policy explicitly means the same as no link.
                policyId = null;
            }
        }

        if (site.Policy?.PolicyId == policyId)
        {
            return ServiceResult.Ok();
        }

        if (site.Policy is null && policyId is null)
        {
            return ServiceResult.Ok();
        }

        if (site.Policy is { Source: LinkSource.ClientTemplate } && policyId is null)
        {
            return ServiceResult.Fail("The client template links this policy and would link it again. Change the client template, or pick another policy for this site.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (site.Policy is not null)
        {
            db.SitePolicies.Remove(site.Policy);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (policyId is not null)
        {
            db.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = site.ClientId, PolicyId = policyId.Value, Source = LinkSource.Manual, CreatedAt = now });
        }

        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Site, ScopeId = site.Id, CreatedAt = now });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.SiteLinksChanged, "Site", site.Id.ToString(), site.ClientId,
            new { Policy = policy is null || policy.IsDefault ? "Default policy" : policy.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Sets the manually linked monitoring templates of the site. Links maintained by the client template cannot be removed
    /// here; they are kept whatever the selection says.
    /// </summary>
    public async Task<ServiceResult> SetMonitoringTemplatesAsync(Caller caller, Guid siteId, IReadOnlyCollection<Guid> monitoringTemplateIds,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var site = await db.Sites.Include(s => s.MonitoringTemplates).SingleOrDefaultAsync(s => s.Id == siteId, cancellationToken);
        if (site is null)
        {
            return ServiceResult.NotFound("site");
        }

        var wanted = monitoringTemplateIds.ToHashSet();
        var allowed = await db.MonitoringTemplates.AsNoTracking()
            .Where(t => wanted.Contains(t.Id) && (t.ClientId == null || t.ClientId == site.ClientId))
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(cancellationToken);
        if (allowed.Count != wanted.Count)
        {
            return ServiceResult.NotFound("monitoring template");
        }

        var templateSourced = site.MonitoringTemplates.Where(l => l.Source == LinkSource.ClientTemplate).Select(l => l.MonitoringTemplateId).ToHashSet();
        if (templateSourced.Any(id => !wanted.Contains(id)))
        {
            return ServiceResult.Fail("A monitoring template from the client template cannot be removed here. Change the client template, or detach the client first.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var removed = site.MonitoringTemplates.Where(l => l.Source == LinkSource.Manual && !wanted.Contains(l.MonitoringTemplateId)).ToList();
        var added = wanted.Where(id => site.MonitoringTemplates.All(l => l.MonitoringTemplateId != id)).ToList();
        if (removed.Count == 0 && added.Count == 0)
        {
            return ServiceResult.Ok();
        }

        db.SiteMonitoringTemplates.RemoveRange(removed);
        foreach (var id in added)
        {
            db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate
            {
                SiteId = site.Id,
                ClientId = site.ClientId,
                MonitoringTemplateId = id,
                Source = LinkSource.Manual,
                CreatedAt = now
            });
        }

        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Site, ScopeId = site.Id, CreatedAt = now });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.SiteLinksChanged, "Site", site.Id.ToString(), site.ClientId,
            new
            {
                Added = allowed.Where(t => added.Contains(t.Id)).Select(t => t.Name).ToList(),
                Removed = removed.Select(l => l.MonitoringTemplateId).ToList()
            }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private static string? Validate(string? name, string? description)
    {
        var cleanName = ServiceSupport.Clean(name);
        if (cleanName is null || cleanName.Length > 100)
        {
            return "Enter a site name of at most 100 characters.";
        }

        return description is { Length: > 1000 } ? "The description can be at most 1000 characters." : null;
    }
}
