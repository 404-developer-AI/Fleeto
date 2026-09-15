using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Infrastructure.Settings;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Web.Services;

public sealed record BackupTile(bool DestinationConfigured, DateTime? LastSucceededAt, bool LastRunFailed, string? LastError);

/// <summary>A stored credential that expires within 30 days or has expired (0.2.0).</summary>
public sealed record CredentialWarning(string Name, DateTime ExpiresAt, bool Expired, string StopsWorking, string NextStep, string SettingsPath,
    bool FallbackInUse);

public sealed record DashboardData(int EndpointsOnline, int EndpointsOffline, int OpenCritical, int OpenWarning, int AgentsOutOfDate,
    LicenseUsage License, BackupTile Backups, IReadOnlyList<AlertView> OpenAlerts, IReadOnlyList<AuditEntryView> RecentActivity,
    IReadOnlyList<CredentialWarning> CredentialWarnings);

/// <summary>Dashboard tiles, the open alert list and recent activity.</summary>
public sealed class DashboardService
{
    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<DashboardService> _logger;

    public DashboardService(IFleetifyDbContextFactory dbFactory, LicenseService licenses, SettingsStore settings, TimeProvider time,
        ILogger<DashboardService> logger)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    public async Task<DashboardData> GetAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var version = FleetifyVersion.Current;

        var online = await db.Endpoints.CountAsync(e => e.IsOnline, cancellationToken);
        var total = await db.Endpoints.CountAsync(cancellationToken);
        // Alerts on hold are left out of the tiles and the list until their hold ends.
        var now = _time.GetUtcNow().UtcDateTime;
        var active = db.Alerts.Where(a => a.State != AlertState.Resolved && (a.HeldUntil == null || a.HeldUntil <= now));
        var critical = await active.CountAsync(a => a.Severity == AlertSeverity.Critical, cancellationToken);
        var warning = await active.CountAsync(a => a.Severity == AlertSeverity.Warning, cancellationToken);
        var outOfDate = await db.Endpoints.CountAsync(e => e.Source == EndpointSource.Agent && e.AgentVersion != version, cancellationToken);

        var openAlerts = await AlertService.Project(db, active.AsNoTracking()
                .OrderByDescending(a => a.OpenedAt).ThenByDescending(a => a.Id)
                .Take(20))
            .ToListAsync(cancellationToken);

        var activity = await db.AuditEntries.AsNoTracking()
            .OrderByDescending(a => a.Id)
            .Take(15)
            .Select(a => new AuditEntryView(a.Id, a.Time, a.ClientId, a.ActorType, a.ActorId, a.ActorName, a.Action, a.TargetType, a.TargetId,
                a.DetailsJson, null))
            .ToListAsync(cancellationToken);

        var license = await _licenses.GetUsageAsync(cancellationToken);
        var backups = await GetBackupTileAsync(db, cancellationToken);

        return new DashboardData(online, total - online, critical, warning, outOfDate, license, backups, openAlerts, activity,
            await GetCredentialWarningsAsync(now, cancellationToken));
    }

    private async Task<IReadOnlyList<CredentialWarning>> GetCredentialWarningsAsync(DateTime now, CancellationToken cancellationToken)
    {
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

    private async Task<BackupTile> GetBackupTileAsync(FleetifyDbContext db, CancellationToken cancellationToken)
    {
        var configured = false;
        try
        {
            var settings = await _settings.GetAsync<BackupSettings>(SettingKeys.Backup, cancellationToken);
            configured = settings is not null && settings.DestinationType != BackupDestinationType.None;
        }
        catch (Exception ex)
        {
            // An unreadable setting is reported as "not configured", which shows the warning tile: honest, not silent.
            _logger.LogError(ex, "The backup settings could not be read");
        }

        var lastSuccess = await db.BackupRuns.AsNoTracking().Where(r => r.Status == BackupRunStatus.Succeeded)
            .OrderByDescending(r => r.StartedAt).Select(r => r.CompletedAt ?? r.StartedAt).FirstOrDefaultAsync(cancellationToken);
        var last = await db.BackupRuns.AsNoTracking().Where(r => r.Status != BackupRunStatus.Running)
            .OrderByDescending(r => r.StartedAt).Select(r => new { r.Status, r.Error }).FirstOrDefaultAsync(cancellationToken);

        return new BackupTile(configured, lastSuccess == default ? null : lastSuccess, last?.Status == BackupRunStatus.Failed, last?.Error);
    }
}
