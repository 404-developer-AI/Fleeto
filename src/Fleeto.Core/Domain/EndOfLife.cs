using System.Globalization;
using System.Text.RegularExpressions;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// The operating systems whose end of life Fleeto reports (0.8.0 step 4, VULNERABILITIES.md §12). Every operating system Fleeto
/// supports is one of these: a test fails when one has no life-cycle source or no line in <see cref="SupportedProducts"/>.
/// </summary>
public enum EndOfLifeProduct
{
    Windows,
    WindowsServer,
    Ubuntu,
    Debian,
    AlmaLinux,
    ProxmoxVe,
    SynologyDsm
}

/// <summary>Where an endpoint stands on the day of a report (§12.2).</summary>
public enum EndOfLifeStatus
{
    /// <summary>Gets security updates, and its end of life is further away than the period.</summary>
    Supported = 0,

    /// <summary>Its end of life falls within the period an admin set.</summary>
    EndingSoon = 1,

    /// <summary>Past its end of life, within a paid extension the client bought (marked, or Ubuntu Pro reported by the agent).</summary>
    Extended = 2,

    /// <summary>No security updates any more.</summary>
    EndOfLife = 3,

    /// <summary>The source knows the release but announced no end of life yet.</summary>
    NoDate = 4,

    /// <summary>The operating system or its release is not recognised, or the source does not know it.</summary>
    NotRecognised = 5
}

/// <summary>How often the report runs by itself; "Refresh now" works with every kind.</summary>
public enum EndOfLifeScheduleKind
{
    Off = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3
}

/// <summary>Where the dates of a product come from.</summary>
public enum EndOfLifeSourceKind
{
    /// <summary>Fetched by the workers from endoflife.date.</summary>
    EndOfLifeDate = 0,

    /// <summary>A list in the code of Fleeto, updated with a release.</summary>
    Builtin = 1
}

/// <summary>A product with its source: the product slug on endoflife.date, or the list in the code.</summary>
public sealed record EndOfLifeProductInfo(EndOfLifeProduct Product, string Name, EndOfLifeSourceKind Source, string? Slug, string SourceName,
    string SourceLink, string? ExtensionName);

/// <summary>
/// One release of a product with its dates. <see cref="EndOfLifeOn"/> is the first day without security updates; null when none was
/// announced. <see cref="ExtensionUntil"/> is the first day a paid extension no longer covers. <see cref="LatestBuild"/> is the build of
/// a Windows release ("10.0.26200"), by which an endpoint is matched.
/// </summary>
public sealed record EndOfLifeRelease(EndOfLifeProduct Product, string Release, string Label, DateOnly? EndOfLifeOn, DateOnly? ExtensionUntil,
    string? LatestBuild);

/// <summary>The verdict on one endpoint.</summary>
public sealed record EndOfLifeVerdict(EndOfLifeStatus Status, EndOfLifeProduct? Product, EndOfLifeRelease? Release, bool ExtensionCounted);

/// <summary>The schedule of the report, set by an admin in Settings, Vulnerabilities.</summary>
public sealed record EndOfLifeSchedule(EndOfLifeScheduleKind Kind, DayOfWeek WeekDay, int MonthDay, int Hour, string TimeZone)
{
    public static readonly EndOfLifeSchedule Default = new(EndOfLifeScheduleKind.Monthly, DayOfWeek.Monday, 1, 7, "Europe/Brussels");
}

/// <summary>
/// The rules of the end-of-life report (0.8.0 step 4, VULNERABILITIES.md §12): which product and release an endpoint runs, judged from
/// what the agent already reports, and where it stands on a day. Pure, so every rule is tested on the names of real endpoints.
/// </summary>
public static partial class EndOfLife
{
    public const int MinPeriodMonths = 1;
    public const int MaxPeriodMonths = 24;
    public const int DefaultPeriodMonths = 6;

    /// <summary>Data of a source older than this is an instance health problem; the report still runs and states its age.</summary>
    public static readonly TimeSpan SourceStaleAfter = TimeSpan.FromDays(7);

    /// <summary>The workers fetch endoflife.date once a day.</summary>
    public static readonly TimeSpan FetchEvery = TimeSpan.FromDays(1);

    /// <summary>A refresh from the tab waits this long after the previous request of the instance.</summary>
    public static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(1);

    /// <summary>Reports are kept this long.</summary>
    public const int ReportRetentionMonths = 13;

    public const string EndOfLifeDateHost = "endoflife.date";

    /// <summary>A response larger than this is refused: the six products together are about 60 KB.</summary>
    public const int MaxDownloadBytes = 2 * 1024 * 1024;

    /// <summary>The products, each with its source. The order is the order of the tabs.</summary>
    public static readonly IReadOnlyList<EndOfLifeProductInfo> Products =
    [
        new(EndOfLifeProduct.Windows, "Windows", EndOfLifeSourceKind.EndOfLifeDate, "windows", "endoflife.date",
            "https://endoflife.date/windows", "Extended Security Updates"),
        new(EndOfLifeProduct.WindowsServer, "Windows Server", EndOfLifeSourceKind.EndOfLifeDate, "windows-server", "endoflife.date",
            "https://endoflife.date/windows-server", "Extended Security Updates"),
        new(EndOfLifeProduct.Ubuntu, "Ubuntu", EndOfLifeSourceKind.EndOfLifeDate, "ubuntu", "endoflife.date",
            "https://endoflife.date/ubuntu", "Ubuntu Pro"),
        new(EndOfLifeProduct.Debian, "Debian", EndOfLifeSourceKind.EndOfLifeDate, "debian", "endoflife.date",
            "https://endoflife.date/debian", "Extended LTS"),
        new(EndOfLifeProduct.AlmaLinux, "AlmaLinux", EndOfLifeSourceKind.EndOfLifeDate, "almalinux", "endoflife.date",
            "https://endoflife.date/almalinux", null),
        new(EndOfLifeProduct.ProxmoxVe, "Proxmox VE", EndOfLifeSourceKind.EndOfLifeDate, "proxmox-ve", "endoflife.date",
            "https://endoflife.date/proxmox-ve", null),
        new(EndOfLifeProduct.SynologyDsm, "Synology DSM", EndOfLifeSourceKind.Builtin, null, "Synology Software Life Cycle Policy (2026-10-13)",
            "https://www.synology.com/en-global/products/status", null),
    ];

    public static EndOfLifeProductInfo Info(EndOfLifeProduct product) => Products.First(p => p.Product == product);

    /// <summary>
    /// The releases of Synology DSM from Synology's Software Life Cycle Policy, last updated 2026-10-13: the end of the Extended Life
    /// Phase of a long-term release, otherwise the end of its Maintenance Phase. The policy gives months, so the end of life is the
    /// first day after that month. The Extended Life Phase of DSM 7.1 covers only a list of older models, which Fleeto does not know, so
    /// 7.1 ends with its Maintenance Phase (both are past). Checked again with every release of Fleeto that touches end of life.
    /// </summary>
    public static readonly IReadOnlyList<EndOfLifeRelease> SynologyDsmReleases =
    [
        Dsm("7.4", "DSM 7.4", 2028, 6),
        Dsm("7.3", "DSM 7.3 (LTS)", 2028, 10),
        Dsm("7.2", "DSM 7.2", 2025, 12),
        Dsm("7.1", "DSM 7.1 (LTS)", 2024, 6),
        Dsm("7.0", "DSM 7.0", 2023, 6),
        Dsm("6.2", "DSM 6.2 (LTS)", 2024, 9),
        Dsm("6.1", "DSM 6.1", 2019, 6),
        Dsm("6.0", "DSM 6.0", 2018, 6),
        Dsm("5.2", "DSM 5.2 (LTS)", 2019, 6),
        Dsm("5.1", "DSM 5.1", 2016, 12),
        Dsm("5.0", "DSM 5.0", 2016, 6),
    ];

    private static EndOfLifeRelease Dsm(string release, string label, int year, int lastMonth) =>
        new(EndOfLifeProduct.SynologyDsm, release, label, new DateOnly(year, lastMonth, 1).AddMonths(1), null, null);

    /// <summary>The product of an endpoint from what its agent reports, or null for an operating system Fleeto does not report on.</summary>
    public static EndOfLifeProduct? ProductOf(string? osPlatform, string? osName)
    {
        var name = osName?.Trim() ?? string.Empty;
        return osPlatform switch
        {
            "windows" when name.Contains("Server", StringComparison.OrdinalIgnoreCase) => EndOfLifeProduct.WindowsServer,
            "windows" when name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) => EndOfLifeProduct.Windows,
            "linux" when RemoteSessionRules.IsProxmox(osPlatform, name) => EndOfLifeProduct.ProxmoxVe,
            "linux" when RemoteSessionRules.IsSynology(osPlatform, name) => EndOfLifeProduct.SynologyDsm,
            "linux" when name.StartsWith("Ubuntu", StringComparison.OrdinalIgnoreCase) => EndOfLifeProduct.Ubuntu,
            "linux" when name.StartsWith("Debian", StringComparison.OrdinalIgnoreCase) => EndOfLifeProduct.Debian,
            "linux" when name.StartsWith("AlmaLinux", StringComparison.OrdinalIgnoreCase) => EndOfLifeProduct.AlmaLinux,
            _ => null
        };
    }

    /// <summary>
    /// The release of an endpoint among the releases of its product (§12.1), or null when it is not recognised. Windows is matched by its
    /// build and edition, Windows Server by the year in its name, the others by the version in their name.
    /// </summary>
    public static EndOfLifeRelease? ReleaseOf(EndOfLifeProduct product, string? osName, string? osVersion, IReadOnlyCollection<EndOfLifeRelease> releases)
    {
        var name = osName?.Trim() ?? string.Empty;
        var own = releases.Where(r => r.Product == product).ToList();
        return product switch
        {
            EndOfLifeProduct.Windows => WindowsRelease(name, osVersion, own),
            EndOfLifeProduct.WindowsServer => WindowsServerRelease(name, osVersion, own),
            _ => VersionFromName(product, name) is { } key ? own.FirstOrDefault(r => r.Release == key) : null
        };
    }

    /// <summary>The release key in the name a Linux agent reports: "Ubuntu 24.04.1 LTS" is 24.04, "AlmaLinux 9.8 (...)" is 9.</summary>
    public static string? VersionFromName(EndOfLifeProduct product, string name)
    {
        var match = product switch
        {
            EndOfLifeProduct.Ubuntu => UbuntuVersion().Match(name),
            EndOfLifeProduct.Debian => DebianVersion().Match(name),
            EndOfLifeProduct.AlmaLinux => AlmaLinuxVersion().Match(name),
            EndOfLifeProduct.ProxmoxVe => ProxmoxVersion().Match(name),
            EndOfLifeProduct.SynologyDsm => DsmVersion().Match(name),
            _ => Match.Empty
        };
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>"10.0.26200.1234" or "10.0.26200" as "10.0.26200", the form of endoflife.date.</summary>
    public static string? WindowsBuild(string? osVersion)
    {
        var parts = (osVersion ?? string.Empty).Trim().Split('.');
        return parts.Length >= 3 && parts.Take(3).All(p => p.Length > 0 && p.All(char.IsAsciiDigit))
            ? string.Join('.', parts.Take(3).Select(p => int.Parse(p, CultureInfo.InvariantCulture)))
            : null;
    }

    private static EndOfLifeRelease? WindowsRelease(string name, string? osVersion, List<EndOfLifeRelease> releases)
    {
        if (WindowsBuild(osVersion) is not { } build)
        {
            return null;
        }

        var candidates = releases.Where(r => r.LatestBuild == build).ToList();
        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        // endoflife.date splits a release by edition: (E) Enterprise, Education and IoT Enterprise, (W) Home and Pro (Pro Education
        // included), LTSC on its own, IoT LTSC on its own.
        var lower = name.ToLowerInvariant();
        var ltsc = lower.Contains("ltsc") || lower.Contains("ltsb");
        var enterprise = lower.Contains("enterprise") || (lower.Contains("education") && !lower.Contains("pro education"));
        var suffix = ltsc ? lower.Contains("iot") ? "-iot-lts" : "-e-lts" : enterprise ? "-e" : "-w";
        return candidates.FirstOrDefault(r => r.Release.EndsWith(suffix, StringComparison.Ordinal))
               ?? candidates.FirstOrDefault(r => !r.Release.EndsWith("-e", StringComparison.Ordinal) && !r.Release.EndsWith("-w", StringComparison.Ordinal) &&
                                                 !r.Release.EndsWith("-lts", StringComparison.Ordinal) && !r.Release.EndsWith("-iot", StringComparison.Ordinal));
    }

    private static EndOfLifeRelease? WindowsServerRelease(string name, string? osVersion, List<EndOfLifeRelease> releases)
    {
        var match = WindowsServerYear().Match(name);
        if (match.Success)
        {
            var year = match.Groups[1].Value;
            var r2 = match.Groups[2].Success;
            string[] keys = (year, r2) switch
            {
                ("2008", true) => ["2008-r2-sp1"],
                ("2008", false) => ["2008-sp2"],
                ("2003", _) => ["2003-sp2", "2003"],
                (_, true) => [$"{year}-r2"],
                _ => [year]
            };
            foreach (var key in keys)
            {
                if (releases.FirstOrDefault(r => r.Release == key) is { } byName)
                {
                    return byName;
                }
            }
        }

        // A name without its year: the long-term release with the build, never a semi-annual one (Server 2019 and 1809 share a build).
        return WindowsBuild(osVersion) is { } build
            ? releases.FirstOrDefault(r => r.LatestBuild == build && !r.Release.EndsWith("-sac", StringComparison.Ordinal) &&
                                           !r.Release.EndsWith("-ac", StringComparison.Ordinal))
            : null;
    }

    /// <summary>
    /// Where an endpoint stands on <paramref name="today"/> (§12.2). <paramref name="extensionRelease"/> is the release a user marked as
    /// extended; <paramref name="ubuntuPro"/> is true when the agent reports the Ubuntu Pro service that extends an Ubuntu release.
    /// </summary>
    public static EndOfLifeVerdict Judge(EndOfLifeProduct? product, EndOfLifeRelease? release, DateOnly today, int periodMonths,
        string? extensionRelease, bool ubuntuPro)
    {
        if (product is null || release is null)
        {
            return new EndOfLifeVerdict(EndOfLifeStatus.NotRecognised, product, release, false);
        }

        if (release.EndOfLifeOn is not { } end)
        {
            return new EndOfLifeVerdict(EndOfLifeStatus.NoDate, product, release, false);
        }

        if (today < end)
        {
            var soon = end <= today.AddMonths(Math.Clamp(periodMonths, MinPeriodMonths, MaxPeriodMonths));
            return new EndOfLifeVerdict(soon ? EndOfLifeStatus.EndingSoon : EndOfLifeStatus.Supported, product, release, false);
        }

        var extended = release.ExtensionUntil is { } until && today < until &&
                       (extensionRelease == release.Release || (product == EndOfLifeProduct.Ubuntu && ubuntuPro));
        return new EndOfLifeVerdict(extended ? EndOfLifeStatus.Extended : EndOfLifeStatus.EndOfLife, product, release, extended);
    }

    /// <summary>The Ubuntu Pro service that extends an Ubuntu release, as the agent reports it (§6).</summary>
    public const string UbuntuProService = "esm-infra";

    /// <summary>The period from its setting: the default when it is missing or unreadable, within the bounds otherwise.</summary>
    public static int PeriodMonths(string? setting) =>
        int.TryParse(setting, NumberStyles.Integer, CultureInfo.InvariantCulture, out var months)
            ? Math.Clamp(months, MinPeriodMonths, MaxPeriodMonths)
            : DefaultPeriodMonths;

    private static readonly System.Text.Json.JsonSerializerOptions ScheduleJsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>The schedule from its setting, or the default when it is missing or unreadable.</summary>
    public static EndOfLifeSchedule ParseSchedule(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EndOfLifeSchedule.Default;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<EndOfLifeSchedule>(json, ScheduleJsonOptions) is { } schedule && ScheduleProblem(schedule) is null
                ? schedule
                : EndOfLifeSchedule.Default;
        }
        catch (System.Text.Json.JsonException)
        {
            return EndOfLifeSchedule.Default;
        }
    }

    public static string ScheduleJson(EndOfLifeSchedule schedule) => System.Text.Json.JsonSerializer.Serialize(schedule, ScheduleJsonOptions);

    /// <summary>The schedule in words: "Monthly on day 1 at 07:00 (Europe/Brussels)".</summary>
    public static string Describe(EndOfLifeSchedule schedule) => schedule.Kind switch
    {
        EndOfLifeScheduleKind.Off => "Off: only when somebody refreshes the report",
        EndOfLifeScheduleKind.Daily => $"Daily at {schedule.Hour:00}:00 ({schedule.TimeZone})",
        EndOfLifeScheduleKind.Weekly => $"Every {schedule.WeekDay} at {schedule.Hour:00}:00 ({schedule.TimeZone})",
        _ => $"Monthly on day {schedule.MonthDay} at {schedule.Hour:00}:00 ({schedule.TimeZone})"
    };

    /// <summary>Why a schedule cannot be saved, or null.</summary>
    public static string? ScheduleProblem(EndOfLifeSchedule schedule)
    {
        if (!Enum.IsDefined(schedule.Kind))
        {
            return "Choose how often the report runs.";
        }

        if (schedule.Hour is < 0 or > 23)
        {
            return "Choose an hour between 0 and 23.";
        }

        if (schedule.MonthDay is < 1 or > 28)
        {
            return "Choose a day of the month between 1 and 28, so the report runs every month.";
        }

        if (!Enum.IsDefined(schedule.WeekDay))
        {
            return "Choose a day of the week.";
        }

        return MaintenanceWindows.FindTimeZone(schedule.TimeZone) is null ? "Choose a time zone from the list." : null;
    }

    /// <summary>
    /// The first run of the schedule after <paramref name="afterUtc"/>, in UTC, or null when it is off or its time zone is unknown. An
    /// hour that does not exist on a day (the clock moves forward) runs an hour later.
    /// </summary>
    public static DateTime? NextRun(EndOfLifeSchedule schedule, DateTime afterUtc)
    {
        if (schedule.Kind == EndOfLifeScheduleKind.Off || MaintenanceWindows.FindTimeZone(schedule.TimeZone) is not { } zone)
        {
            return null;
        }

        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), zone)).AddDays(-1);
        for (var i = 0; i < 70; i++, day = day.AddDays(1))
        {
            var runs = schedule.Kind switch
            {
                EndOfLifeScheduleKind.Daily => true,
                EndOfLifeScheduleKind.Weekly => day.DayOfWeek == schedule.WeekDay,
                _ => day.Day == schedule.MonthDay
            };
            if (!runs)
            {
                continue;
            }

            var local = day.ToDateTime(new TimeOnly(Math.Clamp(schedule.Hour, 0, 23), 0));
            if (zone.IsInvalidTime(local))
            {
                local = local.AddHours(1);
            }

            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
            if (utc > afterUtc)
            {
                return utc;
            }
        }

        return null;
    }

    /// <summary>The day of a report in the time zone of the schedule, which decides the status.</summary>
    public static DateOnly Today(EndOfLifeSchedule schedule, DateTime nowUtc) =>
        DateOnly.FromDateTime(MaintenanceWindows.FindTimeZone(schedule.TimeZone) is { } zone
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone)
            : nowUtc);

    /// <summary>The instance health problem of a product whose dates could not be fetched for <see cref="SourceStaleAfter"/>.</summary>
    public static IReadOnlyList<HealthFinding> HealthFindings(IEnumerable<EndOfLifeSourceState> sources, DateTime now) =>
        sources.Where(s => s.FailingSince is { } since && now - since >= SourceStaleAfter)
            .Select(s =>
            {
                var name = Enum.TryParse<EndOfLifeProduct>(s.Product, out var product) ? Info(product).Name : s.Product;
                var last = s.LastSuccessAt is { } success ? $"The end-of-life dates of {name} date from {success:yyyy-MM-dd} UTC." : $"No end-of-life dates of {name} were fetched yet.";
                return new HealthFinding($"end-of-life:{s.Product}", HealthComponent.Vulnerabilities, AlertSeverity.Warning,
                    $"The end-of-life dates of {name} could not be fetched for 7 days",
                    $"{last} {s.Problem ?? "endoflife.date could not be reached."} Check that the workers can reach {EndOfLifeDateHost}.");
            })
            .ToList();

    [GeneratedRegex(@"^Ubuntu\s+(\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UbuntuVersion();

    [GeneratedRegex(@"^Debian\s+GNU/Linux\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DebianVersion();

    [GeneratedRegex(@"^AlmaLinux\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlmaLinuxVersion();

    [GeneratedRegex(@"^Proxmox VE\s+(\d+)\.", RegexOptions.CultureInvariant)]
    private static partial Regex ProxmoxVersion();

    [GeneratedRegex(@"^Synology DSM\s+(\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex DsmVersion();

    [GeneratedRegex(@"\bServer\s+(\d{4})(\s+R2)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsServerYear();
}

/// <summary>One endpoint in the message of a report: what the channel needs to act, nothing more.</summary>
public sealed record EndOfLifeNoticeEndpoint(string Hostname, string OsName, EndOfLifeStatus Status, DateOnly? EndOfLifeOn);

/// <summary>One client in the message of a report: its counts and the endpoints at or near end of life.</summary>
public sealed record EndOfLifeNoticeClient(string ClientCode, string ClientName, int EndOfLife, int EndingSoon, int Extended, int NotRecognised,
    IReadOnlyList<EndOfLifeNoticeEndpoint> Endpoints);

/// <summary>The message of a scheduled report to one notification channel.</summary>
public sealed record EndOfLifeNotice(DateOnly Day, int PeriodMonths, IReadOnlyList<EndOfLifeNoticeClient> Clients)
{
    /// <summary>A message lists at most this many endpoints; the tab and the PDF hold the rest.</summary>
    public const int MaxEndpoints = 100;

    public int EndOfLife => Clients.Sum(c => c.EndOfLife);
    public int EndingSoon => Clients.Sum(c => c.EndingSoon);
    public int Extended => Clients.Sum(c => c.Extended);
    public int NotRecognised => Clients.Sum(c => c.NotRecognised);

    /// <summary>"3 endpoints at end of life, 5 ending soon" or "No endpoint at or near end of life".</summary>
    public string Headline
    {
        get
        {
            var parts = new List<string>();
            if (EndOfLife > 0)
            {
                parts.Add(EndOfLife == 1 ? "1 endpoint at end of life" : $"{EndOfLife} endpoints at end of life");
            }

            if (EndingSoon > 0)
            {
                parts.Add(parts.Count == 0 ? EndingSoon == 1 ? "1 endpoint ending soon" : $"{EndingSoon} endpoints ending soon" : $"{EndingSoon} ending soon");
            }

            return parts.Count == 0 ? "No endpoint at or near end of life" : string.Join(", ", parts);
        }
    }
}

/// <summary>The state of a source as the rules judge it.</summary>
public sealed record EndOfLifeSourceState(string Product, DateTime? LastSuccessAt, DateTime? FailingSince, string? Problem);

/// <summary>How far Fleeto supports a feature on an operating system.</summary>
public enum SupportLevel
{
    No = 0,
    Partly = 1,
    Yes = 2
}

/// <summary>One feature on one operating system: how far, and its limits in a few words.</summary>
public sealed record FeatureSupport(SupportLevel Level, string? Note = null)
{
    public static readonly FeatureSupport Full = new(SupportLevel.Yes);
}

/// <summary>What Fleeto offers on one operating system (§12): the list on the tab Supported products.</summary>
public sealed record ProductSupport(EndOfLifeProduct Product, string Versions, FeatureSupport Monitoring, FeatureSupport Patching,
    FeatureSupport RemoteControl, FeatureSupport RemoteBackground, FeatureSupport Vulnerabilities, FeatureSupport WebInterface);

/// <summary>
/// The operating systems Fleeto supports and how far (0.8.0 step 4). A catalog in code, kept with the features: a feature that comes to
/// another operating system changes its line in the same commit, and a new operating system adds one (a test enforces that every
/// <see cref="EndOfLifeProduct"/> has a line).
/// </summary>
public static class SupportedProducts
{
    private static readonly FeatureSupport NoWebInterface = new(SupportLevel.No);
    // 0.9.0 step 1: the Fleeto agent reads the missing updates; installing follows in a later step of 0.9.0.
    private static readonly FeatureSupport AptPatching = new(SupportLevel.Partly, "Missing updates by Fleeto (apt); installing follows");
    private static readonly FeatureSupport DnfPatching = new(SupportLevel.Partly, "Missing updates by Fleeto (dnf); installing follows");
    private static readonly FeatureSupport LinuxRemoteControl = new(SupportLevel.Partly, "Only with a desktop on X11, not Wayland");

    public static readonly IReadOnlyList<ProductSupport> All =
    [
        new(EndOfLifeProduct.Windows, "Windows 10 and 11",
            FeatureSupport.Full,
            new(SupportLevel.Yes, "Through Action1"),
            FeatureSupport.Full,
            FeatureSupport.Full,
            new(SupportLevel.No, "Missing updates on the Patches tab, through Action1"),
            NoWebInterface),
        new(EndOfLifeProduct.WindowsServer, "Windows Server 2016 or newer",
            FeatureSupport.Full,
            new(SupportLevel.Yes, "Through Action1"),
            new(SupportLevel.Partly, "Not on Server Core or Nano Server"),
            FeatureSupport.Full,
            new(SupportLevel.No, "Missing updates on the Patches tab, through Action1"),
            NoWebInterface),
        new(EndOfLifeProduct.Ubuntu, "Ubuntu 22.04, 24.04 and 26.04 LTS",
            FeatureSupport.Full,
            AptPatching,
            LinuxRemoteControl,
            FeatureSupport.Full,
            new(SupportLevel.Yes, "18.04 and 20.04 only with Ubuntu Pro"),
            NoWebInterface),
        new(EndOfLifeProduct.Debian, "Debian 12 and 13",
            FeatureSupport.Full,
            AptPatching,
            LinuxRemoteControl,
            FeatureSupport.Full,
            FeatureSupport.Full,
            NoWebInterface),
        new(EndOfLifeProduct.AlmaLinux, "AlmaLinux 8, 9 and 10",
            FeatureSupport.Full,
            DnfPatching,
            LinuxRemoteControl,
            FeatureSupport.Full,
            new(SupportLevel.Yes, "Security updates AlmaLinux released"),
            NoWebInterface),
        new(EndOfLifeProduct.ProxmoxVe, "Proxmox VE 8 and 9",
            FeatureSupport.Full,
            AptPatching,
            new(SupportLevel.No, "No desktop on a Proxmox VE host"),
            FeatureSupport.Full,
            new(SupportLevel.Partly, "Debian packages; Proxmox packages and kernel not covered"),
            new(SupportLevel.Yes, "Proxmox web interface")),
        new(EndOfLifeProduct.SynologyDsm, "Synology DSM 7",
            FeatureSupport.Full,
            new(SupportLevel.No, "DSM updates itself"),
            new(SupportLevel.No, "No desktop on DSM"),
            FeatureSupport.Full,
            new(SupportLevel.No, "DSM packages are not covered"),
            new(SupportLevel.Yes, "Synology web interface")),
    ];
}
