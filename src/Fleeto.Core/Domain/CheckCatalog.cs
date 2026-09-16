using System.Globalization;
using System.Text.RegularExpressions;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>How the value of a check turns into a status (<see cref="CheckEvaluator"/>).</summary>
public enum ThresholdKind
{
    /// <summary>1 is fine, 0 is a problem: critical, or a warning when the warning threshold is set to 1.</summary>
    Flag,
    /// <summary>A higher value is worse: warning and critical from the thresholds.</summary>
    HigherIsWorse,
    /// <summary>A lower value is worse: warning and critical from the thresholds.</summary>
    LowerIsWorse,
    /// <summary>-1 means no response (critical); otherwise a time in milliseconds, higher is worse.</summary>
    Reachability,
    /// <summary>The exit code of a script: 0 OK, 1 warning, any other code critical. No thresholds.</summary>
    ExitCode
}

/// <summary>The platforms a check type runs on.</summary>
[Flags]
public enum CheckPlatforms
{
    Windows = 1,
    Linux = 2,
    MacOs = 4,
    All = Windows | Linux | MacOs
}

public enum ParameterKind
{
    Text,
    Integer,
    Choice,
    Boolean,
    /// <summary>A service name; the check dialog suggests the services seen on the endpoints.</summary>
    Service,
    /// <summary>The id of a library script; the check dialog offers the scripts the check may use.</summary>
    Script
}

public sealed record ParameterChoice(string Value, string Label);

/// <summary>
/// One parameter of a check type. <see cref="VisibleWhen"/> names another parameter and the values for which this one applies;
/// a parameter that does not apply is dropped before saving.
/// </summary>
public sealed record CheckParameterSpec(
    string Name,
    string Label,
    ParameterKind Kind,
    bool Required = false,
    string? Default = null,
    string? Help = null,
    int MaxLength = 200,
    int? Min = null,
    int? Max = null,
    IReadOnlyList<ParameterChoice>? Choices = null,
    (string Parameter, string[] Values)? VisibleWhen = null,
    Func<string, string?>? Validate = null)
{
    public bool AppliesTo(CheckType type, IReadOnlyDictionary<string, string> parameters) =>
        VisibleWhen is not { } condition ||
        condition.Values.Contains(CheckCatalog.ParameterOrDefault(type, parameters, condition.Parameter), StringComparer.OrdinalIgnoreCase);
}

/// <summary>What one check type is: label, description, platforms, parameters, and how its value is judged and shown.</summary>
public sealed record CheckTypeInfo(
    CheckType Type,
    string Label,
    string Description,
    CheckPlatforms Platforms,
    IReadOnlyList<CheckParameterSpec> Parameters,
    int DefaultIntervalSeconds,
    double? DefaultWarning,
    double? DefaultCritical,
    Func<IReadOnlyDictionary<string, string>, ThresholdKind> ThresholdKindFor,
    Func<IReadOnlyDictionary<string, string>, string> UnitFor,
    string ThresholdHelp,
    string? FlagWarningLabel = null,
    Func<IReadOnlyDictionary<string, string>, CheckPlatforms>? PlatformsFor = null)
{
    public bool SupportsWindowsOnly => Platforms == CheckPlatforms.Windows;

    /// <summary>The platforms a check of this type with these parameters runs on (a script check follows its script's language).</summary>
    public CheckPlatforms PlatformsOf(IReadOnlyDictionary<string, string> parameters) => PlatformsFor?.Invoke(parameters) ?? Platforms;
}

/// <summary>
/// The check catalog of Fleeto (ROADMAP 0.2.0, decided 2026-09-15): every check type described once, so validation, evaluation,
/// alert titles, the signed configuration and the check dialog all follow the same description. The agent measures; this
/// catalog decides what a value means.
/// </summary>
public static partial class CheckCatalog
{
    private static readonly IReadOnlyList<ParameterChoice> FileConditions =
    [
        new("exists", "Must exist"),
        new("missing", "Must not exist"),
        new("size", "Size (MB)"),
        new("age", "Hours since the last change")
    ];

    private static readonly IReadOnlyList<ParameterChoice> CertificateLocations =
    [
        new("store", "Windows certificate store"),
        new("path", "File or folder")
    ];

    private static readonly IReadOnlyList<ParameterChoice> EventLevels =
    [
        new("critical", "Critical"),
        new("error", "Error or critical"),
        new("warning", "Warning, error or critical"),
        new("any", "Any level")
    ];

    private static readonly IReadOnlyList<ParameterChoice> SecurityComponents =
    [
        new("antivirus", "Antivirus"),
        new("firewall", "Firewall")
    ];

    private static readonly Dictionary<CheckType, CheckTypeInfo> Types = new[]
    {
        new CheckTypeInfo(CheckType.CpuUsage, "CPU usage", "Average CPU usage over one minute (or the interval, when shorter).",
            CheckPlatforms.All, [], 300, 85, 95, _ => ThresholdKind.HigherIsWorse, _ => "%",
            "Alert when usage reaches or exceeds these percentages."),
        new CheckTypeInfo(CheckType.MemoryUsage, "Memory usage", "Physical memory in use.",
            CheckPlatforms.All, [], 300, 85, 95, _ => ThresholdKind.HigherIsWorse, _ => "%",
            "Alert when usage reaches or exceeds these percentages."),
        new CheckTypeInfo(CheckType.DiskFree, "Free disk space", "Free space per drive or mount point.",
            CheckPlatforms.All,
            [new("drive", "Drive", ParameterKind.Text, Required: true, Default: "*", MaxLength: 256,
                Help: "For example C: or /var, or * for every fixed drive.", Validate: Printable)],
            900, 15, 5, _ => ThresholdKind.LowerIsWorse, _ => "%",
            "Alert when free space drops to or below these percentages."),
        new CheckTypeInfo(CheckType.ServiceRunning, "Service running", "Whether a service is running.",
            CheckPlatforms.All,
            [new("service", "Service name", ParameterKind.Service, Required: true, MaxLength: 256,
                Help: "The service name on Windows or the systemd unit on Linux, not the display name. Pick one from the list or type a name.",
                Validate: ServiceName)],
            300, null, null, _ => ThresholdKind.Flag, _ => string.Empty, string.Empty,
            "Report a stopped service as a warning instead of critical"),
        new CheckTypeInfo(CheckType.Uptime, "Uptime", "Days since the last restart.",
            CheckPlatforms.All, [], 3600, 30, 60, _ => ThresholdKind.HigherIsWorse, _ => "days",
            "Alert when the endpoint has not restarted for this many days."),
        new CheckTypeInfo(CheckType.Ping, "Ping", "Whether a host answers ping from this endpoint, and how fast.",
            CheckPlatforms.All,
            [
                new("host", "Host", ParameterKind.Text, Required: true, MaxLength: 253, Help: "Host name or IP address, for example 10.0.0.1.",
                    Validate: HostName),
                new("count", "Pings per run", ParameterKind.Integer, Default: "3", Min: 1, Max: 10)
            ],
            60, null, null, _ => ThresholdKind.Reachability, _ => "ms",
            "No reply is always critical. Optionally alert when the average round-trip time reaches these milliseconds."),
        new CheckTypeInfo(CheckType.TcpPort, "TCP port", "Whether a TCP port can be reached from this endpoint, and how fast.",
            CheckPlatforms.All,
            [
                new("host", "Host", ParameterKind.Text, Required: true, MaxLength: 253, Help: "Host name or IP address.", Validate: HostName),
                new("port", "Port", ParameterKind.Integer, Required: true, Min: 1, Max: 65535),
                new("timeout_seconds", "Timeout (seconds)", ParameterKind.Integer, Default: "5", Min: 1, Max: 60)
            ],
            60, null, null, _ => ThresholdKind.Reachability, _ => "ms",
            "A port that cannot be reached is always critical. Optionally alert when connecting takes these milliseconds."),
        new CheckTypeInfo(CheckType.Http, "HTTP(S) URL", "Whether a URL answers with the expected status, how fast, and when its certificate expires.",
            CheckPlatforms.All,
            [
                new("url", "URL", ParameterKind.Text, Required: true, MaxLength: 2048, Help: "For example https://intranet.example/health.", Validate: HttpUrl),
                new("expected_status", "Expected status codes", ParameterKind.Text, Default: "200-399", MaxLength: 100,
                    Help: "Codes or ranges, for example 200 or 200-299,301.", Validate: StatusCodes),
                new("contains", "Response must contain", ParameterKind.Text, MaxLength: 200, Help: "Optional text the first megabyte of the response must contain.",
                    Validate: Printable),
                new("timeout_seconds", "Timeout (seconds)", ParameterKind.Integer, Default: "10", Min: 1, Max: 60),
                new("certificate_warning_days", "Certificate warning (days)", ParameterKind.Integer, Default: "14", Min: 0, Max: 365,
                    Help: "HTTPS only: warn when the certificate expires within this many days. 0 turns it off."),
                new("certificate_critical_days", "Certificate critical (days)", ParameterKind.Integer, Default: "3", Min: 0, Max: 365),
                new("ignore_certificate_errors", "Accept an untrusted certificate", ParameterKind.Boolean, Default: "false",
                    Help: "Only for internal sites with a self-signed certificate. The expiry is still checked.")
            ],
            300, null, null, _ => ThresholdKind.Reachability, _ => "ms",
            "A failed request or an unexpected status is always critical. Optionally alert when the response takes these milliseconds."),
        new CheckTypeInfo(CheckType.ProcessRunning, "Process running", "Whether at least one process with this name runs.",
            CheckPlatforms.All,
            [new("process", "Process name", ParameterKind.Text, Required: true, MaxLength: 260, Help: "For example sqlservr.exe or nginx.",
                Validate: ProcessName)],
            300, null, null, _ => ThresholdKind.Flag, _ => string.Empty, string.Empty, "Report a missing process as a warning instead of critical"),
        new CheckTypeInfo(CheckType.PendingReboot, "Pending restart", "Whether the endpoint needs a restart to finish updates or changes.",
            CheckPlatforms.Windows | CheckPlatforms.Linux, [], 3600, 1, null, _ => ThresholdKind.Flag, _ => string.Empty, string.Empty,
            "Report a pending restart as a warning instead of critical"),
        new CheckTypeInfo(CheckType.File, "File or folder", "Whether a file or folder exists, its size, or how long ago it changed.",
            CheckPlatforms.All,
            [
                new("path", "Path", ParameterKind.Text, Required: true, MaxLength: 1024, Help: @"Full path, for example D:\Backups\nightly.bak or /var/backups.",
                    Validate: AbsolutePath),
                new("condition", "Check", ParameterKind.Choice, Required: true, Default: "exists", Choices: FileConditions,
                    Help: "For a folder, size counts every file in it and the age is that of its newest file.")
            ],
            900, null, null,
            p => ParameterOrDefault(CheckType.File, p, "condition") is "size" or "age" ? ThresholdKind.HigherIsWorse : ThresholdKind.Flag,
            p => ParameterOrDefault(CheckType.File, p, "condition") switch { "size" => "MB", "age" => "hours", _ => string.Empty },
            "Alert when the value reaches or exceeds these thresholds.", "Report a problem as a warning instead of critical"),
        new CheckTypeInfo(CheckType.CertificateExpiry, "Certificate expiry", "Days until local certificates expire.",
            CheckPlatforms.All,
            [
                new("location", "Certificates in", ParameterKind.Choice, Required: true, Default: "store", Choices: CertificateLocations,
                    Help: "The Windows certificate store, or a PEM or DER file or a folder of them (every platform)."),
                new("store", "Store", ParameterKind.Text, Required: true, Default: @"LocalMachine\My", MaxLength: 80, VisibleWhen: ("location", ["store"]),
                    Help: @"For example LocalMachine\My or LocalMachine\WebHosting.", Validate: CertificateStore),
                new("path", "Path", ParameterKind.Text, Required: true, MaxLength: 1024, VisibleWhen: ("location", ["path"]), Validate: AbsolutePath),
                new("subject", "Subject contains", ParameterKind.Text, MaxLength: 200,
                    Help: "Optional. Only certificates whose subject contains this text. A certificate replaced by a newer one with the same subject is skipped.",
                    Validate: Printable)
            ],
            86400, 30, 7, _ => ThresholdKind.LowerIsWorse, _ => "days",
            "Alert when a certificate expires within this many days."),
        new CheckTypeInfo(CheckType.EventLog, "Event log", "Number of matching events in a Windows event log within a time window.",
            CheckPlatforms.Windows,
            [
                new("log", "Log", ParameterKind.Text, Required: true, Default: "System", MaxLength: 200,
                    Help: "System, Application, or a channel such as Microsoft-Windows-TaskScheduler/Operational.", Validate: EventLogName),
                new("source", "Source", ParameterKind.Text, MaxLength: 200, Help: "Optional, for example Service Control Manager.", Validate: EventSource),
                new("event_ids", "Event IDs", ParameterKind.Text, MaxLength: 120, Help: "Optional, up to 20 ids separated by commas, for example 7031,7034.",
                    Validate: EventIds),
                new("level", "Level", ParameterKind.Choice, Required: true, Default: "error", Choices: EventLevels),
                new("window_minutes", "Within the last (minutes)", ParameterKind.Integer, Default: "60", Min: 1, Max: 1440)
            ],
            900, 1, null, _ => ThresholdKind.HigherIsWorse, _ => "events",
            "Alert when this many matching events or more were logged within the window."),
        new CheckTypeInfo(CheckType.SecurityCenter, "Antivirus and firewall", "Whether Windows reports antivirus or firewall protection as on.",
            CheckPlatforms.Windows,
            [new("component", "Protection", ParameterKind.Choice, Required: true, Default: "antivirus", Choices: SecurityComponents,
                Help: "Antivirus uses Windows Security Center (workstations) or Microsoft Defender (servers).")],
            3600, null, null, _ => ThresholdKind.Flag, _ => string.Empty, string.Empty, "Report protection that is off as a warning instead of critical"),
        new CheckTypeInfo(CheckType.Script, "Script", "Runs a script from the script library as SYSTEM or root; its exit code sets the status.",
            CheckPlatforms.All,
            [new(ScriptParameter, "Script", ParameterKind.Script, Required: true, MaxLength: 36,
                Help: "Exit code 0 is OK, 1 a warning and any other code critical. The first line of output is shown as the detail.", Validate: ScriptId)],
            900, null, null, _ => ThresholdKind.ExitCode, _ => string.Empty,
            "Exit code 0 is OK, 1 is a warning and any other exit code is critical. A script that times out could not run.",
            PlatformsFor: ScriptPlatforms)
    }.ToDictionary(t => t.Type);

    /// <summary>Parameter of a script check holding the script id.</summary>
    public const string ScriptParameter = "script";

    /// <summary>
    /// Parameter of a script check holding the script's language, copied from the script when the check is saved (a script's language
    /// never changes). It lets the platform rule stay a rule on the check alone; the signer checks the script itself again.
    /// </summary>
    public const string ScriptLanguageParameter = "language";

    private static CheckPlatforms ScriptPlatforms(IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue(ScriptLanguageParameter, out var text) && Enum.GetNames<ScriptLanguage>().Contains(text, StringComparer.Ordinal) &&
        Enum.Parse<ScriptLanguage>(text) is var language
            ? language is ScriptLanguage.PowerShell or ScriptLanguage.Batch ? CheckPlatforms.Windows : CheckPlatforms.Linux | CheckPlatforms.MacOs
            : CheckPlatforms.All;

    public static IEnumerable<CheckTypeInfo> All => Types.Values;

    public static CheckTypeInfo Get(CheckType type) =>
        Types.TryGetValue(type, out var info) ? info : throw new ArgumentOutOfRangeException(nameof(type), type, "The check type is not in the catalog.");

    public static ThresholdKind ThresholdKindOf(CheckType type, IReadOnlyDictionary<string, string> parameters) => Get(type).ThresholdKindFor(parameters);

    public static string UnitOf(CheckType type, IReadOnlyDictionary<string, string> parameters) => Get(type).UnitFor(parameters);

    /// <summary>The parameter value, or its default for this check type when it is not set.</summary>
    public static string ParameterOrDefault(CheckType type, IReadOnlyDictionary<string, string> parameters, string name)
    {
        if (parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Types.TryGetValue(type, out var info) ? info.Parameters.FirstOrDefault(p => p.Name == name)?.Default ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// The parameters to store for a check: only those of its type that apply (see <see cref="CheckParameterSpec.VisibleWhen"/>),
    /// trimmed, without empty values.
    /// </summary>
    public static Dictionary<string, string> CleanParameters(CheckType type, IReadOnlyDictionary<string, string> input)
    {
        var trimmed = input.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.Ordinal);
        return Get(type).Parameters
            .Where(spec => trimmed.ContainsKey(spec.Name) && spec.AppliesTo(type, trimmed))
            .ToDictionary(spec => spec.Name, spec => trimmed[spec.Name], StringComparer.Ordinal);
    }

    /// <summary>
    /// True when the check type runs on an endpoint with this OS platform ("windows", "linux", "darwin"). An unknown platform
    /// counts as supported: the agent then reports what it cannot run.
    /// </summary>
    public static bool IsSupported(CheckType type, string? osPlatform) => PlatformOf(osPlatform) is not { } platform || Get(type).Platforms.HasFlag(platform);

    /// <summary>
    /// True when a check of this type with these parameters runs on the platform. Differs from the type-level rule only for script
    /// checks, whose platform follows the script's language.
    /// </summary>
    public static bool IsSupported(CheckType type, IReadOnlyDictionary<string, string> parameters, string? osPlatform) =>
        PlatformOf(osPlatform) is not { } platform || Get(type).PlatformsOf(parameters).HasFlag(platform);

    public static CheckPlatforms? PlatformOf(string? osPlatform) => osPlatform?.ToLowerInvariant() switch
    {
        "windows" => CheckPlatforms.Windows,
        "linux" => CheckPlatforms.Linux,
        "darwin" => CheckPlatforms.MacOs,
        _ => null
    };

    public static string PlatformLabel(CheckPlatforms platforms) => platforms switch
    {
        // macOS is not supported for now (decided 2026-09-15), so a check for every platform names the supported ones.
        CheckPlatforms.All => "Windows and Linux",
        CheckPlatforms.Windows => "Windows only",
        CheckPlatforms.Windows | CheckPlatforms.Linux => "Windows and Linux",
        _ => string.Join(", ", Enum.GetValues<CheckPlatforms>().Where(p => p != CheckPlatforms.All && platforms.HasFlag(p)))
    };

    /// <summary>
    /// SQL condition, true when check definition <c>d</c> may run on endpoint <c>e</c> by platform (the twin of <see cref="IsSupported"/>).
    /// Built from the catalog, so a new platform-limited type is covered automatically.
    /// </summary>
    public static string PlatformSql
    {
        get
        {
            var limited = Types.Values.Where(t => t.Platforms != CheckPlatforms.All).OrderBy(t => t.Type).ToList();
            var clauses = limited.Select(t =>
                $"(d.\"Type\" <> '{t.Type}' OR lower(e.\"OsPlatform\") NOT IN ('windows', 'linux', 'darwin') OR lower(e.\"OsPlatform\") IN ({PlatformNames(t.Platforms)}))")
                .ToList();

            // A script check follows the language copied into its parameters (see ScriptPlatforms); an unknown language runs anywhere.
            clauses.Add($"(d.\"Type\" <> '{CheckType.Script}' OR lower(e.\"OsPlatform\") NOT IN ('windows', 'linux', 'darwin') " +
                $"OR COALESCE(d.\"ParametersJson\"->>'{ScriptLanguageParameter}', '') NOT IN ('{ScriptLanguage.PowerShell}', '{ScriptLanguage.Batch}', '{ScriptLanguage.Shell}', '{ScriptLanguage.Bash}') " +
                $"OR (d.\"ParametersJson\"->>'{ScriptLanguageParameter}' IN ('{ScriptLanguage.PowerShell}', '{ScriptLanguage.Batch}') AND lower(e.\"OsPlatform\") = 'windows') " +
                $"OR (d.\"ParametersJson\"->>'{ScriptLanguageParameter}' IN ('{ScriptLanguage.Shell}', '{ScriptLanguage.Bash}') AND lower(e.\"OsPlatform\") IN ('linux', 'darwin')))");
            return "(" + string.Join(" AND ", clauses) + ")";
        }
    }

    private static string PlatformNames(CheckPlatforms platforms) => string.Join(", ",
        new[] { (CheckPlatforms.Windows, "windows"), (CheckPlatforms.Linux, "linux"), (CheckPlatforms.MacOs, "darwin") }
            .Where(p => platforms.HasFlag(p.Item1)).Select(p => $"'{p.Item2}'"));

    // -----------------------------------------------------------------------------------------------------------------
    // Parameter validation. Values end up in a signed configuration the agent executes, so the rules are strict.
    // -----------------------------------------------------------------------------------------------------------------

    private static string? Printable(string value) =>
        value.Any(char.IsControl) ? "Remove line breaks and control characters." : null;

    private static string? ScriptId(string value) =>
        Guid.TryParseExact(value, "D", out _) ? null : "Choose a script from the list.";

    private static string? ServiceName(string value) =>
        value.IndexOfAny(['\\', '/', '"']) >= 0 || value.Any(char.IsControl) ? "The service name contains characters that are not allowed." : null;

    private static string? ProcessName(string value) =>
        value.IndexOfAny(['\\', '/', '"', '*', '?']) >= 0 || value.Any(char.IsControl)
            ? "Enter the process name only, without a folder or wildcards, for example sqlservr.exe."
            : null;

    private static string? HostName(string value) =>
        HostPattern().IsMatch(value) && !value.StartsWith('-') ? null : "Enter a host name or IP address, without a scheme, path or port.";

    private static string? HttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return "Enter a full URL starting with http:// or https://.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "Do not put a user name or password in the URL: it would be stored and shown in plain text.";
        }

        return value.Any(char.IsWhiteSpace) ? "Remove spaces from the URL." : null;
    }

    private static string? StatusCodes(string value)
    {
        if (!StatusPattern().IsMatch(value))
        {
            return "Enter status codes or ranges separated by commas, for example 200-299,301.";
        }

        foreach (var part in value.Split(','))
        {
            var bounds = part.Split('-').Select(b => int.Parse(b, CultureInfo.InvariantCulture)).ToArray();
            if (bounds.Any(b => b is < 100 or > 599) || (bounds.Length == 2 && bounds[0] > bounds[1]))
            {
                return "Status codes lie between 100 and 599, and a range starts with its lowest code.";
            }
        }

        return null;
    }

    private static string? AbsolutePath(string value)
    {
        if (value.Any(char.IsControl) || value.Contains('"'))
        {
            return "The path contains characters that are not allowed.";
        }

        return value.StartsWith('/') || WindowsPathPattern().IsMatch(value)
            ? null
            : @"Enter a full path, for example D:\Backups or /var/backups.";
    }

    private static string? CertificateStore(string value) =>
        StorePattern().IsMatch(value) ? null : @"Enter a store as LocalMachine\Name, for example LocalMachine\My.";

    private static string? EventLogName(string value) =>
        LogNamePattern().IsMatch(value) ? null : "The log name contains characters that are not allowed.";

    private static string? EventSource(string value) =>
        SourcePattern().IsMatch(value) ? null : "The source contains characters that are not allowed.";

    private static string? EventIds(string value)
    {
        if (!EventIdsPattern().IsMatch(value))
        {
            return "Enter up to 20 event ids separated by commas, for example 7031,7034.";
        }

        return value.Split(',').Any(id => int.Parse(id, CultureInfo.InvariantCulture) > 65535) ? "Event ids go up to 65535." : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:\-\[\]%]{1,253}$")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^\d{3}(-\d{3})?(,\d{3}(-\d{3})?)*$")]
    private static partial Regex StatusPattern();

    [GeneratedRegex(@"^([A-Za-z]:\\|\\\\[^\\]+\\)")]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"^LocalMachine\\[A-Za-z0-9 _\-]{1,64}$")]
    private static partial Regex StorePattern();

    [GeneratedRegex(@"^[A-Za-z0-9 /._\-]{1,200}$")]
    private static partial Regex LogNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9 ._\-]{1,200}$")]
    private static partial Regex SourcePattern();

    [GeneratedRegex(@"^\d{1,5}(,\d{1,5}){0,19}$")]
    private static partial Regex EventIdsPattern();
}
