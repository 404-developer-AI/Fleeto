using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>A deleted endpoint whose agent Fleeto waits for, or that removed itself recently (0.6.0).</summary>
/// <param name="Problem">Why it cannot be delivered: the signer refused it. Null while all is well.</param>
public sealed record AgentUninstallView(Guid Id, string ClientCode, string Hostname, string RequestedByName, DateTime RequestedAt,
    DateTime ExpiresAt, bool Signed, string? Problem, DateTime? AcceptedAt, AgentComponent? AcceptedBy)
{
    public bool IsWaiting => AcceptedAt is null && Problem is null;

    /// <summary>When the workers remove the row: at its expiry, or a week after the agent removed itself if that is sooner.</summary>
    public DateTime ListedUntil => AcceptedAt is { } accepted && accepted + AgentUninstallRules.KeptAfterAccepted < ExpiresAt
        ? accepted + AgentUninstallRules.KeptAfterAccepted
        : ExpiresAt;
}

/// <summary>
/// The list "Agents waiting to be uninstalled" in Settings (0.6.0): deleted endpoints whose agent and watchdog remove themselves the
/// next time they connect, and those that did in the last days. Admins only, instance-wide like the audit log, because the rows
/// outlive their clients. Stopping to wait deletes the row: the gateway then refuses that agent like any unknown one.
/// </summary>
public sealed class AgentUninstallService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;

    public AgentUninstallService(IFleetoDbContextFactory dbFactory, INotificationBus bus, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _time = time;
    }

    public async Task<IReadOnlyList<AgentUninstallView>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow().UtcDateTime;
        return await db.AgentUninstalls.AsNoTracking()
            .Where(u => u.ExpiresAt > now)
            .OrderBy(u => u.AcceptedAt != null).ThenByDescending(u => u.RequestedAt)
            .Take(500)
            .Select(u => new AgentUninstallView(u.Id, u.ClientCode, u.Hostname, u.RequestedByName, u.RequestedAt, u.ExpiresAt, u.SignedAt != null,
                u.RefusalReason, u.AcceptedAt, u.AcceptedBy))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Stops waiting for a deleted endpoint: its agent stays where it is and is refused when it connects.</summary>
    public async Task<ServiceResult> DismissAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        var uninstall = await db.AgentUninstalls.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (uninstall is null)
        {
            return ServiceResult.NotFound("uninstall");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.AgentUninstalls.Remove(uninstall);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AgentUninstallDismissed, "Endpoint", uninstall.EndpointId.ToString(),
            uninstall.ClientId, new { uninstall.Hostname, uninstall.ClientCode, Accepted = uninstall.AcceptedAt is not null }), now));
        await db.SaveChangesAsync(cancellationToken);
        // The gateway reloads the endpoints it waits for on this channel.
        await _bus.PublishAsync(NotificationChannels.Revocations, uninstall.EndpointId.ToString(), cancellationToken);
        return ServiceResult.Ok();
    }
}
