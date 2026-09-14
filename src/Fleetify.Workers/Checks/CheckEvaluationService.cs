using System.Collections.Concurrent;
using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Workers.Alerts;
using Fleetify.Workers.Hosting;
using Fleetify.Workers.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fleetify.Workers.Checks;

/// <summary>
/// Evaluates check results into check states and alerts (ARCHITECTURE.md §4, Check result). The workers are
/// authoritative: agents report values, thresholds are applied here.
/// <para>
/// Ordering: CheckResults ids are not globally in commit order (concurrent gateway transactions), but they are monotonic
/// per endpoint (an agent sends one batch at a time). So every endpoint has its own cursor
/// (<c>WorkerWatermarks</c> "check-eval:&lt;endpoint id&gt;"), advanced in the same transaction as the states and alerts.
/// A result committed late for one endpoint can never be skipped because another endpoint's cursor moved.
/// </para>
/// <para>
/// Work arrives through <c>fleetify_check_results</c> (payload endpoint id) and a sweep every 30 seconds over recent
/// results (a wide sweep at start and hourly), so a lost notification only delays evaluation. A per-endpoint advisory
/// lock serializes evaluation of one endpoint across concurrent passes and processes.
/// </para>
/// </summary>
public sealed class CheckEvaluationService : WorkerLoop
{
    public const string WatermarkPrefix = "check-eval:";
    public const string ResolvedReasonOk = "Check returned to OK";
    public const string ResolvedReasonNotApplicable = "The check no longer applies to this endpoint";
    public const string ResolvedReasonNotManaged = "The endpoint is no longer managed";

    private const int AdvisoryLockClass = 0x46434556; // "FCEV"
    private const int MaxDrainPerPass = 5000;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WideSweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan LicenseCacheDuration = TimeSpan.FromSeconds(30);

    private const string SweepSql = """
        SELECT r."EndpointId" AS "Value"
        FROM (SELECT "EndpointId", max("Id") AS "MaxId" FROM "CheckResults" WHERE "Time" >= @since GROUP BY "EndpointId") r
        LEFT JOIN "WorkerWatermarks" w ON w."Name" = 'check-eval:' || replace(r."EndpointId"::text, '-', '')
        WHERE w."Value" IS NULL OR w."Value" < r."MaxId"
        """;

    // Resolves unresolved check alerts whose definition was deleted (the foreign key nulls it), disabled, unlinked from
    // the endpoint's site, or no longer matches the endpoint class.
    private const string ResolveNotApplicableSql = """
        UPDATE "Alerts" a
        SET "State" = 'Resolved', "ResolvedAt" = @now, "UpdatedAt" = @now, "ResolvedReason" = @reason
        WHERE a."Kind" = 'Check' AND a."State" <> 'Resolved'
          AND NOT EXISTS (
            SELECT 1
            FROM "CheckDefinitions" d
            JOIN "SiteMonitoringTemplates" l ON l."MonitoringTemplateId" = d."MonitoringTemplateId"
            JOIN "Endpoints" e ON e."SiteId" = l."SiteId"
            WHERE d."Id" = a."CheckDefinitionId" AND e."Id" = a."EndpointId" AND d."Enabled"
              AND (d."AppliesTo" = 'All' OR d."AppliesTo" = COALESCE(e."ClassOverride", e."DetectedClass")))
        RETURNING a."Id" AS "Value"
        """;

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly LicenseService _licenses;
    private readonly AlertNotificationService _notifier;
    private readonly CheckEvaluationOptions _options;
    private readonly ConcurrentQueue<Guid> _queue = new();
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();
    private readonly object _licenseLock = new();
    private (LicenseStatus Status, DateTimeOffset ValidUntil)? _licenseCache;
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private DateTimeOffset _nextWideSweep = DateTimeOffset.MinValue;
    private int _resyncRequested;
    private IDisposable? _subscription;

    public CheckEvaluationService(IFleetifyDbContextFactory dbFactory, INotificationBus bus, LicenseService licenses,
        AlertNotificationService notifier, IOptions<CheckEvaluationOptions> options, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<CheckEvaluationService> logger)
        : base("check-evaluation", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _licenses = licenses;
        _notifier = notifier;
        _options = options.Value;
    }

    protected override TimeSpan Interval => SweepInterval;

    protected override TimeSpan MaxRunDuration => TimeSpan.FromMinutes(15);

    public static string WatermarkName(Guid endpointId) => WatermarkPrefix + endpointId.ToString("N");

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.CheckResults, (payload, _) =>
        {
            if (payload == NotificationBusEvents.Resync)
            {
                Interlocked.Exchange(ref _resyncRequested, 1);
            }
            else if (Guid.TryParse(payload, out var endpointId))
            {
                Enqueue(endpointId);
            }

            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow();
        if (Interlocked.Exchange(ref _resyncRequested, 0) == 1)
        {
            // The listener reconnected: notifications may have been lost while it was down.
            _nextWideSweep = DateTimeOffset.MinValue;
        }

        if (now >= _nextWideSweep)
        {
            await SweepAsync(TimeSpan.FromHours(_options.WideSweepWindowHours), cancellationToken);
            _nextWideSweep = now + WideSweepInterval;
            _nextSweep = now + SweepInterval;
        }
        else if (now >= _nextSweep)
        {
            await SweepAsync(TimeSpan.FromMinutes(_options.SweepWindowMinutes), cancellationToken);
            _nextSweep = now + SweepInterval;
        }

        await ProcessQueueAsync(cancellationToken);
        return !_queue.IsEmpty;
    }

    /// <summary>One full pass as the loop runs it: sweep, alert clean-up and evaluation of every queued endpoint.</summary>
    internal async Task RunPassAsync(bool wideSweep, CancellationToken cancellationToken)
    {
        await SweepAsync(wideSweep ? TimeSpan.FromHours(_options.WideSweepWindowHours) : TimeSpan.FromMinutes(_options.SweepWindowMinutes),
            cancellationToken);
        while (!_queue.IsEmpty)
        {
            await ProcessQueueAsync(cancellationToken);
        }
    }

    public void Enqueue(Guid endpointId)
    {
        if (_queued.TryAdd(endpointId, 0))
        {
            _queue.Enqueue(endpointId);
        }
    }

    /// <summary>Queues endpoints with results newer than their cursor and resolves alerts that no longer apply.</summary>
    internal async Task<int> SweepAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var since = Time.GetUtcNow().UtcDateTime - window;
        List<Guid> behind;
        await using (var db = _dbFactory.CreateSystem())
        {
            behind = await db.Database.SqlQueryRaw<Guid>(SweepSql, new NpgsqlParameter("since", since)).ToListAsync(cancellationToken);
        }

        foreach (var endpointId in behind)
        {
            Enqueue(endpointId);
        }

        await ResolveStaleAlertsAsync(cancellationToken);
        return behind.Count;
    }

    /// <summary>Resolves check alerts whose check no longer applies or whose endpoint is no longer managed.</summary>
    internal async Task<int> ResolveStaleAlertsAsync(CancellationToken cancellationToken)
    {
        var status = await GetLicenseStatusAsync(cancellationToken);
        var now = Time.GetUtcNow().UtcDateTime;
        List<Guid> resolved;

        await using (var db = _dbFactory.CreateSystem())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            resolved = await db.Database.SqlQueryRaw<Guid>(ResolveNotApplicableSql,
                    new NpgsqlParameter("now", now), new NpgsqlParameter("reason", ResolvedReasonNotApplicable))
                .ToListAsync(cancellationToken);
            resolved.AddRange(await AlertCleanup.ResolveNotManagedAsync(db, AlertKind.Check, status, now, cancellationToken));

            if (resolved.Count > 0)
            {
                await _notifier.AddNotificationsAsync(db, resolved.Select(id => new AlertTransition(id, AlertTransitionKind.Resolved)).ToList(),
                    cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await AlertCleanup.PublishAsync(_bus, resolved, cancellationToken);
        return resolved.Count;
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        var batch = new List<Guid>();
        while (batch.Count < MaxDrainPerPass && _queue.TryDequeue(out var endpointId))
        {
            // Removed before processing: a notification that arrives meanwhile queues the endpoint again.
            _queued.TryRemove(endpointId, out _);
            batch.Add(endpointId);
        }

        if (batch.Count == 0)
        {
            return;
        }

        var failures = 0;
        Exception? firstFailure = null;
        await Parallel.ForEachAsync(batch,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Parallelism), CancellationToken = cancellationToken },
            async (endpointId, ct) =>
            {
                try
                {
                    await EvaluateEndpointAsync(endpointId, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // The cursor did not move, so the sweep picks the endpoint up again.
                    Interlocked.Increment(ref failures);
                    Interlocked.CompareExchange(ref firstFailure, ex, null);
                    Logger.LogWarning(ex, "Evaluating check results of endpoint {EndpointId} failed; the sweep retries it", endpointId);
                }
            });

        if (failures == batch.Count && firstFailure is not null)
        {
            // Everything failed: most likely the database is unavailable. Let the loop back off.
            throw new InvalidOperationException("Check evaluation failed for every queued endpoint.", firstFailure);
        }
    }

    /// <summary>Evaluates every unevaluated result of one endpoint, in batches.</summary>
    public async Task EvaluateEndpointAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var conflicts = 0;
        while (true)
        {
            try
            {
                if (!await EvaluateBatchAsync(endpointId, cancellationToken))
                {
                    return;
                }
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && conflicts < 3)
            {
                // Another writer opened the same alert first (unique partial index). Re-read and try again.
                conflicts++;
                Logger.LogDebug("Alert conflict while evaluating endpoint {EndpointId}; retrying", endpointId);
            }
        }
    }

    private async Task<bool> EvaluateBatchAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var transitions = new List<AlertTransition>();
        var stateChanged = false;
        bool more;

        await using (var db = _dbFactory.CreateSystem())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var lockKey = LockKey(endpointId);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockClass}, {lockKey})", cancellationToken);

            var now = Time.GetUtcNow().UtcDateTime;
            var name = WatermarkName(endpointId);
            var watermark = await db.WorkerWatermarks.SingleOrDefaultAsync(w => w.Name == name, cancellationToken);

            var query = db.CheckResults.AsNoTracking().Where(r => r.EndpointId == endpointId);
            if (watermark is null)
            {
                var since = now.AddHours(-_options.WideSweepWindowHours);
                query = query.Where(r => r.Time >= since);
            }
            else
            {
                var cursor = watermark.Value;
                query = query.Where(r => r.Id > cursor);
            }

            var batchSize = Math.Max(1, _options.BatchSize);
            var results = await query.OrderBy(r => r.Id).Take(batchSize).ToListAsync(cancellationToken);
            if (results.Count == 0)
            {
                return false;
            }

            var endpoint = await db.Endpoints.AsNoTracking()
                .Where(e => e.Id == endpointId)
                .Select(e => new EndpointFacts(e.Id, e.ClientId, e.SiteId, e.Hostname, e.Tier, e.ClassOverride ?? e.DetectedClass))
                .FirstOrDefaultAsync(cancellationToken);

            if (endpoint is not null)
            {
                var license = await GetLicenseStatusAsync(cancellationToken);
                if (TierRules.EffectiveTier(endpoint.Tier, license) == EndpointTier.Managed)
                {
                    stateChanged = await ApplyResultsAsync(db, endpoint, results, transitions, now, cancellationToken);
                }
            }

            // The cursor moves even for skipped results (agent-only, deleted definition): they must never be evaluated
            // later. For a deleted endpoint the watermark also tells retention which results to purge.
            var lastId = results[^1].Id;
            if (watermark is null)
            {
                db.WorkerWatermarks.Add(new WorkerWatermark { Name = name, Value = lastId, UpdatedAt = now });
            }
            else
            {
                watermark.Value = lastId;
                watermark.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transitions.Count > 0)
            {
                await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            more = results.Count == batchSize;
        }

        foreach (var transition in transitions.DistinctBy(t => t.AlertId))
        {
            await _bus.PublishAsync(NotificationChannels.Alerts, transition.AlertId.ToString(), cancellationToken);
        }

        if (stateChanged || transitions.Count > 0)
        {
            await _bus.PublishAsync(NotificationChannels.EndpointStatus, endpointId.ToString(), cancellationToken);
        }

        return more;
    }

    private sealed record EndpointFacts(Guid Id, Guid ClientId, Guid SiteId, string Hostname, EndpointTier Tier, EndpointClass Class);

    /// <summary>Applies results in id order to states and alerts. Returns true when a state's status changed.</summary>
    private static async Task<bool> ApplyResultsAsync(FleetifyDbContext db, EndpointFacts endpoint, List<CheckResult> results,
        List<AlertTransition> transitions, DateTime now, CancellationToken cancellationToken)
    {
        var definitionIds = results.Select(r => r.CheckDefinitionId).Distinct().ToList();
        var linkedTemplates = await db.SiteMonitoringTemplates.AsNoTracking()
            .Where(l => l.SiteId == endpoint.SiteId)
            .Select(l => l.MonitoringTemplateId)
            .ToListAsync(cancellationToken);
        var definitions = await db.CheckDefinitions.AsNoTracking()
            .Where(d => definitionIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, cancellationToken);
        var states = await db.CheckStates
            .Where(s => s.EndpointId == endpoint.Id && definitionIds.Contains(s.CheckDefinitionId))
            .ToDictionaryAsync(s => (s.CheckDefinitionId, s.Target), cancellationToken);
        var openAlerts = (await db.Alerts
                .Where(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.Check && a.State != AlertState.Resolved &&
                            a.CheckDefinitionId != null && definitionIds.Contains(a.CheckDefinitionId.Value))
                .ToListAsync(cancellationToken))
            .ToDictionary(a => (a.CheckDefinitionId!.Value, a.Target));

        var statusChanged = false;
        foreach (var result in results)
        {
            if (!definitions.TryGetValue(result.CheckDefinitionId, out var definition) || !Applies(definition, endpoint, linkedTemplates))
            {
                continue;
            }

            var status = CheckEvaluator.Evaluate(definition.Type, result.Value, result.Error, definition.WarningThreshold, definition.CriticalThreshold);
            var key = (definition.Id, result.Target);

            if (!states.TryGetValue(key, out var state))
            {
                state = new CheckState
                {
                    EndpointId = endpoint.Id,
                    CheckDefinitionId = definition.Id,
                    Target = result.Target,
                    ClientId = endpoint.ClientId
                };
                db.CheckStates.Add(state);
                states[key] = state;
                statusChanged = true;
            }
            else if (state.Status != status)
            {
                statusChanged = true;
            }

            state.Status = status;
            state.Value = string.IsNullOrEmpty(result.Error) ? result.Value : null;
            state.Detail = Truncate(result.Detail, 1000);
            state.Error = Truncate(result.Error, 1000);
            state.ConsecutiveNonOk = status == CheckStatus.Ok ? 0 : Math.Min(state.ConsecutiveNonOk + 1, 1_000_000);
            state.LastResultAt = result.Time;
            state.UpdatedAt = now;

            openAlerts.TryGetValue(key, out var alert);
            if (status == CheckStatus.Ok)
            {
                if (alert is not null)
                {
                    alert.State = AlertState.Resolved;
                    alert.ResolvedAt = now;
                    alert.ResolvedReason = ResolvedReasonOk;
                    alert.UpdatedAt = now;
                    openAlerts.Remove(key);
                    transitions.Add(new AlertTransition(alert.Id, AlertTransitionKind.Resolved));
                }

                continue;
            }

            if (state.ConsecutiveNonOk < Math.Max(1, definition.FailuresBeforeAlert))
            {
                continue;
            }

            var severity = CheckEvaluator.SeverityFor(status);
            var title = Truncate(CheckEvaluator.AlertTitle(endpoint.Hostname, definition, result.Target, status, result.Value, result.Error), 500);
            var detail = Truncate(string.IsNullOrEmpty(result.Error) ? result.Detail : result.Error, 2000);

            if (alert is null)
            {
                alert = new Alert
                {
                    Id = Guid.NewGuid(),
                    ClientId = endpoint.ClientId,
                    EndpointId = endpoint.Id,
                    Kind = AlertKind.Check,
                    CheckDefinitionId = definition.Id,
                    Target = result.Target,
                    Severity = severity,
                    State = AlertState.Open,
                    Title = title,
                    Detail = detail,
                    OpenedAt = now,
                    UpdatedAt = now
                };
                db.Alerts.Add(alert);
                openAlerts[key] = alert;
                transitions.Add(new AlertTransition(alert.Id, AlertTransitionKind.Opened));
            }
            else if (alert.Severity != severity)
            {
                var escalated = severity > alert.Severity;
                alert.Severity = severity;
                alert.Title = title;
                alert.Detail = detail;
                alert.UpdatedAt = now;
                if (escalated)
                {
                    // An acknowledged warning that turns critical needs attention again.
                    alert.State = AlertState.Open;
                    transitions.Add(new AlertTransition(alert.Id, AlertTransitionKind.Escalated));
                }
            }
        }

        return statusChanged;
    }

    private static bool Applies(CheckDefinition definition, EndpointFacts endpoint, List<Guid> linkedTemplates) =>
        definition.Enabled &&
        linkedTemplates.Contains(definition.MonitoringTemplateId) &&
        (definition.AppliesTo == CheckAppliesTo.All ||
         (definition.AppliesTo == CheckAppliesTo.Server && endpoint.Class == EndpointClass.Server) ||
         (definition.AppliesTo == CheckAppliesTo.Workstation && endpoint.Class == EndpointClass.Workstation));

    private async Task<LicenseStatus> GetLicenseStatusAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow();
        lock (_licenseLock)
        {
            if (_licenseCache is { } cached && now < cached.ValidUntil)
            {
                return cached.Status;
            }
        }

        var status = await _licenses.GetStatusAsync(cancellationToken);
        lock (_licenseLock)
        {
            _licenseCache = (status, now + LicenseCacheDuration);
        }

        return status;
    }

    /// <summary>Lock key within <see cref="AdvisoryLockClass"/>. A collision only serializes two unrelated endpoints.</summary>
    private static int LockKey(Guid endpointId)
    {
        Span<byte> bytes = stackalloc byte[16];
        endpointId.TryWriteBytes(bytes);
        return BitConverter.ToInt32(bytes[..4]) ^ BitConverter.ToInt32(bytes.Slice(4, 4)) ^
               BitConverter.ToInt32(bytes.Slice(8, 4)) ^ BitConverter.ToInt32(bytes.Slice(12, 4));
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
