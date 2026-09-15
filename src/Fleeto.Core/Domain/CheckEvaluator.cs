using System.Globalization;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// Turns a raw check value into a status. The workers are authoritative: the agent reports values, never
/// statuses, so a compromised or buggy agent cannot declare itself healthy with thresholds it invented.
/// </summary>
public static class CheckEvaluator
{
    /// <summary>Target of the certificate result of an HTTP(S) check.</summary>
    public const string CertificateTarget = "certificate";

    /// <summary>Evaluates one result against the thresholds of its definition (checks without parameters that affect the status).</summary>
    public static CheckStatus Evaluate(CheckType type, double value, string? error, double? warning, double? critical) =>
        Evaluate(type, string.Empty, value, error, warning, critical, new Dictionary<string, string>());

    /// <summary>Evaluates one result of one target against the thresholds and parameters of its (effective) definition.</summary>
    public static CheckStatus Evaluate(CheckType type, string target, double value, string? error, double? warning, double? critical,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (!string.IsNullOrEmpty(error) || double.IsNaN(value) || double.IsInfinity(value))
        {
            return CheckStatus.Unknown;
        }

        if (type == CheckType.Http && target == CertificateTarget)
        {
            return LowerIsWorse(value, Days(parameters, "certificate_warning_days"), Days(parameters, "certificate_critical_days"));
        }

        if (!Enum.IsDefined(type))
        {
            return CheckStatus.Unknown;
        }

        return CheckCatalog.ThresholdKindOf(type, parameters) switch
        {
            // 1 = fine. A problem is critical unless the technician chose "warning instead of critical" (warning threshold 1).
            ThresholdKind.Flag => value >= 1 ? CheckStatus.Ok : warning is >= 1 ? CheckStatus.Warning : CheckStatus.Critical,
            ThresholdKind.Reachability => value < 0 ? CheckStatus.Critical : HigherIsWorse(value, warning, critical),
            ThresholdKind.HigherIsWorse => HigherIsWorse(value, warning, critical),
            ThresholdKind.LowerIsWorse => LowerIsWorse(value, warning, critical),
            ThresholdKind.ExitCode => value == 0 ? CheckStatus.Ok : value == 1 ? CheckStatus.Warning : CheckStatus.Critical,
            _ => CheckStatus.Unknown
        };
    }

    /// <summary>Maps a non-OK status to the alert severity it raises. Unknown raises a warning.</summary>
    public static AlertSeverity SeverityFor(CheckStatus status) =>
        status == CheckStatus.Critical ? AlertSeverity.Critical : AlertSeverity.Warning;

    /// <summary>
    /// Alert title stating cause and next step (branding §8). Example:
    /// "SRV-DC01 has less than 10% free disk space on C:".
    /// </summary>
    public static string AlertTitle(string hostname, CheckDefinition definition, string target, CheckStatus status,
        double value, string? error)
    {
        if (status == CheckStatus.Unknown)
        {
            return $"{hostname}: check \"{definition.Name}\" could not run. Review the error on the endpoint page.";
        }

        var parameters = CheckParameters.Parse(definition.ParametersJson);
        string P(string name) => CheckCatalog.ParameterOrDefault(definition.Type, parameters, name);
        var threshold = status == CheckStatus.Critical ? definition.CriticalThreshold : definition.WarningThreshold;
        var t = threshold?.ToString("0.##", CultureInfo.InvariantCulture);
        var v = value.ToString("0.#", CultureInfo.InvariantCulture);

        return definition.Type switch
        {
            CheckType.CpuUsage => $"{hostname} has used {v}% CPU on average, above the {t}% threshold.",
            CheckType.MemoryUsage => $"{hostname} is using {v}% of its memory, above the {t}% threshold.",
            CheckType.DiskFree => $"{hostname} has less than {t}% free disk space on {DisplayTarget(target)} ({v}% free).",
            CheckType.ServiceRunning => $"Service {ParameterOrTarget(definition, "service", target)} is not running on {hostname}.",
            CheckType.Uptime => $"{hostname} has not restarted for {v} days, above the {t}-day threshold. Plan a restart.",
            CheckType.Ping => value < 0
                ? $"{P("host")} does not answer ping from {hostname}. Check that the host is up and reachable."
                : $"Ping from {hostname} to {P("host")} takes {v} ms, above the {t} ms threshold.",
            CheckType.TcpPort => value < 0
                ? $"Port {P("port")} on {P("host")} cannot be reached from {hostname}. Check that the service is running and not blocked."
                : $"Connecting from {hostname} to {P("host")}:{P("port")} takes {v} ms, above the {t} ms threshold.",
            CheckType.Http when target == CertificateTarget => value < 0
                ? $"The certificate of {P("url")} has expired (checked from {hostname}). Replace it."
                : $"The certificate of {P("url")} expires in {v} days (checked from {hostname}). Renew it before it expires.",
            CheckType.Http => value < 0
                ? $"{P("url")} did not answer as expected when checked from {hostname}. Review the detail on the endpoint page."
                : $"{P("url")} answers in {v} ms from {hostname}, above the {t} ms threshold.",
            CheckType.ProcessRunning => $"Process {P("process")} is not running on {hostname}.",
            CheckType.PendingReboot => $"{hostname} needs a restart to finish installing updates or changes. Plan a restart.",
            CheckType.File => P("condition") switch
            {
                "missing" => $"{P("path")} exists on {hostname}, but it should not. Check what created it.",
                "size" => $"{P("path")} on {hostname} is {v} MB, above the {t} MB threshold.",
                "age" => $"{P("path")} on {hostname} last changed {v} hours ago, above the {t}-hour threshold. Check the job that writes it.",
                _ => $"{P("path")} is missing on {hostname}."
            },
            CheckType.CertificateExpiry => value < 0
                ? $"Certificate {DisplayCertificate(target)} on {hostname} has expired. Replace it."
                : $"Certificate {DisplayCertificate(target)} on {hostname} expires in {v} days. Renew it before it expires.",
            CheckType.EventLog => $"{hostname} logged {v} matching events in the {P("log")} log within {P("window_minutes")} minutes. Review the events on the endpoint.",
            // Never the script's output: alert titles go out by email and webhook.
            CheckType.Script => value == 1
                ? $"Script check \"{definition.Name}\" reports a warning on {hostname} (exit code 1). Review the detail on the endpoint page."
                : $"Script check \"{definition.Name}\" reports a problem on {hostname} (exit code {value.ToString("0", CultureInfo.InvariantCulture)}). Review the detail on the endpoint page.",
            CheckType.SecurityCenter => P("component") == "firewall"
                ? $"The firewall is off on {hostname}{(string.IsNullOrEmpty(target) ? string.Empty : $" ({target})")}. Turn it on."
                : $"Antivirus protection is off or out of date on {hostname}. Check the antivirus software.",
            _ => $"{hostname}: check \"{definition.Name}\" is {status.ToString().ToLowerInvariant()}."
        };
    }

    private static double? Days(IReadOnlyDictionary<string, string> parameters, string name)
    {
        var text = CheckCatalog.ParameterOrDefault(CheckType.Http, parameters, name);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days > 0 ? days : null;
    }

    private static CheckStatus HigherIsWorse(double value, double? warning, double? critical)
    {
        if (critical is not null && value >= critical)
        {
            return CheckStatus.Critical;
        }

        return warning is not null && value >= warning ? CheckStatus.Warning : CheckStatus.Ok;
    }

    private static CheckStatus LowerIsWorse(double value, double? warning, double? critical)
    {
        if (critical is not null && value <= critical)
        {
            return CheckStatus.Critical;
        }

        // An expired certificate (negative days) is critical even without a critical threshold.
        if (value < 0 && (warning is not null || critical is not null))
        {
            return CheckStatus.Critical;
        }

        return warning is not null && value <= warning ? CheckStatus.Warning : CheckStatus.Ok;
    }

    private static string DisplayTarget(string target) => string.IsNullOrEmpty(target) ? "the system drive" : target;

    private static string DisplayCertificate(string target) => string.IsNullOrEmpty(target) ? "(unnamed)" : target;

    private static string ParameterOrTarget(CheckDefinition definition, string name, string target)
    {
        if (!string.IsNullOrEmpty(target))
        {
            return target;
        }

        var parameters = CheckParameters.Parse(definition.ParametersJson);
        return parameters.TryGetValue(name, out var value) ? value : definition.Name;
    }
}
