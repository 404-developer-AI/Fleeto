using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>Where the removal of the Action1 agent of one endpoint stands.</summary>
public enum Action1RemovalStatus
{
    /// <summary>Nothing asked yet.</summary>
    NotStarted,

    /// <summary>The workers ask Action1 to remove the endpoint; a failure is retried by itself.</summary>
    WaitingForAction1,

    /// <summary>Action1 removed the endpoint and uninstalls its agent the next time the endpoint is online.</summary>
    RemovedByAction1,

    /// <summary>The signed job that uninstalls the agent waits for its signature, for the endpoint, or runs.</summary>
    JobRunning,

    /// <summary>The job ended well; the Fleeto agent reports the change with its next inventory.</summary>
    JobSucceeded,

    /// <summary>The job failed, was refused or expired; it can be tried again.</summary>
    JobFailed,

    /// <summary>Fleeto cannot remove it.</summary>
    ByHand
}

/// <summary>One endpoint on which an Action1 agent runs, or that Action1 reports, with how and where its removal stands.</summary>
/// <param name="Detail">The reason of a failure or of a removal by hand; null otherwise.</param>
public sealed record Action1AgentEndpointView(Guid EndpointId, Guid ClientId, string ClientCode, string Hostname, EndpointClass Class,
    bool Managed, Action1RemovalWay Way, Action1RemovalStatus Status, string? Detail)
{
    /// <summary>True when asking for the removal (again) would do something.</summary>
    public bool CanStart => Way != Action1RemovalWay.ByHand && Status is Action1RemovalStatus.NotStarted or Action1RemovalStatus.JobFailed;
}

/// <summary>A patch policy of engine Action1 and what becomes of it under engine Fleeto.</summary>
public sealed record PatchPolicyConversionView(Guid Id, string Name, string? ClientCode, bool Enabled, IReadOnlyList<string> Dropped, bool Disabled);

/// <summary>The patch management of the instance and, while it is Fleeto, the Action1 agents that remain.</summary>
/// <param name="ChangedAt">When an admin last chose; null when the choice was never made by hand.</param>
public sealed record PatchManagementView(PatchManager Manager, DateTime? ChangedAt, IReadOnlyList<Action1AgentEndpointView> Agents);

/// <summary>What choosing <paramref name="Target"/> changes, for the confirmation.</summary>
/// <param name="Automations">The Action1 automations Fleeto made that the workers remove.</param>
/// <param name="FollowClients">True when Action1 follows clients and sites now and stops doing so.</param>
public sealed record PatchManagerPreview(PatchManager Target, IReadOnlyList<PatchPolicyConversionView> Policies, int Automations, bool FollowClients,
    IReadOnlyList<Action1AgentEndpointView> Agents);

/// <summary>What a choice or a removal started: the removals per way, and the endpoints that need a removal by hand.</summary>
public sealed record Action1RemovalsStarted(int ThroughAction1, int ByJob, int ByHand);

/// <summary>
/// The instance-wide choice of patch management (0.9.0, decided 2026-11-27, <c>PATCHING.md</c> §16): Action1, or Fleeto itself.
/// Choosing Fleeto converts every patch policy of engine Action1 to engine Fleeto (its automations are removed by the workers),
/// stops Action1 following clients and sites, and removes the Action1 agents of the managed endpoints, and on request of the
/// agent-only ones: through Action1 where it reports the endpoint, otherwise by a signed job on a managed Windows endpoint. One
/// transaction, one audit entry, confirmed with a fresh code of the Fleeto authenticator. Admins only: it reaches every client.
/// </summary>
public sealed class PatchManagementService
{
    /// <summary>The name of the token that remembers the last code used for this (see <see cref="FreshTotp"/>).</summary>
    internal const string CodeTokenName = "PatchManagementLastCode";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly INotificationBus _bus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<PatchManagementService> _logger;

    public PatchManagementService(IFleetoDbContextFactory dbFactory, LicenseService licenses, INotificationBus bus, IServiceScopeFactory scopeFactory,
        TimeProvider time, ILogger<PatchManagementService> logger)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _bus = bus;
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    /// <summary>The choice only, for the pages that hide what belongs to Action1. Every signed-in user may know it.</summary>
    public async Task<PatchManager> GetManagerAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.Create(caller.Scope);
        return await PatchManagers.GetAsync(db, cancellationToken);
    }

    /// <summary>The choice and, while it is Fleeto, every endpoint that still has an Action1 agent with its removal.</summary>
    public async Task<PatchManagementView> GetAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.InstanceView);
        await using var db = _dbFactory.Create(caller.Scope);
        var setting = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == PatchManagement.SettingKey, cancellationToken);
        var manager = PatchManagement.Parse(setting?.Value);
        var agents = manager == PatchManager.Fleeto ? await AgentsAsync(db, true, cancellationToken) : [];
        return new PatchManagementView(manager, setting?.UpdatedByUserId is null ? null : setting.UpdatedAt, agents);
    }

    /// <summary>What choosing <paramref name="target"/> would change. Admins only.</summary>
    public async Task<ServiceResult<PatchManagerPreview>> PreviewAsync(Caller caller, PatchManager target, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<PatchManagerPreview>.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        if (target != PatchManager.Fleeto)
        {
            return ServiceResult<PatchManagerPreview>.Ok(new PatchManagerPreview(target, [], 0, false, []));
        }

        var policies = await db.PatchPolicies.AsNoTracking().Where(p => p.Engine == PatchEngine.Action1).OrderBy(p => p.Name).ToListAsync(cancellationToken);
        var codes = await db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        var conversions = policies.Select(p =>
        {
            var enabled = p.Enabled;
            // Converted on a copy that is not tracked: nothing is saved here.
            var conversion = PatchManagement.ConvertToFleeto(p);
            return new PatchPolicyConversionView(p.Id, p.Name, p.ClientId is { } c ? codes.GetValueOrDefault(c) : null, enabled, conversion.Dropped,
                conversion.Disabled);
        }).ToList();
        var automations = await db.IntegrationAutomations.AsNoTracking().CountAsync(cancellationToken);
        var follow = await db.Integrations.AsNoTracking().AnyAsync(i => i.Type == IntegrationType.Action1 && i.FollowClients, cancellationToken);
        return ServiceResult<PatchManagerPreview>.Ok(new PatchManagerPreview(target, conversions, automations, follow,
            await AgentsAsync(db, true, cancellationToken)));
    }

    /// <summary>
    /// Chooses the patch management of the instance. Choosing Fleeto converts the patch policies, stops Action1 following clients and
    /// sites, and starts the removal of the Action1 agents of the managed endpoints, and of the agent-only ones when
    /// <paramref name="includeAgentOnly"/> is set. Choosing Action1 changes nothing else: patch policies stay of engine Fleeto and no
    /// Action1 agent is installed by itself. Admins only, with a fresh code of the Fleeto authenticator.
    /// </summary>
    public async Task<ServiceResult<Action1RemovalsStarted>> ChooseAsync(Caller caller, PatchManager target, bool includeAgentOnly, string? code,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<Action1RemovalsStarted>.Forbidden();
        }

        if (!Enum.IsDefined(target))
        {
            return ServiceResult<Action1RemovalsStarted>.Fail("Choose Fleeto or Action1.");
        }

        await using var db = _dbFactory.CreateSystem();
        if (await PatchManagers.GetAsync(db, cancellationToken) == target)
        {
            return ServiceResult<Action1RemovalsStarted>.Fail($"This instance already patches with {PatchManagement.Describe(target)}.");
        }

        if (await FreshTotp.VerifyAsync(_scopeFactory, caller, code, CodeTokenName, "Choosing the patch management", _time.GetUtcNow()) is { } codeProblem)
        {
            return ServiceResult<Action1RemovalsStarted>.Fail(codeProblem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == PatchManagement.SettingKey, cancellationToken);
        if (setting is null)
        {
            setting = new Setting { Key = PatchManagement.SettingKey };
            db.Settings.Add(setting);
        }

        var from = PatchManagement.Parse(setting.Value);
        setting.Value = PatchManagement.ToValue(target);
        setting.IsEncrypted = false;
        setting.UpdatedAt = now;
        setting.UpdatedByUserId = caller.UserId;

        var started = new Action1RemovalsStarted(0, 0, 0);
        object details = new { From = PatchManagement.Describe(from), To = PatchManagement.Describe(target) };
        if (target == PatchManager.Fleeto)
        {
            var policies = await db.PatchPolicies.Where(p => p.Engine == PatchEngine.Action1).ToListAsync(cancellationToken);
            var converted = new List<object>();
            foreach (var policy in policies)
            {
                var conversion = PatchManagement.ConvertToFleeto(policy);
                policy.UpdatedAt = now;
                converted.Add(new { policy.Id, policy.Name, conversion.Dropped, conversion.Disabled });
            }

            var integration = await db.Integrations.SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
            var stoppedFollowing = integration is { FollowClients: true };
            if (integration is not null)
            {
                integration.FollowClients = false;
            }

            // The engine of every converted policy reaches the signed configuration of its endpoints (scans, Windows Update).
            db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Instance, CreatedAt = now });
            started = await StartRemovalsAsync(db, caller, includeAgentOnly, now, cancellationToken);
            details = new
            {
                From = PatchManagement.Describe(from),
                To = PatchManagement.Describe(target),
                PatchPolicies = converted,
                StoppedFollowingClients = stoppedFollowing,
                IncludeAgentOnly = includeAgentOnly,
                Removals = started
            };
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchManagerChanged, "Instance", PatchManagement.SettingKey, null, details), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await NotifyAsync(cancellationToken);
        return ServiceResult<Action1RemovalsStarted>.Ok(started);
    }

    /// <summary>
    /// Asks again for the removal of every Action1 agent that remains while the instance patches with Fleeto and whose removal is not on
    /// its way: one that appeared later, or whose job failed. Admins only, with a fresh code of the Fleeto authenticator.
    /// </summary>
    public async Task<ServiceResult<Action1RemovalsStarted>> RemoveAgentsAsync(Caller caller, bool includeAgentOnly, string? code,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<Action1RemovalsStarted>.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        if (await PatchManagers.GetAsync(db, cancellationToken) != PatchManager.Fleeto)
        {
            return ServiceResult<Action1RemovalsStarted>.Fail("This instance patches with Action1, so Fleeto does not remove its agents. Remove one from the Patches tab of its endpoint.");
        }

        if (await FreshTotp.VerifyAsync(_scopeFactory, caller, code, CodeTokenName, "Removing the Action1 agents", _time.GetUtcNow()) is { } codeProblem)
        {
            return ServiceResult<Action1RemovalsStarted>.Fail(codeProblem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var started = await StartRemovalsAsync(db, caller, includeAgentOnly, now, cancellationToken);
        if (started.ThroughAction1 + started.ByJob == 0)
        {
            return ServiceResult<Action1RemovalsStarted>.Fail(started.ByHand > 0
                ? "Fleeto cannot remove the Action1 agents that remain. Remove them in the Action1 console or on the endpoints."
                : "There is no Action1 agent whose removal is not already on its way.");
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchAgentRemovalsRequested, "Instance", PatchManagement.SettingKey, null,
            new { IncludeAgentOnly = includeAgentOnly, Removals = started }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await NotifyAsync(cancellationToken);
        return ServiceResult<Action1RemovalsStarted>.Ok(started);
    }

    /// <summary>
    /// Adds a removal for every endpoint with an Action1 agent that can start one: an integration operation that removes the endpoint
    /// from its Action1 organization (retried by the workers until it succeeds), or the signed job that uninstalls the agent. Each
    /// endpoint gets the audit entry of a removal; the caller saves.
    /// </summary>
    private async Task<Action1RemovalsStarted> StartRemovalsAsync(FleetoDbContext db, Caller caller, bool includeAgentOnly, DateTime now,
        CancellationToken cancellationToken)
    {
        var agents = await AgentsAsync(db, includeAgentOnly, cancellationToken);
        var integrationId = await db.Integrations.AsNoTracking().Where(i => i.Type == IntegrationType.Action1).Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var endpoints = await db.Endpoints.Where(e => agents.Select(a => a.EndpointId).Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        int throughAction1 = 0, byJob = 0;
        foreach (var agent in agents.Where(a => a.CanStart))
        {
            var endpoint = endpoints[agent.EndpointId];
            if (agent.Way == Action1RemovalWay.Action1 && integrationId is not null &&
                await Action1Removal.OperationAsync(db, endpoint, now, cancellationToken) is { } operation)
            {
                Action1Removal.Add(db, caller, endpoint, operation, "The instance patches with Fleeto", now);
                throughAction1++;
            }
            else if (agent.Way == Action1RemovalWay.Job)
            {
                PatchService.AddComposedJob(db, caller, endpoint.Id, endpoint.ClientId, endpoint.Hostname, JobType.Action1AgentRemoval,
                    Action1AgentRemoval.JobName, Action1AgentRemoval.Body(), Action1AgentRemoval.TimeoutSeconds, Action1AgentRemoval.MaxOutputBytes, now);
                db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchAgentRemovalRequested, "Endpoint", endpoint.Id.ToString(),
                    endpoint.ClientId, new { endpoint.Hostname, Through = "Job", Reason = "The instance patches with Fleeto" }), now));
                byJob++;
            }
        }

        return new Action1RemovalsStarted(throughAction1, byJob, agents.Count(a => a.Way == Action1RemovalWay.ByHand));
    }

    /// <summary>
    /// Every endpoint with an Action1 agent: the Fleeto agent reports one, or Action1 reports the endpoint. Managed first, then by client
    /// and host name; agent-only endpoints only when <paramref name="includeAgentOnly"/> is set. Set-based: a handful of queries
    /// whatever the number of endpoints.
    /// </summary>
    private async Task<IReadOnlyList<Action1AgentEndpointView>> AgentsAsync(FleetoDbContext db, bool includeAgentOnly, CancellationToken cancellationToken)
    {
        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var reported = await db.InventorySnapshots.AsNoTracking().Where(i => i.Action1AgentId != "")
            .Select(i => new { i.EndpointId, i.Action1AgentId })
            .ToDictionaryAsync(i => i.EndpointId, i => i.Action1AgentId, cancellationToken);
        var states = await db.EndpointPatchStates.AsNoTracking().Where(s => s.ExternalEndpointId != "")
            .Select(s => new { s.EndpointId, s.ExternalTenantId, s.ExternalEndpointId })
            .ToDictionaryAsync(s => s.EndpointId, cancellationToken);
        var ids = reported.Keys.Union(states.Keys).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var integrationRow = await db.Integrations.AsNoTracking().Where(i => i.Type == IntegrationType.Action1)
            .Select(i => new { i.Id, i.Enabled })
            .FirstOrDefaultAsync(cancellationToken);
        var integrationId = integrationRow?.Id;
        var mapped = integrationId is { } integration
            ? (await db.IntegrationMappings.AsNoTracking().Where(m => m.IntegrationId == integration && m.ExternalTenantId != "")
                .Select(m => m.ClientId).ToListAsync(cancellationToken)).ToHashSet()
            : [];
        var endpoints = await (from e in db.Endpoints.AsNoTracking()
                               join c in db.Clients.AsNoTracking() on e.ClientId equals c.Id
                               where ids.Contains(e.Id) && e.Source == EndpointSource.Agent
                               select new { e.Id, e.ClientId, c.Code, e.Hostname, Class = e.ClassOverride ?? e.DetectedClass, e.Tier, e.OsPlatform })
            .ToListAsync(cancellationToken);
        var pendingOperations = (await db.IntegrationOperations.AsNoTracking()
                .Where(o => o.Kind == IntegrationOperationKind.RemoveEndpoint)
                .Select(o => new { o.ExternalEndpointId, o.LastError, o.Attempts })
                .ToListAsync(cancellationToken))
            .GroupBy(o => o.ExternalEndpointId.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        var pendingRequests = (await db.PatchRequests.AsNoTracking()
                .Where(r => ids.Contains(r.EndpointId) && r.Kind == PatchRequestKind.RemoveAgent && r.CompletedAt == null)
                .Select(r => r.EndpointId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var jobs = (await db.Jobs.AsNoTracking()
                .Where(j => ids.Contains(j.EndpointId) && j.Type == JobType.Action1AgentRemoval)
                .Select(j => new { j.EndpointId, j.CreatedAt, j.State, j.RefusalReason, j.Error })
                .ToListAsync(cancellationToken))
            .GroupBy(j => j.EndpointId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedAt).First());
        var removed = (await db.Action1AgentInstallStates.AsNoTracking()
                .Where(s => ids.Contains(s.EndpointId) && s.Status == Action1AgentStatus.Removed && s.RemovalId != null)
                .Select(s => new { s.EndpointId, s.UpdatedAt })
                .ToListAsync(cancellationToken))
            .ToDictionary(s => s.EndpointId, s => s.UpdatedAt);

        var result = new List<Action1AgentEndpointView>();
        foreach (var e in endpoints)
        {
            var managed = TierRules.EffectiveTier(e.Tier, license) == EndpointTier.Managed;
            if (!managed && !includeAgentOnly)
            {
                continue;
            }

            var windows = ScriptLanguages.RunsOn(ScriptLanguage.PowerShell, e.OsPlatform);
            states.TryGetValue(e.Id, out var state);
            var agentId = state is { ExternalTenantId.Length: > 0 } ? state.ExternalEndpointId : reported.GetValueOrDefault(e.Id) ?? string.Empty;
            var action1Knows = integrationId is not null && agentId.Length > 0 &&
                               (state is { ExternalTenantId.Length: > 0 } || mapped.Contains(e.ClientId));
            var way = PatchManagement.RemovalWay(action1Knows, managed, windows);
            jobs.TryGetValue(e.Id, out var job);
            var (status, detail) = (Action1RemovalStatus.NotStarted, (string?)null);
            if (agentId.Length > 0 && pendingOperations.TryGetValue(agentId.ToLowerInvariant(), out var operation))
            {
                (status, detail) = (Action1RemovalStatus.WaitingForAction1, integrationRow is { Enabled: false }
                    ? "The Action1 integration is switched off. Switch it on in Settings, Integrations so the workers can ask Action1."
                    : operation.LastError);
            }
            else if (pendingRequests.Contains(e.Id))
            {
                status = Action1RemovalStatus.WaitingForAction1;
            }
            else if (job is { State: JobState.PendingSignature or JobState.Queued or JobState.Running })
            {
                status = Action1RemovalStatus.JobRunning;
            }
            else if (removed.TryGetValue(e.Id, out var removedAt) && (job is null || removedAt >= job.CreatedAt))
            {
                status = Action1RemovalStatus.RemovedByAction1;
            }
            else if (job is { State: JobState.Succeeded })
            {
                status = Action1RemovalStatus.JobSucceeded;
            }
            else if (job is not null)
            {
                (status, detail) = (Action1RemovalStatus.JobFailed, job.RefusalReason ?? job.Error ?? $"The job ended as {job.State.ToString().ToLowerInvariant()}.");
            }
            else if (way == Action1RemovalWay.ByHand)
            {
                (status, detail) = (Action1RemovalStatus.ByHand, PatchManagement.ByHandReason(managed, windows));
            }

            result.Add(new Action1AgentEndpointView(e.Id, e.ClientId, e.Code, e.Hostname, e.Class, managed, way, status,
                detail is { Length: > 300 } ? detail[..300] : detail));
        }

        return [.. result.OrderByDescending(a => a.Managed).ThenBy(a => a.ClientCode, StringComparer.Ordinal)
            .ThenBy(a => a.Hostname, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Wakes the workers (removals, automations). Everything is committed already; a lost notification
    /// only delays the work until the next pass.
    /// </summary>
    private async Task NotifyAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The signer picks up the signing requests of the removal jobs by itself and wakes the gateway once it signed them.
            await _bus.PublishAsync(NotificationChannels.Integrations, IntegrationType.Action1.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not wake the workers after a change of the patch management");
        }
    }
}
