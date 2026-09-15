using System.Collections.Concurrent;
using Fleeto.Core.Interfaces;

namespace Fleeto.Testing;

/// <summary>Notification bus for tests: records every publish and delivers to in-process subscribers synchronously.</summary>
public sealed class InMemoryNotificationBus : INotificationBus
{
    private readonly ConcurrentDictionary<Guid, (string Channel, Func<string, CancellationToken, Task> Handler)> _subscribers = new();

    public ConcurrentQueue<(string Channel, string Payload)> Published { get; } = new();

    public async Task PublishAsync(string channel, string payload, CancellationToken cancellationToken = default)
    {
        Published.Enqueue((channel, payload));
        foreach (var (subscribedChannel, handler) in _subscribers.Values)
        {
            if (subscribedChannel == channel)
            {
                await handler(payload, cancellationToken);
            }
        }
    }

    public IDisposable Subscribe(string channel, Func<string, CancellationToken, Task> handler)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = (channel, handler);
        return new Subscription(() => _subscribers.TryRemove(id, out _));
    }

    public IReadOnlyList<string> PayloadsFor(string channel) =>
        Published.Where(p => p.Channel == channel).Select(p => p.Payload).ToList();

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
