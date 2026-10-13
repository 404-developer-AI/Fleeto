using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Protocol;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Web.Services;

/// <summary>
/// "Also uninstall the Fleeto agent" when an endpoint or a client is deleted (0.6.0). Written in the transaction that deletes the
/// endpoint, before its certificates go with it: an <see cref="AgentUninstall"/> with the fingerprints of every certificate of its
/// agent and watchdog that could still connect, and the request for fleeto-signer to sign the uninstall command. The gateway then
/// delivers the command when the agent or the watchdog connects again, within <see cref="AgentUninstallRules.Lifetime"/>.
/// </summary>
internal static class AgentUninstallRequests
{
    /// <summary>Adds the request for each endpoint that has a certificate to recognise; returns how many. The caller saves.</summary>
    public static async Task<int> AddAsync(FleetoDbContext db, Caller caller, IReadOnlyCollection<Endpoint> endpoints, DateTime now,
        CancellationToken cancellationToken)
    {
        if (endpoints.Count == 0)
        {
            return 0;
        }

        var ids = endpoints.Select(e => e.Id).ToList();
        // Also a certificate that expired within the recovery grace period: such an agent still connects, to recover.
        var since = now - ProtocolLimits.RecoveryGrace;
        var fingerprints = (await db.AgentCertificates.AsNoTracking()
                .Where(c => ids.Contains(c.EndpointId) && c.RevokedAt == null && c.ExpiresAt > since)
                .Select(c => new { c.EndpointId, c.Fingerprint })
                .ToListAsync(cancellationToken))
            .GroupBy(c => c.EndpointId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Fingerprint).Distinct().ToList());
        var clientIds = endpoints.Select(e => e.ClientId).Distinct().ToList();
        var codes = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);

        var added = 0;
        foreach (var endpoint in endpoints)
        {
            if (!fingerprints.TryGetValue(endpoint.Id, out var list) || list.Count == 0)
            {
                continue;
            }

            var uninstall = new AgentUninstall
            {
                Id = Guid.NewGuid(),
                ClientId = endpoint.ClientId,
                ClientCode = codes.GetValueOrDefault(endpoint.ClientId) ?? string.Empty,
                EndpointId = endpoint.Id,
                Hostname = Cut(endpoint.Hostname, 255),
                Fingerprints = list,
                RequestedByUserId = caller.UserId,
                RequestedByName = Cut(caller.Name, 200),
                RequestedAt = now,
                ExpiresAt = now + AgentUninstallRules.Lifetime
            };
            db.AgentUninstalls.Add(uninstall);
            // No client on the request: the client may be deleted in the same transaction, and the row outlives it.
            db.SigningRequests.Add(new SigningRequest
            {
                Id = Guid.NewGuid(),
                Kind = SigningRequestKind.AgentUninstall,
                SubjectId = uninstall.Id,
                Payload = AgentUninstallRules.Binding(endpoint.Id, caller.UserId),
                RequestedBy = "web:" + (caller.IpAddress ?? "unknown"),
                CreatedAt = now
            });
            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AgentUninstallRequested, "Endpoint", endpoint.Id.ToString(),
                endpoint.ClientId, new { endpoint.Hostname, uninstall.ExpiresAt, Certificates = list.Count }), now));
            added++;
        }

        return added;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
