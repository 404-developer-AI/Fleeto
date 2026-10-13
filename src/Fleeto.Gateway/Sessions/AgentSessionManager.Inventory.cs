using Fleeto.Core.Domain;
using Fleeto.Core.Interfaces;
using Fleeto.Protocol.Agent.V1;

namespace Fleeto.Gateway.Sessions;

/// <summary>Refresh on an endpoint (0.6.0): asks a live agent for a fresh inventory a user requested.</summary>
public sealed partial class AgentSessionManager
{
    private async Task OnInventoryRequestAsync(string payload, CancellationToken cancellationToken)
    {
        if (payload == NotificationBusEvents.Resync)
        {
            await DeliverInventoryRequestsAsync(_sessions.Keys.ToArray(), cancellationToken);
        }
        else if (Guid.TryParse(payload, out var endpointId) && _sessions.ContainsKey(endpointId))
        {
            await DeliverInventoryRequestsAsync([endpointId], cancellationToken);
        }
    }

    /// <summary>
    /// Sends an inventory request to each live endpoint with a recent request in the database: the notification only wakes the
    /// gateway up. A request older than <see cref="InventoryRefreshRules.DeliveryWindow"/> is not sent any more; the next session
    /// asks for the inventory anyway when it changed.
    /// </summary>
    internal async Task DeliverInventoryRequestsAsync(Guid[] endpointIds, CancellationToken cancellationToken)
    {
        var live = endpointIds.Where(id => _sessions.TryGetValue(id, out var s) && !s.IsClosing).ToArray();
        if (live.Length == 0)
        {
            return;
        }

        var since = _time.GetUtcNow().UtcDateTime - InventoryRefreshRules.DeliveryWindow;
        foreach (var endpointId in await _store.ReadInventoryRequestsAsync(live, since, cancellationToken))
        {
            if (_sessions.TryGetValue(endpointId, out var session) && !session.IsClosing &&
                session.Send(new ServerMessage { InventoryRequest = new InventoryRequest() }))
            {
                _logger.LogInformation("Endpoint {EndpointId}: asked the agent for a fresh inventory", endpointId);
            }
        }
    }
}
