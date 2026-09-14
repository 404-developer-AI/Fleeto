using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleetify.Workers.Alerts;

/// <summary>
/// Ends alert holds whose time has passed, every 30 seconds: the hold columns are cleared and, for an alert that is still
/// unresolved, one "still open after hold" email is queued in the same transaction. Pages already treat a hold in the past
/// as ended, so a late run only delays the email.
/// </summary>
public sealed class AlertHoldService : WorkerLoop
{
    internal const int BatchSize = 500;

    // The "HeldUntil" <= @now condition makes a hold extended by a technician in the meantime safe: that row no longer matches.
    private const string EndHoldsSql = """
        UPDATE "Alerts" a
        SET "HeldUntil" = NULL, "HeldAt" = NULL, "HeldByUserId" = NULL, "UpdatedAt" = @now
        WHERE a."Id" IN (
          SELECT "Id" FROM "Alerts"
          WHERE "HeldUntil" IS NOT NULL AND "HeldUntil" <= @now AND "State" <> 'Resolved'
          ORDER BY "HeldUntil"
          LIMIT @limit
          FOR UPDATE SKIP LOCKED)
        RETURNING a."Id" AS "Value"
        """;

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly AlertNotificationService _notifier;

    public AlertHoldService(IFleetifyDbContextFactory dbFactory, INotificationBus bus, AlertNotificationService notifier,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<AlertHoldService> logger)
        : base("alert-holds", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _notifier = notifier;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken) =>
        (await EndExpiredHoldsAsync(cancellationToken)).Count >= BatchSize;

    /// <summary>Ends up to <see cref="BatchSize"/> expired holds. Returns the ids of the alerts whose hold ended.</summary>
    public async Task<IReadOnlyList<Guid>> EndExpiredHoldsAsync(CancellationToken cancellationToken)
    {
        List<Guid> ended;
        await using (var db = _dbFactory.CreateSystem())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            ended = await db.Database.SqlQueryRaw<Guid>(EndHoldsSql,
                    new NpgsqlParameter("now", Time.GetUtcNow().UtcDateTime), new NpgsqlParameter("limit", BatchSize))
                .ToListAsync(cancellationToken);
            if (ended.Count > 0)
            {
                await _notifier.AddNotificationsAsync(db, ended.Select(id => new AlertTransition(id, AlertTransitionKind.HoldEnded)).ToList(),
                    cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (ended.Count > 0)
        {
            Logger.LogInformation("The hold ended on {Count} alert(s) that are still unresolved", ended.Count);
            await AlertCleanup.PublishAsync(_bus, ended, cancellationToken);
        }

        return ended;
    }
}
