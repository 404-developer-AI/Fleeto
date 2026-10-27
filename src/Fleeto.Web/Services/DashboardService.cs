using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Web.Services;

/// <param name="ExternalDescription">How the backups are made outside Fleeto (0.6.0), or null when Fleeto makes them.</param>
public sealed record BackupTile(bool DestinationConfigured, DateTime? LastSucceededAt, bool LastRunFailed, string? LastError,
    string? ExternalDescription = null);

/// <summary>A stored credential that expires within 30 days or has expired (0.2.0).</summary>
public sealed record CredentialWarning(string Name, DateTime ExpiresAt, bool Expired, string StopsWorking, string NextStep, string SettingsPath,
    bool FallbackInUse);

/// <summary>Counts of the endpoints in a scope (0.8.0), shared by the endpoint counters of the dashboard.</summary>
public sealed record EndpointCounts(int Total, int Online, int Managed, int OutOfDate, int InMaintenance, string CurrentVersion)
{
    public int Offline => Total - Online;
    public int AgentOnly => Total - Managed;
}

/// <summary>Open alerts by severity, without the alerts on hold, and the alerts on hold (0.8.0).</summary>
public sealed record AlertCounts(int Critical, int Warning, int OnHold);

/// <summary>An endpoint in a list tile of the dashboard (0.8.0).</summary>
public sealed record DashboardEndpointRow(Guid Id, string ClientCode, string Hostname, bool IsOnline, DateTime? LastSeenAt, string AgentVersion);

/// <summary>An endpoint of a task run in the task runs tile (0.8.0).</summary>
public sealed record DashboardTaskRunRow(Guid TaskId, string TaskName, Guid EndpointId, string ClientCode, string Hostname, bool Failed,
    DateTime When);

/// <summary>A bar of a chart (0.8.0).</summary>
public sealed record ChartBar(string Label, int Value, int Critical = 0, Guid? Id = null);

/// <summary>Alerts opened on one day (UTC), by severity.</summary>
public sealed record DailyAlerts(DateOnly Day, int Warning, int Critical);

/// <summary>
/// The data of the dashboard tiles (0.8.0, DASHBOARDS.md §6). Each method reads one source for one scope; the page asks only the
/// sources of the tiles it shows. Every method follows the permission of what it shows and the scope of the caller, which a tile
/// scope narrows (<see cref="DashboardScopes"/>).
/// </summary>
public sealed class DashboardService
{
    /// <summary>A chart of endpoints shows this many groups, the rest as "Other".</summary>
    public const int MaxChartBars = 8;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<DashboardService> _logger;

    public DashboardService(IFleetoDbContextFactory dbFactory, LicenseService licenses, SettingsStore settings, TimeProvider time,
        ILogger<DashboardService> logger)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    public async Task<EndpointCounts?> GetEndpointCountsAsync(Caller caller, EndpointClass? cls, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ClientsView))
        {
            return null;
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime;
        // Out of date means older than the agent release this instance offers (0.2.1), or than the server when none is loaded.
        var version = await db.AgentReleases.AsNoTracking().Where(r => r.IsCurrent).Select(r => r.Version).FirstOrDefaultAsync(cancellationToken)
                      ?? FleetoVersion.Current;
        var endpoints = OfClass(db.Endpoints.AsNoTracking(), cls);
        var counts = await endpoints.GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Online = g.Count(e => e.IsOnline), Managed = g.Count(e => e.Tier == EndpointTier.Managed) })
            .FirstOrDefaultAsync(cancellationToken);
        var outOfDate = (await endpoints.Where(e => e.Source == EndpointSource.Agent).GroupBy(e => e.AgentVersion)
                .Select(g => new { Version = g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
            .Where(v => SemanticVersion.IsOlder(v.Version, version)).Sum(v => v.Count);
        var inMaintenance = await endpoints
            .Where(MaintenanceRules.EndpointInMaintenance(now, db.MaintenanceWindowOccurrences, EffectivePolicies.Query(db)))
            .CountAsync(cancellationToken);
        return new EndpointCounts(counts?.Total ?? 0, counts?.Online ?? 0, counts?.Managed ?? 0, outOfDate, inMaintenance, version);
    }

    public async Task<AlertCounts?> GetAlertCountsAsync(Caller caller, EndpointClass? cls, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.AlertsView))
        {
            return null;
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime;
        var unresolved = OfClass(db, db.Alerts.AsNoTracking().Where(a => a.State != AlertState.Resolved), cls);
        var counts = await unresolved
            .GroupBy(a => new { a.Severity, Held = a.HeldUntil != null && a.HeldUntil > now })
            .Select(g => new { g.Key.Severity, g.Key.Held, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return new AlertCounts(
            counts.Where(c => !c.Held && c.Severity == AlertSeverity.Critical).Sum(c => c.Count),
            counts.Where(c => !c.Held && c.Severity == AlertSeverity.Warning).Sum(c => c.Count),
            counts.Where(c => c.Held).Sum(c => c.Count));
    }

    /// <summary>The newest open alerts. Alerts on hold are left out unless <paramref name="includeHeld"/>.</summary>
    public async Task<IReadOnlyList<AlertView>> GetOpenAlertsAsync(Caller caller, EndpointClass? cls, AlertSeverity? severity,
        IReadOnlyCollection<AlertKind>? kinds, bool includeHeld, int limit, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.AlertsView))
        {
            return [];
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime;
        var alerts = OfClass(db, db.Alerts.AsNoTracking().Where(a => a.State != AlertState.Resolved), cls);
        if (!includeHeld)
        {
            alerts = alerts.Where(a => a.HeldUntil == null || a.HeldUntil <= now);
        }

        if (severity is { } wanted)
        {
            alerts = alerts.Where(a => a.Severity == wanted);
        }

        if (kinds is { Count: > 0 })
        {
            var list = kinds.ToArray();
            alerts = alerts.Where(a => list.Contains(a.Kind));
        }

        return await AlertService.Project(db, alerts.OrderByDescending(a => a.OpenedAt).ThenByDescending(a => a.Id).Take(Math.Clamp(limit, 1, 50)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The newest entries of the audit log in the caller's scope.</summary>
    public async Task<IReadOnlyList<AuditEntryView>> GetRecentActivityAsync(Caller caller, int limit, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ClientsView))
        {
            return [];
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var entries = db.AuditEntries.AsNoTracking();
        if (!caller.Scope.AllClients)
        {
            // A narrower scope shows the activity of its clients only, not the instance-wide entries every user may see.
            var clients = caller.Scope.ClientIds.ToArray();
            entries = entries.Where(a => a.ClientId != null && clients.Contains(a.ClientId.Value));
        }

        return await entries
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(a => new AuditEntryView(a.Id, a.Time, a.ClientId, a.ActorType, a.ActorId, a.ActorName, a.Action, a.TargetType, a.TargetId,
                a.DetailsJson, null))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Endpoints not connected, longest offline first.</summary>
    public async Task<IReadOnlyList<DashboardEndpointRow>> GetOfflineEndpointsAsync(Caller caller, EndpointClass? cls, EndpointTier? tier, int limit,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ClientsView))
        {
            return [];
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoints = OfClass(db.Endpoints.AsNoTracking().Where(e => !e.IsOnline && e.Source == EndpointSource.Agent), cls);
        if (tier is { } wanted)
        {
            endpoints = endpoints.Where(e => e.Tier == wanted);
        }

        return await Rows(db, endpoints.OrderBy(e => e.LastSeenAt == null ? 0 : 1).ThenBy(e => e.LastSeenAt).ThenBy(e => e.Hostname)
            .Take(Math.Clamp(limit, 1, 50))).ToListAsync(cancellationToken);
    }

    /// <summary>Endpoints whose agent is older than the current release, oldest version first.</summary>
    public async Task<IReadOnlyList<DashboardEndpointRow>> GetOutOfDateEndpointsAsync(Caller caller, EndpointClass? cls, int limit,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ClientsView))
        {
            return [];
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var version = await db.AgentReleases.AsNoTracking().Where(r => r.IsCurrent).Select(r => r.Version).FirstOrDefaultAsync(cancellationToken)
                      ?? FleetoVersion.Current;
        var endpoints = OfClass(db.Endpoints.AsNoTracking().Where(e => e.Source == EndpointSource.Agent), cls);
        var olderVersions = (await endpoints.Select(e => e.AgentVersion).Distinct().ToListAsync(cancellationToken))
            .Where(v => SemanticVersion.IsOlder(v, version)).ToArray();
        if (olderVersions.Length == 0)
        {
            return [];
        }

        var rows = await Rows(db, endpoints.Where(e => olderVersions.Contains(e.AgentVersion)).OrderBy(e => e.Hostname).Take(200))
            .ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.AgentVersion, Comparer<string>.Create((a, b) =>
                SemanticVersion.IsOlder(a, b) ? -1 : SemanticVersion.IsOlder(b, a) ? 1 : 0))
            .ThenBy(r => r.Hostname, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 50)).ToList();
    }

    /// <summary>Endpoints of task runs that are running or failed, newest first.</summary>
    public async Task<IReadOnlyList<DashboardTaskRunRow>> GetTaskRunsAsync(Caller caller, bool failedOnly, int limit,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.TasksView))
        {
            return [];
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var targets = db.AutomationTaskRunTargets.AsNoTracking()
            .Where(t => t.Outcome == TaskTargetOutcome.Failed || (!failedOnly && t.State == TaskTargetState.Started && t.FinishedAt == null));
        return await targets
            .OrderByDescending(t => t.FinishedAt ?? t.DueAt)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(t => new DashboardTaskRunRow(t.AutomationTaskId,
                db.AutomationTasks.Where(a => a.Id == t.AutomationTaskId).Select(a => a.Name).FirstOrDefault() ?? string.Empty,
                t.EndpointId,
                db.Clients.Where(c => c.Id == t.ClientId).Select(c => c.Code).FirstOrDefault() ?? string.Empty,
                db.Endpoints.Where(e => e.Id == t.EndpointId).Select(e => e.Hostname).FirstOrDefault() ?? string.Empty,
                t.Outcome == TaskTargetOutcome.Failed,
                t.FinishedAt ?? t.DueAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Endpoints grouped by operating system, class or tier; the largest groups, the rest as "Other".</summary>
    public async Task<IReadOnlyList<ChartBar>> GetEndpointGroupsAsync(Caller caller, EndpointClass? cls, Core.Dashboards.EndpointGrouping grouping,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ClientsView))
        {
            return [];
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoints = OfClass(db.Endpoints.AsNoTracking(), cls);
        List<ChartBar> bars = grouping switch
        {
            Core.Dashboards.EndpointGrouping.Class => (await endpoints.GroupBy(e => e.ClassOverride ?? e.DetectedClass)
                    .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
                .Select(g => new ChartBar(g.Key == EndpointClass.Server ? "Servers" : "Workstations", g.Count)).ToList(),
            Core.Dashboards.EndpointGrouping.Tier => (await endpoints.GroupBy(e => e.Tier)
                    .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
                .Select(g => new ChartBar(g.Key == EndpointTier.Managed ? "Managed" : "Agent-only", g.Count)).ToList(),
            _ => (await endpoints.GroupBy(e => e.OsName)
                    .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
                .Select(g => new ChartBar(string.IsNullOrWhiteSpace(g.Key) ? "Unknown" : g.Key, g.Count)).ToList()
        };

        bars = bars.OrderByDescending(b => b.Value).ThenBy(b => b.Label, StringComparer.OrdinalIgnoreCase).ToList();
        if (bars.Count > MaxChartBars)
        {
            var rest = bars.Skip(MaxChartBars - 1).Sum(b => b.Value);
            bars = [.. bars.Take(MaxChartBars - 1), new ChartBar("Other", rest)];
        }

        return bars;
    }

    /// <summary>The clients with the most open alerts (without alerts on hold), most first.</summary>
    public async Task<IReadOnlyList<ChartBar>> GetTopClientsAsync(Caller caller, EndpointClass? cls, int limit, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.AlertsView))
        {
            return [];
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime;
        var counts = await OfClass(db, db.Alerts.AsNoTracking()
                .Where(a => a.State != AlertState.Resolved && (a.HeldUntil == null || a.HeldUntil <= now)), cls)
            .GroupBy(a => a.ClientId)
            .Select(g => new { ClientId = g.Key, Total = g.Count(), Critical = g.Count(a => a.Severity == AlertSeverity.Critical) })
            .OrderByDescending(g => g.Total).ThenByDescending(g => g.Critical)
            .Take(Math.Clamp(limit, 1, 20))
            .ToListAsync(cancellationToken);
        var ids = counts.Select(c => c.ClientId).ToList();
        var codes = await db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        return counts.Select(c => new ChartBar(codes.GetValueOrDefault(c.ClientId, "?"), c.Total, c.Critical, c.ClientId)).ToList();
    }

    /// <summary>
    /// Alerts opened per day over the last <paramref name="days"/> days, oldest first, every day present. Clients the caller sees
    /// wholly are read from the counts of the workers; alerts of a client it sees in part are counted from its own alerts, so the
    /// chart never counts an endpoint outside its groups.
    /// </summary>
    public async Task<IReadOnlyList<DailyAlerts>> GetAlertsPerDayAsync(Caller caller, int days, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.AlertsView))
        {
            return [];
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var from = today.AddDays(1 - Math.Clamp(days, 1, 400));
        var counts = db.DashboardDailyCounts.AsNoTracking().Where(c => c.Day >= from);
        var partial = Array.Empty<Guid>();
        if (!caller.Scope.AllClients)
        {
            var full = caller.Scope.FullClientIds.ToArray();
            partial = caller.Scope.ClientIds.Except(full).ToArray();
            counts = counts.Where(c => full.Contains(c.ClientId));
        }

        var rows = await counts.GroupBy(c => c.Day)
            .Select(g => new { Day = g.Key, Warning = g.Sum(c => c.Warning), Critical = g.Sum(c => c.Critical) })
            .ToListAsync(cancellationToken);
        var byDay = rows.ToDictionary(r => r.Day, r => (r.Warning, r.Critical));
        if (partial.Length > 0)
        {
            var since = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var own = await db.Alerts.AsNoTracking().Where(a => partial.Contains(a.ClientId) && a.OpenedAt >= since)
                .GroupBy(a => new { a.OpenedAt.Date, a.Severity })
                .Select(g => new { g.Key.Date, g.Key.Severity, Count = g.Count() })
                .ToListAsync(cancellationToken);
            foreach (var row in own)
            {
                var day = DateOnly.FromDateTime(row.Date);
                var (warning, critical) = byDay.GetValueOrDefault(day);
                byDay[day] = row.Severity == AlertSeverity.Critical ? (warning, critical + row.Count) : (warning + row.Count, critical);
            }
        }

        var result = new List<DailyAlerts>();
        for (var day = from; day <= today; day = day.AddDays(1))
        {
            var (warning, critical) = byDay.GetValueOrDefault(day);
            result.Add(new DailyAlerts(day, warning, critical));
        }

        return result;
    }

    /// <summary>The license of the whole instance (0.6.0), only with its permission.</summary>
    public async Task<LicenseUsage?> GetLicenseAsync(Caller caller, CancellationToken cancellationToken = default) =>
        caller.Has(Permissions.LicensingManage) ? await _licenses.GetUsageAsync(cancellationToken) : null;

    public async Task<IReadOnlyList<CredentialWarning>> GetCredentialWarningsAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            return (await ExpiringCredentials.ListAsync(_settings, cancellationToken))
                .Where(c => c.StageAt(now) != CredentialExpiryStage.None)
                .Select(c => new CredentialWarning(c.Name, c.ExpiresAt, c.StageAt(now) == CredentialExpiryStage.Expired, c.StopsWorking, c.NextStep,
                    c.SettingsPath, c.FallbackInUse))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The expiring credentials could not be read");
            return [];
        }
    }

    /// <summary>The backups of the instance; every user with a role sees whether they run.</summary>
    public async Task<BackupTile> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        var configured = false;
        string? external = null;
        try
        {
            var settings = await _settings.GetAsync<BackupSettings>(SettingKeys.Backup, cancellationToken);
            configured = settings is not null && settings.DestinationType != BackupDestinationType.None;
            external = settings is { IsExternal: true } ? settings.ExternalDescription!.Trim() : null;
        }
        catch (Exception ex)
        {
            // An unreadable setting is reported as "not configured", which shows the warning tile: honest, not silent.
            _logger.LogError(ex, "The backup settings could not be read");
        }

        await using var db = _dbFactory.CreateSystem();
        var lastSuccess = await db.BackupRuns.AsNoTracking().Where(r => r.Status == BackupRunStatus.Succeeded)
            .OrderByDescending(r => r.StartedAt).Select(r => r.CompletedAt ?? r.StartedAt).FirstOrDefaultAsync(cancellationToken);
        var last = await db.BackupRuns.AsNoTracking().Where(r => r.Status != BackupRunStatus.Running)
            .OrderByDescending(r => r.StartedAt).Select(r => new { r.Status, r.Error }).FirstOrDefaultAsync(cancellationToken);

        return new BackupTile(configured, lastSuccess == default ? null : lastSuccess, last?.Status == BackupRunStatus.Failed, last?.Error, external);
    }

    private static IQueryable<Endpoint> OfClass(IQueryable<Endpoint> endpoints, EndpointClass? cls) =>
        cls is { } wanted ? endpoints.Where(e => (e.ClassOverride ?? e.DetectedClass) == wanted) : endpoints;

    private static IQueryable<Alert> OfClass(FleetoDbContext db, IQueryable<Alert> alerts, EndpointClass? cls) =>
        cls is { } wanted
            ? alerts.Where(a => db.Endpoints.Any(e => e.Id == a.EndpointId && (e.ClassOverride ?? e.DetectedClass) == wanted))
            : alerts;

    private static IQueryable<DashboardEndpointRow> Rows(FleetoDbContext db, IQueryable<Endpoint> endpoints) =>
        endpoints.Select(e => new DashboardEndpointRow(e.Id,
            db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Code).FirstOrDefault() ?? string.Empty,
            e.Hostname, e.IsOnline, e.LastSeenAt, e.AgentVersion));
}
