using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;
using Endpoint = Fleetify.Core.Entities.Endpoint;

namespace Fleetify.Web.Services;

/// <summary>What maintenance mode is started on.</summary>
public enum MaintenanceTarget
{
    Client,
    Site,
    Endpoint
}

/// <summary>
/// Maintenance mode for a client, a site or one managed endpoint (ARCHITECTURE.md §4, Maintenance mode): started by hand with an
/// optional end time, changed, or ended. While it lasts no alert opens or escalates for the endpoints below it; the rule itself is
/// <see cref="MaintenanceRules"/>. Starting, changing and ending are audit entries; the workers audit an end time that passes.
/// </summary>
public sealed class MaintenanceService
{
    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(IFleetifyDbContextFactory dbFactory, LicenseService licenses, INotificationBus bus, TimeProvider time,
        ILogger<MaintenanceService> logger)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Starts maintenance, or changes the end time and reason of maintenance that is already active (its start stays).
    /// <paramref name="endsAtUtc"/> null means until turned off.
    /// </summary>
    public async Task<ServiceResult> StartAsync(Caller caller, MaintenanceTarget target, Guid id, DateTime? endsAtUtc, string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        endsAtUtc = endsAtUtc is { } end ? DateTime.SpecifyKind(end, DateTimeKind.Utc) : null;
        if (MaintenanceRules.ValidateEnd(endsAtUtc, now) is { } endProblem)
        {
            return ServiceResult.Fail(endProblem);
        }

        var cleanReason = ServiceSupport.Clean(reason);
        if (cleanReason?.Length > MaintenanceRules.MaxReasonLength)
        {
            return ServiceResult.Fail($"The reason can be at most {MaintenanceRules.MaxReasonLength} characters.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var row = await LoadAsync(db, target, id, cancellationToken);
        if (row is null)
        {
            return ServiceResult.NotFound(TargetName(target));
        }

        if (row.Entity is Endpoint endpoint)
        {
            var license = await _licenses.GetStatusAsync(db, cancellationToken);
            if (TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed)
            {
                return ServiceResult.Fail(endpoint.Tier == EndpointTier.Managed
                    ? "Maintenance mode is not available while the license has expired: no alerts open anyway. Load a new license in Settings."
                    : "Maintenance mode is only available on managed endpoints: an agent-only endpoint raises no alerts.");
            }
        }

        var wasActive = row.Period.IsActive(now);
        var entry = db.Entry(row.Entity);
        if (!wasActive)
        {
            entry.Property("MaintenanceStartedAt").CurrentValue = now;
        }

        entry.Property("MaintenanceEndsAt").CurrentValue = endsAtUtc;
        entry.Property("MaintenanceStartedByUserId").CurrentValue = caller.UserId;
        entry.Property("MaintenanceStartedByName").CurrentValue = caller.Name.Length <= 200 ? caller.Name : caller.Name[..200];
        entry.Property("MaintenanceReason").CurrentValue = cleanReason;
        entry.Property("UpdatedAt").CurrentValue = now;

        // The reason is free text a technician typed: only its length goes into the audit log.
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(wasActive ? AuditActions.MaintenanceChanged : AuditActions.MaintenanceStarted,
            target.ToString(), id.ToString(), row.ClientId,
            new { row.Name, EndsAt = endsAtUtc, UntilTurnedOff = endsAtUtc is null, ReasonLength = cleanReason?.Length ?? 0 }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(target, id, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Ends active maintenance. Refused for an endpoint whose site or client keeps it in maintenance: end it there, otherwise the
    /// endpoint would look out of maintenance and still raise nothing.
    /// </summary>
    public async Task<ServiceResult> EndAsync(Caller caller, MaintenanceTarget target, Guid id, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var row = await LoadAsync(db, target, id, cancellationToken);
        if (row is null)
        {
            return ServiceResult.NotFound(TargetName(target));
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (row.Entity is Endpoint endpoint)
        {
            var inherited = await db.Endpoints.AsNoTracking()
                .Where(e => e.Id == endpoint.Id)
                .Select(e => new { Site = e.Site!.MaintenanceStartedAt, SiteEnds = e.Site.MaintenanceEndsAt, SiteName = e.Site.Name,
                    Client = e.Site.Client!.MaintenanceStartedAt, ClientEnds = e.Site.Client.MaintenanceEndsAt, ClientCode = e.Site.Client.Code })
                .SingleAsync(cancellationToken);
            if (MaintenanceRules.IsActive(inherited.Site, inherited.SiteEnds, now))
            {
                return ServiceResult.Fail($"Site {inherited.SiteName} keeps this endpoint in maintenance. End maintenance on the site instead.");
            }

            if (MaintenanceRules.IsActive(inherited.Client, inherited.ClientEnds, now))
            {
                return ServiceResult.Fail($"Client {inherited.ClientCode} keeps this endpoint in maintenance. End maintenance on the client instead.");
            }
        }

        if (!row.Period.IsActive(now))
        {
            return ServiceResult.Fail($"This {TargetName(target)} is not in maintenance.");
        }

        var entry = db.Entry(row.Entity);
        foreach (var column in new[] { "MaintenanceStartedAt", "MaintenanceEndsAt", "MaintenanceStartedByUserId", "MaintenanceStartedByName", "MaintenanceReason" })
        {
            entry.Property(column).CurrentValue = null;
        }

        entry.Property("UpdatedAt").CurrentValue = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MaintenanceEnded, target.ToString(), id.ToString(), row.ClientId,
            new { row.Name, row.Period.StartedAt, PlannedEnd = row.Period.EndsAt }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(target, id, cancellationToken);
        return ServiceResult.Ok();
    }

    private sealed record Loaded(object Entity, Guid ClientId, string Name, MaintenancePeriod Period);

    private static async Task<Loaded?> LoadAsync(FleetifyDbContext db, MaintenanceTarget target, Guid id, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case MaintenanceTarget.Client:
                var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
                return client is null ? null : new Loaded(client, client.Id, client.Code, client.Maintenance);
            case MaintenanceTarget.Site:
                var site = await db.Sites.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
                return site is null ? null : new Loaded(site, site.ClientId, site.Name, site.Maintenance);
            default:
                var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
                return endpoint is null ? null : new Loaded(endpoint, endpoint.ClientId, endpoint.Hostname, endpoint.Maintenance);
        }
    }

    private static string TargetName(MaintenanceTarget target) => target.ToString().ToLowerInvariant();

    private async Task PublishAsync(MaintenanceTarget target, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            // A client or site changes every endpoint below it: Guid.Empty tells open pages to reload.
            var payload = target == MaintenanceTarget.Endpoint ? id : Guid.Empty;
            await _bus.PublishAsync(NotificationChannels.EndpointStatus, payload.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish the maintenance change of {Target} {Id}", target, id);
        }
    }
}
