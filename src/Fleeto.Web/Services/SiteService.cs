using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Audit;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;
using AuditActions = Fleeto.Core.Interfaces.AuditActions;

namespace Fleeto.Web.Services;

public sealed record EndpointListItem(Guid Id, string Hostname, bool IsOnline, EndpointTier Tier, EndpointClass EffectiveClass,
    string OsName, string OsVersion, string AgentVersion, DateTime? LastSeenAt, int OpenAlertCount);

public sealed record LinkedTemplate(Guid Id, string Name, bool IsGlobal, LinkSource Source);

/// <param name="AppliesTo">The endpoints the policy, patch policy or monitoring template is for (0.6.0).</param>
public sealed record LinkOption(Guid Id, string Name, bool IsGlobal, bool IsDefault = false, CheckAppliesTo AppliesTo = CheckAppliesTo.All);

public sealed record SiteDetail(Guid Id, string Name, string? Description, Guid ClientId, string ClientCode, string ClientName,
    bool FromTemplate, string PolicyName, IReadOnlyList<LinkedTemplate> MonitoringTemplates,
    IReadOnlyList<EndpointListItem> Endpoints);

/// <summary>Site header data without its endpoints: cheap enough for every page load.</summary>
public sealed record SiteSummary(Guid Id, string Name, string? Description, Guid ClientId, string ClientCode, string ClientName, bool FromTemplate,
    string PolicyName, int MonitoringTemplateCount, MaintenancePeriod Maintenance, MaintenancePeriod ClientMaintenance);

/// <summary>Sites of a client: detail with endpoints, create, edit and delete. Their links are saved by <see cref="LinkService"/>.</summary>
public sealed class SiteService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<SiteService> _logger;

    public SiteService(IFleetoDbContextFactory dbFactory, INotificationBus bus, TimeProvider time, ILogger<SiteService> logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _time = time;
        _logger = logger;
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

        var policyNames = await LinkService.SitePolicyNamesAsync(db, site.ClientId, cancellationToken);

        var templates = await db.SiteMonitoringTemplates.AsNoTracking().Where(l => l.SiteId == siteId)
            .OrderBy(l => l.MonitoringTemplate!.Name)
            .Select(l => new LinkedTemplate(l.MonitoringTemplateId, l.MonitoringTemplate!.Name, l.MonitoringTemplate.ClientId == null, l.Source))
            .ToListAsync(cancellationToken);

        var endpoints = await ListEndpointsQuery(db, siteId, _time.GetUtcNow().UtcDateTime).ToListAsync(cancellationToken);

        return new SiteDetail(site.Id, site.Name, site.Description, site.ClientId, site.Code, site.ClientName, site.ClientTemplateSiteId is not null,
            policyNames.GetValueOrDefault(site.Id, "Default policy"), templates, endpoints);
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
                Templates = db.SiteMonitoringTemplates.Count(l => l.SiteId == s.Id),
                Maintenance = new MaintenancePeriod(s.MaintenanceStartedAt, s.MaintenanceEndsAt, s.MaintenanceStartedByName, s.MaintenanceReason),
                ClientMaintenance = new MaintenancePeriod(s.Client.MaintenanceStartedAt, s.Client.MaintenanceEndsAt, s.Client.MaintenanceStartedByName,
                    s.Client.MaintenanceReason)
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (site is null)
        {
            return null;
        }

        var policyName = (await LinkService.SitePolicyNamesAsync(db, site.ClientId, cancellationToken)).GetValueOrDefault(site.Id, "Default policy");
        return new SiteSummary(site.Id, site.Name, site.Description, site.ClientId, site.Code, site.ClientName, site.FromTemplate, policyName,
            site.Templates, site.Maintenance, site.ClientMaintenance);
    }

    public async Task<IReadOnlyList<EndpointListItem>> ListEndpointsAsync(Caller caller, Guid siteId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await ListEndpointsQuery(db, siteId, _time.GetUtcNow().UtcDateTime).ToListAsync(cancellationToken);
    }

    private static IQueryable<EndpointListItem> ListEndpointsQuery(FleetoDbContext db, Guid siteId, DateTime now) =>
        db.Endpoints.AsNoTracking()
            .Where(e => e.SiteId == siteId)
            .OrderBy(e => e.Hostname)
            .Select(e => new EndpointListItem(e.Id, e.Hostname, e.IsOnline, e.Tier, e.ClassOverride ?? e.DetectedClass, e.OsName, e.OsVersion,
                e.AgentVersion, e.LastSeenAt, db.Alerts.Count(a => a.EndpointId == e.Id && a.State != AlertState.Resolved && (a.HeldUntil == null || a.HeldUntil <= now))));

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

        // Action1 follows the sites (0.6.0): the workers give the site its endpoint group.
        await NotifyIfFollowingAsync(db, cancellationToken);
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
        if (previous != cleanName)
        {
            await NotifyIfFollowingAsync(db, cancellationToken);
        }

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
        // Action1 follows the sites (0.6.0): the endpoint group of the site goes too.
        var follow = await IntegrationFollow.ActiveAsync(db, cancellationToken);
        var group = follow is { } integrationId
            ? await db.IntegrationSiteGroups.AsNoTracking().FirstOrDefaultAsync(g => g.IntegrationId == integrationId && g.SiteId == siteId, cancellationToken)
            : null;
        if (group is not null)
        {
            db.IntegrationOperations.Add(IntegrationFollow.Operation(group.IntegrationId, IntegrationOperationKind.DeleteGroup, null,
                group.ExternalTenantId, group.ExternalGroupId, site.Name, now));
        }

        db.Sites.Remove(site);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.SiteDeleted, "Site", site.Id.ToString(), site.ClientId, new { site.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        if (group is not null)
        {
            await IntegrationFollow.NotifyAsync(_bus, _logger, cancellationToken);
        }

        return ServiceResult.Ok();
    }

    private async Task NotifyIfFollowingAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        if (await IntegrationFollow.ActiveAsync(db, cancellationToken) is not null)
        {
            await IntegrationFollow.NotifyAsync(_bus, _logger, cancellationToken);
        }
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
