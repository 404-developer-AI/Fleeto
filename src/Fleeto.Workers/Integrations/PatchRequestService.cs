using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Carries out what a technician asked about the patch management of one endpoint (0.6.0): read it from the product now
/// instead of at the next sync of its organization, or remove it from the product, which uninstalls the product's agent.
/// <para>
/// Web writes the request because only the workers can reach the product; this service makes the calls and writes the
/// outcome on the request, which the Patches tab shows. A sync of one endpoint costs a handful of calls against the
/// request budget of the whole instance, and web allows one per endpoint a minute.
/// </para>
/// </summary>
public sealed class PatchRequestService : WorkerLoop
{
    /// <summary>Requests handled in one pass; the rest waits for the next, so a burst cannot starve the patch sync.</summary>
    public const int MaxPerPass = 5;

    public const string RemovedReason = "The Action1 agent was removed from this endpoint.";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly Action1ClientFactory _clients;
    private readonly AlertNotificationService _notifier;
    private readonly LicenseService _licenses;
    private IDisposable? _subscription;

    public PatchRequestService(IFleetoDbContextFactory dbFactory, INotificationBus bus, Action1ClientFactory clients,
        AlertNotificationService notifier, LicenseService licenses, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<PatchRequestService> logger)
        : base("patch-requests", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _clients = clients;
        _notifier = notifier;
        _licenses = licenses;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override TimeSpan MaxRunDuration => TimeSpan.FromMinutes(10);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.PatchRequests, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override Task<bool> RunOnceAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    /// <summary>One pass over the waiting requests, oldest first. Returns true when more wait.</summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var pending = await db.PatchRequests.Where(r => r.CompletedAt == null)
            .OrderBy(r => r.RequestedAt)
            .Take(MaxPerPass + 1)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return false;
        }

        var integration = await db.Integrations.Include(i => i.Mappings)
            .SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        using var client = integration is { Enabled: true } ? _clients.TryCreate(integration) : null;
        var license = await _licenses.GetStatusAsync(db, cancellationToken);

        foreach (var request in pending.Take(MaxPerPass))
        {
            var now = Time.GetUtcNow().UtcDateTime;
            var outcome = new Outcome();
            var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == request.EndpointId, cancellationToken);
            var mapping = integration?.Mappings.FirstOrDefault(m => m.ClientId == request.ClientId);
            string? failure;
            if (integration is not { Enabled: true })
            {
                failure = "Patch management is not connected. Check Settings, Integrations.";
            }
            else if (client is null)
            {
                failure = "The Action1 credentials could not be read. Enter them again in Settings, Integrations.";
            }
            else if (endpoint is null || TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed)
            {
                failure = "This endpoint is not managed, so patch management does not apply to it.";
            }
            else if (request.Kind == PatchRequestKind.RemoveAgent)
            {
                failure = await RemoveAsync(db, client, request, endpoint, outcome, now, cancellationToken);
            }
            else if (mapping is null)
            {
                failure = "No Action1 organization is mapped to this client. Map one in Settings, Integrations.";
            }
            else
            {
                failure = await SyncAsync(db, client, mapping, endpoint, outcome, now, cancellationToken);
            }

            request.CompletedAt = now;
            request.Failure = failure is null ? null : Cut(failure, 1000);
            await db.SaveChangesAsync(cancellationToken);
            await FinishAsync(db, request, outcome, now, cancellationToken);
        }

        return pending.Count > MaxPerPass;
    }

    /// <summary>
    /// Reads one endpoint from the product as the sync of its organization would, and the automation of its patch policy
    /// with it: the next run, and a run that just happened as a scheduled deployment. Returns the cause of a failure.
    /// </summary>
    private async Task<string?> SyncAsync(FleetoDbContext db, Action1Client client, IntegrationMapping mapping, Endpoint endpoint, Outcome outcome,
        DateTime now, CancellationToken cancellationToken)
    {
        var agentId = await db.InventorySnapshots.AsNoTracking()
            .Where(i => i.EndpointId == endpoint.Id)
            .Select(i => i.Action1AgentId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(agentId))
        {
            return "The Fleeto agent reports no Action1 agent on this endpoint, so there is nothing to read from Action1. Install the Action1 agent first.";
        }

        var existing = await db.EndpointPatchStates.SingleOrDefaultAsync(s => s.EndpointId == endpoint.Id, cancellationToken);
        var read = await client.GetEndpointAsync(mapping.ExternalTenantId, agentId, cancellationToken);
        if (!read.Ok)
        {
            return $"Action1 could not be read: {read.Message}";
        }

        if (read.Value is not { } reported)
        {
            if (existing is not null && existing.ExternalTenantId == mapping.ExternalTenantId)
            {
                await PatchStateWriter.ForgetAsync(db, existing, outcome.Transitions, now, PatchStateWriter.GoneReason, cancellationToken);
                outcome.Gone = true;
            }

            outcome.Evaluate = true;
            return "Action1 does not hold this endpoint in the organization of this client. Check in the Action1 console where its agent registered.";
        }

        var created = existing is null;
        var (critical, other) = existing is null ? (0, 0) : (existing.MissingCritical, existing.MissingOther);
        var wasCompliant = existing?.IsCompliant ?? true;
        var state = PatchStateWriter.Store(db, existing, endpoint, reported.Endpoint, mapping.ExternalTenantId, now);
        string? failure = null;
        if (reported.CountsKnown && state.IsCompliant)
        {
            await PatchStateWriter.ClearDetailAsync(db, endpoint.Id, cancellationToken);
        }
        else
        {
            // Action1 may leave the counts out of a single endpoint: the list of missing updates then gives them.
            var updates = await PatchStateWriter.ReadDetailAsync(db, client, mapping.ExternalTenantId, endpoint, state, now, Logger, cancellationToken);
            if (updates is not null && !reported.CountsKnown)
            {
                state.MissingCritical = updates.Count(u => u.Severity == PatchSeverity.Critical);
                state.MissingOther = updates.Count - state.MissingCritical;
            }
            else if (updates is null && !reported.CountsKnown)
            {
                if (created)
                {
                    db.EndpointPatchStates.Remove(state);
                    return "Action1 answered, but the missing updates of this endpoint could not be read. Try again in a few minutes.";
                }

                (state.MissingCritical, state.MissingOther) = (critical, other);
                failure = "The missing updates of this endpoint could not be read from Action1, so they show what was read before. Try again in a few minutes.";
            }
            else if (updates is null)
            {
                failure = "The missing updates of this endpoint could not be read from Action1, so they show what was read before. Try again in a few minutes.";
            }
        }

        var inMaintenance = (await PatchStateWriter.InMaintenanceAsync(db, endpoint.ClientId, now, cancellationToken)).Contains(endpoint.Id);
        await PatchStateWriter.AlertAsync(db, endpoint, state, inMaintenance, outcome.Transitions, now, cancellationToken);
        outcome.Synced = true;
        outcome.Evaluate = true;

        if (state.AutomationId is { } automationId &&
            await db.IntegrationAutomations.SingleOrDefaultAsync(a => a.Id == automationId, cancellationToken) is { ExternalAutomationId: { } externalId } row)
        {
            var automation = await client.GetAutomationAsync(row.ExternalTenantId, externalId, cancellationToken);
            if (automation is { Ok: true, Value: null })
            {
                // Removed in the Action1 console: the automations of patch policies create it again.
                row.ExternalAutomationId = null;
                row.NextRunAt = null;
                row.UpdatedAt = now;
            }
            else if (automation is { Ok: true, Value: { } info })
            {
                outcome.Started = await ScheduledDeployments.RecordAsync(db, row, info, now, cancellationToken);
            }
            else
            {
                Logger.LogInformation("The automation of endpoint {EndpointId} could not be read: {Message}", endpoint.Id, automation.Message);
            }
        }

        if (wasCompliant != state.IsCompliant)
        {
            Logger.LogInformation("Endpoint {EndpointId} synced by request: {Critical} critical and {Other} other updates missing", endpoint.Id,
                state.MissingCritical, state.MissingOther);
        }

        return failure;
    }

    /// <summary>
    /// Removes the endpoint from its organization, after which Action1 uninstalls its agent, and forgets its patch state at
    /// once instead of at the next sync. Returns the cause of a failure.
    /// </summary>
    private async Task<string?> RemoveAsync(FleetoDbContext db, Action1Client client, PatchRequest request, Endpoint endpoint, Outcome outcome,
        DateTime now, CancellationToken cancellationToken)
    {
        string? failure = null;
        if (request.ExternalTenantId.Length == 0 || request.ExternalEndpointId.Length == 0)
        {
            failure = "Action1 did not report this endpoint when the removal was asked, so there was nothing to remove.";
        }
        else
        {
            var removed = await client.DeleteEndpointAsync(request.ExternalTenantId, request.ExternalEndpointId, cancellationToken);
            if (!removed.Ok)
            {
                failure = removed.Permanent
                    ? $"{removed.Message} Check that the role of the Action1 API credentials may manage endpoints."
                    : $"Action1 did not answer: {removed.Message} Try again in a few minutes.";
            }
        }

        if (failure is null &&
            await db.EndpointPatchStates.SingleOrDefaultAsync(s => s.EndpointId == endpoint.Id && s.ExternalEndpointId == request.ExternalEndpointId,
                cancellationToken) is { } state)
        {
            await PatchStateWriter.ForgetAsync(db, state, outcome.Transitions, now, RemovedReason, cancellationToken);
            outcome.Gone = true;
            outcome.Evaluate = true;
        }

        db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.PatchAgentRemovalEnded, "Endpoint", endpoint.Id.ToString(), endpoint.ClientId,
            AuditActorType.System, "fleeto-workers", "fleeto-workers", new
            {
                endpoint.Hostname,
                Removed = failure is null,
                Failure = failure,
                RequestedBy = request.RequestedByName
            }), now));
        Logger.LogInformation("Removal of the Action1 agent of endpoint {EndpointId}: {Outcome}", endpoint.Id, failure ?? "done");
        return failure;
    }

    /// <summary>
    /// Writes what follows from a request after it was saved: the checks Fleeto evaluates from the patch state, the alerts
    /// that changed, and the notifications the Patches tab and the deployment service wait for.
    /// </summary>
    private async Task FinishAsync(FleetoDbContext db, PatchRequest request, Outcome outcome, DateTime now, CancellationToken cancellationToken)
    {
        var endpointIds = new[] { request.EndpointId };
        var written = new List<Guid>();
        if (outcome.Synced)
        {
            written.AddRange(await MissingUpdateChecks.WriteResultsAsync(db, endpointIds, now, cancellationToken));
        }

        if (outcome.Gone)
        {
            outcome.Transitions.AddRange(await MissingUpdateChecks.ForgetAsync(db, endpointIds, now, cancellationToken));
        }

        if (outcome.Evaluate)
        {
            var (scheduled, transitions) = await ScheduledPatchingChecks.EvaluateAsync(db, endpointIds, now, cancellationToken);
            written.AddRange(scheduled);
            outcome.Transitions.AddRange(transitions);
        }

        await _notifier.AddNotificationsAsync(db, outcome.Transitions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AlertCleanup.PublishAsync(_bus, outcome.Transitions.Select(t => t.AlertId), cancellationToken);
        foreach (var endpointId in written.Distinct())
        {
            await _bus.PublishAsync(NotificationChannels.CheckResults, endpointId.ToString(), cancellationToken);
        }

        if (outcome.Started is { } deployment)
        {
            await _bus.PublishAsync(NotificationChannels.PatchDeployments, deployment.Id.ToString(), cancellationToken);
        }

        await _bus.PublishAsync(NotificationChannels.PatchRequests, request.Id.ToString(), cancellationToken);
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>What a request changed, for the work that follows its save.</summary>
    private sealed class Outcome
    {
        public List<AlertTransition> Transitions { get; } = [];

        /// <summary>The patch state was read: the missing updates checks get a result.</summary>
        public bool Synced { get; set; }

        /// <summary>The patch state is gone: the missing updates checks lose theirs.</summary>
        public bool Gone { get; set; }

        /// <summary>The scheduled patching checks are evaluated again.</summary>
        public bool Evaluate { get; set; }

        /// <summary>A scheduled deployment was found while reading the automation.</summary>
        public PatchDeployment? Started { get; set; }
    }
}
