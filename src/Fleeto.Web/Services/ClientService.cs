using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record ClientListItem(Guid Id, string Code, string Name, string? TemplateName, int SiteCount, int EndpointCount,
    int OnlineCount, int OpenAlertCount);

public sealed record SiteListItem(Guid Id, string Name, string? Description, bool FromTemplate, int EndpointCount, int OnlineCount,
    int OpenAlertCount, string? PolicyName);

public sealed record ClientDetail(Guid Id, string Code, string Name, Guid? ClientTemplateId, string? TemplateName, DateTime CreatedAt,
    IReadOnlyList<SiteListItem> Sites, MaintenancePeriod Maintenance);

public sealed record ClientOption(Guid Id, string Code, string Name);

/// <summary>
/// A client in the clients panel, with its sites and live counts. <see cref="InMaintenanceCount"/> counts endpoints in effective
/// maintenance; <see cref="MaintenanceActive"/> is the client's own maintenance.
/// </summary>
public sealed record ClientTreeItem(Guid Id, string Code, string Name, int EndpointCount, int OnlineCount, int OpenAlertCount,
    IReadOnlyList<SiteTreeItem> Sites, bool MaintenanceActive = false, int InMaintenanceCount = 0);

public sealed record SiteTreeItem(Guid Id, string Name, int EndpointCount, int OnlineCount, int OpenAlertCount, bool MaintenanceActive = false,
    int InMaintenanceCount = 0);

/// <summary>Clients: list, create (optionally from a client template), rename, detach from template, delete.</summary>
public sealed class ClientService
{
    /// <summary>Name of the site created for a client without a client template: a client has at least one site.</summary>
    public const string DefaultSiteName = "Main site";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<ClientService> _logger;

    public ClientService(IFleetoDbContextFactory dbFactory, INotificationBus bus, TimeProvider time, ILogger<ClientService> logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ClientListItem>> ListAsync(Caller caller, string? search = null, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime; // alerts on hold are not counted as open
        var query = db.Clients.AsNoTracking();
        if (ServiceSupport.Clean(search) is { } term)
        {
            var pattern = ServiceSupport.LikePattern(term);
            query = query.Where(c => EF.Functions.ILike(c.Code, pattern) || EF.Functions.ILike(c.Name, pattern));
        }

        return await query
            .OrderBy(c => c.Code)
            .Take(1000)
            .Select(c => new ClientListItem(
                c.Id,
                c.Code,
                c.Name,
                db.ClientTemplates.Where(t => t.Id == c.ClientTemplateId).Select(t => t.Name).FirstOrDefault(),
                db.Sites.Count(s => s.ClientId == c.Id),
                db.Endpoints.Count(e => e.ClientId == c.Id),
                db.Endpoints.Count(e => e.ClientId == c.Id && e.IsOnline),
                db.Alerts.Count(a => a.ClientId == c.Id && a.State != AlertState.Resolved && (a.HeldUntil == null || a.HeldUntil <= now))))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Clients with their sites for the clients panel. A search on code or name keeps the matching clients; a search that
    /// matches a site name keeps that site's client too.
    /// </summary>
    public async Task<IReadOnlyList<ClientTreeItem>> ListTreeAsync(Caller caller, string? search = null, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime; // alerts on hold are not counted as open
        var clients = db.Clients.AsNoTracking();
        if (ServiceSupport.Clean(search) is { } term)
        {
            var pattern = ServiceSupport.LikePattern(term);
            clients = clients.Where(c => EF.Functions.ILike(c.Code, pattern) || EF.Functions.ILike(c.Name, pattern) ||
                                         db.Sites.Any(s => s.ClientId == c.Id && EF.Functions.ILike(s.Name, pattern)));
        }

        var clientRows = await clients
            .OrderBy(c => c.Code)
            .Take(1000)
            .Select(c => new
            {
                c.Id,
                c.Code,
                c.Name,
                Endpoints = db.Endpoints.Count(e => e.ClientId == c.Id),
                Online = db.Endpoints.Count(e => e.ClientId == c.Id && e.IsOnline),
                Alerts = db.Alerts.Count(a => a.ClientId == c.Id && a.State != AlertState.Resolved && (a.HeldUntil == null || a.HeldUntil <= now)),
                Maintenance = c.MaintenanceStartedAt != null && c.MaintenanceStartedAt <= now && (c.MaintenanceEndsAt == null || c.MaintenanceEndsAt > now)
            })
            .ToListAsync(cancellationToken);

        var clientIds = clientRows.Select(c => c.Id).ToList();
        var siteRows = await db.Sites.AsNoTracking()
            .Where(s => clientIds.Contains(s.ClientId))
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.ClientId,
                Item = new SiteTreeItem(
                    s.Id,
                    s.Name,
                    db.Endpoints.Count(e => e.SiteId == s.Id),
                    db.Endpoints.Count(e => e.SiteId == s.Id && e.IsOnline),
                    db.Alerts.Count(a => a.ClientId == s.ClientId && a.State != AlertState.Resolved && (a.HeldUntil == null || a.HeldUntil <= now) &&
                                         db.Endpoints.Any(e => e.Id == a.EndpointId && e.SiteId == s.Id)),
                    s.MaintenanceStartedAt != null && s.MaintenanceStartedAt <= now && (s.MaintenanceEndsAt == null || s.MaintenanceEndsAt > now))
            })
            .ToListAsync(cancellationToken);

        // One grouped count for the whole tree instead of a subquery per client and site.
        var inMaintenance = await db.Endpoints.AsNoTracking()
            .Where(e => clientIds.Contains(e.ClientId))
            .Where(MaintenanceRules.EndpointInMaintenance(now, db.MaintenanceWindowOccurrences, db.SitePolicies, db.Policies))
            .GroupBy(e => new { e.ClientId, e.SiteId })
            .Select(g => new { g.Key.ClientId, g.Key.SiteId, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var siteMaintenance = inMaintenance.ToDictionary(m => m.SiteId, m => m.Count);
        var clientMaintenance = inMaintenance.GroupBy(m => m.ClientId).ToDictionary(g => g.Key, g => g.Sum(m => m.Count));

        var sitesByClient = siteRows.GroupBy(s => s.ClientId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<SiteTreeItem>)g.Select(s => s.Item with { InMaintenanceCount = siteMaintenance.GetValueOrDefault(s.Item.Id) }).ToList());
        return clientRows
            .Select(c => new ClientTreeItem(c.Id, c.Code, c.Name, c.Endpoints, c.Online, c.Alerts, sitesByClient.GetValueOrDefault(c.Id, []),
                c.Maintenance, clientMaintenance.GetValueOrDefault(c.Id)))
            .ToList();
    }

    public async Task<IReadOnlyList<ClientOption>> ListOptionsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await db.Clients.AsNoTracking().OrderBy(c => c.Code)
            .Select(c => new ClientOption(c.Id, c.Code, c.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<ClientDetail?> GetAsync(Caller caller, Guid clientId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime; // alerts on hold are not counted as open
        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => new
            {
                c.Id,
                c.Code,
                c.Name,
                c.ClientTemplateId,
                TemplateName = db.ClientTemplates.Where(t => t.Id == c.ClientTemplateId).Select(t => t.Name).FirstOrDefault(),
                c.CreatedAt,
                Maintenance = new MaintenancePeriod(c.MaintenanceStartedAt, c.MaintenanceEndsAt, c.MaintenanceStartedByName, c.MaintenanceReason)
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (client is null)
        {
            return null;
        }

        var defaultPolicy = await db.Policies.AsNoTracking().Where(p => p.IsDefault).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken);
        var sites = await db.Sites.AsNoTracking()
            .Where(s => s.ClientId == clientId)
            .OrderBy(s => s.Name)
            .Select(s => new SiteListItem(
                s.Id,
                s.Name,
                s.Description,
                s.ClientTemplateSiteId != null,
                db.Endpoints.Count(e => e.SiteId == s.Id),
                db.Endpoints.Count(e => e.SiteId == s.Id && e.IsOnline),
                db.Alerts.Count(a => a.State != AlertState.Resolved && (a.HeldUntil == null || a.HeldUntil <= now) && db.Endpoints.Any(e => e.Id == a.EndpointId && e.SiteId == s.Id)),
                db.SitePolicies.Where(l => l.SiteId == s.Id).Select(l => l.Policy!.Name).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        sites = sites.Select(s => s with { PolicyName = s.PolicyName ?? defaultPolicy }).ToList();
        return new ClientDetail(client.Id, client.Code, client.Name, client.ClientTemplateId, client.TemplateName, client.CreatedAt, sites,
            client.Maintenance);
    }

    /// <summary>
    /// Creates a client. With a client template, the template's sites and their links are created in the same unit of
    /// work (<see cref="ClientTemplateSync"/>); without one, a single site named <see cref="DefaultSiteName"/> is created.
    /// </summary>
    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, string? code, string? name, Guid? clientTemplateId,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        var normalizedCode = ClientCode.Normalize(code);
        if (ClientCode.Validate(normalizedCode) is { } codeProblem)
        {
            return ServiceResult<Guid>.Fail(codeProblem);
        }

        var cleanName = ServiceSupport.Clean(name);
        if (cleanName is null || cleanName.Length > 200)
        {
            return ServiceResult<Guid>.Fail("Enter a client name of at most 200 characters.");
        }

        try
        {
            await using var db = _dbFactory.Create(caller.Scope);
            if (await db.Clients.IgnoreQueryFilters().AnyAsync(c => c.Code == normalizedCode, cancellationToken))
            {
                return ServiceResult<Guid>.Fail($"A client with code {normalizedCode} already exists. Choose another code.");
            }

            string? templateName = null;
            if (clientTemplateId is { } templateId)
            {
                templateName = await db.ClientTemplates.Where(t => t.Id == templateId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken);
                if (templateName is null)
                {
                    return ServiceResult<Guid>.NotFound("client template");
                }
            }

            var now = _time.GetUtcNow().UtcDateTime;
            var client = new Client
            {
                Id = Guid.NewGuid(),
                Code = normalizedCode,
                Name = cleanName,
                ClientTemplateId = clientTemplateId,
                CreatedAt = now,
                UpdatedAt = now
            };

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.Clients.Add(client);
            await db.SaveChangesAsync(cancellationToken);

            if (clientTemplateId is not null)
            {
                await ClientTemplateSync.ApplyAsync(db, client, now, cancellationToken);
            }
            else
            {
                db.Sites.Add(new Site { Id = Guid.NewGuid(), ClientId = client.Id, Name = DefaultSiteName, CreatedAt = now, UpdatedAt = now });
            }

            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ClientCreated, "Client", client.Id.ToString(), client.Id,
                new { client.Code, client.Name, ClientTemplate = templateName }), now));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<Guid>.Ok(client.Id);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return ServiceResult<Guid>.Fail($"A client with code {normalizedCode} already exists. Choose another code.");
        }
    }

    public async Task<ServiceResult> RenameAsync(Caller caller, Guid clientId, string? name, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        var cleanName = ServiceSupport.Clean(name);
        if (cleanName is null || cleanName.Length > 200)
        {
            return ServiceResult.Fail("Enter a client name of at most 200 characters.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken);
        if (client is null)
        {
            return ServiceResult.NotFound("client");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var previous = client.Name;
        client.Name = cleanName;
        client.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ClientUpdated, "Client", client.Id.ToString(), client.Id,
            new { client.Code, From = previous, To = cleanName }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Stops following the client template. Sites and links stay; template-sourced links become manual, so later template
    /// changes no longer apply to this client.
    /// </summary>
    public async Task<ServiceResult> DetachFromTemplateAsync(Caller caller, Guid clientId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken);
        if (client is null)
        {
            return ServiceResult.NotFound("client");
        }

        if (client.ClientTemplateId is null)
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var templateId = client.ClientTemplateId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        client.ClientTemplateId = null;
        client.UpdatedAt = now;
        await ClientTemplateSync.ApplyAsync(db, client, now, cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ClientDetachedFromTemplate, "Client", client.Id.ToString(), client.Id,
            new { client.Code, ClientTemplateId = templateId }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Deletes the client and, through cascading foreign keys, every site, endpoint and all other data of the client.
    /// The caller must type the client code. Revocations are published so the gateway drops live agent connections.
    /// </summary>
    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid clientId, string? typedCode, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken);
        if (client is null)
        {
            return ServiceResult.NotFound("client");
        }

        if (!string.Equals(ClientCode.Normalize(typedCode), client.Code, StringComparison.Ordinal))
        {
            return ServiceResult.Fail($"Type the client code {client.Code} to confirm the deletion.");
        }

        var endpointIds = await db.Endpoints.Where(e => e.ClientId == clientId).Select(e => e.Id).ToListAsync(cancellationToken);
        var siteCount = await db.Sites.CountAsync(s => s.ClientId == clientId, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;

        db.Clients.Remove(client);
        // ClientId stays null on purpose: the client no longer exists, the entry belongs to the instance.
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ClientDeleted, "Client", client.Id.ToString(), null,
            new { client.Code, client.Name, Sites = siteCount, Endpoints = endpointIds.Count }), now));
        await db.SaveChangesAsync(cancellationToken);

        foreach (var endpointId in endpointIds)
        {
            try
            {
                await _bus.PublishAsync(NotificationChannels.Revocations, endpointId.ToString(), cancellationToken);
                await _bus.PublishAsync(NotificationChannels.EndpointStatus, endpointId.ToString(), cancellationToken);
            }
            catch (Exception ex)
            {
                // The deletion is committed; the gateway's periodic allow-list reload drops the connection anyway.
                _logger.LogWarning(ex, "Could not publish the revocation of endpoint {EndpointId} after deleting client {ClientId}", endpointId, clientId);
            }
        }

        return ServiceResult.Ok();
    }
}
