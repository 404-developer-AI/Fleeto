using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations.Action1;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Turns a run of the automation of a patch policy into a scheduled deployment (0.6.0), so the Patches tab shows whether
/// the patch round of a policy happened and how it went, with the same states and Action1 history as a deployment a
/// technician started. <see cref="PatchDeploymentService"/> follows it from there like any other deployment.
/// <para>
/// Action1 names the last run of an automation in <c>last_instance</c>. A schedule runs at most once a day, and Fleeto reads
/// every automation of its own at least once an hour, so reading that one field finds every run without listing them.
/// </para>
/// </summary>
internal static class ScheduledDeployments
{
    /// <summary>
    /// Records what Action1 says about one automation of Fleeto: its next run, and its last run as a scheduled deployment
    /// when that is new. Returns the deployment when one was created; the caller saves and wakes the deployment service.
    /// </summary>
    public static async Task<PatchDeployment?> RecordAsync(FleetoDbContext db, IntegrationAutomation row, Action1AutomationInfo info, DateTime now,
        CancellationToken cancellationToken)
    {
        row.NextRunAt = info.NextRunAt;
        row.ReadAt = now;
        if (info.LastInstanceId.Length == 0 ||
            db.PatchDeployments.Local.Any(d => d.ClientId == row.ClientId && d.ExternalDeploymentId == info.LastInstanceId) ||
            await db.PatchDeployments.AnyAsync(d => d.ClientId == row.ClientId && d.ExternalDeploymentId == info.LastInstanceId, cancellationToken))
        {
            return null;
        }

        // The endpoints Fleeto put in the automation. One that Action1 lists in the run but Fleeto does not know is not
        // Fleeto's to show; one that left the policy since still ran, and its result is read like the others.
        var targets = await (from s in db.EndpointPatchStates
                             join e in db.Endpoints on s.EndpointId equals e.Id
                             where s.AutomationId == row.Id && s.ExternalEndpointId != ""
                             select new { e.Id, e.ClientId, e.Hostname, s.ExternalEndpointId })
            .ToListAsync(cancellationToken);
        if (targets.Count == 0)
        {
            return null;
        }

        var policy = await db.PatchPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == row.PatchPolicyId, cancellationToken);
        // A policy in UTC runs when Action1 says; for one in the endpoint's own time Action1 names no time zone, so the
        // moment Fleeto found the run is the honest start.
        var startedAt = policy is { EndpointLocalTime: false } && info.LastRunAt is { } last && last <= now ? last : now;
        var deployment = new PatchDeployment
        {
            Id = Guid.NewGuid(),
            ClientId = row.ClientId,
            BatchId = Guid.NewGuid(),
            ExternalTenantId = row.ExternalTenantId,
            ExternalDeploymentId = info.LastInstanceId,
            ExternalAutomationId = row.ExternalAutomationId ?? string.Empty,
            PatchPolicyId = row.PatchPolicyId,
            Scope = PatchDeploymentScope.PatchPolicy,
            AutoReboot = policy?.AutoReboot ?? false,
            State = PatchDeploymentState.Running,
            RequestedByUserId = Guid.Empty,
            RequestedByName = Cut(policy?.Name ?? "A patch policy that no longer exists", 200),
            RequestedAt = startedAt,
            StartedAt = startedAt,
            FollowUntil = startedAt + FollowFor(policy)
        };
        foreach (var target in targets)
        {
            deployment.Targets.Add(new PatchDeploymentTarget
            {
                Id = Guid.NewGuid(),
                DeploymentId = deployment.Id,
                ClientId = target.ClientId,
                EndpointId = target.Id,
                ExternalEndpointId = target.ExternalEndpointId,
                Hostname = Cut(target.Hostname, 255),
                State = PatchDeploymentTargetState.Pending,
                UpdatedAt = now
            });
        }

        db.PatchDeployments.Add(deployment);
        return deployment;
    }

    /// <summary>
    /// How long a scheduled deployment is followed: as long as Action1 keeps retrying endpoints it could not reach, plus
    /// an hour for the last answers, and never shorter than a deployment a technician started.
    /// </summary>
    public static TimeSpan FollowFor(PatchPolicy? policy)
    {
        var retry = TimeSpan.FromHours((policy?.RetryHours ?? 24) + 1);
        return retry > PatchRules.FollowFor ? retry : PatchRules.FollowFor;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
