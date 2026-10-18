using Fleeto.Core.Interfaces;
using Fleeto.Core.Performance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleeto.Infrastructure.Performance;

/// <summary>
/// Keeps <see cref="PerfSettingsCache"/> current in every component (PERFORMANCE.md §5): read at start, again when web announces a
/// change on <see cref="NotificationChannels.Performance"/>, and every 30 seconds in case a notification was lost. A read that
/// fails keeps the settings it had.
/// </summary>
public sealed class PerfSettingsLoader : BackgroundService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private const string ReadSql = """SELECT "Json" FROM "PerformanceSettings" WHERE "Id" = 1""";

    private readonly NpgsqlDataSource _dataSource;
    private readonly PerfSettingsCache _cache;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<PerfSettingsLoader> _logger;
    private readonly SemaphoreSlim _changed = new(0, 1);

    public PerfSettingsLoader(NpgsqlDataSource dataSource, PerfSettingsCache cache, INotificationBus bus, TimeProvider time,
        ILogger<PerfSettingsLoader> logger)
    {
        _dataSource = dataSource;
        _cache = cache;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _bus.Subscribe(NotificationChannels.Performance, (_, _) =>
        {
            if (_changed.CurrentCount == 0)
            {
                try
                {
                    _changed.Release();
                }
                catch (SemaphoreFullException)
                {
                    // A refresh is already pending.
                }
            }

            return Task.CompletedTask;
        });

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await LoadAsync(stoppingToken))
            {
                failures = 0;
            }
            else if (++failures == 3)
            {
                _logger.LogWarning("The performance monitor settings could not be read three times in a row; it keeps the settings it has");
            }

            try
            {
                await _changed.WaitAsync(RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Reads the settings once. False when the database could not be read.</summary>
    public async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = PerfContext.Begin(PerfKeys.MonitorSource);
        try
        {
            await using var command = _dataSource.CreateCommand(ReadSql);
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            _cache.Set(PerformanceSettings.Parse(json));
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "The performance monitor settings could not be read");
            return false;
        }
    }
}
