using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Fleeto.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleeto.Infrastructure.Notifications;

/// <summary>
/// Notifications over PostgreSQL LISTEN/NOTIFY. One dedicated connection listens to every channel in
/// <see cref="NotificationChannels"/>; subscribers filter in process. After a reconnect every subscriber gets
/// <see cref="NotificationBusEvents.Resync"/> so it can catch up from the database: a lost notification delays
/// work, it never loses data.
/// <para>
/// PostgreSQL instead of Valkey because fleeto-signer may only talk to the database, so database
/// notifications are needed anyway; one mechanism is simpler on a single VPS.
/// </para>
/// </summary>
public sealed class PostgresNotificationBus : BackgroundService, INotificationBus
{
    private static readonly string[] AllChannels = typeof(NotificationChannels)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresNotificationBus> _logger;
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();

    public PostgresNotificationBus(NpgsqlDataSource dataSource, ILogger<PostgresNotificationBus> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task PublishAsync(string channel, string payload, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("SELECT pg_notify(@channel, @payload)");
        command.Parameters.AddWithValue("channel", channel);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public IDisposable Subscribe(string channel, Func<string, CancellationToken, Task> handler)
    {
        var subscription = new Subscription(channel, handler, _logger);
        _subscriptions[subscription.Id] = subscription;
        return new Unsubscriber(() =>
        {
            if (_subscriptions.TryRemove(subscription.Id, out var removed))
            {
                removed.Complete();
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var firstConnection = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += (_, e) => Dispatch(e.Channel, e.Payload);

                foreach (var channel in AllChannels)
                {
                    // Channel names are compile-time constants from NotificationChannels, never input.
#pragma warning disable CA2100
                    await using var listen = new NpgsqlCommand($"LISTEN \"{channel}\"", connection);
#pragma warning restore CA2100
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                if (!firstConnection)
                {
                    _logger.LogInformation("Notification listener reconnected; asking subscribers to resync");
                    foreach (var channel in AllChannels)
                    {
                        Dispatch(channel, NotificationBusEvents.Resync);
                    }
                }

                firstConnection = false;
                delay = TimeSpan.FromSeconds(1);

                while (!stoppingToken.IsCancellationRequested)
                {
                    // Wake up regularly so a dead connection is noticed even when no notifications arrive.
                    await connection.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                    if (!await PingAsync(connection, stoppingToken))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Notification listener lost its database connection; retrying in {Delay}", delay);
                firstConnection = false;
                await Task.Delay(delay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Complete();
        }
    }

    private void Dispatch(string channel, string payload)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.Channel == channel)
            {
                subscription.Enqueue(payload);
            }
        }
    }

    private static async Task<bool> PingAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var ping = new NpgsqlCommand("SELECT 1", connection);
            await ping.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    /// <summary>One subscriber with its own queue, so a slow handler never blocks the listener or other subscribers.</summary>
    private sealed class Subscription
    {
        private readonly Channel<string> _queue = System.Threading.Channels.Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _cts = new();

        public Subscription(string channel, Func<string, CancellationToken, Task> handler, ILogger logger)
        {
            Channel = channel;
            _ = Task.Run(async () =>
            {
                await foreach (var payload in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await handler(payload, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Notification handler for {Channel} failed", channel);
                    }
                }
            });
        }

        public Guid Id { get; } = Guid.NewGuid();
        public string Channel { get; }

        public void Enqueue(string payload) => _queue.Writer.TryWrite(payload);

        public void Complete()
        {
            _queue.Writer.TryComplete();
            _cts.Cancel();
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                dispose();
            }
        }
    }
}
