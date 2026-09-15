using System.Linq.Expressions;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>Where the maintenance of an endpoint comes from.</summary>
public enum MaintenanceSource
{
    Endpoint,
    Site,
    Client,
    /// <summary>A maintenance window of the endpoint's policy (0.2.0).</summary>
    PolicyWindow
}

/// <summary>The maintenance of one client, site or endpoint as stored, whether or not it is active.</summary>
public sealed record MaintenancePeriod(DateTime? StartedAt, DateTime? EndsAt, string? StartedByName, string? Reason)
{
    public static readonly MaintenancePeriod None = new(null, null, null, null);

    public bool IsActive(DateTime now) => MaintenanceRules.IsActive(StartedAt, EndsAt, now);
}

/// <summary>An active maintenance that applies to an endpoint, with where it comes from. EndsAt null means until turned off.</summary>
/// <param name="SourceName">For a policy window: the policy name.</param>
public sealed record EffectiveMaintenance(MaintenanceSource Source, DateTime StartedAt, DateTime? EndsAt, string? StartedByName, string? Reason,
    string? SourceName = null);

/// <summary>
/// The one rule for maintenance mode (ARCHITECTURE.md §4, Maintenance mode). A maintenance is active while it has started and
/// its end time is unset or in the future; nothing clears expired values, every reader compares with the current time. An
/// endpoint is in <em>effective maintenance</em> when its own, its site's or its client's maintenance is active, or an occurrence of a
/// maintenance window of its policy (the site's linked policy, else the default policy) is running and applies to its class.
/// <para>
/// Used in C# (<see cref="Effective"/>), in EF Core queries (<see cref="EndpointInMaintenance"/>) and, for set-based statements
/// in the workers, through its SQL twin <c>MaintenanceSql.EndpointInMaintenance</c>; a test proves they agree.
/// </para>
/// While an endpoint is in maintenance no alert opens or escalates; open alerts stay open and still resolve. Duplicate identity
/// alerts are never suppressed: they are a security signal, not monitoring.
/// </summary>
public static class MaintenanceRules
{
    public const int MaxReasonLength = 500;

    /// <summary>A chosen end time may lie at most this far ahead; longer means "until turned off".</summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(366);

    public static bool IsActive(DateTime? startedAt, DateTime? endsAt, DateTime now) =>
        startedAt is { } started && started <= now && (endsAt is null || endsAt > now);

    /// <summary>
    /// The maintenance that applies to an endpoint: the active source that lasts longest (until turned off beats any end time),
    /// the endpoint's own first on a tie. Null when none is active.
    /// </summary>
    /// <param name="policyWindow">The running window occurrence that applies to the endpoint, as a period whose
    /// <see cref="MaintenancePeriod.StartedByName"/> is the policy name and whose reason is the window name.</param>
    public static EffectiveMaintenance? Effective(MaintenancePeriod endpoint, MaintenancePeriod site, MaintenancePeriod client, DateTime now,
        MaintenancePeriod? policyWindow = null)
    {
        EffectiveMaintenance? best = null;
        foreach (var (source, period) in new[]
                 {
                     (MaintenanceSource.Endpoint, endpoint), (MaintenanceSource.Site, site), (MaintenanceSource.Client, client),
                     (MaintenanceSource.PolicyWindow, policyWindow ?? MaintenancePeriod.None)
                 })
        {
            if (!period.IsActive(now))
            {
                continue;
            }

            var candidate = source == MaintenanceSource.PolicyWindow
                ? new EffectiveMaintenance(source, period.StartedAt!.Value, period.EndsAt, null, period.Reason, period.StartedByName)
                : new EffectiveMaintenance(source, period.StartedAt!.Value, period.EndsAt, period.StartedByName, period.Reason);
            if (best is null || Lasts(candidate) > Lasts(best))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// EF Core predicate for endpoints in effective maintenance at <paramref name="now"/>. Navigations to the site and client are
    /// translated to joins; the policy window is a subquery on the occurrence, site policy and policy sets of the same context.
    /// </summary>
    public static Expression<Func<Endpoint, bool>> EndpointInMaintenance(DateTime now, IQueryable<MaintenanceWindowOccurrence> occurrences,
        IQueryable<SitePolicy> sitePolicies, IQueryable<Policy> policies) => e =>
        (e.MaintenanceStartedAt != null && e.MaintenanceStartedAt <= now && (e.MaintenanceEndsAt == null || e.MaintenanceEndsAt > now)) ||
        (e.Site!.MaintenanceStartedAt != null && e.Site.MaintenanceStartedAt <= now && (e.Site.MaintenanceEndsAt == null || e.Site.MaintenanceEndsAt > now)) ||
        (e.Site.Client!.MaintenanceStartedAt != null && e.Site.Client.MaintenanceStartedAt <= now &&
         (e.Site.Client.MaintenanceEndsAt == null || e.Site.Client.MaintenanceEndsAt > now)) ||
        occurrences.Any(o => o.StartsAt <= now && o.EndsAt > now &&
                             (o.AppliesTo == CheckAppliesTo.All ||
                              (o.AppliesTo == CheckAppliesTo.Server && (e.ClassOverride ?? e.DetectedClass) == EndpointClass.Server) ||
                              (o.AppliesTo == CheckAppliesTo.Workstation && (e.ClassOverride ?? e.DetectedClass) == EndpointClass.Workstation)) &&
                             o.PolicyId == (sitePolicies.Where(sp => sp.SiteId == e.SiteId).Select(sp => (Guid?)sp.PolicyId).FirstOrDefault() ??
                                            policies.Where(p => p.IsDefault).Select(p => (Guid?)p.Id).FirstOrDefault()));

    /// <summary>True when a window occurrence applies to an endpoint of <paramref name="endpointClass"/>.</summary>
    public static bool WindowAppliesTo(CheckAppliesTo appliesTo, EndpointClass endpointClass) =>
        appliesTo == CheckAppliesTo.All ||
        (appliesTo == CheckAppliesTo.Server && endpointClass == EndpointClass.Server) ||
        (appliesTo == CheckAppliesTo.Workstation && endpointClass == EndpointClass.Workstation);

    /// <summary>Validates a requested end time. Returns a problem (cause and next step), or null when it is acceptable.</summary>
    public static string? ValidateEnd(DateTime? endsAt, DateTime now)
    {
        if (endsAt is null)
        {
            return null;
        }

        if (endsAt <= now.AddMinutes(1))
        {
            return "Choose an end time in the future, or keep maintenance on until you turn it off.";
        }

        return endsAt > now + MaximumDuration
            ? "An end time can be at most a year ahead. Choose an earlier time, or keep maintenance on until you turn it off."
            : null;
    }

    private static DateTime Lasts(EffectiveMaintenance maintenance) => maintenance.EndsAt ?? DateTime.MaxValue;
}
