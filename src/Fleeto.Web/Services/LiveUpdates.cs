using Fleeto.Core.Interfaces;

namespace Fleeto.Web.Services;

/// <summary>
/// Pushes database notifications to open pages: endpoint status, alerts and check results. Components subscribe in
/// <c>OnInitialized</c>, unsubscribe in <c>Dispose</c> and refresh through a <see cref="DebouncedRefresh"/>. No polling.
/// The id <see cref="Guid.Empty"/> means "anything may have changed" (sent after the notification listener reconnects),
/// so subscribers reload from the database.
/// </summary>
public sealed class LiveUpdates : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ILogger<LiveUpdates> _logger;

    public LiveUpdates(INotificationBus bus, ILogger<LiveUpdates> logger)
    {
        _logger = logger;
        _subscriptions.Add(bus.Subscribe(NotificationChannels.EndpointStatus, (payload, _) => Raise(EndpointStatusChanged, payload)));
        _subscriptions.Add(bus.Subscribe(NotificationChannels.Alerts, (payload, _) => Raise(AlertChanged, payload)));
        _subscriptions.Add(bus.Subscribe(NotificationChannels.CheckResults, (payload, _) => Raise(CheckResultsChanged, payload)));
        _subscriptions.Add(bus.Subscribe(NotificationChannels.Jobs, (payload, _) => Raise(JobsChanged, payload)));
    }

    /// <summary>Payload: endpoint id, or <see cref="Guid.Empty"/> after a resync.</summary>
    public event Action<Guid>? EndpointStatusChanged;

    /// <summary>Payload: alert id, or <see cref="Guid.Empty"/> after a resync.</summary>
    public event Action<Guid>? AlertChanged;

    /// <summary>Payload: endpoint id, or <see cref="Guid.Empty"/> after a resync.</summary>
    public event Action<Guid>? CheckResultsChanged;

    /// <summary>Payload: endpoint id whose jobs changed, or <see cref="Guid.Empty"/> after a resync (0.2.0).</summary>
    public event Action<Guid>? JobsChanged;

    private Task Raise(Action<Guid>? handlers, string payload)
    {
        if (handlers is null)
        {
            return Task.CompletedTask;
        }

        var id = payload == NotificationBusEvents.Resync ? Guid.Empty : Guid.TryParse(payload, out var parsed) ? parsed : Guid.Empty;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<Guid>>())
        {
            try
            {
                handler(id);
            }
            catch (Exception ex)
            {
                // One broken page must not stop updates for every other open page.
                _logger.LogWarning(ex, "A live update subscriber failed");
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }
}

/// <summary>
/// Collapses bursts of notifications into one refresh: the first trigger schedules a refresh after the delay, later
/// triggers within that window are absorbed. A one-shot timer, not a polling loop.
/// </summary>
public sealed class DebouncedRefresh : IDisposable
{
    private readonly Func<Task> _refresh;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;
    private readonly Lock _lock = new();
    private bool _pending;
    private bool _disposed;

    public DebouncedRefresh(Func<Task> refresh, TimeSpan? delay = null)
    {
        _refresh = refresh;
        _delay = delay ?? TimeSpan.FromSeconds(1);
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Trigger()
    {
        lock (_lock)
        {
            if (_disposed || _pending)
            {
                return;
            }

            _pending = true;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async void Fire()
    {
        lock (_lock)
        {
            _pending = false;
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            await _refresh();
        }
        catch (Exception)
        {
            // The page shows its own error state on the next explicit load; a background refresh never throws.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }
}
