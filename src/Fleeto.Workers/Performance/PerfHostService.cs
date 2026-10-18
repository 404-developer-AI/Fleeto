using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Performance;
using Fleeto.Workers.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Performance;

/// <summary>
/// Writes one row about the VPS per minute for the performance monitor (0.7.0, PERFORMANCE.md §4.6). The workers measure the VPS
/// for the whole instance; every instance on the VPS measures the same machine, which the page says.
/// </summary>
public sealed class PerfHostService : WorkerLoop
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly PerfHostReader _reader;
    private DateTime _lastMinute;

    public PerfHostService(IFleetoDbContextFactory dbFactory, PerfHostReader reader, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<PerfHostService> logger)
        : base("perf-host", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _reader = reader;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(15);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        // Every 15 seconds, written once per minute: the first read of a minute closes the previous one, so the rates cover a full
        // minute however the loop was woken.
        var minute = PerfRecorder.MinuteOf(Time.GetUtcNow().UtcDateTime);
        if (minute == _lastMinute)
        {
            return false;
        }

        var first = _lastMinute == default;
        _lastMinute = minute;
        var row = await _reader.ReadAsync(minute, cancellationToken);
        if (first || !Perf.Recorder.Collects(PerfCollector.Host))
        {
            return false;
        }

        // The minute that just ended.
        row.Minute = minute.AddMinutes(-1);
        await using var db = _dbFactory.CreateSystem();
        db.PerfHostMinutes.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return false;
    }
}
