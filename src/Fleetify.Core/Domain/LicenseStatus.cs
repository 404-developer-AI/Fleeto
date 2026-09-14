namespace Fleetify.Core.Domain;

public enum LicenseState
{
    /// <summary>No license loaded. Every endpoint behaves as agent-only.</summary>
    Missing,
    /// <summary>Valid and more than <see cref="LicenseStatus.WarningDays"/> days from expiry.</summary>
    Valid,
    /// <summary>Valid, expiring within <see cref="LicenseStatus.WarningDays"/> days. Dashboard warns.</summary>
    ExpiringSoon,
    /// <summary>Expired, within the grace period: everything keeps working, banner and daily email.</summary>
    GracePeriod,
    /// <summary>Grace period over. Every endpoint behaves as agent-only until a new license is loaded.</summary>
    Expired
}

/// <summary>Evaluated license situation of the instance at a point in time.</summary>
public sealed record LicenseStatus(LicenseState State, int ManagedEndpointCount, DateTime? ExpiresAt, DateTime? GraceEndsAt)
{
    /// <summary>Days before expiry the dashboard starts warning.</summary>
    public const int WarningDays = 14;

    /// <summary>Days after expiry during which everything keeps working (ARCHITECTURE.md §5, Licensing).</summary>
    public const int GraceDays = 14;

    public static readonly LicenseStatus None = new(LicenseState.Missing, 0, null, null);

    /// <summary>True when managed endpoints keep their managed behaviour.</summary>
    public bool AllowsManaged => State is LicenseState.Valid or LicenseState.ExpiringSoon or LicenseState.GracePeriod;

    /// <summary>Licenses available for managed endpoints; zero once managed behaviour is no longer allowed.</summary>
    public int Capacity => AllowsManaged ? ManagedEndpointCount : 0;

    /// <summary>
    /// Evaluates a license. <paramref name="now"/> must already be the protected clock (the latest time the
    /// instance has seen), so setting the system clock back does not extend a license.
    /// </summary>
    public static LicenseStatus Evaluate(int managedEndpointCount, DateTime expiresAt, DateTime now)
    {
        var graceEndsAt = expiresAt.AddDays(GraceDays);
        var state = now >= graceEndsAt ? LicenseState.Expired
            : now >= expiresAt ? LicenseState.GracePeriod
            : now >= expiresAt.AddDays(-WarningDays) ? LicenseState.ExpiringSoon
            : LicenseState.Valid;

        return new LicenseStatus(state, managedEndpointCount, expiresAt, graceEndsAt);
    }
}
