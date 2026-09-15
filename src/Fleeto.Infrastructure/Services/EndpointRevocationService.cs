using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>
/// Revokes agent certificates and deletes endpoints. Both publish on <see cref="NotificationChannels.Revocations"/>
/// so the gateway drops a live connection at once; the gateway also reloads its allow list every minute.
/// </summary>
public sealed class EndpointRevocationService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;

    public EndpointRevocationService(IFleetoDbContextFactory dbFactory, INotificationBus bus, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _time = time;
    }

    /// <summary>Revokes every certificate of the endpoint. The agent must enroll again with a new token.</summary>
    public async Task<bool> RevokeAsync(Guid endpointId, string reason, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.Create(actor.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return false;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var certificates = await db.AgentCertificates
            .Where(c => c.EndpointId == endpointId && c.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var certificate in certificates)
        {
            certificate.RevokedAt = now;
            certificate.RevokedByUserId = actor.UserId;
            certificate.RevokedReason = reason.Length > 500 ? reason[..500] : reason;
        }

        db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.CertificateRevoked, "Endpoint", endpointId.ToString(),
            endpoint.ClientId, AuditActorType.User, actor.UserId.ToString(), actor.Name,
            new { endpoint.Hostname, Reason = reason, Certificates = certificates.Count }, actor.IpAddress), now));

        await db.SaveChangesAsync(cancellationToken);
        await _bus.PublishAsync(NotificationChannels.Revocations, endpointId.ToString(), cancellationToken);
        return true;
    }

    /// <summary>
    /// Deletes the endpoint and, through cascading foreign keys, everything that belongs to it, including its
    /// certificates (so the allow list no longer contains them). Check results are purged by the workers.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid endpointId, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.Create(actor.Scope);
        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return false;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.Endpoints.Remove(endpoint);
        db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.EndpointDeleted, "Endpoint", endpointId.ToString(),
            endpoint.ClientId, AuditActorType.User, actor.UserId.ToString(), actor.Name,
            new { endpoint.Hostname, endpoint.SiteId }, actor.IpAddress), now));

        await db.SaveChangesAsync(cancellationToken);
        await _bus.PublishAsync(NotificationChannels.Revocations, endpointId.ToString(), cancellationToken);
        await _bus.PublishAsync(NotificationChannels.EndpointStatus, endpointId.ToString(), cancellationToken);
        return true;
    }
}
