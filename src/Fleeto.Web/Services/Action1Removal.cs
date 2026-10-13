using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Web.Services;

/// <summary>
/// The removal of an endpoint's Action1 agent when the endpoint leaves patch management (0.6.0): deleted from Fleeto, or
/// switched to agent-only. Web cannot reach Action1, and a deleted endpoint takes its own requests with it, so the removal
/// is an <see cref="IntegrationOperation"/> of kind <see cref="IntegrationOperationKind.RemoveEndpoint"/> that outlives the
/// endpoint; the workers remove the endpoint from its organization, which makes Action1 uninstall the agent.
/// </summary>
internal static class Action1Removal
{
    /// <summary>
    /// The operation that removes the endpoint from Action1, or null when there is nothing Action1 can remove: no Action1
    /// integration, or neither Action1 nor the Fleeto agent knows an Action1 agent on it. The organization is the one Action1
    /// reports the endpoint in, or else the organization of the endpoint's client. Not added to the context.
    /// </summary>
    public static async Task<IntegrationOperation?> OperationAsync(FleetoDbContext db, Endpoint endpoint, DateTime now,
        CancellationToken cancellationToken)
    {
        var integrationId = await db.Integrations.AsNoTracking()
            .Where(i => i.Type == IntegrationType.Action1)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (integrationId is null)
        {
            return null;
        }

        var state = await db.EndpointPatchStates.AsNoTracking()
            .Where(s => s.EndpointId == endpoint.Id)
            .Select(s => new { s.ExternalTenantId, s.ExternalEndpointId })
            .SingleOrDefaultAsync(cancellationToken);
        var tenantId = state?.ExternalTenantId ?? string.Empty;
        var externalId = state?.ExternalEndpointId ?? string.Empty;
        if (tenantId.Length == 0 || externalId.Length == 0)
        {
            externalId = await db.InventorySnapshots.AsNoTracking()
                .Where(i => i.EndpointId == endpoint.Id)
                .Select(i => i.Action1AgentId)
                .SingleOrDefaultAsync(cancellationToken) ?? string.Empty;
            tenantId = await db.IntegrationMappings.AsNoTracking()
                .Where(m => m.IntegrationId == integrationId && m.ClientId == endpoint.ClientId)
                .Select(m => m.ExternalTenantId)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        }

        if (tenantId.Length == 0 || externalId.Length == 0)
        {
            return null;
        }

        var operation = IntegrationFollow.Operation(integrationId.Value, IntegrationOperationKind.RemoveEndpoint, null, tenantId, string.Empty,
            endpoint.Hostname, now);
        operation.ExternalEndpointId = externalId;
        return operation;
    }

    /// <summary>Adds the operation and its audit entry to the context; the caller saves.</summary>
    public static void Add(FleetoDbContext db, Caller caller, Endpoint endpoint, IntegrationOperation operation, string reason, DateTime now)
    {
        db.IntegrationOperations.Add(operation);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchAgentRemovalRequested, "Endpoint", endpoint.Id.ToString(),
            endpoint.ClientId, new { endpoint.Hostname, Through = "Action1", Reason = reason }), now));
    }
}
