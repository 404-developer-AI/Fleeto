namespace Fleetify.Workers.Common;

/// <summary>
/// Opens after a number of consecutive failures and stays open for a pause, so a dead SMTP server or storage endpoint
/// is not hammered and the logs are not flooded. Thread-safe.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly object _lock = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _pause;
    private readonly TimeProvider _time;
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;

    public CircuitBreaker(int failureThreshold, TimeSpan pause, TimeProvider time)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _pause = pause;
        _time = time;
    }

    /// <summary>True while calls must not be attempted.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _openUntil is not null && _time.GetUtcNow() < _openUntil;
            }
        }
    }

    public DateTimeOffset? OpenUntil
    {
        get
        {
            lock (_lock)
            {
                return IsOpenUnlocked() ? _openUntil : null;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
        }
    }

    /// <summary>Records a failure. Returns true when this failure opened the breaker.</summary>
    public bool RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures < _failureThreshold)
            {
                return false;
            }

            _consecutiveFailures = 0;
            _openUntil = _time.GetUtcNow() + _pause;
            return true;
        }
    }

    private bool IsOpenUnlocked() => _openUntil is not null && _time.GetUtcNow() < _openUntil;
}

/// <summary>Retries a transient operation with exponential backoff.</summary>
public static class Retry
{
    public static async Task<T> WithBackoffAsync<T>(Func<CancellationToken, Task<T>> operation, int attempts, TimeSpan firstDelay,
        Action<Exception, int>? onRetry, CancellationToken cancellationToken)
    {
        var delay = firstDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception ex) when (attempt < attempts && ex is not OperationCanceledException)
            {
                onRetry?.Invoke(ex, attempt);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromTicks(delay.Ticks * 3);
            }
        }
    }
}
