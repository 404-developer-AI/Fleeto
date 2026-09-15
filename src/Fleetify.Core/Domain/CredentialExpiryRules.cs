namespace Fleetify.Core.Domain;

/// <summary>How close a stored credential is to its end date. Higher is more urgent.</summary>
public enum CredentialExpiryStage
{
    None,
    Days30,
    Days14,
    Days7,
    Day1,
    Expired
}

/// <summary>
/// Expiring credentials (0.2.0): from 30 days before the end date the dashboard warns and admins get an email, repeated when
/// 14, 7 and 1 days remain, and once more after expiry. A stage that was skipped (the worker was down, or the date was entered
/// late) sends only the most urgent one.
/// </summary>
public static class CredentialExpiryRules
{
    public static CredentialExpiryStage StageFor(DateTime expiresAt, DateTime now)
    {
        var remaining = expiresAt - now;
        return remaining switch
        {
            _ when remaining <= TimeSpan.Zero => CredentialExpiryStage.Expired,
            _ when remaining <= TimeSpan.FromDays(1) => CredentialExpiryStage.Day1,
            _ when remaining <= TimeSpan.FromDays(7) => CredentialExpiryStage.Days7,
            _ when remaining <= TimeSpan.FromDays(14) => CredentialExpiryStage.Days14,
            _ when remaining <= TimeSpan.FromDays(30) => CredentialExpiryStage.Days30,
            _ => CredentialExpiryStage.None
        };
    }

    /// <summary>True when <paramref name="current"/> needs an email given the stage already warned about for the same end date.</summary>
    public static bool NeedsWarning(CredentialExpiryStage current, CredentialExpiryStage alreadyWarned) =>
        current != CredentialExpiryStage.None && current > alreadyWarned;
}
