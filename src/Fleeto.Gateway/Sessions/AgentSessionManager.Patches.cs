using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Gateway.Data;
using Fleeto.Protocol.Agent.V1;
using Npgsql;

namespace Fleeto.Gateway.Sessions;

/// <summary>Patch scans (0.9.0): scan reports from the agent, scan requests to it.</summary>
public sealed partial class AgentSessionManager
{
    /// <summary>
    /// Stores a patch scan and acknowledges it only after the commit, so the agent keeps it on disk until then. A report of an
    /// agent-only endpoint, one of a tool Fleeto does not know, a duplicate and one over the daily limit are acknowledged without
    /// storing, so the agent drops them.
    /// </summary>
    private async Task SavePatchScanAsync(AgentSession session, PatchScanReport report, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(report.ScanId, out var scanId) || scanId == Guid.Empty)
        {
            session.Close(DisconnectCode.ProtocolError, "A patch scan report needs a scan id.");
            return;
        }

        var ack = new ServerMessage { PatchScanAck = new PatchScanAck { ScanId = report.ScanId } };
        if (session.Tier != EndpointTier.Managed)
        {
            // Tier enforcement, layer 3: patch management is for managed endpoints only.
            _logger.LogWarning("Endpoint {EndpointId} is agent-only but sent a patch scan; it is acknowledged and not stored", session.EndpointId);
            session.Send(ack);
            return;
        }

        if (!PatchScanRules.Tools.Contains(report.Engine))
        {
            _logger.LogWarning("Endpoint {EndpointId} sent a patch scan of an unknown tool; it is acknowledged and not stored", session.EndpointId);
            session.Send(ack);
            return;
        }

        PatchScanOutcome outcome;
        try
        {
            outcome = await _store.SavePatchScanAsync(session.EndpointId, session.ClientId, scanId, report, _time.GetUtcNow().UtcDateTime,
                cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // No ack: the agent keeps the report on disk and sends it again.
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not store a patch scan; it is not acknowledged", session.EndpointId);
            return;
        }

        session.Send(ack);
        switch (outcome)
        {
            case PatchScanOutcome.Stored:
                await PublishAsync(NotificationChannels.PatchScans, session.EndpointId);
                break;
            case PatchScanOutcome.OverLimit:
                _logger.LogWarning("Endpoint {EndpointId} sent more than {Limit} patch scans in 24 hours; the scan is acknowledged and not stored",
                    session.EndpointId, PatchScanRules.MaxScansPerEndpointPerDay);
                break;
        }
    }

    private async Task OnPatchScanRequestAsync(string payload, CancellationToken cancellationToken)
    {
        if (payload == NotificationBusEvents.Resync)
        {
            await DeliverPatchScanRequestsAsync(_sessions.Keys.ToArray(), cancellationToken);
        }
        else if (Guid.TryParse(payload, out var requestId) &&
                 await _store.ReadPatchScanRequestEndpointAsync(requestId, cancellationToken) is { } endpointId &&
                 _sessions.ContainsKey(endpointId))
        {
            await DeliverPatchScanRequestsAsync([endpointId], cancellationToken);
        }
    }

    /// <summary>
    /// Delivers pending patch scan requests to live agents; one message answers every pending request of an endpoint. Tier
    /// enforcement, layer 3: never to an endpoint whose stored tier is agent-only (the request then expires). The agent itself scans
    /// only on a managed configuration with a patch scan interval and ignores requests within 15 minutes of its previous scan.
    /// </summary>
    internal async Task DeliverPatchScanRequestsAsync(Guid[] endpointIds, CancellationToken cancellationToken)
    {
        var live = endpointIds.Where(id => _sessions.TryGetValue(id, out var s) && !s.IsClosing && s.Tier == EndpointTier.Managed).ToArray();
        if (live.Length == 0)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var requests = await _store.ReadDeliverablePatchScanRequestsAsync(live, now, cancellationToken);
        foreach (var endpointId in requests.Select(r => r.EndpointId).Distinct())
        {
            if (!_sessions.TryGetValue(endpointId, out var session) || session.IsClosing || session.Tier != EndpointTier.Managed)
            {
                continue;
            }

            if (await _store.MarkPatchScanRequestsDeliveredAsync(endpointId, now, cancellationToken) is not { } requestId)
            {
                continue;
            }

            var message = new ServerMessage { PatchScanRequest = new Protocol.Agent.V1.PatchScanRequest { RequestId = requestId.ToString("D") } };
            if (session.Send(message))
            {
                _logger.LogInformation("Endpoint {EndpointId}: asked the agent to scan for missing updates (request {RequestId})", endpointId, requestId);
            }
            else
            {
                // A scheduled scan still runs; the technician can ask again.
                _logger.LogWarning("Endpoint {EndpointId}: patch scan request {RequestId} could not be sent", endpointId, requestId);
            }
        }
    }
}
