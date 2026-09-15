using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Endpoints;

/// <summary>
/// Keeps the stored occurrences of policy maintenance windows ahead (<see cref="MaintenanceWindowSchedule"/>): at start and every
/// hour it recomputes every policy, so the horizon always covers the coming week and a change in time zone rules is picked up.
/// Every minute it publishes a status notification when an occurrence started or ended since the last pass, so open pages show
/// the maintenance change. The maintenance rule itself never waits for this service: it compares stored times with the clock.
/// </summary>
public sealed class MaintenanceWindowService : WorkerLoop
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private DateTime? _lastCheck;

    public MaintenanceWindowService(IFleetoDbContextFactory dbFactory, INotificationBus bus, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<MaintenanceWindowService> logger)
        : base("maintenance-windows", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Time.GetUtcNow() - _lastRefresh >= RefreshInterval)
        {
            await RefreshAsync(cancellationToken);
        }

        await NotifyBoundariesAsync(cancellationToken);
        return false;
    }

    /// <summary>Recomputes the occurrences of every policy. Returns the number stored.</summary>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var stored = await MaintenanceWindowSchedule.RefreshAllAsync(db, Time.GetUtcNow().UtcDateTime, cancellationToken);
        _lastRefresh = Time.GetUtcNow();
        Logger.LogDebug("Stored {Count} maintenance window occurrence(s)", stored);
        return stored;
    }

    /// <summary>Publishes one status notification when a window started or ended since the last pass. Returns true when it did.</summary>
    public async Task<bool> NotifyBoundariesAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var since = _lastCheck ?? now;
        _lastCheck = now;
        if (since >= now)
        {
            return false;
        }

        await using var db = _dbFactory.CreateSystem();
        var changed = await db.MaintenanceWindowOccurrences.AsNoTracking()
            .AnyAsync(o => (o.StartsAt > since && o.StartsAt <= now) || (o.EndsAt > since && o.EndsAt <= now), cancellationToken);
        if (!changed)
        {
            return false;
        }

        try
        {
            // Guid.Empty tells pages to reload: a window can cover many endpoints.
            await _bus.PublishAsync(NotificationChannels.EndpointStatus, Guid.Empty.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not publish the maintenance window notification");
        }

        return true;
    }
}
