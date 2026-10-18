using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Stores what the patch management product reports about one endpoint (0.4.0 step 2), shared by the sync of every
/// organization (<see cref="PatchSyncService"/>) and the sync of one endpoint a technician asks for (0.6.0,
/// <see cref="PatchRequestService"/>), so both store, alert and forget the same way. The caller saves.
/// </summary>
internal static class PatchStateWriter
{
    /// <summary>After this long without contact, the product's patch state is too old to mean anything.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    public const string GoneReason = "The endpoint is no longer in patch management.";

    /// <summary>Copies what the product reported onto the state of the endpoint, creating the state when it has none.</summary>
    public static EndpointPatchState Store(FleetoDbContext db, EndpointPatchState? state, Endpoint endpoint, Action1Endpoint item, string tenantId,
        DateTime now)
    {
        if (state is null)
        {
            state = new EndpointPatchState { EndpointId = endpoint.Id, ClientId = endpoint.ClientId };
            db.EndpointPatchStates.Add(state);
        }

        state.ExternalEndpointId = item.Id;
        state.ExternalTenantId = tenantId;
        state.Coverage = item.Coverage;
        state.MissingCritical = item.MissingCritical;
        state.MissingOther = item.MissingOther;
        state.RebootRequired = item.RebootRequired;
        state.ProductLastSeenAt = item.LastSeenAt;
        state.ProductAgentVersion = Cut(item.AgentVersion, 50);
        state.UpdatedAt = now;
        return state;
    }

    /// <summary>
    /// Replaces the missing updates of the endpoint with what the product lists now. Returns them, or null when they could
    /// not be read, in which case the stored ones stay.
    /// </summary>
    public static async Task<IReadOnlyList<Action1MissingUpdate>?> ReadDetailAsync(FleetoDbContext db, Action1Client client, string tenantId,
        Endpoint endpoint, EndpointPatchState state, DateTime now, ILogger logger, CancellationToken cancellationToken)
    {
        var updates = await client.ListMissingUpdatesAsync(tenantId, state.ExternalEndpointId, cancellationToken);
        if (!updates.Ok)
        {
            logger.LogInformation("The missing updates of endpoint {EndpointId} could not be read: {Message}", endpoint.Id, updates.Message);
            return null;
        }

        await ClearDetailAsync(db, endpoint.Id, cancellationToken);
        foreach (var update in updates.Value ?? [])
        {
            db.EndpointMissingUpdates.Add(new EndpointMissingUpdate
            {
                Id = Guid.NewGuid(),
                EndpointId = endpoint.Id,
                ClientId = endpoint.ClientId,
                ExternalUpdateId = Cut(update.Id, 200),
                Name = Cut(update.Name, 300),
                Vendor = Cut(update.Vendor, 200),
                Version = Cut(update.Version, 100),
                KbNumber = Cut(update.KbNumber, 20),
                Severity = update.Severity,
                InstalledVersion = Cut(update.InstalledVersion, 100),
                ReleaseDate = update.ReleaseDate,
                UpdateType = Cut(update.UpdateType, 50),
                ApprovalStatus = Cut(update.ApprovalStatus, 20),
                Cves = [.. update.Cves.Take(200)],
                UpdatedAt = now
            });
        }

        return updates.Value ?? [];
    }

    public static async Task ClearDetailAsync(FleetoDbContext db, Guid endpointId, CancellationToken cancellationToken) =>
        db.EndpointMissingUpdates.RemoveRange(await db.EndpointMissingUpdates.Where(u => u.EndpointId == endpointId).ToListAsync(cancellationToken));

    /// <summary>
    /// Opens or resolves the alert about the patch state itself: the product does not patch this endpoint, or has not
    /// seen it for too long. Missing updates are not an alert; they are the state the UI shows.
    /// </summary>
    public static async Task AlertAsync(FleetoDbContext db, Endpoint endpoint, EndpointPatchState state, bool inMaintenance,
        List<AlertTransition> transitions, DateTime now, CancellationToken cancellationToken)
    {
        var problem = state.Coverage == PatchCoverage.Inactive
            ? $"{endpoint.Hostname} is not patched by Action1 any more. Check the Action1 subscription: endpoints above the licensed number become inactive."
            : state.ProductLastSeenAt is { } seen && now - seen > StaleAfter
                ? $"Action1 has not seen {endpoint.Hostname} since {seen:yyyy-MM-dd}. Its patch state is too old to rely on; check the Action1 agent on the endpoint."
                : null;

        var open = await db.Alerts.FirstOrDefaultAsync(
            a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState && a.State != AlertState.Resolved, cancellationToken);

        if (problem is null)
        {
            if (open is not null)
            {
                Resolve(open, "Action1 patches this endpoint again.", transitions, now);
            }

            return;
        }

        if (open is not null || inMaintenance)
        {
            // An endpoint in maintenance opens no alerts (CLAUDE.md, Maintenance mode); the state is stored either way.
            return;
        }

        var target = state.Coverage == PatchCoverage.Inactive ? "coverage" : "stale";
        if (await db.Alerts.Where(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState && a.Target == target)
                .AnyAsync(AlertHoldRules.HoldsSource(now), cancellationToken))
        {
            // A hold on an alert of this problem is maintenance for it (AlertHoldRules).
            return;
        }

        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            Kind = AlertKind.PatchState,
            Target = target,
            Severity = AlertSeverity.Warning,
            State = AlertState.Open,
            Title = Cut(problem, 500),
            Detail = Cut($"Missing updates: {state.MissingCritical} critical, {state.MissingOther} other.", 2000),
            OpenedAt = now,
            UpdatedAt = now
        };
        db.Alerts.Add(alert);
        transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Opened));
    }

    /// <summary>
    /// An endpoint the product no longer reports loses its state and its missing updates, and its patch state alert
    /// resolves: saying nothing is better than showing a state that was true a week ago.
    /// </summary>
    public static async Task ForgetAsync(FleetoDbContext db, EndpointPatchState gone, List<AlertTransition> transitions, DateTime now, string reason,
        CancellationToken cancellationToken)
    {
        db.EndpointPatchStates.Remove(gone);
        await ClearDetailAsync(db, gone.EndpointId, cancellationToken);
        var open = await db.Alerts.FirstOrDefaultAsync(
            a => a.EndpointId == gone.EndpointId && a.Kind == AlertKind.PatchState && a.State != AlertState.Resolved, cancellationToken);
        if (open is not null)
        {
            Resolve(open, reason, transitions, now);
        }
    }

    /// <summary>The endpoints of the client that are in maintenance right now, by any of the four ways maintenance applies.</summary>
    public static async Task<HashSet<Guid>> InMaintenanceAsync(FleetoDbContext db, Guid clientId, DateTime now,
        CancellationToken cancellationToken) =>
        [.. await db.Endpoints
            .Where(e => e.ClientId == clientId)
            .Where(MaintenanceRules.EndpointInMaintenance(now, db.MaintenanceWindowOccurrences, EffectivePolicies.Query(db)))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken)];

    private static void Resolve(Alert alert, string reason, List<AlertTransition> transitions, DateTime now)
    {
        alert.State = AlertState.Resolved;
        alert.ResolvedAt = now;
        alert.ResolvedReason = reason;
        alert.UpdatedAt = now;
        transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Resolved));
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
