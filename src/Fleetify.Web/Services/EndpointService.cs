using System.Text.Json;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Services;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Web.Services;

public sealed record EndpointDetail(
    Guid Id, string Hostname, Guid ClientId, string ClientCode, string ClientName, Guid SiteId, string SiteName,
    bool IsOnline, EndpointTier Tier, EndpointSource Source, EndpointClass DetectedClass, EndpointClass? ClassOverride,
    string OsPlatform, string OsName, string OsVersion, string Architecture, string AgentVersion,
    DateTime EnrolledAt, DateTime? LastSeenAt, long ConfigVersion, long AppliedConfigVersion,
    int ActiveCertificates, DateTime? CertificateExpiresAt, int OpenAlertCount, IReadOnlyList<SiteOption> ClientSites);

public sealed record SiteOption(Guid Id, string Name);

public enum EndpointStatusFilter
{
    All,
    Online,
    Offline,
    WithOpenAlerts
}

/// <summary>Which endpoints the endpoint list shows: all clients, one client or one site, plus search and filters.</summary>
public sealed record EndpointListQuery(Guid? ClientId, Guid? SiteId, EndpointClass? Class, string? Search, EndpointStatusFilter Status,
    EndpointTier? Tier);

public sealed record EndpointRow(Guid Id, string Hostname, Guid ClientId, string ClientCode, string ClientName, Guid SiteId, string SiteName,
    bool IsOnline, EndpointTier Tier, EndpointClass EffectiveClass, string OsName, string OsVersion, string AgentVersion, string LoggedOnUser,
    DateTime? LastSeenAt, int OpenAlertCount, bool HasCriticalAlert);

/// <summary>
/// One page of the endpoint list. Counts cover the whole scope and filters; <see cref="Rows"/> holds at most
/// <see cref="EndpointService.ListLimit"/> endpoints of the requested class.
/// </summary>
public sealed record EndpointListPage(IReadOnlyList<EndpointRow> Rows, int ServerCount, int WorkstationCount, bool Truncated)
{
    public int TotalCount => ServerCount + WorkstationCount;
}

/// <summary>Inventory items are agent data: every field has a default, so missing values never break a page.</summary>
public sealed class DiskInfo
{
    public string Mount { get; init; } = string.Empty;
    public string Filesystem { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
}

public sealed class NetworkInterfaceInfo
{
    public string Name { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty;
    public List<string> IpAddresses { get; init; } = [];
}

public sealed class SoftwareInfo
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string InstallDate { get; init; } = string.Empty;
}

public sealed record InventoryView(DateTime ReceivedAt, string Manufacturer, string Model, string SerialNumber, string CpuModel, int CpuCores,
    int CpuLogicalProcessors, long MemoryTotalBytes, DateTime? BootTime, string Domain, string LoggedOnUser,
    IReadOnlyList<DiskInfo> Disks, IReadOnlyList<NetworkInterfaceInfo> NetworkInterfaces, IReadOnlyList<SoftwareInfo> Software);

public sealed record CheckStateView(Guid CheckDefinitionId, string Name, CheckType Type, string Target, CheckStatus Status, double? Value,
    string Detail, string Error, DateTime LastResultAt, int IntervalSeconds, string MonitoringTemplateName, bool Enabled);

public sealed record AlertView(Guid Id, Guid EndpointId, string Hostname, Guid ClientId, string ClientCode, AlertKind Kind, AlertSeverity Severity,
    AlertState State, string Title, string Detail, DateTime OpenedAt, DateTime UpdatedAt, DateTime? AcknowledgedAt, DateTime? ResolvedAt,
    string? ResolvedReason);

public sealed record EndpointEventView(long Id, EndpointEventKind Kind, string Detail, DateTime Time);

public sealed record AuditEntryView(long Id, DateTime Time, Guid? ClientId, AuditActorType ActorType, string ActorId, string ActorName,
    string Action, string TargetType, string TargetId, string DetailsJson, string? IpAddress);

/// <summary>Endpoint detail, inventory, checks and history, plus tier, class, site, revoke and delete operations.</summary>
public sealed class EndpointService
{
    private static readonly JsonSerializerOptions InventoryJson = new() { PropertyNameCaseInsensitive = true };

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly EndpointTierService _tiers;
    private readonly EndpointRevocationService _revocations;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<EndpointService> _logger;

    public EndpointService(IFleetifyDbContextFactory dbFactory, EndpointTierService tiers, EndpointRevocationService revocations,
        INotificationBus bus, TimeProvider time, ILogger<EndpointService> logger)
    {
        _dbFactory = dbFactory;
        _tiers = tiers;
        _revocations = revocations;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    /// <summary>Rows shown at most in one endpoint list; beyond that the page asks to search or narrow the scope.</summary>
    public const int ListLimit = 500;

    /// <summary>
    /// The endpoint list of the clients workspace: all clients, one client or one site, filtered and searched in the
    /// database. Class counts ignore the class filter so the tabs show their totals.
    /// </summary>
    public async Task<EndpointListPage> ListAsync(Caller caller, EndpointListQuery query, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoints = db.Endpoints.AsNoTracking();

        if (query.SiteId is { } siteId)
        {
            endpoints = endpoints.Where(e => e.SiteId == siteId);
        }
        else if (query.ClientId is { } clientId)
        {
            endpoints = endpoints.Where(e => e.ClientId == clientId);
        }

        if (ServiceSupport.Clean(query.Search) is { } term)
        {
            var pattern = ServiceSupport.LikePattern(term);
            endpoints = endpoints.Where(e => EF.Functions.ILike(e.Hostname, pattern) || EF.Functions.ILike(e.OsName, pattern) ||
                                             (e.Inventory != null && EF.Functions.ILike(e.Inventory.LoggedOnUser, pattern)));
        }

        endpoints = query.Status switch
        {
            EndpointStatusFilter.Online => endpoints.Where(e => e.IsOnline),
            EndpointStatusFilter.Offline => endpoints.Where(e => !e.IsOnline),
            EndpointStatusFilter.WithOpenAlerts => endpoints.Where(e => db.Alerts.Any(a => a.EndpointId == e.Id && a.State != AlertState.Resolved)),
            _ => endpoints
        };

        if (query.Tier is { } tier)
        {
            endpoints = endpoints.Where(e => e.Tier == tier);
        }

        var counts = await endpoints
            .GroupBy(e => e.ClassOverride ?? e.DetectedClass)
            .Select(g => new { Class = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var serverCount = counts.Where(c => c.Class == EndpointClass.Server).Sum(c => c.Count);
        var workstationCount = counts.Where(c => c.Class == EndpointClass.Workstation).Sum(c => c.Count);

        if (query.Class is { } endpointClass)
        {
            endpoints = endpoints.Where(e => (e.ClassOverride ?? e.DetectedClass) == endpointClass);
        }

        var rows = await endpoints
            .OrderBy(e => e.Hostname).ThenBy(e => e.Id)
            .Take(ListLimit + 1)
            .Select(e => new EndpointRow(
                e.Id,
                e.Hostname,
                e.ClientId,
                db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Code).FirstOrDefault() ?? string.Empty,
                db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Name).FirstOrDefault() ?? string.Empty,
                e.SiteId,
                e.Site!.Name,
                e.IsOnline,
                e.Tier,
                e.ClassOverride ?? e.DetectedClass,
                e.OsName,
                e.OsVersion,
                e.AgentVersion,
                e.Inventory != null ? e.Inventory.LoggedOnUser : string.Empty,
                e.LastSeenAt,
                db.Alerts.Count(a => a.EndpointId == e.Id && a.State != AlertState.Resolved),
                db.Alerts.Any(a => a.EndpointId == e.Id && a.State != AlertState.Resolved && a.Severity == AlertSeverity.Critical)))
            .ToListAsync(cancellationToken);

        var truncated = rows.Count > ListLimit;
        return new EndpointListPage(truncated ? rows.Take(ListLimit).ToList() : rows, serverCount, workstationCount, truncated);
    }

    public async Task<EndpointDetail?> GetAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime;
        var endpoint = await db.Endpoints.AsNoTracking()
            .Where(e => e.Id == endpointId)
            .Select(e => new
            {
                Endpoint = e,
                SiteName = e.Site!.Name,
                ClientCode = db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Code).FirstOrDefault(),
                ClientName = db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Name).FirstOrDefault(),
                ActiveCertificates = db.AgentCertificates.Count(c => c.EndpointId == e.Id && c.RevokedAt == null && c.ExpiresAt > now),
                CertificateExpiresAt = db.AgentCertificates.Where(c => c.EndpointId == e.Id && c.RevokedAt == null)
                    .OrderByDescending(c => c.ExpiresAt).Select(c => (DateTime?)c.ExpiresAt).FirstOrDefault(),
                OpenAlerts = db.Alerts.Count(a => a.EndpointId == e.Id && a.State != AlertState.Resolved)
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var e = endpoint.Endpoint;
        var sites = await db.Sites.AsNoTracking().Where(s => s.ClientId == e.ClientId).OrderBy(s => s.Name)
            .Select(s => new SiteOption(s.Id, s.Name)).ToListAsync(cancellationToken);

        return new EndpointDetail(e.Id, e.Hostname, e.ClientId, endpoint.ClientCode ?? string.Empty, endpoint.ClientName ?? string.Empty, e.SiteId,
            endpoint.SiteName, e.IsOnline, e.Tier, e.Source, e.DetectedClass, e.ClassOverride, e.OsPlatform, e.OsName, e.OsVersion, e.Architecture,
            e.AgentVersion, e.EnrolledAt, e.LastSeenAt, e.ConfigVersion, e.AppliedConfigVersion, endpoint.ActiveCertificates,
            endpoint.CertificateExpiresAt, endpoint.OpenAlerts, sites);
    }

    public async Task<InventoryView?> GetInventoryAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var snapshot = await db.InventorySnapshots.AsNoTracking().SingleOrDefaultAsync(i => i.EndpointId == endpointId, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        return new InventoryView(snapshot.ReceivedAt, snapshot.Manufacturer, snapshot.Model, snapshot.SerialNumber, snapshot.CpuModel,
            snapshot.CpuCores, snapshot.CpuLogicalProcessors, snapshot.MemoryTotalBytes, snapshot.BootTime, snapshot.Domain, snapshot.LoggedOnUser,
            ParseList<DiskInfo>(snapshot.DisksJson, endpointId), ParseList<NetworkInterfaceInfo>(snapshot.NetworkInterfacesJson, endpointId),
            ParseList<SoftwareInfo>(snapshot.SoftwareJson, endpointId));
    }

    public async Task<IReadOnlyList<CheckStateView>> GetChecksAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await db.CheckStates.AsNoTracking()
            .Where(s => s.EndpointId == endpointId)
            .Join(db.CheckDefinitions, s => s.CheckDefinitionId, d => d.Id, (s, d) => new { State = s, Definition = d })
            .OrderBy(x => x.Definition.Name).ThenBy(x => x.State.Target)
            .Select(x => new CheckStateView(x.Definition.Id, x.Definition.Name, x.Definition.Type, x.State.Target, x.State.Status, x.State.Value,
                x.State.Detail, x.State.Error, x.State.LastResultAt, x.Definition.IntervalSeconds, x.Definition.MonitoringTemplate!.Name,
                x.Definition.Enabled))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AlertView>> GetAlertsAsync(Caller caller, Guid endpointId, int limit = 100, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await AlertService.Project(db, db.Alerts.AsNoTracking().Where(a => a.EndpointId == endpointId)
                .OrderByDescending(a => a.OpenedAt).ThenByDescending(a => a.Id).Take(Math.Clamp(limit, 1, 500)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EndpointEventView>> GetEventsAsync(Caller caller, Guid endpointId, int limit = 100, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await db.EndpointEvents.AsNoTracking()
            .Where(e => e.EndpointId == endpointId)
            .OrderByDescending(e => e.Time).ThenByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(e => new EndpointEventView(e.Id, e.Kind, e.Detail, e.Time))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Audit entries about this endpoint. IP addresses are left out; the full audit log is for admins.</summary>
    public async Task<IReadOnlyList<AuditEntryView>> GetAuditAsync(Caller caller, Guid endpointId, int limit = 100, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var id = endpointId.ToString();
        return await db.AuditEntries.AsNoTracking()
            .Where(a => a.TargetType == "Endpoint" && a.TargetId == id)
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(a => new AuditEntryView(a.Id, a.Time, a.ClientId, a.ActorType, a.ActorId, a.ActorName, a.Action, a.TargetType, a.TargetId,
                a.DetailsJson, null))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Switches endpoints between agent-only and managed. The license problem text is returned verbatim.</summary>
    public async Task<ServiceResult<int>> SetTierAsync(Caller caller, IReadOnlyCollection<Guid> endpointIds, EndpointTier tier,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<int>.Forbidden();
        }

        var result = await _tiers.SetTierAsync(endpointIds.Distinct().ToList(), tier, caller.ToActor(), cancellationToken);
        return result.Success ? ServiceResult<int>.Ok(result.Changed) : ServiceResult<int>.Fail(result.Problem ?? ServiceSupport.GenericProblem);
    }

    /// <summary>Overrides the detected class, or clears the override (null).</summary>
    public async Task<ServiceResult> SetClassOverrideAsync(Caller caller, Guid endpointId, EndpointClass? classOverride,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult.NotFound("endpoint");
        }

        if (endpoint.ClassOverride == classOverride)
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var previous = endpoint.ClassOverride;
        endpoint.ClassOverride = classOverride;
        endpoint.UpdatedAt = now;
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Endpoint, ScopeId = endpoint.Id, CreatedAt = now });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.EndpointClassChanged, "Endpoint", endpoint.Id.ToString(), endpoint.ClientId,
            new { endpoint.Hostname, From = previous?.ToString() ?? "Detected", To = classOverride?.ToString() ?? "Detected", Detected = endpoint.DetectedClass.ToString() }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(endpoint.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Moves the endpoint to another site of the same client. Endpoints never move between clients.</summary>
    public async Task<ServiceResult> MoveAsync(Caller caller, Guid endpointId, Guid targetSiteId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult.NotFound("endpoint");
        }

        if (endpoint.SiteId == targetSiteId)
        {
            return ServiceResult.Ok();
        }

        var target = await db.Sites.AsNoTracking().Where(s => s.Id == targetSiteId).Select(s => new { s.Id, s.ClientId, s.Name }).SingleOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return ServiceResult.NotFound("site");
        }

        if (target.ClientId != endpoint.ClientId)
        {
            return ServiceResult.Fail("An endpoint can only move to another site of the same client. To move it to another client, enroll it again there.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var previousSite = endpoint.SiteId;
        endpoint.SiteId = target.Id;
        endpoint.UpdatedAt = now;
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Endpoint, ScopeId = endpoint.Id, CreatedAt = now });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.EndpointMoved, "Endpoint", endpoint.Id.ToString(), endpoint.ClientId,
            new { endpoint.Hostname, FromSiteId = previousSite, ToSiteId = target.Id, ToSite = target.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(endpoint.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> RevokeAsync(Caller caller, Guid endpointId, string? reason, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        var cleanReason = ServiceSupport.Clean(reason);
        if (cleanReason is null)
        {
            return ServiceResult.Fail("Enter a reason for revoking the agent, for example a stolen laptop or a decommissioned server.");
        }

        if (cleanReason.Length > 500)
        {
            return ServiceResult.Fail("The reason can be at most 500 characters.");
        }

        return await _revocations.RevokeAsync(endpointId, cleanReason, caller.ToActor(), cancellationToken)
            ? ServiceResult.Ok()
            : ServiceResult.NotFound("endpoint");
    }

    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        return await _revocations.DeleteAsync(endpointId, caller.ToActor(), cancellationToken)
            ? ServiceResult.Ok()
            : ServiceResult.NotFound("endpoint");
    }

    private async Task PublishStatusAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        try
        {
            await _bus.PublishAsync(NotificationChannels.EndpointStatus, endpointId.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            // Live updates are a convenience; the change is committed and pages reload it.
            _logger.LogWarning(ex, "Could not publish a status notification for endpoint {EndpointId}", endpointId);
        }
    }

    private IReadOnlyList<T> ParseList<T>(string json, Guid endpointId)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T?>>(json, InventoryJson)?.Where(i => i is not null).Select(i => i!).ToList() ?? [];
        }
        catch (JsonException ex)
        {
            // Agent data is untrusted input: show what can be read, never break the page.
            _logger.LogWarning(ex, "Inventory of endpoint {EndpointId} contains data that could not be read", endpointId);
            return [];
        }
    }
}
