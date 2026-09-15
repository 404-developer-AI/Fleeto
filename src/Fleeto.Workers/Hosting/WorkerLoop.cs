using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Hosting;

/// <summary>
/// Base class of every workers loop. Runs <see cref="RunOnceAsync"/> on an interval and whenever <see cref="Wake"/> is
/// called (a notification arrived). A failed run is logged and retried with exponential backoff; an exception never
/// leaves the loop, so one broken job cannot stop the host or the other loops. The loop reports to the
/// <see cref="WorkerHeartbeat"/> at least every 30 seconds.
/// </summary>
public abstract class WorkerLoop : BackgroundService
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly WorkerHeartbeat _heartbeat;

    protected WorkerLoop(string name, WorkerHeartbeat heartbeat, TimeProvider time, ILogger logger)
    {
        Name = name;
        _heartbeat = heartbeat;
        Time = time;
        Logger = logger;
    }

    /// <summary>Loop name for logs and the heartbeat.</summary>
    public string Name { get; }

    protected TimeProvider Time { get; }
    protected ILogger Logger { get; }

    /// <summary>Time between runs when nothing wakes the loop.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>Longest a single run may take without reporting before the process counts as unhealthy.</summary>
    protected virtual TimeSpan MaxRunDuration => TimeSpan.FromMinutes(5);

    /// <summary>One pass. Returns true when more work is waiting, so the loop runs again at once.</summary>
    protected abstract Task<bool> RunOnceAsync(CancellationToken cancellationToken);

    /// <summary>Called once before the first run, e.g. to subscribe to notifications. Must not throw.</summary>
    protected virtual void OnStarting()
    {
    }

    /// <summary>Called when the host stops, e.g. to dispose subscriptions.</summary>
    protected virtual void OnStopping()
    {
    }

    /// <summary>Requests a run as soon as the current one has finished. Safe from any thread.</summary>
    public void Wake() => _wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Why: BackgroundService.StartAsync runs ExecuteAsync synchronously up to the first await; yield so a slow
        // first run never delays host start-up or the other loops.
        await Task.Yield();

        _heartbeat.Report(Name);
        try
        {
            OnStarting();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loop {Loop} could not subscribe to notifications; it continues on its interval", Name);
        }

        var backoff = TimeSpan.FromSeconds(5);
        var nextRun = Time.GetUtcNow();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _heartbeat.Report(Name);
                if (Time.GetUtcNow() >= nextRun)
                {
                    try
                    {
                        _heartbeat.BeginRun(Name, MaxRunDuration);
                        var more = await RunOnceAsync(stoppingToken);
                        backoff = TimeSpan.FromSeconds(5);
                        nextRun = more ? Time.GetUtcNow() : Time.GetUtcNow() + Interval;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Loop {Loop} failed; retrying in {Backoff}", Name, backoff);
                        nextRun = Time.GetUtcNow() + backoff;
                        backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
                    }
                    finally
                    {
                        _heartbeat.EndRun(Name);
                    }
                }

                var wait = nextRun - Time.GetUtcNow();
                if (wait <= TimeSpan.Zero)
                {
                    continue;
                }

                if (wait > ReportInterval)
                {
                    wait = ReportInterval;
                }

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var woken = _wake.Reader.WaitToReadAsync(waitCts.Token).AsTask();
                var delay = Task.Delay(wait, Time, waitCts.Token);
                var completed = await Task.WhenAny(woken, delay);
                await waitCts.CancelAsync();

                if (completed == woken && woken.IsCompletedSuccessfully && _wake.Reader.TryRead(out _))
                {
                    // A wake-up during backoff does not skip the backoff: the database may still be down.
                    if (backoff <= TimeSpan.FromSeconds(5))
                    {
                        nextRun = Time.GetUtcNow();
                    }
                }
            }
        }
        finally
        {
            OnStopping();
        }
    }
}
