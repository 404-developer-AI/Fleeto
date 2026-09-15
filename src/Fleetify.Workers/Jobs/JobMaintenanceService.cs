using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleetify.Workers.Jobs;

/// <summary>
/// Moves jobs that nobody will finish to their final state (0.2.0, ARCHITECTURE.md §4, Job and Job output), every minute:
/// <list type="bullet">
/// <item>a job fleetify-signer did not sign within 15 minutes is refused (the signer was down; the technician starts it again);</item>
/// <item>a queued job that did not start before its ValidUntil (plus the agent's clock tolerance) expires;</item>
/// <item>a running job without a result long after its timeout becomes lost: the outcome is unknown, and a late result still
/// replaces it;</item>
/// <item>output still missing 7 days after the job ended stays incomplete.</item>
/// </list>
/// Every statement is conditional on the state it changes, so it never overwrites what the gateway stored meanwhile.
/// </summary>
public sealed class JobMaintenanceService : WorkerLoop
{
    public const string SignerTimeoutReason = "fleetify-signer did not sign the job in time. Start the job again; check that fleetify-signer is running.";
    public static readonly TimeSpan SigningTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The agent refuses a job this long after ValidUntil; after that it can no longer start.</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromMinutes(10);

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;

    public JobMaintenanceService(IFleetifyDbContextFactory dbFactory, INotificationBus bus, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<JobMaintenanceService> logger)
        : base("job-maintenance", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await RunAsync(cancellationToken);
        return false;
    }

    /// <summary>Applies every transition once. Returns the number of jobs changed.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var endpoints = new HashSet<Guid>();

        var signingCutoff = now - SigningTimeout;
        var unsigned = db.Jobs.Where(j => j.State == JobState.PendingSignature && j.CreatedAt < signingCutoff);
        endpoints.UnionWith(await unsigned.Select(j => j.EndpointId).Distinct().ToListAsync(cancellationToken));
        var changed = await unsigned.ExecuteUpdateAsync(s => s
            .SetProperty(j => j.State, JobState.Refused)
            .SetProperty(j => j.RefusalReason, SignerTimeoutReason)
            .SetProperty(j => j.CompletedAt, now), cancellationToken);

        var startCutoff = now - StartTolerance;
        var expired = db.Jobs.Where(j => j.State == JobState.Queued && j.ValidUntil < startCutoff);
        endpoints.UnionWith(await expired.Select(j => j.EndpointId).Distinct().ToListAsync(cancellationToken));
        changed += await expired.ExecuteUpdateAsync(s => s.SetProperty(j => j.State, JobState.Expired), cancellationToken);

        // Timeout is per job; the grace covers a slow reconnect after the process ended.
        var lostSeconds = (int)ScriptRules.LostGrace.TotalSeconds;
        var lost = db.Jobs.Where(j => j.State == JobState.Running && j.CompletedAt == null && j.StartedAt != null &&
                                      j.StartedAt.Value.AddSeconds(j.TimeoutSeconds + lostSeconds) < now);
        endpoints.UnionWith(await lost.Select(j => j.EndpointId).Distinct().ToListAsync(cancellationToken));
        changed += await lost.ExecuteUpdateAsync(s => s.SetProperty(j => j.State, JobState.Lost), cancellationToken);

        var outputCutoff = now - ScriptRules.OutputRecoveryPeriod;
        changed += await db.Jobs.Where(j => j.OutputState == JobOutputState.Receiving && j.CompletedAt != null && j.CompletedAt < outputCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.OutputState, JobOutputState.Incomplete), cancellationToken);

        if (changed > 0)
        {
            Logger.LogInformation("Job maintenance changed {Count} job(s)", changed);
        }

        foreach (var endpointId in endpoints)
        {
            try
            {
                await _bus.PublishAsync(NotificationChannels.Jobs, endpointId.ToString(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug(ex, "Could not publish a job notification");
            }
        }

        return changed;
    }
}
