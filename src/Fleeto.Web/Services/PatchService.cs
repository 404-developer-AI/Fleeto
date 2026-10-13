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

/// <summary>One update an endpoint is missing, as the patch management product reports it.</summary>
/// <param name="Version">The version the product would install.</param>
/// <param name="Cves">The CVEs it remediates, as one comma-separated text so the view keeps value equality (a chosen update
/// stays chosen after a refresh).</param>
public sealed record MissingUpdateView(string Id, string Name, string Vendor, string Version, string KbNumber, PatchSeverity Severity,
    string InstalledVersion, DateOnly? ReleaseDate, string UpdateType, string ApprovalStatus, string Cves)
{
    public IReadOnlyList<string> CveList => Cves.Length == 0 ? [] : Cves.Split(", ");
}

/// <summary>
/// The patch state of one endpoint (0.4.0).
/// </summary>
/// <param name="Managed">False for an agent-only endpoint: patch management is a managed feature.</param>
/// <param name="Configured">False while no patch management integration is set up for this client at all.</param>
/// <param name="State">Null when the product does not know this endpoint, even though the integration is configured.</param>
/// <param name="CanInstallAgent">
/// True when Fleeto can put the Action1 agent on this endpoint itself (0.4.0 step 3): a managed Windows endpoint the
/// product does not know yet, whose client has an installer link, and on which the Fleeto agent reports no Action1 agent.
/// </param>
/// <param name="AgentOnEndpoint">The id of the Action1 agent the Fleeto agent reports on the endpoint (0.6.0); empty when none.</param>
/// <param name="Schedule">When and how the endpoint's patch policy deploys updates on it (0.6.0); null without patch state.</param>
/// <param name="LastRequest">The newest sync or agent removal a technician asked for on this endpoint (0.6.0).</param>
/// <param name="CanRemoveAgentByJob">
/// True when an Action1 agent runs on this managed Windows endpoint that Action1 does not report (0.6.0): Fleeto can only
/// remove it with a signed job, since Action1 cannot remove what it does not hold.
/// </param>
public sealed record EndpointPatchView(bool Managed, bool Configured, EndpointPatchState? State, IReadOnlyList<MissingUpdateView> Missing,
    DateTime? DetailUpdatedAt, bool CanInstallAgent = false, string AgentOnEndpoint = "", PatchScheduleView? Schedule = null,
    PatchRequestView? LastRequest = null, bool CanRemoveAgentByJob = false);

/// <summary>
/// The patch policy of an endpoint as it plays out in the product (0.6.0): which policy, its schedule in words, and when the
/// product plans the next deployment on the endpoint.
/// </summary>
/// <param name="PolicyName">Null when no patch policy applies to the endpoint, so nothing is scheduled.</param>
/// <param name="NextRunAt">
/// When the product plans the next run; null when it named none. In the endpoint's own time when
/// <paramref name="EndpointLocalTime"/>, else UTC.
/// </param>
/// <param name="Problem">Why the next run is not known or will not happen, in the words of the UI; null when all is well.</param>
/// <param name="ProblemNeedsAction">True when the problem lasts until somebody acts; false while Fleeto or Action1 is still busy.</param>
public sealed record PatchScheduleView(Guid? PolicyId, string? PolicyName, string? Schedule, bool Enabled, bool AutoReboot, bool EndpointLocalTime,
    DateTime? NextRunAt, string? Problem, bool ProblemNeedsAction = false);

/// <summary>A sync or agent removal a technician asked for (0.6.0), with what the workers made of it.</summary>
/// <param name="Failure">Cause and next step when it failed; null when it succeeded or still waits.</param>
public sealed record PatchRequestView(Guid Id, PatchRequestKind Kind, string RequestedByName, DateTime RequestedAt, DateTime? CompletedAt, string? Failure)
{
    public bool IsPending => CompletedAt is null;
}

/// <summary>Compliance of a set of endpoints: how many have patch state, and how many of those miss nothing.</summary>
/// <param name="Covered">Endpoints the product reports on.</param>
/// <param name="Compliant">Of those, the ones missing nothing.</param>
/// <param name="MissingCritical">Endpoints missing at least one update the vendor calls critical.</param>
/// <param name="NotPatched">Endpoints the product no longer patches (over the quota of the subscription).</param>
public sealed record PatchCompliance(int Covered, int Compliant, int MissingCritical, int NotPatched)
{
    public static readonly PatchCompliance None = new(0, 0, 0, 0);

    /// <summary>Percentage of covered endpoints that miss nothing, or null when nothing is covered.</summary>
    public int? Percentage => Covered == 0 ? null : (int)Math.Round(Compliant * 100d / Covered);
}

/// <summary>One endpoint of a deployment as the UI shows it.</summary>
public sealed record DeploymentTargetView(Guid EndpointId, string Hostname, PatchDeploymentTargetState State, string? Message);

/// <summary>
/// One deployment with what the product reports per endpoint (0.4.0 step 3).
/// </summary>
/// <param name="UpdateNames">The updates it installs, empty when it installs every update the endpoints miss.</param>
public sealed record DeploymentView(Guid Id, Guid BatchId, Guid ClientId, PatchDeploymentScope Scope, bool AutoReboot,
    PatchDeploymentState State, string? StatusMessage, string RequestedByName, DateTime RequestedAt, DateTime? CompletedAt,
    IReadOnlyList<string> UpdateNames, IReadOnlyList<DeploymentTargetView> Targets, Guid? PatchPolicyId = null)
{
    public int Succeeded => Targets.Count(t => t.State == PatchDeploymentTargetState.Succeeded);
    public int Failed => Targets.Count(t => t.State == PatchDeploymentTargetState.Failed);
    public bool IsOpen => State is PatchDeploymentState.Requested or PatchDeploymentState.Running;

    /// <summary>A run of a patch policy on its schedule (0.6.0); <see cref="RequestedByName"/> is then the name of the policy.</summary>
    public bool IsScheduled => Scope == PatchDeploymentScope.PatchPolicy;
}

/// <summary>One line of the history the product keeps for one endpoint of a deployment (0.6.0), in its own words.</summary>
public sealed record DeploymentStepView(DateTime? Time, string Operation, string Status, string Details);

/// <summary>The history of one endpoint of a deployment, and when Fleeto last read it from the product.</summary>
/// <param name="ReadAt">Null while the history was not read since the state of the endpoint last changed.</param>
public sealed record DeploymentHistoryView(Guid DeploymentId, Guid EndpointId, string Hostname, PatchDeploymentTargetState State,
    DateTime RequestedAt, DateTime? ReadAt, IReadOnlyList<DeploymentStepView> Steps);

/// <param name="Deployments">One per client of the run, in the order the clients were named.</param>
/// <param name="Skipped">Endpoints nothing was started for, with the reason.</param>
public sealed record DeploymentStartResult(Guid BatchId, IReadOnlyList<DeploymentView> Deployments, IReadOnlyList<JobRunTarget> Skipped)
{
    public int Endpoints => Deployments.Sum(d => d.Targets.Count);
}

/// <summary>
/// What Fleeto shows about patch management, and what a technician starts there (0.4.0). Action1 does the patching; this
/// reads the state the workers stored and writes what was asked for, with the same client scope and tier rules as
/// everything else. Nothing here calls Action1: web has no outbound access at all, so a deployment travels to the workers
/// through the database and a notification.
/// </summary>
public sealed class PatchService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;

    public PatchService(IFleetoDbContextFactory dbFactory, LicenseService licenses, INotificationBus bus, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _bus = bus;
        _time = time;
    }

    /// <summary>The patch state of one endpoint. Null when the endpoint does not exist or is not visible to the caller.</summary>
    public async Task<EndpointPatchView?> GetAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new { e.Tier, e.ClientId, e.OsPlatform }).SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var managed = TierRules.EffectiveTier(endpoint.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) == EndpointTier.Managed;
        var configured = await db.IntegrationMappings.AsNoTracking().AnyAsync(m => m.ClientId == endpoint.ClientId, cancellationToken);
        if (!managed || !configured)
        {
            return new EndpointPatchView(managed, configured, null, [], null);
        }

        var agentOnEndpoint = await db.InventorySnapshots.AsNoTracking()
            .Where(i => i.EndpointId == endpointId)
            .Select(i => i.Action1AgentId)
            .SingleOrDefaultAsync(cancellationToken) ?? string.Empty;
        var lastRequest = await db.PatchRequests.AsNoTracking()
            .Where(r => r.EndpointId == endpointId)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new PatchRequestView(r.Id, r.Kind, r.RequestedByName, r.RequestedAt, r.CompletedAt, r.Failure))
            .FirstOrDefaultAsync(cancellationToken);
        var windows = ScriptLanguages.RunsOn(ScriptLanguage.PowerShell, endpoint.OsPlatform);
        var state = await db.EndpointPatchStates.AsNoTracking().SingleOrDefaultAsync(p => p.EndpointId == endpointId, cancellationToken);
        if (state is null)
        {
            // The product does not know this endpoint. When it is Windows without an Action1 agent and the client has an
            // installer link, Fleeto can put the product's agent there itself instead of leaving the technician to do it by
            // hand. When an agent does run there, Action1 holds it elsewhere or not at all, and only a job can remove it.
            var installerUrl = await db.IntegrationMappings.AsNoTracking()
                .Where(m => m.ClientId == endpoint.ClientId)
                .Select(m => m.AgentInstallerUrl)
                .FirstOrDefaultAsync(cancellationToken);
            return new EndpointPatchView(true, true, null, [], null,
                windows && agentOnEndpoint.Length == 0 && Action1AgentInstall.IsValidInstallerUrl(installerUrl), agentOnEndpoint, null, lastRequest,
                windows && agentOnEndpoint.Length > 0);
        }

        var rows = await db.EndpointMissingUpdates.AsNoTracking()
            .Where(u => u.EndpointId == endpointId)
            .ToListAsync(cancellationToken);
        // Sorted here, not in the database: the severity is stored by name, so the database would sort it alphabetically
        // and put "Low" above "Critical". One endpoint has tens of missing updates, not thousands.
        List<MissingUpdateView> missing = [.. rows
            .Select(u => new MissingUpdateView(u.ExternalUpdateId, u.Name, u.Vendor, u.Version, u.KbNumber, u.Severity,
                u.InstalledVersion, u.ReleaseDate, u.UpdateType, u.ApprovalStatus, string.Join(", ", u.Cves)))
            .OrderByDescending(u => u.Severity).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)];
        var detailAt = rows.Count == 0 ? (DateTime?)null : rows.Max(u => u.UpdatedAt);

        return new EndpointPatchView(true, true, state, missing, detailAt, false, agentOnEndpoint,
            await ScheduleAsync(db, endpointId, endpoint.ClientId, state, cancellationToken), lastRequest);
    }

    /// <summary>
    /// The patch policy that applies to the endpoint and what the product made of it (0.6.0): its automation, whether the
    /// endpoint is among its targets, and the next run the product planned.
    /// </summary>
    private static async Task<PatchScheduleView> ScheduleAsync(FleetoDbContext db, Guid endpointId, Guid clientId, EndpointPatchState state,
        CancellationToken cancellationToken)
    {
        var policyId = await EffectivePolicies.Query(db).Where(p => p.EndpointId == endpointId).Select(p => p.PatchPolicyId)
            .SingleOrDefaultAsync(cancellationToken);
        var policy = policyId is { } id ? await db.PatchPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken) : null;
        if (policy is null)
        {
            return new PatchScheduleView(null, null, null, false, false, false, null,
                "No patch policy applies to this endpoint, so Action1 deploys nothing on a schedule. Link one to the client, site or endpoint.", true);
        }

        var automation = await db.IntegrationAutomations.AsNoTracking()
            .SingleOrDefaultAsync(a => a.ClientId == clientId && a.PatchPolicyId == policy.Id, cancellationToken);
        var problem = !policy.Enabled
            ? "The patch policy is switched off, so Action1 does not run it."
            : automation?.LastError is { } error
                ? $"Action1 refused the automation of this patch policy: {error}"
                : automation?.ExternalAutomationId is null
                    ? "Fleeto is writing the automation of this patch policy to Action1. This takes a few minutes."
                    : state.AutomationId != automation.Id
                        ? "Fleeto is adding this endpoint to the automation of this patch policy in Action1. This takes a few minutes."
                        : automation.NextRunAt is null
                            ? "Action1 has not planned the next deployment yet."
                            : null;
        return new PatchScheduleView(policy.Id, policy.Name, PatchPolicyRules.DescribeSchedule(policy), policy.Enabled, policy.AutoReboot,
            policy.EndpointLocalTime, problem is null ? automation!.NextRunAt : null, problem, !policy.Enabled || automation?.LastError is not null);
    }

    /// <summary>
    /// Asks the workers to read this endpoint from the product now (0.6.0): its patch state, missing updates, the next run
    /// of its patch policy and a scheduled deployment that just ran. One request per endpoint a minute; a request that
    /// still waits is answered by itself.
    /// </summary>
    public async Task<ServiceResult> RequestSyncAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new { e.Id, e.ClientId, e.Tier })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult.NotFound("endpoint");
        }

        if (TierRules.EffectiveTier(endpoint.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) != EndpointTier.Managed)
        {
            return ServiceResult.Fail("This endpoint is not managed. Switch it to managed to use patch management on it.");
        }

        if (!await db.IntegrationMappings.AsNoTracking().AnyAsync(m => m.ClientId == endpoint.ClientId, cancellationToken))
        {
            return ServiceResult.Fail("No Action1 organization is mapped to this client. Map one in Settings, Integrations.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var last = await db.PatchRequests.AsNoTracking()
            .Where(r => r.EndpointId == endpointId && r.Kind == PatchRequestKind.Sync)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (last is { CompletedAt: null })
        {
            return ServiceResult.Ok();
        }

        if (last is not null && now - last.RequestedAt < SyncInterval)
        {
            return ServiceResult.Fail("This endpoint was synced less than a minute ago. Wait a moment and try again.");
        }

        db.PatchRequests.Add(new PatchRequest
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            Kind = PatchRequestKind.Sync,
            RequestedByUserId = caller.UserId,
            RequestedByName = Cut(caller.Name, 200),
            RequestedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Removes the Action1 agent from this endpoint (0.6.0). When Action1 reports the endpoint, the workers remove it from
    /// its organization, which makes Action1 uninstall the agent and drop it from its console; the value is true. When an
    /// agent runs that Action1 does not report, only a signed job can remove it, composed by fleeto-signer; the value is
    /// false.
    /// </summary>
    public async Task<ServiceResult<bool>> RemoveAgentAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<bool>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new { e.Id, e.ClientId, e.Hostname, e.Tier, e.OsPlatform })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult<bool>.NotFound("endpoint");
        }

        if (TierRules.EffectiveTier(endpoint.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) != EndpointTier.Managed)
        {
            return ServiceResult<bool>.Fail("This endpoint is not managed. Switch it to managed to remove the Action1 agent from it.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var state = await db.EndpointPatchStates.AsNoTracking().SingleOrDefaultAsync(s => s.EndpointId == endpointId, cancellationToken);
        if (state is { ExternalEndpointId.Length: > 0, ExternalTenantId.Length: > 0 })
        {
            if (await db.PatchRequests.AsNoTracking().AnyAsync(
                    r => r.EndpointId == endpointId && r.Kind == PatchRequestKind.RemoveAgent && r.CompletedAt == null, cancellationToken))
            {
                return ServiceResult<bool>.Fail("The removal of the Action1 agent is already on its way. Its outcome appears on this tab.");
            }

            var request = new PatchRequest
            {
                Id = Guid.NewGuid(),
                ClientId = endpoint.ClientId,
                EndpointId = endpoint.Id,
                Kind = PatchRequestKind.RemoveAgent,
                ExternalTenantId = state.ExternalTenantId,
                ExternalEndpointId = state.ExternalEndpointId,
                RequestedByUserId = caller.UserId,
                RequestedByName = Cut(caller.Name, 200),
                RequestedAt = now
            };
            db.PatchRequests.Add(request);
            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchAgentRemovalRequested, "Endpoint", endpoint.Id.ToString(),
                endpoint.ClientId, new { endpoint.Hostname, Through = "Action1" }), now));
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<bool>.Ok(true);
        }

        var agentOnEndpoint = await db.InventorySnapshots.AsNoTracking()
            .Where(i => i.EndpointId == endpointId)
            .Select(i => i.Action1AgentId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(agentOnEndpoint))
        {
            return ServiceResult<bool>.Fail("Neither Action1 nor the Fleeto agent reports an Action1 agent on this endpoint, so there is nothing to remove.");
        }

        if (!ScriptLanguages.RunsOn(ScriptLanguage.PowerShell, endpoint.OsPlatform))
        {
            return ServiceResult<bool>.Fail("Fleeto removes the Action1 agent from Windows endpoints only. Remove it on a Linux endpoint from the Action1 console.");
        }

        if (await db.Jobs.AsNoTracking().AnyAsync(j => j.EndpointId == endpointId && j.Type == JobType.Action1AgentRemoval &&
                (j.State == JobState.PendingSignature || j.State == JobState.Queued || j.State == JobState.Running), cancellationToken))
        {
            return ServiceResult<bool>.Fail("A removal of the Action1 agent is already on its way to this endpoint. Its result is on the Jobs tab.");
        }

        AddComposedJob(db, caller, endpoint.Id, endpoint.ClientId, endpoint.Hostname, JobType.Action1AgentRemoval, Action1AgentRemoval.JobName,
            Action1AgentRemoval.Body(), Action1AgentRemoval.TimeoutSeconds, Action1AgentRemoval.MaxOutputBytes, now);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchAgentRemovalRequested, "Endpoint", endpoint.Id.ToString(),
            endpoint.ClientId, new { endpoint.Hostname, Through = "Job" }), now));
        await db.SaveChangesAsync(cancellationToken);
        await _bus.PublishAsync(NotificationChannels.Jobs, endpoint.Id.ToString(), cancellationToken);
        return ServiceResult<bool>.Ok(false);
    }

    /// <summary>One request to sync an endpoint a minute (0.6.0): each costs a handful of calls against the budget of the instance.</summary>
    public static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Compliance over the endpoints the caller may see, optionally of one client or one site. Counts rows of patch state,
    /// so an endpoint without it (no Action1 agent, or not managed) is not counted as compliant.
    /// </summary>
    public async Task<PatchCompliance> GetComplianceAsync(Caller caller, Guid? clientId = null, Guid? siteId = null,
        CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var states = db.EndpointPatchStates.AsNoTracking();
        if (clientId is { } client)
        {
            states = states.Where(p => p.ClientId == client);
        }

        if (siteId is { } site)
        {
            states = states.Where(p => db.Endpoints.Any(e => e.Id == p.EndpointId && e.SiteId == site));
        }

        var counts = await states
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Covered = g.Count(),
                Compliant = g.Count(p => p.MissingCritical == 0 && p.MissingOther == 0),
                MissingCritical = g.Count(p => p.MissingCritical > 0),
                NotPatched = g.Count(p => p.Coverage == PatchCoverage.Inactive)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return counts is null
            ? PatchCompliance.None
            : new PatchCompliance(counts.Covered, counts.Compliant, counts.MissingCritical, counts.NotPatched);
    }

    /// <summary>
    /// Starts a deployment of updates on the given endpoints (0.4.0 step 3). With <paramref name="updateIds"/> only those
    /// updates go, which is allowed on one endpoint at a time because the technician picked them from its own list;
    /// without them every update the product reports as missing goes. The product runs a deployment per tenant, so a
    /// selection that spans clients becomes one deployment per client, sharing a batch id.
    /// <para>
    /// Nothing is sent from here: the rows are the request, and the workers make the call. An endpoint that cannot take
    /// part is skipped with the reason instead of quietly left out.
    /// </para>
    /// </summary>
    public async Task<ServiceResult<DeploymentStartResult>> StartDeploymentAsync(Caller caller, IReadOnlyCollection<Guid> endpointIds,
        IReadOnlyCollection<string>? updateIds = null, bool autoReboot = false, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<DeploymentStartResult>.Forbidden();
        }

        var ids = endpointIds.Distinct().ToList();
        if (ids.Count == 0 || ids.Count > PatchRules.MaxEndpointsPerDeployment)
        {
            return ServiceResult<DeploymentStartResult>.Fail($"Choose between 1 and {PatchRules.MaxEndpointsPerDeployment} endpoints.");
        }

        var chosen = updateIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (chosen.Count > 0 && ids.Count != 1)
        {
            return ServiceResult<DeploymentStartResult>.Fail(
                "Updates can only be chosen for one endpoint. Deploy every missing update, or start the deployment from the endpoint itself.");
        }

        if (chosen.Count > PatchRules.MaxUpdatesPerDeployment)
        {
            return ServiceResult<DeploymentStartResult>.Fail(
                $"Choose at most {PatchRules.MaxUpdatesPerDeployment} updates, or deploy every missing update at once.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoints = await db.Endpoints.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.ClientId, e.Hostname, e.Tier })
            .ToListAsync(cancellationToken);
        if (endpoints.Count == 0)
        {
            return ServiceResult<DeploymentStartResult>.NotFound("endpoint");
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var clientIds = endpoints.Select(e => e.ClientId).Distinct().ToList();
        var tenants = await db.IntegrationMappings.AsNoTracking().Where(m => clientIds.Contains(m.ClientId))
            .ToDictionaryAsync(m => m.ClientId, m => m.ExternalTenantId, cancellationToken);
        var states = await db.EndpointPatchStates.AsNoTracking().Where(p => ids.Contains(p.EndpointId))
            .ToDictionaryAsync(p => p.EndpointId, cancellationToken);

        var scope = chosen.Count > 0 ? PatchDeploymentScope.Specified : PatchDeploymentScope.AllMissing;
        var updates = new List<PatchDeploymentUpdate>();
        if (scope == PatchDeploymentScope.Specified)
        {
            var missing = await db.EndpointMissingUpdates.AsNoTracking()
                .Where(u => u.EndpointId == ids[0] && chosen.Contains(u.ExternalUpdateId))
                .ToListAsync(cancellationToken);
            if (missing.Count != chosen.Count)
            {
                return ServiceResult<DeploymentStartResult>.Fail(
                    "One of the chosen updates is no longer missing on this endpoint. Refresh the Patches tab and choose again.");
            }

            // Without a version the product cannot be told which package to install, and picking the newest one would
            // install something the technician did not choose.
            if (missing.FirstOrDefault(u => string.IsNullOrEmpty(u.Version)) is { } unversioned)
            {
                return ServiceResult<DeploymentStartResult>.Fail(
                    $"Action1 reports no version for {unversioned.Name}, so Fleeto cannot deploy that update by name. Deploy every missing update instead.");
            }

            updates = [.. missing.Select(u => new PatchDeploymentUpdate
            {
                ExternalUpdateId = u.ExternalUpdateId, Name = u.Name, Version = u.Version
            })];
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var batchId = Guid.NewGuid();
        var skipped = new List<JobRunTarget>();
        var deployments = new List<PatchDeployment>();
        foreach (var group in endpoints.GroupBy(e => e.ClientId))
        {
            var targets = new List<PatchDeploymentTarget>();
            foreach (var endpoint in group.OrderBy(e => e.Hostname, StringComparer.OrdinalIgnoreCase))
            {
                states.TryGetValue(endpoint.Id, out var state);
                var problem = TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed
                    ? "Not managed. Switch it to managed to deploy updates."
                    : !tenants.ContainsKey(endpoint.ClientId)
                        ? "This client is not mapped to an Action1 organization. Map it in Settings, Integrations."
                    : state is null
                        ? "Action1 does not report this endpoint. Install the Action1 agent on it first."
                    : state.Coverage == PatchCoverage.Inactive
                        ? "Action1 does not patch this endpoint. Check the Action1 subscription: endpoints above the licensed number become inactive."
                    : scope == PatchDeploymentScope.AllMissing && state.IsCompliant
                        ? "Action1 reports no missing updates for this endpoint."
                        : null;
                if (problem is not null)
                {
                    skipped.Add(new JobRunTarget(endpoint.Id, endpoint.Hostname, problem));
                    continue;
                }

                targets.Add(new PatchDeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    ClientId = endpoint.ClientId,
                    EndpointId = endpoint.Id,
                    ExternalEndpointId = state!.ExternalEndpointId,
                    Hostname = endpoint.Hostname,
                    State = PatchDeploymentTargetState.Pending,
                    UpdatedAt = now
                });
            }

            if (targets.Count == 0)
            {
                continue;
            }

            var deployment = new PatchDeployment
            {
                Id = Guid.NewGuid(),
                ClientId = group.Key,
                BatchId = batchId,
                ExternalTenantId = tenants[group.Key],
                Scope = scope,
                AutoReboot = autoReboot,
                State = PatchDeploymentState.Requested,
                RequestedByUserId = caller.UserId,
                RequestedByName = caller.Name.Length > 200 ? caller.Name[..200] : caller.Name,
                RequestedAt = now
            };
            deployment.Targets.AddRange(targets);
            foreach (var update in updates)
            {
                deployment.Updates.Add(new PatchDeploymentUpdate
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = deployment.Id,
                    ClientId = group.Key,
                    ExternalUpdateId = update.ExternalUpdateId,
                    Name = update.Name,
                    Version = update.Version
                });
            }

            deployments.Add(deployment);
            db.PatchDeployments.Add(deployment);
            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchDeploymentStarted, "PatchDeployment", deployment.Id.ToString(),
                group.Key, new
                {
                    BatchId = batchId,
                    Scope = scope.ToString(),
                    deployment.AutoReboot,
                    Endpoints = targets.Select(t => new { t.EndpointId, t.Hostname }),
                    Updates = deployment.Updates.Select(u => new { u.ExternalUpdateId, u.Name, u.Version })
                }), now));
        }

        foreach (var unknown in ids.Except(endpoints.Select(e => e.Id)))
        {
            skipped.Add(new JobRunTarget(unknown, "Unknown endpoint", "The endpoint no longer exists."));
        }

        if (deployments.Count == 0)
        {
            return ServiceResult<DeploymentStartResult>.Fail(skipped.Count == 1
                ? $"Nothing was deployed: {skipped[0].Problem}"
                : $"Nothing was deployed. None of the endpoints can take a deployment, for example {skipped[0].Hostname}: {skipped[0].Problem}");
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var deployment in deployments)
        {
            await _bus.PublishAsync(NotificationChannels.PatchDeployments, deployment.Id.ToString(), cancellationToken);
        }

        return ServiceResult<DeploymentStartResult>.Ok(new DeploymentStartResult(batchId, [.. deployments.Select(ToView)], skipped));
    }

    /// <summary>
    /// Starts the job that installs the Action1 agent on one Windows endpoint (0.4.0 step 3), so patch management can
    /// cover it at all. Web only names the endpoint: fleeto-signer composes and signs what runs, from the installer link
    /// of the client's organization.
    /// </summary>
    public async Task<ServiceResult> InstallAgentAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new { e.Id, e.ClientId, e.Hostname, e.Tier, e.OsPlatform })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult.NotFound("endpoint");
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        if (TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed)
        {
            return ServiceResult.Fail("This endpoint is not managed. Switch it to managed to install the Action1 agent on it.");
        }

        if (!ScriptLanguages.RunsOn(ScriptLanguage.PowerShell, endpoint.OsPlatform))
        {
            return ServiceResult.Fail("Fleeto installs the Action1 agent on Windows endpoints only. Install it on a Linux endpoint from the Action1 console.");
        }

        var installerUrl = await db.IntegrationMappings.AsNoTracking()
            .Where(m => m.ClientId == endpoint.ClientId)
            .Select(m => m.AgentInstallerUrl)
            .FirstOrDefaultAsync(cancellationToken);
        if (!Action1AgentInstall.IsValidInstallerUrl(installerUrl))
        {
            return ServiceResult.Fail(
                "There is no Action1 agent installer link for this client. Add it in Settings, Integrations, next to the Action1 organization of this client.");
        }

        var reported = await db.InventorySnapshots.AsNoTracking()
            .Where(i => i.EndpointId == endpointId)
            .Select(i => i.Action1AgentId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrEmpty(reported))
        {
            return ServiceResult.Fail(
                "The Fleeto agent reports that an Action1 agent is already installed on this endpoint. Sync the patch state, or remove that agent first.");
        }

        var open = await db.Jobs.AsNoTracking().AnyAsync(j => j.EndpointId == endpointId && j.Type == JobType.Action1Agent &&
            (j.State == JobState.PendingSignature || j.State == JobState.Queued || j.State == JobState.Running), cancellationToken);
        if (open)
        {
            return ServiceResult.Fail("An installation of the Action1 agent is already on its way to this endpoint. Its result is on the Jobs tab.");
        }

        // The body the link gives right now. The signer composes it again and refuses when the link has changed since, so
        // what runs is what the technician asked for and nothing else.
        AddComposedJob(db, caller, endpoint.Id, endpoint.ClientId, endpoint.Hostname, JobType.Action1Agent, Action1AgentInstall.JobName,
            Action1AgentInstall.Body(installerUrl!), Action1AgentInstall.TimeoutSeconds, Action1AgentInstall.MaxOutputBytes,
            _time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);
        await _bus.PublishAsync(NotificationChannels.Jobs, endpoint.Id.ToString(), cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Writes a job whose body fleeto-signer composes itself (the Action1 agent install and removal), with its signing
    /// request and audit entry. Only the hash of the body is stored: the signer writes the body again and refuses a
    /// different one.
    /// </summary>
    private static void AddComposedJob(FleetoDbContext db, Caller caller, Guid endpointId, Guid clientId, string hostname, JobType type, string name,
        string body, int timeoutSeconds, long maxOutputBytes, DateTime now)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            EndpointId = endpointId,
            BatchId = Guid.NewGuid(),
            Type = type,
            ScriptName = name,
            Language = ScriptLanguage.PowerShell,
            ScriptSha256 = ScriptLanguages.Sha256(body),
            TimeoutSeconds = timeoutSeconds,
            MaxOutputBytes = maxOutputBytes,
            RunAs = JobRunAs.Service,
            CreatedAt = now,
            ValidUntil = now + ScriptRules.DefaultValidity,
            InitiatedByUserId = caller.UserId,
            InitiatedByName = Cut(caller.Name, 200)
        };
        db.Jobs.Add(job);
        db.SigningRequests.Add(new SigningRequest
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Kind = SigningRequestKind.Job,
            SubjectId = job.Id,
            Payload = [],
            RequestedBy = "web:" + (caller.IpAddress ?? "unknown"),
            CreatedAt = now
        });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.JobCreated, "Job", job.Id.ToString(), clientId, new
        {
            Hostname = hostname,
            EndpointId = endpointId,
            Type = type.ToString(),
            job.ScriptName,
            job.ValidUntil
        }), now));
    }

    /// <summary>The deployments that touched one endpoint, newest first. Only what the caller may see.</summary>
    public async Task<IReadOnlyList<DeploymentView>> ListDeploymentsAsync(Caller caller, Guid endpointId, int limit = 10,
        CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var deployments = await db.PatchDeployments.AsNoTracking()
            .Include(d => d.Targets)
            .Include(d => d.Updates)
            .Where(d => d.Targets.Any(t => t.EndpointId == endpointId))
            .OrderByDescending(d => d.RequestedAt)
            .Take(Math.Clamp(limit, 1, 50))
            .ToListAsync(cancellationToken);
        return [.. deployments.Select(ToView)];
    }

    /// <summary>
    /// What the product logged for one endpoint of a deployment (0.6.0), newest first as Action1 shows its "Automation
    /// History". Read by the workers; null when the deployment or its endpoint is not in the caller's scope.
    /// </summary>
    public async Task<DeploymentHistoryView?> GetHistoryAsync(Caller caller, Guid deploymentId, Guid endpointId,
        CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var target = await db.PatchDeploymentTargets.AsNoTracking()
            .Where(t => t.DeploymentId == deploymentId && t.EndpointId == endpointId)
            .Select(t => new
            {
                t.Id, t.Hostname, t.State, t.StepsReadAt,
                RequestedAt = db.PatchDeployments.Where(d => d.Id == t.DeploymentId).Select(d => d.RequestedAt).First()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return null;
        }

        var steps = await db.PatchDeploymentSteps.AsNoTracking()
            .Where(s => s.TargetId == target.Id)
            .OrderByDescending(s => s.Time).ThenByDescending(s => s.Position)
            .Select(s => new DeploymentStepView(s.Time, s.Operation, s.Status, s.Details))
            .ToListAsync(cancellationToken);
        return new DeploymentHistoryView(deploymentId, endpointId, target.Hostname, target.State, target.RequestedAt, target.StepsReadAt, steps);
    }

    /// <summary>The deployments of one run, for the window that shows how it went.</summary>
    public async Task<IReadOnlyList<DeploymentView>> ListBatchAsync(Caller caller, Guid batchId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var deployments = await db.PatchDeployments.AsNoTracking()
            .Include(d => d.Targets)
            .Include(d => d.Updates)
            .Where(d => d.BatchId == batchId)
            .OrderBy(d => d.RequestedAt)
            .ToListAsync(cancellationToken);
        return [.. deployments.Select(ToView)];
    }

    private static DeploymentView ToView(PatchDeployment deployment) => new(
        deployment.Id, deployment.BatchId, deployment.ClientId, deployment.Scope, deployment.AutoReboot, deployment.State,
        deployment.StatusMessage, deployment.RequestedByName, deployment.RequestedAt, deployment.CompletedAt,
        [.. deployment.Updates
            .Select(u => u.Version.Length == 0 ? u.Name : $"{u.Name} {u.Version}")
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)],
        [.. deployment.Targets
            .Select(t => new DeploymentTargetView(t.EndpointId, t.Hostname, t.State, t.Message))
            .OrderBy(t => t.Hostname, StringComparer.OrdinalIgnoreCase)],
        deployment.PatchPolicyId);

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
