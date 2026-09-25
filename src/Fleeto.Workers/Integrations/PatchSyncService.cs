using Fleeto.Infrastructure.Services;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Common;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Reads the patch state of every managed endpoint from the patch management product (0.4.0 step 2).
///
/// One pass per organization every <see cref="SyncInterval"/>: the counts of missing updates come from one listing, the
/// detail only for endpoints that miss something, because Action1 counts every call against one budget for the whole
/// enterprise. Endpoints are matched on the id the Fleeto agent reads from the Action1 agent on the endpoint itself, and
/// only within the client the organization is mapped to, so patch state can never appear under another client.
/// <para>
/// What Fleeto stores is the current state, not a history: the product keeps that. An endpoint the product no longer
/// knows loses its state rather than keeping a stale one, and an endpoint the product does not patch (over its quota, or
/// not seen for too long) opens an alert instead of looking compliant.
/// </para>
/// </summary>
public sealed class PatchSyncService : WorkerLoop
{
    /// <summary>How often the patch state of every organization is read.</summary>
    public static readonly TimeSpan SyncInterval = TimeSpan.FromHours(4);

    /// <summary>After this long without contact, the product's patch state is too old to mean anything.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// Endpoints whose missing updates are read in detail in one pass. The counts of every endpoint are always current;
    /// detail beyond this many endpoints waits for the next pass, so one large client cannot spend the whole budget.
    /// </summary>
    public const int MaxDetailPerPass = 200;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly Action1ClientFactory _clients;
    private readonly AlertNotificationService _notifier;
    private readonly LicenseService _licenses;
    private readonly CircuitBreaker _breaker;

    public PatchSyncService(IFleetoDbContextFactory dbFactory, INotificationBus bus, Action1ClientFactory clients,
        AlertNotificationService notifier, LicenseService licenses, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<PatchSyncService> logger)
        : base("patch-sync", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _clients = clients;
        _notifier = notifier;
        _licenses = licenses;
        _breaker = new CircuitBreaker(failureThreshold: 3, pause: TimeSpan.FromMinutes(15), time);
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);

    protected override TimeSpan MaxRunDuration => TimeSpan.FromMinutes(30);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await SyncAsync(force: false, cancellationToken);
        return false;
    }

    /// <summary>
    /// Reads the patch state of every mapped organization whose turn it is. <paramref name="force"/> ignores both the
    /// interval and the circuit breaker, which is what a test in Settings does.
    /// </summary>
    public async Task SyncAsync(bool force, CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var integration = await db.Integrations.Include(i => i.Mappings)
            .SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        if (integration is null || !integration.Enabled || integration.Mappings.Count == 0)
        {
            return;
        }

        var now = Time.GetUtcNow().UtcDateTime;

        // Patching is a managed feature: an endpoint that is agent-only, and every endpoint once the license has run out
        // of its grace period, keeps no patch alerts (CLAUDE.md, Licensing).
        var license = await _licenses.GetStatusAsync(cancellationToken);
        var released = await AlertCleanup.ResolveNotManagedAsync(db, AlertKind.PatchState, license, now, cancellationToken);
        if (released.Count > 0)
        {
            await _notifier.AddNotificationsAsync(db, [.. released.Select(id => new AlertTransition(id, NotificationEvent.Resolved))],
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await AlertCleanup.PublishAsync(_bus, released, cancellationToken);
        }

        if (!license.AllowsManaged)
        {
            // Nothing is read while the instance has no managed tier: the state would only say what nobody may act on.
            return;
        }

        if (!force)
        {
            if (_breaker.IsOpen)
            {
                return;
            }

            if (integration.PatchSyncedAt is { } last && now - last < SyncInterval)
            {
                return;
            }
        }

        using var client = _clients.TryCreate(integration);
        if (client is null)
        {
            return;
        }

        var transitions = new List<AlertTransition>();
        var detailBudget = MaxDetailPerPass;
        var failed = false;
        var reportedByTenant = new Dictionary<string, IReadOnlyList<Action1Endpoint>>(StringComparer.Ordinal);

        foreach (var mapping in integration.Mappings.ToList())
        {
            var result = await client.ListEndpointsAsync(mapping.ExternalTenantId, cancellationToken);
            if (!result.Ok)
            {
                failed = true;
                Logger.LogWarning("Reading the endpoints of organization {Tenant} failed: {Message}", mapping.ExternalTenantId, result.Message);
                integration.Status = IntegrationStatus.Failing;
                integration.StatusMessage = result.Message.Length <= 1000 ? result.Message : result.Message[..1000];
                continue;
            }

            reportedByTenant[mapping.ExternalTenantId] = result.Value ?? [];
            detailBudget = await SyncClientAsync(db, client, mapping, result.Value ?? [], detailBudget, transitions, now, cancellationToken);
        }

        if (integration.FollowClients)
        {
            await QueueMovesAsync(db, integration, reportedByTenant, now, cancellationToken);
        }

        integration.PatchSyncedAt = now;
        if (!failed)
        {
            _breaker.RecordSuccess();
            integration.Status = IntegrationStatus.Ok;
            integration.StatusMessage = null;
            integration.LastSuccessAt = now;
        }
        else if (_breaker.RecordFailure())
        {
            Logger.LogWarning("Patch sync keeps failing; pausing until {OpenUntil}", _breaker.OpenUntil);
        }

        integration.LastAttemptAt = now;
        await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AlertCleanup.PublishAsync(_bus, transitions.Select(t => t.AlertId), cancellationToken);
        await _bus.PublishAsync(NotificationChannels.Integrations, IntegrationType.Action1.ToString(), cancellationToken);
    }

    /// <summary>
    /// Finds endpoints Action1 keeps in the organization of another client than the one they belong to in Fleeto (0.6.0),
    /// while Action1 follows the clients: an endpoint enrolled again under another client keeps its Action1 agent, and so
    /// its old organization. The Fleeto endpoint that reported the Action1 id most recently decides where it belongs, so an
    /// old record left behind under the previous client never pulls it back. Managed endpoints only, and only into an
    /// organization that is mapped; the move itself is an <see cref="IntegrationOperation"/> for the workers that follow
    /// the clients.
    /// </summary>
    private static async Task QueueMovesAsync(FleetoDbContext db, Integration integration,
        IReadOnlyDictionary<string, IReadOnlyList<Action1Endpoint>> reportedByTenant, DateTime now, CancellationToken cancellationToken)
    {
        var reported = reportedByTenant
            .SelectMany(t => t.Value.Select(e => (Tenant: t.Key, Id: e.Id, Key: e.Id.ToLowerInvariant())))
            .ToList();
        if (reported.Count == 0)
        {
            return;
        }

        var keys = reported.Select(r => r.Key).Distinct().ToList();
        var reporters = await (from i in db.InventorySnapshots
                               join e in db.Endpoints on i.EndpointId equals e.Id
                               where keys.Contains(i.Action1AgentId.ToLower())
                               select new { Key = i.Action1AgentId.ToLower(), e.ClientId, e.Hostname, e.Tier, i.ReceivedAt })
            .ToListAsync(cancellationToken);
        var latest = reporters.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ReceivedAt).First());
        var tenantOfClient = integration.Mappings.ToDictionary(m => m.ClientId, m => m.ExternalTenantId);
        var waiting = (await db.IntegrationOperations.AsNoTracking()
                .Where(o => o.IntegrationId == integration.Id && o.Kind == IntegrationOperationKind.MoveEndpoint)
                .Select(o => o.ExternalEndpointId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in reported)
        {
            if (!latest.TryGetValue(item.Key, out var owner) || owner.Tier != EndpointTier.Managed ||
                !tenantOfClient.TryGetValue(owner.ClientId, out var target) || target == item.Tenant || !waiting.Add(item.Id))
            {
                continue;
            }

            db.IntegrationOperations.Add(new IntegrationOperation
            {
                Id = Guid.NewGuid(),
                IntegrationId = integration.Id,
                Kind = IntegrationOperationKind.MoveEndpoint,
                TargetClientId = owner.ClientId,
                ExternalTenantId = item.Tenant,
                ExternalEndpointId = item.Id,
                Name = Cut(owner.Hostname, 200),
                NextAttemptAt = now,
                CreatedAt = now
            });
        }
    }

    /// <summary>One organization: match, store, alert. Returns what is left of the detail budget.</summary>
    private async Task<int> SyncClientAsync(FleetoDbContext db, Action1Client client, IntegrationMapping mapping,
        IReadOnlyList<Action1Endpoint> reported, int detailBudget, List<AlertTransition> transitions, DateTime now,
        CancellationToken cancellationToken)
    {
        // Only managed endpoints of this client, and only those whose agent reported an Action1 agent: an agent-only
        // endpoint gets nothing from patch management (CLAUDE.md, Licensing).
        var candidates = await db.Endpoints
            .Where(e => e.ClientId == mapping.ClientId && e.Tier == EndpointTier.Managed)
            .Join(db.InventorySnapshots.Where(i => i.Action1AgentId != string.Empty),
                e => e.Id, i => i.EndpointId, (e, i) => new { Endpoint = e, i.Action1AgentId })
            .ToListAsync(cancellationToken);
        var byExternalId = candidates.ToDictionary(c => c.Action1AgentId, c => c.Endpoint, StringComparer.OrdinalIgnoreCase);

        var states = await db.EndpointPatchStates.Where(p => p.ClientId == mapping.ClientId).ToListAsync(cancellationToken);
        var statesByEndpoint = states.ToDictionary(p => p.EndpointId);
        var seen = new HashSet<Guid>();
        var inMaintenance = await InMaintenanceAsync(db, mapping.ClientId, now, cancellationToken);

        foreach (var item in reported)
        {
            if (!byExternalId.TryGetValue(item.Id, out var endpoint))
            {
                // An endpoint of the organization that Fleeto does not manage, or one whose agent has not reported its
                // Action1 id yet. Nothing to store: patch state exists only for endpoints Fleeto knows.
                continue;
            }

            seen.Add(endpoint.Id);
            if (!statesByEndpoint.TryGetValue(endpoint.Id, out var state))
            {
                state = new EndpointPatchState { EndpointId = endpoint.Id, ClientId = endpoint.ClientId };
                db.EndpointPatchStates.Add(state);
                statesByEndpoint[endpoint.Id] = state;
            }

            var wasCompliant = state.IsCompliant;
            state.ExternalEndpointId = item.Id;
            state.ExternalTenantId = mapping.ExternalTenantId;
            state.Coverage = item.Coverage;
            state.MissingCritical = item.MissingCritical;
            state.MissingOther = item.MissingOther;
            state.RebootRequired = item.RebootRequired;
            state.ProductLastSeenAt = item.LastSeenAt;
            state.ProductAgentVersion = item.AgentVersion.Length <= 50 ? item.AgentVersion : item.AgentVersion[..50];
            state.UpdatedAt = now;

            if (!state.IsCompliant && detailBudget > 0 && (wasCompliant || state.MissingCritical > 0 || detailBudget > MaxDetailPerPass / 2))
            {
                detailBudget--;
                await ReadDetailAsync(db, client, mapping, endpoint, state, now, cancellationToken);
            }
            else if (state.IsCompliant)
            {
                db.EndpointMissingUpdates.RemoveRange(
                    await db.EndpointMissingUpdates.Where(u => u.EndpointId == endpoint.Id).ToListAsync(cancellationToken));
            }

            await AlertAsync(db, endpoint, state, inMaintenance.Contains(endpoint.Id), transitions, now, cancellationToken);
        }

        // Endpoints the product no longer reports lose their state: saying nothing is better than showing a state that
        // was true a week ago.
        foreach (var gone in states.Where(s => !seen.Contains(s.EndpointId) && s.ExternalTenantId == mapping.ExternalTenantId))
        {
            db.EndpointPatchStates.Remove(gone);
            db.EndpointMissingUpdates.RemoveRange(
                await db.EndpointMissingUpdates.Where(u => u.EndpointId == gone.EndpointId).ToListAsync(cancellationToken));
            await ResolveAsync(db, gone.EndpointId, transitions, now, "The endpoint is no longer in patch management.", cancellationToken);
        }

        return detailBudget;
    }

    private async Task ReadDetailAsync(FleetoDbContext db, Action1Client client, IntegrationMapping mapping, Endpoint endpoint,
        EndpointPatchState state, DateTime now, CancellationToken cancellationToken)
    {
        var updates = await client.ListMissingUpdatesAsync(mapping.ExternalTenantId, state.ExternalEndpointId, cancellationToken);
        if (!updates.Ok)
        {
            Logger.LogInformation("The missing updates of endpoint {EndpointId} could not be read: {Message}", endpoint.Id, updates.Message);
            return;
        }

        db.EndpointMissingUpdates.RemoveRange(
            await db.EndpointMissingUpdates.Where(u => u.EndpointId == endpoint.Id).ToListAsync(cancellationToken));

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
                RebootNeeded = update.RebootNeeded,
                UpdatedAt = now
            });
        }
    }

    /// <summary>
    /// Opens or resolves the alert about the patch state itself: the product does not patch this endpoint, or has not
    /// seen it for too long. Missing updates are not an alert; they are the state the UI shows.
    /// </summary>
    private async Task AlertAsync(FleetoDbContext db, Endpoint endpoint, EndpointPatchState state, bool inMaintenance,
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
                open.State = AlertState.Resolved;
                open.ResolvedAt = now;
                open.ResolvedReason = "Action1 patches this endpoint again.";
                open.UpdatedAt = now;
                transitions.Add(new AlertTransition(open.Id, NotificationEvent.Resolved));
            }

            return;
        }

        if (open is not null || inMaintenance)
        {
            // An endpoint in maintenance opens no alerts (CLAUDE.md, Maintenance mode); the state is stored either way.
            return;
        }

        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            Kind = AlertKind.PatchState,
            Target = state.Coverage == PatchCoverage.Inactive ? "coverage" : "stale",
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

    private async Task ResolveAsync(FleetoDbContext db, Guid endpointId, List<AlertTransition> transitions, DateTime now, string reason,
        CancellationToken cancellationToken)
    {
        var open = await db.Alerts.FirstOrDefaultAsync(
            a => a.EndpointId == endpointId && a.Kind == AlertKind.PatchState && a.State != AlertState.Resolved, cancellationToken);
        if (open is null)
        {
            return;
        }

        open.State = AlertState.Resolved;
        open.ResolvedAt = now;
        open.ResolvedReason = reason;
        open.UpdatedAt = now;
        transitions.Add(new AlertTransition(open.Id, NotificationEvent.Resolved));
    }

    /// <summary>The endpoints of the client that are in maintenance right now, by any of the four ways maintenance applies.</summary>
    private static async Task<HashSet<Guid>> InMaintenanceAsync(FleetoDbContext db, Guid clientId, DateTime now,
        CancellationToken cancellationToken) =>
        [.. await db.Endpoints
            .Where(e => e.ClientId == clientId)
            .Where(MaintenanceRules.EndpointInMaintenance(now, db.MaintenanceWindowOccurrences, EffectivePolicies.Query(db)))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken)];

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
