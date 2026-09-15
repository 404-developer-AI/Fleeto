using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Endpoints;

/// <summary>
/// Notices maintenance whose end time passed (ARCHITECTURE.md §4, Maintenance mode), every 30 seconds: writes a
/// <c>maintenance.expired</c> audit entry per client, site or endpoint and publishes a status notification so open pages update.
/// Nothing else has to happen at expiry: every reader compares the end time with the current time, so alerts open again on the
/// next failing result and offline alerts on the next health pass.
/// <para>
/// Progress is a watermark on the end time (<see cref="WatermarkName"/>, UTC ticks), advanced in the same transaction as the audit
/// entries: a pass that fails writes nothing and the next pass covers the same period. On the very first run the watermark starts
/// at the current time, so maintenance that ended before this release is not reported.
/// </para>
/// </summary>
public sealed class MaintenanceExpiryService : WorkerLoop
{
    public const string WatermarkName = "maintenance-expiry";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;

    public MaintenanceExpiryService(IFleetoDbContextFactory dbFactory, INotificationBus bus, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<MaintenanceExpiryService> logger)
        : base("maintenance-expiry", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await ProcessAsync(cancellationToken);
        return false;
    }

    private sealed record Expired(string TargetType, Guid Id, Guid? ClientId, string Name, DateTime EndsAt);

    /// <summary>Records maintenance that ended since the last pass. Returns the number of expiries found.</summary>
    public async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        List<Expired> expired;
        var endpointIds = new List<Guid>();
        await using (var db = _dbFactory.CreateSystem())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var watermark = await db.WorkerWatermarks
                .FromSql($"""SELECT * FROM "WorkerWatermarks" WHERE "Name" = {WatermarkName} FOR UPDATE""")
                .SingleOrDefaultAsync(cancellationToken);
            if (watermark is null)
            {
                db.WorkerWatermarks.Add(new WorkerWatermark { Name = WatermarkName, Value = now.Ticks, UpdatedAt = now });
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return 0;
            }

            var since = new DateTime(watermark.Value, DateTimeKind.Utc);
            expired = (await db.Clients.AsNoTracking()
                    .Where(c => c.MaintenanceStartedAt != null && c.MaintenanceEndsAt > since && c.MaintenanceEndsAt <= now)
                    .Select(c => new Expired("Client", c.Id, c.Id, c.Code, c.MaintenanceEndsAt!.Value))
                    .ToListAsync(cancellationToken))
                .Concat(await db.Sites.AsNoTracking()
                    .Where(s => s.MaintenanceStartedAt != null && s.MaintenanceEndsAt > since && s.MaintenanceEndsAt <= now)
                    .Select(s => new Expired("Site", s.Id, s.ClientId, s.Name, s.MaintenanceEndsAt!.Value))
                    .ToListAsync(cancellationToken))
                .Concat(await db.Endpoints.AsNoTracking()
                    .Where(e => e.MaintenanceStartedAt != null && e.MaintenanceEndsAt > since && e.MaintenanceEndsAt <= now)
                    .Select(e => new Expired("Endpoint", e.Id, e.ClientId, e.Hostname, e.MaintenanceEndsAt!.Value))
                    .ToListAsync(cancellationToken))
                .OrderBy(x => x.EndsAt)
                .ToList();

            foreach (var item in expired)
            {
                db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.MaintenanceExpired, item.TargetType, item.Id.ToString(),
                    item.ClientId, AuditActorType.System, "fleeto-workers", "fleeto-workers", new { item.Name, EndedAt = item.EndsAt }), now));
                if (item.TargetType == "Endpoint")
                {
                    endpointIds.Add(item.Id);
                }
            }

            // No batch limit: only what ended within one pass (normally 30 seconds) is read, and ties on the end time are never split.
            watermark.Value = Math.Max(watermark.Value, now.Ticks);
            watermark.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (expired.Count == 0)
        {
            return 0;
        }

        Logger.LogInformation("Maintenance ended for {Count} client(s), site(s) or endpoint(s)", expired.Count);
        try
        {
            foreach (var endpointId in endpointIds)
            {
                await _bus.PublishAsync(NotificationChannels.EndpointStatus, endpointId.ToString(), cancellationToken);
            }

            if (expired.Count > endpointIds.Count)
            {
                // A client or site: every endpoint below it changed. Guid.Empty tells pages to reload.
                await _bus.PublishAsync(NotificationChannels.EndpointStatus, Guid.Empty.ToString(), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Notifications only refresh open pages; the audit entries are committed.
            Logger.LogWarning(ex, "Could not publish maintenance expiry notifications");
        }

        return expired.Count;
    }
}
