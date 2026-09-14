using System.Globalization;
using Fleetify.Core.Entities;

namespace Fleetify.Core.Domain;

/// <summary>
/// Turns a raw check value into a status. The workers are authoritative: the agent reports values, never
/// statuses, so a compromised or buggy agent cannot declare itself healthy with thresholds it invented.
/// </summary>
public static class CheckEvaluator
{
    /// <summary>Evaluates one result against the thresholds of its definition.</summary>
    public static CheckStatus Evaluate(CheckType type, double value, string? error, double? warning, double? critical)
    {
        if (!string.IsNullOrEmpty(error))
        {
            return CheckStatus.Unknown;
        }

        return type switch
        {
            // Higher is worse.
            CheckType.CpuUsage or CheckType.MemoryUsage or CheckType.Uptime => HigherIsWorse(value, warning, critical),
            // Lower is worse.
            CheckType.DiskFree => LowerIsWorse(value, warning, critical),
            // 1 = running. Not running is critical unless the technician configured a warning threshold of 1,
            // which marks "not running" as a warning instead.
            CheckType.ServiceRunning => value >= 1 ? CheckStatus.Ok : warning is >= 1 ? CheckStatus.Warning : CheckStatus.Critical,
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
            _ => $"{hostname}: check \"{definition.Name}\" is {status.ToString().ToLowerInvariant()}."
        };
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

        return warning is not null && value <= warning ? CheckStatus.Warning : CheckStatus.Ok;
    }

    private static string DisplayTarget(string target) => string.IsNullOrEmpty(target) ? "the system drive" : target;

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
