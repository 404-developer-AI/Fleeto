using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>
/// What clients and sites tell Action1 while the integration follows them (0.6.0). Web cannot reach Action1: it records
/// what the product has to do in the same transaction as the change in Fleeto and wakes the workers, which make the
/// calls. Renames need no record: the workers compare names with what they last sent.
/// </summary>
internal static class IntegrationFollow
{
    /// <summary>The id of the Action1 integration when it follows clients and sites, else null.</summary>
    public static Task<Guid?> ActiveAsync(FleetoDbContext db, CancellationToken cancellationToken) =>
        db.Integrations.AsNoTracking()
            .Where(i => i.Type == IntegrationType.Action1 && i.FollowClients)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public static IntegrationOperation Operation(Guid integrationId, IntegrationOperationKind kind, Guid? clientId, string tenantId,
        string groupId, string name, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        IntegrationId = integrationId,
        Kind = kind,
        TargetClientId = clientId,
        ExternalTenantId = tenantId,
        ExternalGroupId = groupId,
        Name = name.Length <= 200 ? name : name[..200],
        NextAttemptAt = now,
        CreatedAt = now
    };

    /// <summary>
    /// Wakes the workers. The change is committed already; a lost notification only delays the work until the workers'
    /// next pass, so a failure here is logged and never reported to the user.
    /// </summary>
    public static async Task NotifyAsync(INotificationBus bus, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await bus.PublishAsync(NotificationChannels.Integrations, IntegrationType.Action1.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not wake the workers to follow a client or site change in Action1");
        }
    }
}
