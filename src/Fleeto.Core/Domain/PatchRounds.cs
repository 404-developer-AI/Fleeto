using System.Globalization;
using System.Text.RegularExpressions;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// The rounds of a patch policy of engine Fleeto (0.9.0 step 4, <c>PATCHING.md</c> §7): when they happen, in which time zone, how an
/// endpoint's start is spread, until when its job may start, what it installs and whether it restarts. Shared by the workers (writing
/// rounds), fleeto-signer (checking and composing a round's job) and web (the next round on the tab and in the list), so the three agree.
/// <para>
/// A schedule is local time in the time zone of the round: the endpoint's own (<see cref="PatchPolicy.EndpointLocalTime"/>, from what the
/// agent reports, UTC when it reports nothing) or UTC. Daylight saving follows <see cref="MaintenanceWindows"/>: a start time the clock
/// jumps over starts at the first valid minute after the jump, one that occurs twice starts at its first occurrence.
/// </para>
/// </summary>
public static class PatchRounds
{
    /// <summary>The initiator of a round's job: no user, the same empty id as every job Fleeto starts by itself.</summary>
    public static readonly Guid AutomaticInitiator = Action1AgentInstall.AutomaticInitiator;

    public static string AutomaticInitiatorName(string policyName) => $"Fleeto (patch policy {policyName}, round)";

    /// <summary>Longest stretch the workers look back for an occurrence after a stop: a job older than the retry window cannot start anyway.</summary>
    public static readonly TimeSpan MaxLookback = TimeSpan.FromHours(PatchPolicyRules.MaxRetryHours);

    private const int MaxLookaheadDays = 400;

    // The states of Inventory.distribution_updates (0.9.0); the agent reports the same values.
    public const string DistributionTurnedOff = "turned_off";
    public const string DistributionKept = "kept";
    public const string DistributionNone = "none";
    public const string DistributionUntouched = "untouched";

    public const string NothingToInstallMessage = "Nothing to install: the last scan found no missing update this patch policy installs.";
    public const string PreviousOpenMessage = "Skipped: an earlier installation on this endpoint had not ended yet.";

    /// <summary>
    /// The time zone an agent reports, as stored: at most 64 characters of the form of an IANA or Windows name ("Europe/Brussels",
    /// "Romance Standard Time", "Etc/GMT+1"), otherwise empty. Whether the zone exists is decided where it is used.
    /// </summary>
    public static string NormalizeTimeZone(string? name)
    {
        var value = name?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '+' or '-' or '/' or '.' or '(' or ')')
            ? value
            : string.Empty;
    }

    /// <summary>The state as stored: a value the agent may report, or empty.</summary>
    public static string NormalizeDistributionState(string? state) =>
        state is DistributionTurnedOff or DistributionKept or DistributionNone or DistributionUntouched ? state : string.Empty;

    /// <summary>The distribution's own automatic updates in words for the Patches tab; null when there is nothing to show.</summary>
    public static string? DescribeDistributionUpdates(bool requested, string? state) => (requested, NormalizeDistributionState(state)) switch
    {
        (_, DistributionTurnedOff) when !requested => "Fleeto turns them back on: the patch policy no longer asks for it.",
        (false, _) => null,
        (_, DistributionTurnedOff) => "Turned off by Fleeto. Only the rounds of the patch policy install updates.",
        (_, DistributionKept) => "Configured by somebody else, so Fleeto left them. Turn them off on the endpoint to let only the rounds install updates.",
        (_, DistributionNone) => "Neither unattended-upgrades nor dnf-automatic runs on this endpoint.",
        (_, DistributionUntouched) => "Not turned off yet. Fleeto does it with the next configuration of the agent.",
        _ => $"Not reported. The Fleeto agent turns them off from version {PatchRules.MinimumInstallAgentVersion}."
    };

    /// <summary>
    /// The time zone of an endpoint as its agent reports it: an IANA name (Linux) or a Windows name, converted to IANA where the platform
    /// knows it. Null when it is empty or unknown.
    /// </summary>
    public static TimeZoneInfo? FindEndpointZone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            return null;
        }

        if (MaintenanceWindows.FindTimeZone(name) is { } zone)
        {
            return zone;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(name, out var iana) ? MaintenanceWindows.FindTimeZone(iana) : null;
    }

    /// <summary>The time zone of a round of <paramref name="policy"/> on an endpoint: its own when the policy runs in endpoint time, else UTC.</summary>
    public static TimeZoneInfo ZoneOf(PatchPolicy policy, string? endpointTimeZone) =>
        policy.EndpointLocalTime ? FindEndpointZone(endpointTimeZone) ?? TimeZoneInfo.Utc : TimeZoneInfo.Utc;

    /// <summary>The occurrences of the schedule after <paramref name="fromUtc"/> up to and including <paramref name="toUtc"/>, oldest first.</summary>
    public static IReadOnlyList<DateTime> Occurrences(PatchPolicy policy, TimeZoneInfo zone, DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
        {
            return [];
        }

        if (toUtc - fromUtc > MaxLookback)
        {
            fromUtc = toUtc - MaxLookback;
        }

        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone)).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(toUtc, zone)).AddDays(1);
        var result = new List<DateTime>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (OccurrenceOn(policy, zone, day) is { } occurrence && occurrence > fromUtc && occurrence <= toUtc)
            {
                result.Add(occurrence);
            }
        }

        result.Sort();
        return result;
    }

    /// <summary>The first occurrence after <paramref name="afterUtc"/>, or null when there is none within a year.</summary>
    public static DateTime? Next(PatchPolicy policy, TimeZoneInfo zone, DateTime afterUtc)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone)).AddDays(-1);
        for (var i = 0; i < MaxLookaheadDays; i++, day = day.AddDays(1))
        {
            if (OccurrenceOn(policy, zone, day) is { } occurrence && occurrence > afterUtc)
            {
                return occurrence;
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="occurrenceUtc"/> is exactly an occurrence of the schedule in <paramref name="zone"/> (the signer checks a round with it).</summary>
    public static bool IsOccurrence(PatchPolicy policy, TimeZoneInfo zone, DateTime occurrenceUtc)
    {
        var utc = DateTime.SpecifyKind(occurrenceUtc, DateTimeKind.Utc);
        return Occurrences(policy, zone, utc.AddMinutes(-1), utc).Contains(utc);
    }

    /// <summary>A text that changes whenever the schedule does, so the workers start counting again after an edit.</summary>
    public static string ScheduleHash(PatchPolicy policy) => string.Join('|', policy.ScheduleKind, (int)(policy.WeekDays & WeekDays.All), policy.MonthDay,
        policy.MonthWeek, policy.MonthWeekday, policy.StartMinute, policy.EndpointLocalTime);

    private static DateTime? OccurrenceOn(PatchPolicy policy, TimeZoneInfo zone, DateOnly day)
    {
        var runs = policy.ScheduleKind switch
        {
            PatchScheduleKind.Weekly => (policy.WeekDays & PatchPolicyRules.Flag(day.DayOfWeek)) != 0,
            // A month without this day skips it, as the dialog says.
            PatchScheduleKind.MonthlyDay => day.Day == policy.MonthDay,
            PatchScheduleKind.MonthlyWeekday => day.DayOfWeek == policy.MonthWeekday && (day.Day - 1) / 7 + 1 == policy.MonthWeek,
            _ => false
        };
        if (!runs || policy.StartMinute is < 0 or > 1439)
        {
            return null;
        }

        return MaintenanceWindows.ToUtc(day.ToDateTime(new TimeOnly(policy.StartMinute / 60, policy.StartMinute % 60)), zone);
    }

    /// <summary>
    /// When an endpoint of a round starts: the occurrence plus its share of the random delay, the same for the same round and endpoint.
    /// The delay never takes more than half of the retry window, so every endpoint keeps time to start.
    /// </summary>
    public static DateTime DueAt(PatchPolicy policy, Guid deploymentId, Guid endpointId, DateTime roundAt)
    {
        var minutes = Math.Clamp(policy.RandomDelayMinutes, 0, Math.Min(PatchPolicyRules.MaxRandomDelayMinutes, (int)(RetryWindow(policy).TotalMinutes / 2)));
        return roundAt + AutomationTasks.DelayFor(deploymentId, endpointId, minutes);
    }

    /// <summary>The retry window of a round: from its occurrence, the jobs of its endpoints may start for this long (at most 7 days).</summary>
    public static TimeSpan RetryWindow(PatchPolicy policy) =>
        TimeSpan.FromHours(Math.Clamp(policy.RetryHours, PatchPolicyRules.MinRetryHours, PatchPolicyRules.MaxRetryHours));

    /// <summary>Until when the job of a round may start.</summary>
    public static DateTime ValidUntil(PatchPolicy policy, DateTime roundAt) => roundAt + RetryWindow(policy);

    /// <summary>The timeout of the installation job of a round, in seconds.</summary>
    public static int TimeoutSeconds(PatchPolicy policy) =>
        Math.Clamp(policy.InstallTimeoutMinutes, PatchPolicyRules.MinInstallTimeoutMinutes, PatchPolicyRules.MaxInstallTimeoutMinutes) * 60;

    /// <summary>
    /// True when a round may restart the endpoint: the policy allows it, and the endpoint is not a Proxmox VE host (decided 2026-11-04: two
    /// nodes of a cluster restarting in the same round lose its quorum; a technician restarts a host).
    /// </summary>
    public static bool Restarts(PatchPolicy policy, string? osPlatform, string? osName) =>
        policy.AutoReboot && EndOfLife.ProductOf(osPlatform, osName) != EndOfLifeProduct.ProxmoxVe;

    /// <summary>
    /// The seconds between the notice and the restart: the time of the policy. With the message off a server restarts after a minute; on a
    /// workstation with a signed-in user the agent always shows the notice for this time (decided 2026-11-04).
    /// </summary>
    public static int NoticeSeconds(PatchPolicy policy, EndpointClass endpointClass) =>
        !policy.RebootMessage && endpointClass == EndpointClass.Server
            ? 60
            : Math.Clamp(policy.RebootTimeoutMinutes, PatchPolicyRules.MinRebootTimeoutMinutes, PatchPolicyRules.MaxRebootTimeoutMinutes) * 60;

    /// <summary>The text of the notice; empty lets the agent use its own wording.</summary>
    public static string NoticeMessage(PatchPolicy policy) =>
        policy.RebootMessage ? (string.IsNullOrWhiteSpace(policy.RebootMessageText) ? PatchPolicyRules.DefaultRebootMessage : policy.RebootMessageText.Trim()) : string.Empty;

    /// <summary>
    /// True when the policy narrows what a round installs beyond what the agent can judge itself (filters, names with wildcards, severities
    /// on Linux, the wait after release): the signer then lists the updates the round may install (<see cref="Allows"/>), and the agent
    /// installs nothing else.
    /// </summary>
    public static bool Restricts(PatchPolicy policy) => policy.Scope == PatchUpdateScope.Filtered || policy.InstallDelayDays > 0;

    /// <summary>
    /// True when a round of <paramref name="policy"/> installs <paramref name="update"/>, missing on an endpoint whose platform is
    /// <paramref name="osPlatform"/>, at <paramref name="nowUtc"/>. Held updates, removals and feature updates never; drivers only with
    /// the option; on Linux with "Security updates only" only security updates; with filters an update must match every filter that is set;
    /// with a wait after release the update must be that old: its release date (Windows), the published date of its advisory (Linux) or,
    /// without either, the day Fleeto first saw it missing.
    /// </summary>
    public static bool Allows(PatchPolicy policy, EndpointMissingUpdate update, string? osPlatform, DateTime nowUtc)
    {
        if (update.Held || update.Removal || update.IsFeatureUpdate || (update.IsDriver && !policy.IncludeDrivers))
        {
            return false;
        }

        var linux = string.Equals(osPlatform, "linux", StringComparison.OrdinalIgnoreCase);
        if (linux && policy.LinuxSecurityOnly && update.UpdateType != "Security Updates")
        {
            return false;
        }

        if (policy.Scope == PatchUpdateScope.Filtered)
        {
            if (policy.UpdateTypes.Count > 0 && !policy.UpdateTypes.Contains(update.UpdateType, StringComparer.Ordinal))
            {
                return false;
            }

            if (policy.Severities.Count > 0 && !policy.Severities.Contains(update.Severity.ToString(), StringComparer.Ordinal))
            {
                return false;
            }

            if (policy.ExcludedNames.Any(pattern => Matches(pattern, update.Name)))
            {
                return false;
            }
        }

        if (policy.InstallDelayDays > 0)
        {
            var released = update.ReleaseDate is { } date
                ? date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                : update.FirstSeenAt ?? update.UpdatedAt;
            if (released.AddDays(policy.InstallDelayDays) > nowUtc)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A name pattern of a policy, with * as a wildcard, against a name; case-insensitive and whole.</summary>
    public static bool Matches(string pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = "^" + string.Join(".*", pattern.Trim().Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(value ?? string.Empty, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>"Wednesday 2026-11-11 20:00 (Europe/Brussels)", for the tab and the list.</summary>
    public static string DescribeRound(DateTime roundAtUtc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(roundAtUtc, DateTimeKind.Utc), zone);
        return $"{local.DayOfWeek} {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ({(zone == TimeZoneInfo.Utc ? "UTC" : zone.Id)})";
    }
}
