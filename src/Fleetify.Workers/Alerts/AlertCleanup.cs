using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleetify.Workers.Alerts;

/// <summary>Set-based alert resolutions shared by the check and endpoint health services.</summary>
public static class AlertCleanup
{
    private const string ResolveNotManagedSql = """
        UPDATE "Alerts" a
        SET "State" = 'Resolved', "ResolvedAt" = @now, "UpdatedAt" = @now, "ResolvedReason" = @reason
        FROM "Endpoints" e
        WHERE e."Id" = a."EndpointId" AND a."Kind" = @kind AND a."State" <> 'Resolved'
          AND (@allEndpoints OR e."Tier" <> 'Managed')
        RETURNING a."Id" AS "Value"
        """;

    /// <summary>
    /// Resolves unresolved alerts of <paramref name="kind"/> on endpoints that are no longer managed: agent-only endpoints,
    /// or every endpoint when the license no longer allows managed behaviour. Runs in the caller's transaction.
    /// </summary>
    public static Task<List<Guid>> ResolveNotManagedAsync(FleetifyDbContext db, AlertKind kind, LicenseStatus license, DateTime now,
        CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<Guid>(ResolveNotManagedSql,
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("reason", Checks.CheckEvaluationService.ResolvedReasonNotManaged),
                new NpgsqlParameter("kind", kind.ToString()),
                new NpgsqlParameter("allEndpoints", !license.AllowsManaged))
            .ToListAsync(cancellationToken);

    /// <summary>Publishes changed alert ids after commit.</summary>
    public static async Task PublishAsync(INotificationBus bus, IEnumerable<Guid> alertIds, CancellationToken cancellationToken)
    {
        foreach (var id in alertIds.Distinct())
        {
            await bus.PublishAsync(NotificationChannels.Alerts, id.ToString(), cancellationToken);
        }
    }
}
