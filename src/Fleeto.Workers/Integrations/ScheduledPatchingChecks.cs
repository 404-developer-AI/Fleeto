using System.Globalization;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Alerts;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Gives the scheduled patching checks their results (0.6.0): the days since a scheduled deployment of the endpoint's patch
/// policy last succeeded on it, so a patch round that fails, or that Action1 never runs, raises an alert like any check.
/// <para>
/// Only an endpoint that an automation of Fleeto targets gets a result: without a patch policy nothing is scheduled, and
/// there is nothing to be late. Until a scheduled deployment succeeded, the days count from when the automation of the
/// policy first targeted the endpoint (<see cref="EndpointPatchState.ScheduledSince"/>), so a policy that never ran on
/// it is noticed too.
/// </para>
/// </summary>
public static class ScheduledPatchingChecks
{
    public const string ResolvedReasonUnscheduled = "No patch policy schedules deployments on this endpoint any more.";

    /// <summary>
    /// Writes a result for every scheduled patching check that applies to one of <paramref name="endpointIds"/> with an
    /// automation, and forgets the checks of the ones without. Returns the endpoints that got a result and the alert
    /// transitions of the forgotten checks. The caller saves.
    /// </summary>
    public static async Task<(IReadOnlyList<Guid> Written, IReadOnlyList<AlertTransition> Transitions)> EvaluateAsync(FleetoDbContext db,
        IReadOnlyCollection<Guid> endpointIds, DateTime now, CancellationToken cancellationToken)
    {
        if (endpointIds.Count == 0 || !await db.CheckDefinitions.AnyAsync(d => d.Type == CheckType.ScheduledPatching, cancellationToken))
        {
            return ([], []);
        }

        var ids = endpointIds.ToList();
        var scheduled = await db.EndpointPatchStates.AsNoTracking()
            .Where(s => ids.Contains(s.EndpointId) && s.AutomationId != null && s.ScheduledSince != null)
            .ToDictionaryAsync(s => s.EndpointId, s => s.ScheduledSince!.Value, cancellationToken);
        var transitions = await MissingUpdateChecks.ForgetChecksAsync(db, CheckType.ScheduledPatching,
            [.. ids.Where(id => !scheduled.ContainsKey(id))], now, ResolvedReasonUnscheduled, cancellationToken);
        if (scheduled.Count == 0)
        {
            return ([], transitions);
        }

        var scheduledIds = scheduled.Keys.ToList();
        var lastSuccess = await (from t in db.PatchDeploymentTargets
                                 join d in db.PatchDeployments on t.DeploymentId equals d.Id
                                 where scheduledIds.Contains(t.EndpointId) && d.Scope == PatchDeploymentScope.PatchPolicy &&
                                       t.State == PatchDeploymentTargetState.Succeeded
                                 group t by t.EndpointId into g
                                 select new { EndpointId = g.Key, At = g.Max(t => t.UpdatedAt) })
            .ToDictionaryAsync(x => x.EndpointId, x => x.At, cancellationToken);
        var endpoints = await db.Endpoints.AsNoTracking()
            .Where(e => scheduledIds.Contains(e.Id) && e.Tier == EndpointTier.Managed)
            .ToListAsync(cancellationToken);

        var written = new List<Guid>();
        foreach (var endpoint in endpoints)
        {
            var since = scheduled[endpoint.Id];
            var succeeded = lastSuccess.TryGetValue(endpoint.Id, out var at) && at >= since ? at : (DateTime?)null;
            var from = succeeded ?? since;
            var days = Math.Max(0, Math.Floor((now - from).TotalDays));
            var detail = succeeded is { } ok
                ? $"A scheduled deployment last succeeded on {ok.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}."
                : $"No scheduled deployment succeeded since the patch policy started on this endpoint on {since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";

            var checks = (await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: false, cancellationToken))
                .Where(c => c.Type == CheckType.ScheduledPatching)
                .ToList();
            foreach (var check in checks)
            {
                db.CheckResults.Add(new CheckResult
                {
                    Time = now,
                    AgentTime = now,
                    ClientId = endpoint.ClientId,
                    EndpointId = endpoint.Id,
                    CheckDefinitionId = check.Id,
                    Target = string.Empty,
                    Value = days,
                    Detail = detail
                });
            }

            if (checks.Count > 0)
            {
                written.Add(endpoint.Id);
            }
        }

        return (written, transitions);
    }
}
