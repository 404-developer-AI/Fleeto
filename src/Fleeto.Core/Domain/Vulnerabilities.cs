using System.Text.RegularExpressions;

namespace Fleeto.Core.Domain;

/// <summary>The severity of a vulnerability on one scale for every distribution (VULNERABILITIES.md §7). Stored by name.</summary>
public enum VulnerabilitySeverity
{
    Unknown,
    Low,
    Medium,
    High,
    Critical
}

/// <summary>What a distribution says of a CVE on one package of one release. Stored by name.</summary>
public enum VulnerabilityRecordStatus
{
    /// <summary>A fixed version is released (it may need Ubuntu Pro, see <see cref="VulnerabilityRecord.ProService"/>).</summary>
    Fixed,

    /// <summary>Affected, no fix yet.</summary>
    NoFix,

    /// <summary>
    /// Not open on purpose: the distribution decided not to fix it (Debian's unimportant and end-of-life) or has not decided yet
    /// (Debian's undetermined). Shown apart, never counted as open.
    /// </summary>
    NotOpen
}

/// <summary>How an endpoint stands with a finding. Stored by name.</summary>
public enum VulnerabilityFixState
{
    /// <summary>A fix the endpoint can install.</summary>
    Fixable,

    /// <summary>No fix yet.</summary>
    NoFix,

    /// <summary>The fix is only in Ubuntu Pro, and the endpoint does not have the service that ships it.</summary>
    ProOnly,

    /// <summary>The distribution does not count it as open (<see cref="VulnerabilityRecordStatus.NotOpen"/>).</summary>
    NotOpen
}

/// <summary>Why a finding ended. Only <see cref="Fixed"/> counts as remediated. Stored by name.</summary>
public enum VulnerabilityEndReason
{
    /// <summary>A later inventory has the fixed version (or Livepatch patched the running kernel).</summary>
    Fixed,

    /// <summary>The package is gone from the endpoint.</summary>
    Removed,

    /// <summary>The feed no longer lists it for this package, or the endpoint is no longer matched (agent-only, release not covered).</summary>
    Withdrawn
}

/// <summary>Whether the packages of an endpoint can be judged at all. Stored by name.</summary>
public enum VulnerabilityCoverage
{
    /// <summary>Matched against the feeds of its distribution.</summary>
    Covered,

    /// <summary>The agent is older than 0.8.0 and reports no package details.</summary>
    AgentTooOld,

    /// <summary>A distribution Fleeto has no feed for.</summary>
    DistributionNotCovered,

    /// <summary>A release past its support (or past standard support without the Ubuntu Pro service that extends it).</summary>
    ReleaseNotSupported,

    /// <summary>The feed of its release has not been fetched yet.</summary>
    FeedsPending
}

/// <summary>
/// One statement of a feed: a CVE on one package of one release (VULNERABILITIES.md §5, §8). <see cref="Package"/> is the source
/// package on Ubuntu and Debian and the binary package on AlmaLinux. A kernel of Ubuntu is one record per CVE and kernel flavour,
/// with the pattern of <c>uname -r</c> that flavour runs as.
/// </summary>
public sealed record VulnerabilityRecord(
    string Distribution,
    string Release,
    string Package,
    string Cve,
    VulnerabilityRecordStatus Status,
    string? FixedVersion,
    string? ProService,
    VulnerabilitySeverity Severity,
    string? SourceSeverity,
    decimal? Cvss,
    string? Advisory,
    DateTime? PublishedAt,
    bool IsKernel = false,
    string? KernelPattern = null,
    string? ModuleStreams = null);

/// <summary>One finding of an endpoint: one CVE on one source package.</summary>
public sealed record VulnerabilityFinding(
    string Cve,
    string Package,
    string InstalledVersion,
    string? FixedVersion,
    VulnerabilityFixState State,
    VulnerabilitySeverity Severity,
    string? SourceSeverity,
    decimal? Cvss,
    string? Advisory,
    IReadOnlyList<string> Binaries,
    bool IsKernel);

/// <summary>The outcome of matching one endpoint.</summary>
public sealed record VulnerabilityMatch(
    VulnerabilityCoverage Coverage,
    string? Distribution,
    string? Release,
    IReadOnlyList<VulnerabilityFinding> Findings,
    IReadOnlyList<InstalledPackage> NotCovered);

/// <summary>The support of one release: until when it gets security fixes, and how long Ubuntu Pro extends that.</summary>
public sealed record ReleaseSupport(string Distribution, string Release, string Codename, DateOnly StandardUntil, DateOnly? ExtendedUntil,
    string? ExtendedService);

/// <summary>
/// The distributions and releases vulnerability reporting covers (0.8.0, VULNERABILITIES.md §1), the severity scale, and the
/// matching of an endpoint against the records of its release (§7). Pure, so every rule is tested without a database.
/// </summary>
public static partial class Vulnerabilities
{
    public const string Ubuntu = "ubuntu";
    public const string Debian = "debian";
    public const string AlmaLinux = "almalinux";

    /// <summary>The apt origins whose packages the feeds of Ubuntu and Debian cover; anything else is not covered.</summary>
    public static readonly IReadOnlySet<string> DistributionOrigins = new HashSet<string>(StringComparer.Ordinal)
    {
        "Ubuntu", "UbuntuESM", "UbuntuESMApps", "Debian"
    };

    /// <summary>
    /// The vendors of the packages of AlmaLinux itself: the early AlmaLinux 8 builds came out of the build system of CloudLinux and
    /// carry its name (322 of 814 packages on the AlmaLinux 8.5 test endpoint, 2026-10-26).
    /// </summary>
    public static readonly IReadOnlySet<string> AlmaLinuxVendors = new HashSet<string>(StringComparer.Ordinal) { "AlmaLinux", "CloudLinux" };

    /// <summary>
    /// The releases with their end of standard support and, for Ubuntu, of Ubuntu Pro (Expanded Security Maintenance). Debian counts
    /// its LTS years as standard: the security tracker follows them. Checked against the published life cycles on 2026-10-26.
    /// </summary>
    public static readonly IReadOnlyList<ReleaseSupport> Releases =
    [
        new(Ubuntu, "18.04", "bionic", new DateOnly(2023, 5, 31), new DateOnly(2028, 4, 30), "esm-infra"),
        new(Ubuntu, "20.04", "focal", new DateOnly(2025, 5, 31), new DateOnly(2030, 4, 30), "esm-infra"),
        new(Ubuntu, "22.04", "jammy", new DateOnly(2027, 6, 1), new DateOnly(2032, 4, 30), "esm-infra"),
        new(Ubuntu, "24.04", "noble", new DateOnly(2029, 5, 31), new DateOnly(2034, 4, 30), "esm-infra"),
        new(Ubuntu, "26.04", "resolute", new DateOnly(2031, 5, 31), new DateOnly(2036, 4, 30), "esm-infra"),
        new(Debian, "12", "bookworm", new DateOnly(2028, 6, 30), null, null),
        new(Debian, "13", "trixie", new DateOnly(2030, 6, 30), null, null),
        new(AlmaLinux, "8", "8", new DateOnly(2029, 3, 1), null, null),
        new(AlmaLinux, "9", "9", new DateOnly(2032, 5, 31), null, null),
        new(AlmaLinux, "10", "10", new DateOnly(2035, 5, 31), null, null),
    ];

    /// <summary>The release of an endpoint as the feeds name it: "22.04", "12", or the major version on AlmaLinux ("9.4" is "9").</summary>
    public static (string? Distribution, string? Release) ReleaseOf(PackageState state)
    {
        var id = state.DistributionId.Trim().ToLowerInvariant();
        var version = state.DistributionVersion.Trim();
        return id switch
        {
            Ubuntu => (Ubuntu, version),
            Debian => (Debian, version.Split('.')[0]),
            AlmaLinux => (AlmaLinux, version.Split('.')[0]),
            _ => (null, null)
        };
    }

    public static ReleaseSupport? Support(string distribution, string release) =>
        Releases.FirstOrDefault(r => r.Distribution == distribution && r.Release == release);

    /// <summary>
    /// Whether a release still gets security fixes on a day: within standard support, or within Ubuntu Pro when the endpoint has the
    /// service that extends it.
    /// </summary>
    public static bool IsSupported(ReleaseSupport support, IReadOnlyCollection<string> proServices, DateOnly today) =>
        today <= support.StandardUntil ||
        (support.ExtendedUntil is { } extended && today <= extended && support.ExtendedService is { } service && proServices.Contains(service));

    /// <summary>A feed that cannot be fetched for this long is an instance health problem; its findings stay and show their age.</summary>
    public static readonly TimeSpan FeedStaleAfter = TimeSpan.FromHours(48);

    /// <summary>Ended findings are kept this long for the history.</summary>
    public static readonly TimeSpan EndedRetention = TimeSpan.FromDays(395);

    /// <summary>The instance health problems of the vulnerability feeds: one per feed that failed for 48 hours.</summary>
    public static IReadOnlyList<HealthFinding> HealthFindings(IEnumerable<(string Key, DateTime? LastSuccessAt, DateTime? FailingSince, string? Problem)> feeds,
        DateTime now) =>
        feeds.Where(f => f.FailingSince is { } since && now - since >= FeedStaleAfter)
            .Select(f => new HealthFinding($"vulnerabilities:{f.Key}", Entities.HealthComponent.Vulnerabilities, Entities.AlertSeverity.Warning,
                $"The vulnerability feed {f.Key} could not be fetched for 48 hours",
                (f.LastSuccessAt is { } success
                    ? $"The vulnerabilities of its endpoints date from {success:yyyy-MM-dd HH:mm} UTC. "
                    : "Its endpoints show no vulnerabilities yet. ") +
                (f.Problem ?? "The feed could not be reached.") + " Check that the workers can reach the internet."))
            .ToList();

    /// <summary>Ubuntu's priority on the Fleeto scale.</summary>
    public static VulnerabilitySeverity FromUbuntuPriority(string? priority) => priority?.Trim().ToLowerInvariant() switch
    {
        "critical" => VulnerabilitySeverity.Critical,
        "high" => VulnerabilitySeverity.High,
        "medium" => VulnerabilitySeverity.Medium,
        "low" or "negligible" => VulnerabilitySeverity.Low,
        _ => VulnerabilitySeverity.Unknown
    };

    /// <summary>Debian's urgency on the Fleeto scale; "not yet assigned" and an empty urgency are Unknown.</summary>
    public static VulnerabilitySeverity FromDebianUrgency(string? urgency)
    {
        var text = urgency?.Trim().ToLowerInvariant().TrimEnd('*') ?? string.Empty;
        return text switch
        {
            "high" => VulnerabilitySeverity.High,
            "medium" => VulnerabilitySeverity.Medium,
            "low" or "unimportant" => VulnerabilitySeverity.Low,
            _ => VulnerabilitySeverity.Unknown
        };
    }

    /// <summary>The severity of an AlmaLinux errata, the first word of its summary ("Important: openssh security update").</summary>
    public static (VulnerabilitySeverity Severity, string? Word) FromAlmaLinuxSummary(string? summary)
    {
        var word = summary?.Split(':', 2)[0].Trim();
        return word?.ToLowerInvariant() switch
        {
            "critical" => (VulnerabilitySeverity.Critical, word),
            "important" => (VulnerabilitySeverity.High, word),
            "moderate" => (VulnerabilitySeverity.Medium, word),
            "low" => (VulnerabilitySeverity.Low, word),
            _ => (VulnerabilitySeverity.Unknown, null)
        };
    }

    /// <summary>The severity of a CVSS base score, used where the distribution gives no word of its own.</summary>
    public static VulnerabilitySeverity FromCvss(decimal? score) => score switch
    {
        >= 9.0m => VulnerabilitySeverity.Critical,
        >= 7.0m => VulnerabilitySeverity.High,
        >= 4.0m => VulnerabilitySeverity.Medium,
        > 0m => VulnerabilitySeverity.Low,
        _ => VulnerabilitySeverity.Unknown
    };

    /// <summary>Whether a package is covered by the feeds of its distribution, by the origin the agent reported.</summary>
    public static bool IsCovered(InstalledPackage package, PackageState state) =>
        state.PackageManager == PackageInventory.Rpm
            ? package.Origin is { } vendor && AlmaLinuxVendors.Contains(vendor)
            // Without apt lists the agent sends no origin; the tab then says the lists are missing rather than "not covered".
            : string.IsNullOrEmpty(package.Origin) || DistributionOrigins.Contains(package.Origin);

    /// <summary>
    /// Matches the packages of one endpoint against the records of its release (VULNERABILITIES.md §7). <paramref name="records"/>
    /// gives the records of a package name of the release; <paramref name="ubuntuPriority"/> the priority Ubuntu gave a CVE, the
    /// severity a Debian record falls back to. <paramref name="architecture"/> is the one the agent reports (amd64, arm64,
    /// riscv64): it tells kernel flavours apart that run under the same name.
    /// </summary>
    public static VulnerabilityMatch Match(PackageState? state, IReadOnlyList<InstalledPackage> packages, string? runningKernel, DateOnly today,
        Func<string, IReadOnlyList<VulnerabilityRecord>>? records, Func<string, VulnerabilitySeverity>? ubuntuPriority = null, string? architecture = null)
    {
        if (state is null)
        {
            return new VulnerabilityMatch(VulnerabilityCoverage.AgentTooOld, null, null, [], []);
        }

        var (distribution, release) = ReleaseOf(state);
        if (distribution is null || release is null)
        {
            return new VulnerabilityMatch(VulnerabilityCoverage.DistributionNotCovered, state.DistributionId, state.DistributionVersion, [], []);
        }

        if (Support(distribution, release) is not { } support || !IsSupported(support, state.UbuntuProServices, today))
        {
            return new VulnerabilityMatch(VulnerabilityCoverage.ReleaseNotSupported, distribution, release, [], []);
        }

        if (records is null)
        {
            return new VulnerabilityMatch(VulnerabilityCoverage.FeedsPending, distribution, release, [], []);
        }

        var enabledStreams = (state.ModuleStreams ?? []).ToHashSet(StringComparer.Ordinal);
        var covered = new List<InstalledPackage>();
        var notCovered = new List<InstalledPackage>();
        foreach (var package in packages)
        {
            var isCovered = IsCovered(package, state) &&
                            (distribution != AlmaLinux || !StreamUnknown(package, records(package.Name), enabledStreams));
            (isCovered ? covered : notCovered).Add(package);
        }

        var findings = distribution switch
        {
            AlmaLinux => MatchRpm(covered, runningKernel, records, enabledStreams),
            _ => MatchDpkg(distribution, covered, runningKernel, architecture, state, records, ubuntuPriority)
        };

        // A fix Livepatch applied to the running kernel counts as fixed.
        if (state.LivepatchActive && state.LivepatchFixes.Count > 0)
        {
            var patched = state.LivepatchFixes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            findings = findings.Where(f => !(f.IsKernel && patched.Contains(f.Cve))).ToList();
        }

        return new VulnerabilityMatch(VulnerabilityCoverage.Covered, distribution, release, findings, notCovered);
    }

    /// <summary>Ubuntu and Debian: per source package with its source version.</summary>
    private static List<VulnerabilityFinding> MatchDpkg(string distribution, IReadOnlyList<InstalledPackage> packages, string? runningKernel, string? architecture,
        PackageState state,
        Func<string, IReadOnlyList<VulnerabilityRecord>> records, Func<string, VulnerabilitySeverity>? ubuntuPriority)
    {
        var findings = new List<VulnerabilityFinding>();
        var proServices = state.UbuntuProServices.ToHashSet(StringComparer.Ordinal);

        // The kernel source package of Debian is "linux"; several versions are installed at once, and only the running one counts.
        var runningKernelPackage = runningKernel is null ? null : packages.FirstOrDefault(p => p.Name == "linux-image-" + runningKernel);
        foreach (var group in packages.GroupBy(p => SourceOf(p).Name, StringComparer.Ordinal))
        {
            var source = group.Key;
            string version;
            if (distribution == Debian && source == "linux")
            {
                if (runningKernelPackage is null)
                {
                    continue;
                }

                version = SourceOf(runningKernelPackage).Version;
            }
            else
            {
                version = group.Select(p => SourceOf(p).Version).Aggregate((a, b) => PackageVersions.CompareDpkg(a, b) >= 0 ? a : b);
            }

            var binaries = group.Select(p => p.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            foreach (var record in records(source).Where(r => !r.IsKernel))
            {
                if (Judge(record, version, proServices, PackageVersions.CompareDpkg) is { } state2)
                {
                    var (severity, word) = Severity(record, distribution, ubuntuPriority);
                    findings.Add(new VulnerabilityFinding(record.Cve, source, version, record.FixedVersion, state2, severity, word, record.Cvss, record.Advisory,
                        binaries, distribution == Debian && source == "linux"));
                }
            }
        }

        // Ubuntu: the running kernel, by the one flavour whose pattern its release matches. The RISC-V flavours run under the same
        // name as the generic one ("-generic"), so they count only on riscv64.
        if (distribution == Ubuntu && runningKernel is { Length: > 0 } && KernelAbi(runningKernel) is { } abi)
        {
            var riscv = string.Equals(architecture, "riscv64", StringComparison.OrdinalIgnoreCase);
            var flavour = records(KernelRecordsKey)
                .Where(r => r.KernelPattern is { } p && r.Package.Contains("riscv", StringComparison.Ordinal) == riscv && KernelMatches(p, runningKernel))
                .GroupBy(r => r.Package)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (flavour is not null)
            {
                foreach (var record in flavour)
                {
                    var fixedAbi = record.FixedVersion is null ? null : KernelAbi(record.FixedVersion);
                    var state2 = record.Status switch
                    {
                        VulnerabilityRecordStatus.Fixed when fixedAbi is not null && PackageVersions.CompareDpkg(abi, fixedAbi) >= 0 => (VulnerabilityFixState?)null,
                        VulnerabilityRecordStatus.Fixed => record.ProService is { } service && !proServices.Contains(service)
                            ? VulnerabilityFixState.ProOnly
                            : VulnerabilityFixState.Fixable,
                        VulnerabilityRecordStatus.NoFix => VulnerabilityFixState.NoFix,
                        _ => VulnerabilityFixState.NotOpen
                    };
                    if (state2 is { } fixState)
                    {
                        findings.Add(new VulnerabilityFinding(record.Cve, record.Package, runningKernel, record.FixedVersion, fixState,
                            record.Severity, record.SourceSeverity, record.Cvss, record.Advisory, [], true));
                    }
                }
            }
        }

        return findings;
    }

    /// <summary>The key under which the kernel records of an Ubuntu release are looked up.</summary>
    public const string KernelRecordsKey = "*kernel*";

    /// <summary>AlmaLinux: per installed binary package against the errata that name it, grouped by source package.</summary>
    private static List<VulnerabilityFinding> MatchRpm(IReadOnlyList<InstalledPackage> packages, string? runningKernel,
        Func<string, IReadOnlyList<VulnerabilityRecord>> records, IReadOnlySet<string> enabledStreams)
    {
        var running = RunningRpmKernel(runningKernel);
        var hits = new List<(InstalledPackage Package, string Version, VulnerabilityRecord Record)>();
        foreach (var group in packages.GroupBy(p => p.Name, StringComparer.Ordinal))
        {
            var isKernel = IsRpmKernel(group.Key);
            InstalledPackage? package;
            if (isKernel)
            {
                // Several kernels are installed; only the running one counts.
                package = running is null ? null : group.FirstOrDefault(p => p.Version == running);
            }
            else
            {
                package = group.Aggregate((a, b) => PackageVersions.CompareRpm(RpmEvr(a), RpmEvr(b)) >= 0 ? a : b);
            }

            if (package is null)
            {
                continue;
            }

            var evr = RpmEvr(package);
            foreach (var record in records(package.Name))
            {
                if (record.FixedVersion is null || !SameStream(package, record, enabledStreams))
                {
                    continue;
                }

                if (PackageVersions.CompareRpm(evr, record.FixedVersion) < 0)
                {
                    hits.Add((package, package.Version, record));
                }
            }
        }

        return hits.GroupBy(h => (h.Record.Cve, Source: SourceOf(h.Package).Name))
            .Select(g =>
            {
                var first = g.OrderBy(h => h.Record.Severity).Last();
                var binaries = g.Select(h => h.Package.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                var fixedVersion = g.Select(h => h.Record.FixedVersion!).Aggregate((a, b) => PackageVersions.CompareRpm(a, b) >= 0 ? a : b);
                return new VulnerabilityFinding(g.Key.Cve, g.Key.Source, first.Version, fixedVersion, VulnerabilityFixState.Fixable, first.Record.Severity,
                    first.Record.SourceSeverity, first.Record.Cvss, first.Record.Advisory, binaries, g.Any(h => IsRpmKernel(h.Package.Name)));
            })
            .ToList();
    }

    /// <summary>How a record stands for an installed version, or null when it does not apply (fixed already, or not affected).</summary>
    private static VulnerabilityFixState? Judge(VulnerabilityRecord record, string installed, IReadOnlySet<string> proServices, Func<string, string, int> compare) =>
        record.Status switch
        {
            VulnerabilityRecordStatus.Fixed when record.FixedVersion is null || compare(installed, record.FixedVersion) >= 0 => null,
            VulnerabilityRecordStatus.Fixed when record.ProService is { } service && !proServices.Contains(service) => VulnerabilityFixState.ProOnly,
            VulnerabilityRecordStatus.Fixed => VulnerabilityFixState.Fixable,
            VulnerabilityRecordStatus.NoFix => VulnerabilityFixState.NoFix,
            _ => VulnerabilityFixState.NotOpen
        };

    private static (VulnerabilitySeverity Severity, string? Word) Severity(VulnerabilityRecord record, string distribution,
        Func<string, VulnerabilitySeverity>? ubuntuPriority)
    {
        if (record.Severity != VulnerabilitySeverity.Unknown || distribution != Debian)
        {
            return (record.Severity, record.SourceSeverity);
        }

        // Debian has no urgency for most CVEs: Ubuntu's priority for the same CVE, named as such.
        var borrowed = ubuntuPriority?.Invoke(record.Cve) ?? VulnerabilitySeverity.Unknown;
        return borrowed == VulnerabilitySeverity.Unknown
            ? (FromCvss(record.Cvss), record.SourceSeverity)
            : (borrowed, $"Ubuntu: {borrowed.ToString().ToLowerInvariant()}");
    }

    /// <summary>The source package of a binary, or the binary itself when the agent named none.</summary>
    public static (string Name, string Version) SourceOf(InstalledPackage package) =>
        (string.IsNullOrEmpty(package.SourceName) ? package.Name : package.SourceName,
            string.IsNullOrEmpty(package.SourceVersion) ? package.Version : package.SourceVersion);

    /// <summary>The ABI of an Ubuntu kernel: "5.15.0-125-generic" and "5.15.0-125.135" are both "5.15.0-125".</summary>
    public static string? KernelAbi(string version)
    {
        var match = KernelAbiPattern().Match(version);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(?:\d+:)?(\d+\.\d+\.\d+-\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex KernelAbiPattern();

    /// <summary>Whether the running kernel is of the flavour a pattern of the feed describes. A pattern that does not compile matches nothing.</summary>
    public static bool KernelMatches(string pattern, string runningKernel)
    {
        try
        {
            return Regex.IsMatch(runningKernel, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsRpmKernel(string name) => name is "kernel" || name.StartsWith("kernel-", StringComparison.Ordinal) &&
        !name.StartsWith("kernel-tools", StringComparison.Ordinal) && !name.StartsWith("kernel-headers", StringComparison.Ordinal) &&
        name != "kernel-srpm-macros";

    /// <summary>"5.14.0-427.13.1.el9_4.x86_64" is the kernel package version "5.14.0-427.13.1.el9_4".</summary>
    private static string? RunningRpmKernel(string? uname)
    {
        if (string.IsNullOrEmpty(uname))
        {
            return null;
        }

        var dot = uname.LastIndexOf('.');
        return dot > 0 && uname[(dot + 1)..] is "x86_64" or "aarch64" or "ppc64le" or "s390x" ? uname[..dot] : uname;
    }

    private static string RpmEvr(InstalledPackage package) => package.Epoch > 0 ? $"{package.Epoch}:{package.Version}" : package.Version;

    /// <summary>Whether an rpm version is a build of a module stream ("3.3.1-9.module_el8.5.0+2586+018f24d7").</summary>
    public static bool IsModuleBuild(string version) =>
        version.Contains(".module_el", StringComparison.Ordinal) || version.Contains(".module+el", StringComparison.Ordinal);

    [GeneratedRegex(@"(?<![\w.+-])([A-Za-z0-9_.+-]+):([A-Za-z0-9_.+-]+)(?![\w.+-])", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleStreamPattern();

    /// <summary>
    /// The module streams an AlmaLinux errata names in its summary, as "name:stream" separated by commas: "Important: virt:rhel and
    /// virt-devel:rhel security update" gives "virt:rhel,virt-devel:rhel". Null when it names none.
    /// </summary>
    public static string? ModuleStreamsOf(string? summary)
    {
        if (string.IsNullOrEmpty(summary))
        {
            return null;
        }

        var streams = ModuleStreamPattern().Matches(summary).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
        return streams.Count == 0 ? null : string.Join(',', streams);
    }

    private static IEnumerable<string> StreamsOf(VulnerabilityRecord record) =>
        record.ModuleStreams is { } streams ? streams.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];

    private static string ModuleOf(string stream) => stream.Split(':')[0];

    /// <summary>
    /// A module build without %{MODULARITYLABEL} (every module package on the AlmaLinux 8.5 test endpoint) whose module has no stream
    /// enabled on the endpoint, or whose streams the agent did not report: which errata apply is unknown, so the package is not
    /// covered rather than shown clean.
    /// </summary>
    private static bool StreamUnknown(InstalledPackage package, IReadOnlyList<VulnerabilityRecord> records, IReadOnlySet<string> enabledStreams)
    {
        if (!string.IsNullOrEmpty(package.ModuleStream) || !IsModuleBuild(package.Version))
        {
            return false;
        }

        var modules = records.SelectMany(StreamsOf).Select(ModuleOf).ToHashSet(StringComparer.Ordinal);
        return modules.Count > 0 && !enabledStreams.Any(s => modules.Contains(ModuleOf(s)));
    }

    /// <summary>
    /// Whether an errata applies to the stream of an installed package (VULNERABILITIES.md §5): a package outside streams only to fixed
    /// versions outside module builds; a module build to the errata of its stream, from %{MODULARITYLABEL} or else from the streams
    /// enabled on the endpoint, as the errata names them in its summary.
    /// </summary>
    private static bool SameStream(InstalledPackage package, VulnerabilityRecord record, IReadOnlySet<string> enabledStreams)
    {
        var fixedVersion = record.FixedVersion!;
        if (!IsModuleBuild(package.Version) && string.IsNullOrEmpty(package.ModuleStream))
        {
            return !IsModuleBuild(fixedVersion);
        }

        if (!IsModuleBuild(fixedVersion))
        {
            return false;
        }

        var streams = StreamsOf(record).ToList();
        if (!string.IsNullOrEmpty(package.ModuleStream))
        {
            // "nginx:1.20:8040020210610090125:9edba152" is the stream "nginx:1.20".
            var parts = package.ModuleStream.Split(':');
            return streams.Count > 0 && parts.Length >= 2
                ? streams.Contains($"{parts[0]}:{parts[1]}", StringComparer.Ordinal)
                : SameStream(package.ModuleStream, fixedVersion);
        }

        return streams.Any(enabledStreams.Contains);
    }

    /// <summary>
    /// AlmaLinux 8 module streams by version, for an errata whose summary names no stream: a package of a stream is compared only
    /// with fixed versions of a module build of the same stream version, a package outside streams only with fixed versions outside
    /// module builds (VULNERABILITIES.md §5).
    /// </summary>
    public static bool SameStream(string? moduleStream, string fixedVersion)
    {
        var fixedIsModule = fixedVersion.Contains(".module_el", StringComparison.Ordinal) || fixedVersion.Contains(".module+el", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(moduleStream))
        {
            return !fixedIsModule;
        }

        if (!fixedIsModule)
        {
            return false;
        }

        var parts = moduleStream.Split(':');
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            return true;
        }

        var (_, version, _) = PackageVersions.SplitRpm(fixedVersion);
        return version == parts[1] || version.StartsWith(parts[1] + ".", StringComparison.Ordinal);
    }
}
