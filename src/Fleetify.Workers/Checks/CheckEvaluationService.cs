using System.Collections.Concurrent;
using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Infrastructure.Services;
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

    /// <summary>Advisory lock class of per-endpoint evaluation; also taken by the check run request service for resets.</summary>
    internal const int AdvisoryLockClass = 0x46434556; // "FCEV"
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

    // Resolves unresolved check alerts whose definition was deleted (the foreign key nulls it), disabled, unlinked from the
    // endpoint's site and the endpoint, disabled on the endpoint, or no longer matches the endpoint class.
    private static readonly string ResolveNotApplicableSql = """
        UPDATE "Alerts" a
        SET "State" = 'Resolved', "ResolvedAt" = @now, "UpdatedAt" = @now, "ResolvedReason" = @reason
        WHERE a."Kind" = 'Check' AND a."State" <> 'Resolved'
          AND NOT EXISTS (
            SELECT 1
            FROM "CheckDefinitions" d
            JOIN "Endpoints" e ON e."Id" = a."EndpointId"
            WHERE d."Id" = a."CheckDefinitionId" AND
        """ + EffectiveCheckResolver.AppliesSql + """
            )
        RETURNING a."Id" AS "Value"
        """;

    // States of checks that no longer apply are removed, so a check that applies again starts as "not run yet" instead of
    // showing an old result. Runs on the wide sweep only; the endpoint page hides such states in the meantime.
    private static readonly string DeleteNotApplicableStatesSql = """
        DELETE FROM "CheckStates" s
        USING "Endpoints" e
        WHERE e."Id" = s."EndpointId"
          AND NOT EXISTS (
            SELECT 1 FROM "CheckDefinitions" d
            WHERE d."Id" = s."CheckDefinitionId" AND
        """ + EffectiveCheckResolver.AppliesSql + """
            )
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
            await DeleteStaleStatesAsync(cancellationToken);
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
        if (wideSweep)
        {
            await DeleteStaleStatesAsync(cancellationToken);
        }

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
                await _notifier.AddNotificationsAsync(db, resolved.Select(id => new AlertTransition(id, NotificationEvent.Resolved)).ToList(),
                    cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await AlertCleanup.PublishAsync(_bus, resolved, cancellationToken);
        return resolved.Count;
    }

    /// <summary>Deletes check states of checks that no longer apply to their endpoint. Returns the number deleted.</summary>
    internal async Task<int> DeleteStaleStatesAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var deleted = await db.Database.ExecuteSqlRawAsync(DeleteNotApplicableStatesSql, cancellationToken);
        if (deleted > 0)
        {
            Logger.LogInformation("Removed {Count} check state(s) of checks that no longer apply", deleted);
        }

        return deleted;
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
                .Select(e => new EndpointFacts(e.Id, e.ClientId, e.SiteId, e.Hostname, e.Tier, e.ClassOverride ?? e.DetectedClass, e.OsPlatform))
                .FirstOrDefaultAsync(cancellationToken);

            if (endpoint is not null)
            {
                var license = await GetLicenseStatusAsync(cancellationToken);
                if (TierRules.EffectiveTier(endpoint.Tier, license) == EndpointTier.Managed)
                {
                    var inMaintenance = await db.Endpoints.AsNoTracking()
                        .Where(e => e.Id == endpointId)
                        .Where(MaintenanceRules.EndpointInMaintenance(now, db.MaintenanceWindowOccurrences, db.SitePolicies, db.Policies))
                        .AnyAsync(cancellationToken);
                    stateChanged = await ApplyResultsAsync(db, endpoint, results, transitions, now, inMaintenance, cancellationToken);
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

    private sealed record EndpointFacts(Guid Id, Guid ClientId, Guid SiteId, string Hostname, EndpointTier Tier, EndpointClass Class, string OsPlatform);

    /// <summary>
    /// Applies results in id order to states and alerts. Returns true when a state's status changed. In maintenance
    /// (<paramref name="inMaintenance"/>) states and failure counts still update and alerts still resolve, but no alert opens or
    /// escalates; the first failing result after maintenance opens the alert at once, because the failure count kept counting.
    /// </summary>
    private static async Task<bool> ApplyResultsAsync(FleetifyDbContext db, EndpointFacts endpoint, List<CheckResult> results,
        List<AlertTransition> transitions, DateTime now, bool inMaintenance, CancellationToken cancellationToken)
    {
        var definitionIds = results.Select(r => r.CheckDefinitionId).Distinct().ToList();
        var checks = (await EffectiveCheckResolver.LoadAsync(db, endpoint.Id, endpoint.SiteId, endpoint.Class, endpoint.OsPlatform,
                includeDisabledOnEndpoint: false, cancellationToken))
            .ToDictionary(c => c.Id);
        var states = await db.CheckStates
            .Where(s => s.EndpointId == endpoint.Id && definitionIds.Contains(s.CheckDefinitionId))
            .ToDictionaryAsync(s => (s.CheckDefinitionId, s.Target), cancellationToken);
        var openAlerts = (await db.Alerts
                .Where(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.Check && a.State != AlertState.Resolved &&
                            a.CheckDefinitionId != null && definitionIds.Contains(a.CheckDefinitionId.Value))
                .ToListAsync(cancellationToken))
            .ToDictionary(a => (a.CheckDefinitionId!.Value, a.Target));

        var statusChanged = false;
        var hourly = new Dictionary<(Guid, string, DateTime), RollupAccumulator>();
        var daily = new Dictionary<(Guid, string, DateTime), RollupAccumulator>();
        var parameterCache = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        foreach (var result in results)
        {
            if (!checks.TryGetValue(result.CheckDefinitionId, out var check))
            {
                continue;
            }

            var definition = check.Definition;
            if (!parameterCache.TryGetValue(definition.Id, out var parameters))
            {
                parameters = CheckParameters.Parse(definition.ParametersJson);
                parameterCache[definition.Id] = parameters;
            }

            // History counts every result of an applying check, also one that a reset makes the evaluation ignore.
            var at = CheckHistoryRules.EffectiveTime(result.AgentTime, result.Time);
            var noResponse = string.IsNullOrEmpty(result.Error) && CheckHistoryRules.IsNoResponse(definition.Type, result.Target, parameters, result.Value);
            Accumulate(hourly, (definition.Id, result.Target, CheckHistoryRules.HourBucket(at)), result, noResponse);
            Accumulate(daily, (definition.Id, result.Target, CheckHistoryRules.DayBucket(at)), result, noResponse);

            var key = (definition.Id, result.Target);
            if (states.TryGetValue(key, out var existing) && existing.ResetAt is { } resetAt && result.Time < resetAt)
            {
                // Ingested before a technician reset the check: that result belongs to the old state.
                continue;
            }

            var status = CheckEvaluator.Evaluate(definition.Type, result.Target, result.Value, result.Error, check.WarningThreshold,
                check.CriticalThreshold, parameters);

            if (existing is not { } state)
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
            else if (state.Status != status || state.RerunRequested)
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
                    transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Resolved));
                }

                continue;
            }

            if (state.ConsecutiveNonOk < Math.Max(1, check.FailuresBeforeAlert))
            {
                continue;
            }

            var severity = CheckEvaluator.SeverityFor(status);
            var title = Truncate(CheckEvaluator.AlertTitle(endpoint.Hostname, check.ToEffectiveDefinition(), result.Target, status, result.Value, result.Error), 500);
            var detail = Truncate(string.IsNullOrEmpty(result.Error) ? result.Detail : result.Error, 2000);

            if (alert is null)
            {
                if (inMaintenance)
                {
                    continue;
                }

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
                transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Opened));
            }
            else if (alert.Severity != severity && !(inMaintenance && severity > alert.Severity))
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
                    transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Escalated));
                }
            }
        }

        await UpsertRollupsAsync(db, "CheckResultsHourly", endpoint, hourly, cancellationToken);
        await UpsertRollupsAsync(db, "CheckResultsDaily", endpoint, daily, cancellationToken);
        return statusChanged;
    }

    private sealed class RollupAccumulator
    {
        public double? Min;
        public double? Max;
        public double Sum;
        public int Values;
        public int Errors;
        public int NoResponses;
    }

    private static void Accumulate(Dictionary<(Guid, string, DateTime), RollupAccumulator> buckets, (Guid, string, DateTime) key, CheckResult result,
        bool noResponse)
    {
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new RollupAccumulator();
            buckets[key] = bucket;
        }

        if (!string.IsNullOrEmpty(result.Error) || double.IsNaN(result.Value) || double.IsInfinity(result.Value))
        {
            bucket.Errors++;
        }
        else if (noResponse)
        {
            bucket.NoResponses++;
        }
        else
        {
            bucket.Min = bucket.Min is { } min ? Math.Min(min, result.Value) : result.Value;
            bucket.Max = bucket.Max is { } max ? Math.Max(max, result.Value) : result.Value;
            bucket.Sum += result.Value;
            bucket.Values++;
        }
    }

    // LEAST and GREATEST ignore nulls in PostgreSQL, so a bucket without values keeps null until a value arrives. The table name is a
    // constant chosen by the caller, never input.
    private const string UpsertRollupSql = """
        INSERT INTO "{0}" AS t ("EndpointId", "CheckDefinitionId", "Target", "Bucket", "ClientId", "MinValue", "MaxValue", "SumValue", "ValueCount", "ErrorCount", "NoResponseCount")
        SELECT @endpointId, u.check_id, u.target, u.bucket, @clientId, u.min_value, u.max_value, u.sum_value, u.value_count, u.error_count, u.no_response_count
        FROM unnest(@checkIds, @targets, @buckets, @mins, @maxs, @sums, @valueCounts, @errorCounts, @noResponseCounts)
          AS u(check_id, target, bucket, min_value, max_value, sum_value, value_count, error_count, no_response_count)
        ON CONFLICT ("EndpointId", "CheckDefinitionId", "Target", "Bucket") DO UPDATE SET
          "MinValue" = LEAST(t."MinValue", EXCLUDED."MinValue"),
          "MaxValue" = GREATEST(t."MaxValue", EXCLUDED."MaxValue"),
          "SumValue" = t."SumValue" + EXCLUDED."SumValue",
          "ValueCount" = t."ValueCount" + EXCLUDED."ValueCount",
          "ErrorCount" = t."ErrorCount" + EXCLUDED."ErrorCount",
          "NoResponseCount" = t."NoResponseCount" + EXCLUDED."NoResponseCount"
        """;

    private static async Task UpsertRollupsAsync(FleetifyDbContext db, string table, EndpointFacts endpoint,
        Dictionary<(Guid, string, DateTime), RollupAccumulator> buckets, CancellationToken cancellationToken)
    {
        if (buckets.Count == 0)
        {
            return;
        }

        var rows = buckets.ToList();
#pragma warning disable EF1002 // The table name is one of two constants; every value is a parameter.
        await db.Database.ExecuteSqlRawAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, UpsertRollupSql, table),
            [
                new NpgsqlParameter("endpointId", endpoint.Id),
                new NpgsqlParameter("clientId", endpoint.ClientId),
                new NpgsqlParameter("checkIds", rows.Select(r => r.Key.Item1).ToArray()),
                new NpgsqlParameter("targets", rows.Select(r => r.Key.Item2).ToArray()),
                new NpgsqlParameter("buckets", rows.Select(r => DateTime.SpecifyKind(r.Key.Item3, DateTimeKind.Utc)).ToArray()),
                new NpgsqlParameter("mins", rows.Select(r => r.Value.Min).ToArray()),
                new NpgsqlParameter("maxs", rows.Select(r => r.Value.Max).ToArray()),
                new NpgsqlParameter("sums", rows.Select(r => r.Value.Sum).ToArray()),
                new NpgsqlParameter("valueCounts", rows.Select(r => r.Value.Values).ToArray()),
                new NpgsqlParameter("errorCounts", rows.Select(r => r.Value.Errors).ToArray()),
                new NpgsqlParameter("noResponseCounts", rows.Select(r => r.Value.NoResponses).ToArray())
            ], cancellationToken);
#pragma warning restore EF1002
    }

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
    internal static int LockKey(Guid endpointId)
    {
        Span<byte> bytes = stackalloc byte[16];
        endpointId.TryWriteBytes(bytes);
        return BitConverter.ToInt32(bytes[..4]) ^ BitConverter.ToInt32(bytes.Slice(4, 4)) ^
               BitConverter.ToInt32(bytes.Slice(8, 4)) ^ BitConverter.ToInt32(bytes.Slice(12, 4));
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
