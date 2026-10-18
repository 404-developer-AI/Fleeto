using System.Linq.Expressions;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// The one rule for an alert hold (decided 2026-10-18): a hold is maintenance for the source of the alert, the check and target
/// or the service alert of its endpoint, until <see cref="Alert.HeldUntil"/>. While it lasts no alert of that source opens or
/// escalates, whether the held alert is still unresolved or resolved meanwhile; alerts still resolve, and the first failure after
/// the hold opens the alert again, as after maintenance. Duplicate identity alerts are never silenced: they are a security signal.
/// <para>
/// The workers read it through <see cref="HoldsSource"/>; set-based statements repeat the same condition in SQL
/// (<c>EndpointHealthService</c>), proven equal by the hold tests.
/// </para>
/// </summary>
public static class AlertHoldRules
{
    /// <summary>True while a hold on this alert keeps its source quiet at <paramref name="now"/>.</summary>
    public static bool HoldsSource(AlertKind kind, DateTime? heldUntil, DateTime now) =>
        kind != AlertKind.DuplicateIdentity && heldUntil is { } until && until > now;

    /// <summary>EF Core predicate for alerts whose hold keeps their source quiet at <paramref name="now"/>, in any state.</summary>
    public static Expression<Func<Alert, bool>> HoldsSource(DateTime now) =>
        a => a.Kind != AlertKind.DuplicateIdentity && a.HeldUntil != null && a.HeldUntil > now;
}
