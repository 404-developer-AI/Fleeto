using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Checks;

/// <summary>
/// Applies the reset of check run requests (ARCHITECTURE.md §4, Run now and reset): the open alerts of the check are
/// resolved and its states become "re-run requested" (status Unknown without a value, never a made-up OK), under the same
/// per-endpoint advisory lock as check evaluation, so a result being evaluated cannot interleave. Marking the reset applied
/// wakes the gateway, which then delivers the request to the agent. Requests that expired undelivered are closed.
/// Woken by <c>fleeto_check_run_requests</c>, and polls every 30 seconds so a lost notification only delays work.
/// </summary>
public sealed class CheckRunRequestService : WorkerLoop
{
    internal const int BatchSize = 100;
    public const string ResolvedReasonPrefix = "Reset by ";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly LicenseService _licenses;
    private readonly AlertNotificationService _notifier;
    private IDisposable? _subscription;

    public CheckRunRequestService(IFleetoDbContextFactory dbFactory, INotificationBus bus, LicenseService licenses,
        AlertNotificationService notifier, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<CheckRunRequestService> logger)
        : base("check-run-requests", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _licenses = licenses;
        _notifier = notifier;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.CheckRunRequests, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await ExpireAsync(cancellationToken);
        return await ApplyPendingResetsAsync(cancellationToken) >= BatchSize;
    }

    /// <summary>Closes undelivered requests whose time passed. Returns the number closed.</summary>
    public async Task<int> ExpireAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        return await db.CheckRunRequests
            .Where(r => r.DeliveredAt == null && r.Outcome == null && r.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Outcome, CheckRunRequestOutcome.Expired), cancellationToken);
    }

    /// <summary>Applies up to <see cref="BatchSize"/> pending resets, oldest first. Returns the number handled.</summary>
    public async Task<int> ApplyPendingResetsAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        await using (var db = _dbFactory.CreateSystem())
        {
            ids = await db.CheckRunRequests.AsNoTracking()
                .Where(r => r.Reset && r.ResetAppliedAt == null && r.Outcome == null)
                .OrderBy(r => r.RequestedAt)
                .Select(r => r.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        var handled = 0;
        foreach (var id in ids)
        {
            if (await ApplyResetAsync(id, cancellationToken))
            {
                handled++;
            }
        }

        return handled;
    }

    private async Task<bool> ApplyResetAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var transitions = new List<AlertTransition>();
        Guid endpointId;

        await using (var db = _dbFactory.CreateSystem())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var request = (await db.CheckRunRequests
                    .FromSql($"""SELECT * FROM "CheckRunRequests" WHERE "Id" = {requestId} AND "Reset" AND "ResetAppliedAt" IS NULL AND "Outcome" IS NULL FOR UPDATE SKIP LOCKED""")
                    .ToListAsync(cancellationToken))
                .FirstOrDefault();
            if (request is null)
            {
                // Handled by another pass, or deleted with its endpoint.
                return false;
            }

            endpointId = request.EndpointId;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({CheckEvaluationService.AdvisoryLockClass}, {CheckEvaluationService.LockKey(endpointId)})", cancellationToken);

            var now = Time.GetUtcNow().UtcDateTime;
            var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
            var license = await _licenses.GetStatusAsync(cancellationToken);
            if (endpoint is null)
            {
                return false;
            }

            if (request.ExpiresAt <= now)
            {
                request.Outcome = CheckRunRequestOutcome.Expired;
            }
            else if (TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed)
            {
                request.Outcome = CheckRunRequestOutcome.NotManaged;
            }
            else
            {
                var checks = await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: false, cancellationToken);
                if (checks.All(c => c.Id != request.CheckDefinitionId))
                {
                    request.Outcome = CheckRunRequestOutcome.NotApplicable;
                }
                else
                {
                    var reason = Truncate(ResolvedReasonPrefix + request.RequestedByName, 500);
                    var alerts = await db.Alerts
                        .Where(a => a.EndpointId == endpointId && a.Kind == AlertKind.Check && a.CheckDefinitionId == request.CheckDefinitionId &&
                                    a.State != AlertState.Resolved)
                        .ToListAsync(cancellationToken);
                    foreach (var alert in alerts)
                    {
                        alert.State = AlertState.Resolved;
                        alert.ResolvedAt = now;
                        alert.ResolvedReason = reason;
                        alert.UpdatedAt = now;
                        transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Resolved));
                    }

                    await db.CheckStates
                        .Where(s => s.EndpointId == endpointId && s.CheckDefinitionId == request.CheckDefinitionId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.Status, CheckStatus.Unknown)
                            .SetProperty(x => x.Value, (double?)null)
                            .SetProperty(x => x.Detail, string.Empty)
                            .SetProperty(x => x.Error, string.Empty)
                            .SetProperty(x => x.ConsecutiveNonOk, 0)
                            .SetProperty(x => x.ResetAt, now)
                            .SetProperty(x => x.UpdatedAt, now), cancellationToken);

                    request.ResetAppliedAt = now;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transitions.Count > 0)
            {
                await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await AlertCleanup.PublishAsync(_bus, transitions.Select(t => t.AlertId), cancellationToken);
        await _bus.PublishAsync(NotificationChannels.EndpointStatus, endpointId.ToString(), cancellationToken);
        return true;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
