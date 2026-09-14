using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Workers.Alerts;
using Fleetify.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleetify.Workers.Endpoints;

/// <summary>
/// Processes connection events written by the gateway. A duplicate agent identity (a second connection with a
/// certificate that is already connected, e.g. a cloned machine) opens a critical alert; connects and disconnects are
/// history only. Woken by <c>fleetify_endpoint_events</c>, polls every 30 seconds.
/// <para>
/// The duplicate identity alert opens for agent-only endpoints too: it is a security signal about the instance, not a
/// monitoring feature, and hiding a cloned identity would be lying about the endpoint.
/// </para>
/// </summary>
public sealed class EndpointEventService : WorkerLoop
{
    internal const int BatchSize = 500;

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly AlertNotificationService _notifier;
    private IDisposable? _subscription;

    public EndpointEventService(IFleetifyDbContextFactory dbFactory, INotificationBus bus, AlertNotificationService notifier,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<EndpointEventService> logger)
        : base("endpoint-events", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _notifier = notifier;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.EndpointEvents, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken) =>
        await ProcessPendingAsync(cancellationToken) >= BatchSize;

    public static string DuplicateIdentityTitle(string hostname) =>
        $"Two endpoints use the agent identity of {hostname}. Revoke the agent on the endpoint page and enroll the copies again.";

    /// <summary>Processes one batch of unprocessed events in id order. Returns the number processed.</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var transitions = new List<AlertTransition>();
        int count;

        await using (var db = _dbFactory.CreateSystem())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var events = await db.EndpointEvents
                .FromSql($"""SELECT * FROM "EndpointEvents" WHERE "ProcessedAt" IS NULL ORDER BY "Id" LIMIT {BatchSize} FOR UPDATE SKIP LOCKED""")
                .ToListAsync(cancellationToken);
            if (events.Count == 0)
            {
                return 0;
            }

            var now = Time.GetUtcNow().UtcDateTime;
            var duplicates = events.Where(e => e.Kind == EndpointEventKind.DuplicateIdentity).GroupBy(e => e.EndpointId).ToList();
            if (duplicates.Count > 0)
            {
                var endpointIds = duplicates.Select(g => g.Key).ToList();
                var alreadyOpen = await db.Alerts.AsNoTracking()
                    .Where(a => endpointIds.Contains(a.EndpointId) && a.Kind == AlertKind.DuplicateIdentity && a.State != AlertState.Resolved)
                    .Select(a => a.EndpointId)
                    .ToListAsync(cancellationToken);
                var endpoints = await db.Endpoints.AsNoTracking()
                    .Where(e => endpointIds.Contains(e.Id))
                    .Select(e => new { e.Id, e.ClientId, e.Hostname })
                    .ToDictionaryAsync(e => e.Id, cancellationToken);

                foreach (var group in duplicates)
                {
                    if (alreadyOpen.Contains(group.Key) || !endpoints.TryGetValue(group.Key, out var endpoint))
                    {
                        continue;
                    }

                    var latest = group.OrderBy(e => e.Id).Last();
                    var alert = new Alert
                    {
                        Id = Guid.NewGuid(),
                        ClientId = endpoint.ClientId,
                        EndpointId = endpoint.Id,
                        Kind = AlertKind.DuplicateIdentity,
                        Target = string.Empty,
                        Severity = AlertSeverity.Critical,
                        State = AlertState.Open,
                        Title = Truncate(DuplicateIdentityTitle(endpoint.Hostname), 500),
                        Detail = Truncate(latest.Detail, 2000),
                        OpenedAt = now,
                        UpdatedAt = now
                    };
                    db.Alerts.Add(alert);
                    transitions.Add(new AlertTransition(alert.Id, AlertTransitionKind.Opened));
                    Logger.LogWarning("Duplicate agent identity reported for endpoint {EndpointId}", endpoint.Id);
                }
            }

            foreach (var endpointEvent in events)
            {
                endpointEvent.ProcessedAt = now;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another writer opened the alert first; nothing was committed and the next pass sees that alert.
                Logger.LogInformation("Duplicate identity alert conflict with another writer; retrying on the next pass");
                return 0;
            }

            await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            count = events.Count;
        }

        await AlertCleanup.PublishAsync(_bus, transitions.Select(t => t.AlertId), cancellationToken);
        return count;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
