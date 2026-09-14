using System.Collections.Concurrent;
using Fleetify.Infrastructure.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleetify.Workers.Hosting;

/// <summary>
/// Liveness of the workers process. Every loop reports here; the heartbeat file is touched only while every loop has
/// reported within <see cref="MaxSilence"/> or is inside a run that has not exceeded its own maximum duration (a
/// backup may take hours). A stuck loop therefore makes the container unhealthy, so Docker restarts it.
/// </summary>
public sealed class WorkerHeartbeat
{
    public static readonly TimeSpan MaxSilence = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, LoopState> _loops = new();
    private readonly TimeProvider _time;

    public WorkerHeartbeat(TimeProvider time)
    {
        _time = time;
    }

    public void Report(string loop)
    {
        var now = _time.GetUtcNow();
        _loops.AddOrUpdate(loop, _ => new LoopState(now, null), (_, state) => state with { LastReport = now });
    }

    /// <summary>Marks the start of a run that may take up to <paramref name="maxDuration"/> without reporting.</summary>
    public void BeginRun(string loop, TimeSpan maxDuration)
    {
        var now = _time.GetUtcNow();
        _loops[loop] = new LoopState(now, now + maxDuration);
    }

    public void EndRun(string loop) => _loops[loop] = new LoopState(_time.GetUtcNow(), null);

    /// <summary>Loops that are silent for too long; empty when the process is healthy.</summary>
    public IReadOnlyList<string> StaleLoops()
    {
        var now = _time.GetUtcNow();
        return _loops
            .Where(l => now - l.Value.LastReport > MaxSilence && (l.Value.RunDeadline is null || now > l.Value.RunDeadline))
            .Select(l => l.Key)
            .ToList();
    }

    private sealed record LoopState(DateTimeOffset LastReport, DateTimeOffset? RunDeadline);
}

/// <summary>Touches the heartbeat file every 30 seconds while all loops are healthy.</summary>
public sealed class HeartbeatFileService : BackgroundService
{
    private readonly WorkerHeartbeat _heartbeat;
    private readonly ILogger<HeartbeatFileService> _logger;

    public HeartbeatFileService(WorkerHeartbeat heartbeat, ILogger<HeartbeatFileService> logger)
    {
        _heartbeat = heartbeat;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reportedStale = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var stale = _heartbeat.StaleLoops();
            if (stale.Count == 0)
            {
                HeartbeatFile.Touch(FleetifyComponent.Workers);
                reportedStale = false;
            }
            else if (!reportedStale)
            {
                _logger.LogError("Worker loops stopped reporting: {Loops}. The health check will fail until they recover",
                    string.Join(", ", stale));
                reportedStale = true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
