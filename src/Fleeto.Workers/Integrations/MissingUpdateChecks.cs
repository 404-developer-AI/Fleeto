using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Alerts;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Gives the missing updates checks their results after a patch sync (0.6.0). Fleeto evaluates these checks itself from the
/// patch state Action1 reported, so the workers write a result as the gateway does for an agent, and check evaluation turns
/// it into a state and an alert exactly like any other: thresholds, failures before alert, maintenance, acknowledge and hold.
/// <para>
/// A result is written only when Fleeto knows what the endpoint misses: a compliant endpoint misses nothing (value 0), and
/// an endpoint that misses something counts only once its missing updates were read in detail, never on the counts alone.
/// </para>
/// </summary>
public static class MissingUpdateChecks
{
    public const string ResolvedReasonGone = "The endpoint is no longer in patch management.";

    /// <summary>
    /// Writes a result for every missing updates check that applies to one of <paramref name="endpointIds"/>. Returns the
    /// endpoints that got one, so check evaluation can be woken for them. The caller saves.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> WriteResultsAsync(FleetoDbContext db, IReadOnlyCollection<Guid> endpointIds, DateTime now,
        CancellationToken cancellationToken)
    {
        if (endpointIds.Count == 0 ||
            !await db.CheckDefinitions.AnyAsync(d => d.Type == CheckType.MissingUpdates && d.Enabled, cancellationToken))
        {
            return [];
        }

        var ids = endpointIds.ToList();
        var endpoints = await db.Endpoints.AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.Tier == EndpointTier.Managed)
            .ToListAsync(cancellationToken);
        var states = await db.EndpointPatchStates.AsNoTracking()
            .Where(s => ids.Contains(s.EndpointId))
            .ToDictionaryAsync(s => s.EndpointId, cancellationToken);
        var missing = (await db.EndpointMissingUpdates.AsNoTracking()
                .Where(u => ids.Contains(u.EndpointId))
                .Select(u => new { u.EndpointId, u.Name, u.KbNumber, u.Severity, u.ReleaseDate, u.ApprovalStatus })
                .ToListAsync(cancellationToken))
            .ToLookup(u => u.EndpointId, u => new MissingUpdateAge.Update(u.Name, u.KbNumber, u.Severity, u.ReleaseDate, u.ApprovalStatus));

        var today = DateOnly.FromDateTime(now);
        var written = new List<Guid>();
        foreach (var endpoint in endpoints)
        {
            if (!states.TryGetValue(endpoint.Id, out var state) || (!state.IsCompliant && !missing[endpoint.Id].Any()))
            {
                continue;
            }

            var checks = (await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: false, cancellationToken))
                .Where(c => c.Type == CheckType.MissingUpdates)
                .ToList();
            foreach (var check in checks)
            {
                var parameters = CheckParameters.Parse(check.Definition.ParametersJson);
                var minimum = CheckCatalog.ParameterOrDefault(CheckType.MissingUpdates, parameters, "severity");
                double? threshold = (check.WarningThreshold, check.CriticalThreshold) switch
                {
                    ({ } warning, { } critical) => Math.Min(warning, critical),
                    (var warning, var critical) => warning ?? critical
                };
                var result = MissingUpdateAge.Evaluate(missing[endpoint.Id], minimum, today, threshold);
                db.CheckResults.Add(new CheckResult
                {
                    Time = now,
                    AgentTime = now,
                    ClientId = endpoint.ClientId,
                    EndpointId = endpoint.Id,
                    CheckDefinitionId = check.Id,
                    Target = string.Empty,
                    Value = result.Value,
                    Detail = result.Detail.Length <= 1000 ? result.Detail : result.Detail[..1000]
                });
            }

            if (checks.Count > 0)
            {
                written.Add(endpoint.Id);
            }
        }

        return written;
    }

    /// <summary>
    /// An endpoint Action1 no longer reports has no missing updates Fleeto knows about: its missing updates checks lose
    /// their state and their open alerts resolve, instead of keeping a value from a patch state that is gone. The caller
    /// saves and sends the returned transitions.
    /// </summary>
    public static async Task<IReadOnlyList<AlertTransition>> ForgetAsync(FleetoDbContext db, IReadOnlyCollection<Guid> endpointIds, DateTime now,
        CancellationToken cancellationToken)
    {
        if (endpointIds.Count == 0)
        {
            return [];
        }

        var ids = endpointIds.ToList();
        var definitions = await db.CheckDefinitions.AsNoTracking()
            .Where(d => d.Type == CheckType.MissingUpdates)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);
        if (definitions.Count == 0)
        {
            return [];
        }

        db.CheckStates.RemoveRange(await db.CheckStates
            .Where(s => ids.Contains(s.EndpointId) && definitions.Contains(s.CheckDefinitionId))
            .ToListAsync(cancellationToken));

        var transitions = new List<AlertTransition>();
        foreach (var alert in await db.Alerts
                     .Where(a => ids.Contains(a.EndpointId) && a.Kind == AlertKind.Check && a.State != AlertState.Resolved &&
                                 a.CheckDefinitionId != null && definitions.Contains(a.CheckDefinitionId.Value))
                     .ToListAsync(cancellationToken))
        {
            alert.State = AlertState.Resolved;
            alert.ResolvedAt = now;
            alert.ResolvedReason = ResolvedReasonGone;
            alert.UpdatedAt = now;
            transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Resolved));
        }

        return transitions;
    }
}
