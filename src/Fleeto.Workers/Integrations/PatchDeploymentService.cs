using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Hands the deployments a technician started to the patch management product and follows them (0.4.0 step 3).
///
/// Web writes what was asked for and can do nothing else: it has no outbound access, and no rights to change a deployment
/// row afterwards. Everything an endpoint state says therefore comes from the product, through this service. A deployment
/// the product refuses is failed with its own message; one it accepts is polled every
/// <see cref="PatchRules.PollInterval"/> until every endpoint has an answer, and abandoned after
/// <see cref="PatchRules.FollowFor"/> rather than kept open forever.
/// </summary>
public sealed class PatchDeploymentService : WorkerLoop
{
    /// <summary>
    /// Deployments handled in one pass. Action1 counts every call against one budget for the whole instance, so a pile of
    /// open deployments waits its turn instead of spending what the patch sync needs.
    /// </summary>
    public const int MaxCallsPerPass = 10;

    /// <summary>
    /// Endpoint histories read in one pass (0.6.0). A history is read when the state of its endpoint changed and, while it
    /// runs, every <see cref="StepsRefresh"/>; the rest waits for the next pass rather than spending the budget of the sync.
    /// </summary>
    public const int MaxStepReadsPerPass = 10;

    public static readonly TimeSpan StepsRefresh = TimeSpan.FromMinutes(5);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly Action1ClientFactory _clients;
    private readonly LicenseService _licenses;
    private IDisposable? _subscription;

    public PatchDeploymentService(IFleetoDbContextFactory dbFactory, INotificationBus bus, Action1ClientFactory clients,
        LicenseService licenses, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<PatchDeploymentService> logger)
        : base("patch-deployments", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _clients = clients;
        _licenses = licenses;
    }

    protected override TimeSpan Interval => PatchRules.PollInterval;

    protected override TimeSpan MaxRunDuration => TimeSpan.FromMinutes(15);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.PatchDeployments, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override Task<bool> RunOnceAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    /// <summary>One pass over every open deployment. Returns true when work is left that this pass could not do.</summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var open = await db.PatchDeployments
            .Include(d => d.Targets)
            .Include(d => d.Updates)
            .Where(d => d.State == PatchDeploymentState.Requested || d.State == PatchDeploymentState.Running)
            .OrderBy(d => d.RequestedAt)
            .ToListAsync(cancellationToken);
        if (open.Count == 0 && !await StepsDue(db, Time.GetUtcNow().UtcDateTime).AnyAsync(cancellationToken))
        {
            return false;
        }

        var now = Time.GetUtcNow().UtcDateTime;
        var integration = await db.Integrations.Include(i => i.Mappings)
            .SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        var license = await _licenses.GetStatusAsync(cancellationToken);

        // Without a working integration or the managed tier nothing can be deployed, and leaving the deployment open
        // would say it is still running. The technician is told why instead.
        var blocked = integration is null || !integration.Enabled
            ? "Patch management is not connected. Check Settings, Integrations."
            : !license.AllowsManaged
                ? "The license of this instance no longer allows managed endpoints, so nothing was deployed."
                : null;
        if (blocked is not null)
        {
            foreach (var deployment in open)
            {
                End(deployment, PatchDeploymentState.Failed, blocked, now);
                Audit(db, deployment, AuditActions.PatchDeploymentEnded, now);
            }

            await SaveAsync(db, open, cancellationToken);
            return false;
        }

        using var client = _clients.TryCreate(integration!);
        if (client is null)
        {
            // The credentials cannot be read. Retrying every minute does not help, but failing a deployment an admin can
            // still rescue by entering them again would be premature; it waits until Fleeto gives up following it.
            Logger.LogWarning("The Action1 credentials could not be read; {Count} deployment(s) wait", open.Count);
            foreach (var deployment in open.Where(d => now - d.RequestedAt > PatchRules.FollowFor))
            {
                End(deployment, PatchDeploymentState.Failed,
                    "The Action1 credentials could not be read, so the deployment never started. Enter them again in Settings, Integrations.", now);
                Audit(db, deployment, AuditActions.PatchDeploymentEnded, now);
            }

            await SaveAsync(db, open, cancellationToken);
            return false;
        }

        var calls = 0;
        var more = false;
        var finished = false;
        foreach (var deployment in open)
        {
            var mapping = integration!.Mappings.FirstOrDefault(m => m.ClientId == deployment.ClientId);
            if (mapping is null || mapping.ExternalTenantId != deployment.ExternalTenantId)
            {
                End(deployment, PatchDeploymentState.Failed,
                    "The client is no longer mapped to the Action1 organization of this deployment. Check the mapping in Settings, Integrations.",
                    now);
                Audit(db, deployment, AuditActions.PatchDeploymentEnded, now);
                finished = true;
                continue;
            }

            if (deployment.State == PatchDeploymentState.Running && deployment.PolledAt is { } polled && now - polled < PatchRules.PollInterval)
            {
                continue;
            }

            if (calls++ >= MaxCallsPerPass)
            {
                more = true;
                break;
            }

            var ended = deployment.State == PatchDeploymentState.Requested
                ? await StartAsync(db, client, deployment, now, cancellationToken)
                : await PollAsync(db, client, deployment, now, cancellationToken);
            finished |= ended;
        }

        // A deployment that ended changes what its endpoints are missing, so the patch state is read again instead of
        // showing for up to four hours what a technician has just installed.
        if (finished)
        {
            integration!.PatchSyncedAt = null;
        }

        await SaveAsync(db, open, cancellationToken);
        more |= await ReadStepsAsync(db, client, now, cancellationToken);
        return more;
    }

    /// <summary>
    /// The endpoints of deployments of the last two days whose history is worth reading: never read since the state last
    /// changed (a change clears <see cref="PatchDeploymentTarget.StepsReadAt"/>), or running and read longer than
    /// <see cref="StepsRefresh"/> ago. An endpoint that is still pending has nothing to tell yet.
    /// </summary>
    private static IQueryable<PatchDeploymentTarget> StepsDue(FleetoDbContext db, DateTime now)
    {
        var since = now - 2 * PatchRules.FollowFor;
        var stale = now - StepsRefresh;
        return db.PatchDeploymentTargets
            .Where(t => t.State != PatchDeploymentTargetState.Pending && t.ExternalEndpointId != string.Empty)
            .Where(t => db.PatchDeployments.Any(d => d.Id == t.DeploymentId && d.ExternalDeploymentId != string.Empty && d.RequestedAt > since))
            .Where(t => t.StepsReadAt == null || (t.State == PatchDeploymentTargetState.Running && t.StepsReadAt < stale));
    }

    /// <summary>
    /// Reads what Action1 logged per endpoint (0.6.0), the lines of its "Automation History", and replaces what Fleeto kept.
    /// Runs after the states of this pass are saved, so it sees them. Returns true when more histories wait.
    /// </summary>
    private async Task<bool> ReadStepsAsync(FleetoDbContext db, Action1Client client, DateTime now, CancellationToken cancellationToken)
    {
        var due = await StepsDue(db, now)
            .OrderByDescending(t => t.UpdatedAt)
            .Take(MaxStepReadsPerPass + 1)
            .Select(t => new
            {
                Target = t,
                Deployment = db.PatchDeployments.Where(d => d.Id == t.DeploymentId)
                    .Select(d => new { d.ExternalTenantId, d.ExternalDeploymentId }).First()
            })
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return false;
        }

        var touched = new HashSet<Guid>();
        foreach (var item in due.Take(MaxStepReadsPerPass))
        {
            var target = item.Target;
            var result = await client.ListDeploymentStepsAsync(item.Deployment.ExternalTenantId, item.Deployment.ExternalDeploymentId,
                target.ExternalEndpointId, cancellationToken);
            if (!result.Ok)
            {
                Logger.LogInformation("The Action1 history of endpoint {EndpointId} in deployment {DeploymentId} could not be read: {Message}",
                    target.EndpointId, target.DeploymentId, result.Message);
                if (result.Permanent)
                {
                    // Asking again changes nothing; the next change of state tries once more.
                    target.StepsReadAt = now;
                }

                continue;
            }

            // Old lines and new lines swap in one transaction, so a reader never sees half a history.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.PatchDeploymentSteps.Where(s => s.TargetId == target.Id).ExecuteDeleteAsync(cancellationToken);
            var position = 0;
            foreach (var step in (result.Value ?? []).Take(500))
            {
                db.PatchDeploymentSteps.Add(new PatchDeploymentStep
                {
                    Id = Guid.NewGuid(),
                    TargetId = target.Id,
                    ClientId = target.ClientId,
                    Position = position++,
                    Time = step.Time,
                    Operation = step.Operation,
                    Status = step.Status,
                    Details = step.Details
                });
            }

            target.StepsReadAt = now;
            touched.Add(target.DeploymentId);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var deploymentId in touched)
        {
            await _bus.PublishAsync(NotificationChannels.PatchDeployments, deploymentId.ToString(), cancellationToken);
        }

        return due.Count > MaxStepReadsPerPass;
    }

    /// <summary>Hands one deployment to the product. Returns true when it ended here, because the product refused it.</summary>
    private async Task<bool> StartAsync(FleetoDbContext db, Action1Client client, PatchDeployment deployment, DateTime now,
        CancellationToken cancellationToken)
    {
        var endpointIds = deployment.Targets.Select(t => t.ExternalEndpointId).Where(id => id.Length > 0).Distinct().ToList();
        if (endpointIds.Count == 0)
        {
            End(deployment, PatchDeploymentState.Failed, "None of the endpoints is known to Action1 any more, so nothing was deployed.", now);
            Audit(db, deployment, AuditActions.PatchDeploymentEnded, now);
            return true;
        }

        var packages = deployment.Scope == PatchDeploymentScope.Specified
            ? deployment.Updates.Select(u => new Action1Package(u.ExternalUpdateId, u.Version)).ToList()
            : [];
        var summary = deployment.Scope == PatchDeploymentScope.Specified
            ? $"{Count(deployment.Updates.Count, "update")} on {Count(endpointIds.Count, "endpoint")}"
            : $"Every missing update on {Count(endpointIds.Count, "endpoint")}";
        var request = new Action1Deployment(
            $"Fleeto {deployment.RequestedAt:yyyy-MM-dd HH:mm} UTC",
            summary,
            endpointIds,
            packages,
            deployment.AutoReboot,
            PatchRules.RebootMessage,
            PatchRules.RebootTimeoutSeconds,
            PatchRules.RetryMinutes);

        var result = await client.StartDeploymentAsync(deployment.ExternalTenantId, request, cancellationToken);
        if (result.Ok && result.Value is { Length: > 0 } externalId)
        {
            deployment.ExternalDeploymentId = Cut(externalId, 200);
            deployment.State = PatchDeploymentState.Running;
            deployment.StartedAt = now;
            deployment.PolledAt = now;
            deployment.StatusMessage = null;
            Audit(db, deployment, AuditActions.PatchDeploymentAccepted, now);
            Logger.LogInformation("Deployment {DeploymentId} accepted by Action1 as {ExternalId} for {Endpoints} endpoint(s)",
                deployment.Id, deployment.ExternalDeploymentId, endpointIds.Count);
            return false;
        }

        if (result.Permanent || now - deployment.RequestedAt > PatchRules.FollowFor)
        {
            End(deployment, PatchDeploymentState.Failed, result.Message, now);
            Audit(db, deployment, AuditActions.PatchDeploymentEnded, now);
            return true;
        }

        // Temporary: the product may take it in the next pass, and the technician sees why it has not started yet.
        deployment.StatusMessage = Cut(result.Message, 1000);
        deployment.PolledAt = now;
        return false;
    }

    /// <summary>Reads what the product says about a running deployment. Returns true when it ended here.</summary>
    private async Task<bool> PollAsync(FleetoDbContext db, Action1Client client, PatchDeployment deployment, DateTime now,
        CancellationToken cancellationToken)
    {
        deployment.PolledAt = now;
        var result = await client.ListDeploymentResultsAsync(deployment.ExternalTenantId, deployment.ExternalDeploymentId, cancellationToken);
        if (!result.Ok)
        {
            deployment.StatusMessage = Cut(result.Message, 1000);
            if (!result.Permanent && now - (deployment.StartedAt ?? deployment.RequestedAt) <= PatchRules.FollowFor)
            {
                return false;
            }

            // Either the product will never answer about this deployment, or it has had long enough. What each endpoint
            // did is unknown from here, and the rows say exactly that.
            End(deployment, PatchDeploymentState.Abandoned,
                $"Fleeto stopped following this deployment: {result.Message} Check it in the Action1 console.", now);
            Audit(db, deployment, AuditActions.PatchDeploymentEnded, now);
            return true;
        }

        deployment.StatusMessage = null;
        var byEndpoint = (result.Value ?? [])
            .GroupBy(r => r.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var target in deployment.Targets)
        {
            if (!byEndpoint.TryGetValue(target.ExternalEndpointId, out var reported) || reported.State == target.State)
            {
                continue;
            }

            if (reported.State == PatchDeploymentTargetState.Unknown)
            {
                Logger.LogInformation("Action1 reports status {Status} for endpoint {EndpointId}, which Fleeto does not know",
                    reported.Status, target.EndpointId);
            }

            target.State = reported.State;
            target.Message = reported.State is PatchDeploymentTargetState.Failed or PatchDeploymentTargetState.Unknown
                ? Cut(reported.Status, 1000)
                : null;
            target.UpdatedAt = now;
            target.StepsReadAt = null;
        }

        if (deployment.Targets.All(t => t.State is PatchDeploymentTargetState.Succeeded or PatchDeploymentTargetState.Failed))
        {
            End(deployment, PatchDeploymentState.Completed, null, now);
            Audit(db, deployment, AuditActions.PatchDeploymentEnded, now);
            return true;
        }

        if (now - (deployment.StartedAt ?? deployment.RequestedAt) > PatchRules.FollowFor)
        {
            End(deployment, PatchDeploymentState.Abandoned,
                "Action1 did not finish this deployment within a day, so Fleeto stopped following it. Check it in the Action1 console.", now);
            Audit(db, deployment, AuditActions.PatchDeploymentEnded, now);
            return true;
        }

        return false;
    }

    /// <summary>Closes a deployment and gives every endpoint without an answer the only honest state there is.</summary>
    private static void End(PatchDeployment deployment, PatchDeploymentState state, string? message, DateTime now)
    {
        deployment.State = state;
        deployment.StatusMessage = message is null ? null : Cut(message, 1000);
        deployment.CompletedAt = now;
        deployment.PolledAt = now;
        foreach (var target in deployment.Targets.Where(t => t.State is PatchDeploymentTargetState.Pending or PatchDeploymentTargetState.Running))
        {
            target.State = state == PatchDeploymentState.Failed ? PatchDeploymentTargetState.Failed : PatchDeploymentTargetState.Unknown;
            target.Message = message is null ? null : Cut(message, 1000);
            target.UpdatedAt = now;
            target.StepsReadAt = null;
        }
    }

    private static void Audit(FleetoDbContext db, PatchDeployment deployment, string action, DateTime now) =>
        db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(action, "PatchDeployment", deployment.Id.ToString(), deployment.ClientId,
            AuditActorType.System, "fleeto-workers", "fleeto-workers", new
            {
                State = deployment.State.ToString(),
                deployment.ExternalDeploymentId,
                deployment.StatusMessage,
                Endpoints = deployment.Targets.Count,
                Succeeded = deployment.Targets.Count(t => t.State == PatchDeploymentTargetState.Succeeded),
                Failed = deployment.Targets.Count(t => t.State == PatchDeploymentTargetState.Failed),
                RequestedBy = deployment.RequestedByName
            }), now));

    private async Task SaveAsync(FleetoDbContext db, IReadOnlyList<PatchDeployment> touched, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        foreach (var deployment in touched)
        {
            await _bus.PublishAsync(NotificationChannels.PatchDeployments, deployment.Id.ToString(), cancellationToken);
        }
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
