using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Gateway.Data;
using Fleeto.Protocol.Agent.V1;
using Npgsql;

namespace Fleeto.Gateway.Sessions;

/// <summary>Storage analysis (0.6.0): scan reports from the agent, scan requests to it.</summary>
public sealed partial class AgentSessionManager
{
    /// <summary>
    /// Stores a volume scan and acknowledges it only after the commit, so the agent keeps it on disk until then. A report of an
    /// agent-only endpoint, a duplicate and one over the daily limit are acknowledged without storing, so the agent drops them.
    /// </summary>
    private async Task SaveStorageScanAsync(AgentSession session, StorageScanReport report, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(report.ScanId, out var scanId) || scanId == Guid.Empty || string.IsNullOrEmpty(report.Volume))
        {
            session.Close(DisconnectCode.ProtocolError, "A storage scan report needs a scan id and a volume.");
            return;
        }

        var ack = new ServerMessage { StorageScanAck = new StorageScanAck { ScanId = report.ScanId } };
        if (session.Tier != EndpointTier.Managed)
        {
            // Tier enforcement, layer 3: storage analysis is for managed endpoints only.
            _logger.LogWarning("Endpoint {EndpointId} is agent-only but sent a storage scan; it is acknowledged and not stored", session.EndpointId);
            session.Send(ack);
            return;
        }

        StorageScanOutcome outcome;
        try
        {
            outcome = await _store.SaveStorageScanAsync(session.EndpointId, session.ClientId, scanId, report, _time.GetUtcNow().UtcDateTime,
                cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // No ack: the agent keeps the report on disk and sends it again.
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not store a storage scan; it is not acknowledged", session.EndpointId);
            return;
        }

        session.Send(ack);
        switch (outcome)
        {
            case StorageScanOutcome.Stored:
                await PublishAsync(NotificationChannels.StorageScans, session.EndpointId);
                break;
            case StorageScanOutcome.OverLimit:
                _logger.LogWarning("Endpoint {EndpointId} sent more than {Limit} storage scans in 24 hours; the scan is acknowledged and not stored",
                    session.EndpointId, Core.Domain.StorageRules.MaxScansPerEndpointPerDay);
                break;
        }
    }

    private async Task OnStorageScanRequestAsync(string payload, CancellationToken cancellationToken)
    {
        if (payload == NotificationBusEvents.Resync)
        {
            await DeliverStorageScanRequestsAsync(_sessions.Keys.ToArray(), cancellationToken);
        }
        else if (Guid.TryParse(payload, out var requestId) &&
                 await _store.ReadStorageScanRequestEndpointAsync(requestId, cancellationToken) is { } endpointId &&
                 _sessions.ContainsKey(endpointId))
        {
            await DeliverStorageScanRequestsAsync([endpointId], cancellationToken);
        }
    }

    /// <summary>
    /// Delivers pending storage scan requests to live agents; one message answers every pending request of an endpoint. Tier
    /// enforcement, layer 3: never to an endpoint whose stored tier is agent-only (the request then expires). The agent itself
    /// scans only on a managed configuration and ignores requests within 15 minutes of its previous scan.
    /// </summary>
    internal async Task DeliverStorageScanRequestsAsync(Guid[] endpointIds, CancellationToken cancellationToken)
    {
        var live = endpointIds.Where(id => _sessions.TryGetValue(id, out var s) && !s.IsClosing && s.Tier == EndpointTier.Managed).ToArray();
        if (live.Length == 0)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var requests = await _store.ReadDeliverableStorageScanRequestsAsync(live, now, cancellationToken);
        foreach (var endpointId in requests.Select(r => r.EndpointId).Distinct())
        {
            if (!_sessions.TryGetValue(endpointId, out var session) || session.IsClosing || session.Tier != EndpointTier.Managed)
            {
                continue;
            }

            if (await _store.MarkStorageScanRequestsDeliveredAsync(endpointId, now, cancellationToken) is not { } requestId)
            {
                continue;
            }

            var message = new ServerMessage { StorageScanRequest = new Protocol.Agent.V1.StorageScanRequest { RequestId = requestId.ToString("D") } };
            if (session.Send(message))
            {
                _logger.LogInformation("Endpoint {EndpointId}: asked the agent to scan its storage (request {RequestId})", endpointId, requestId);
            }
            else
            {
                // A scheduled scan still runs; the technician can ask again.
                _logger.LogWarning("Endpoint {EndpointId}: storage scan request {RequestId} could not be sent", endpointId, requestId);
            }
        }
    }
}
