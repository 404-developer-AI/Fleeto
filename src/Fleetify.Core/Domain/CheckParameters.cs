using System.Text.Json;
using Fleetify.Core.Entities;

namespace Fleetify.Core.Domain;

/// <summary>Parsing and validation of the type-specific parameters of a check definition.</summary>
public static class CheckParameters
{
    /// <summary>Smallest allowed interval. The agent adds jitter, so tighter intervals only add load.</summary>
    public const int MinimumIntervalSeconds = 10;

    /// <summary>Largest allowed interval: once a month (31 days).</summary>
    public const int MaximumIntervalSeconds = 31 * 24 * 60 * 60;

    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static string Serialize(IReadOnlyDictionary<string, string> parameters) =>
        JsonSerializer.Serialize(parameters);

    /// <summary>
    /// Validates a definition before it is saved. Returns human-readable problems (cause and next step), empty
    /// when the definition is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(CheckDefinition definition)
    {
        var problems = new List<string>();
        var parameters = Parse(definition.ParametersJson);

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            problems.Add("Enter a name for the check.");
        }

        if (definition.IntervalSeconds < MinimumIntervalSeconds || definition.IntervalSeconds > MaximumIntervalSeconds)
        {
            problems.Add($"The interval must be between {MinimumIntervalSeconds} seconds and once a month.");
        }

        if (definition.FailuresBeforeAlert is < 1 or > 100)
        {
            problems.Add("Failures before alert must be between 1 and 100.");
        }

        switch (definition.Type)
        {
            case CheckType.CpuUsage:
            case CheckType.MemoryUsage:
                RequirePercent(definition, problems);
                if (definition.WarningThreshold is not null && definition.CriticalThreshold is not null &&
                    definition.WarningThreshold > definition.CriticalThreshold)
                {
                    problems.Add("The warning threshold must be lower than the critical threshold.");
                }
                break;
            case CheckType.DiskFree:
                RequirePercent(definition, problems);
                if (definition.WarningThreshold is not null && definition.CriticalThreshold is not null &&
                    definition.WarningThreshold < definition.CriticalThreshold)
                {
                    problems.Add("For free disk space the warning threshold must be higher than the critical threshold.");
                }
                if (!parameters.TryGetValue("drive", out var drive) || string.IsNullOrWhiteSpace(drive))
                {
                    problems.Add("Enter a drive such as C: or * for every fixed drive.");
                }
                break;
            case CheckType.ServiceRunning:
                if (!parameters.TryGetValue("service", out var service) || string.IsNullOrWhiteSpace(service))
                {
                    problems.Add("Enter the service name, for example Spooler.");
                }
                else if (service.Length > 256 || service.IndexOfAny(['\\', '/', '"']) >= 0)
                {
                    problems.Add("The service name contains characters that are not allowed.");
                }
                break;
            case CheckType.Uptime:
                if (definition.WarningThreshold is null && definition.CriticalThreshold is null)
                {
                    problems.Add("Set a warning or critical threshold in days.");
                }
                break;
        }

        return problems;
    }

    private static void RequirePercent(CheckDefinition definition, List<string> problems)
    {
        if (definition.WarningThreshold is null && definition.CriticalThreshold is null)
        {
            problems.Add("Set a warning or critical threshold.");
        }

        if (definition.WarningThreshold is < 0 or > 100 || definition.CriticalThreshold is < 0 or > 100)
        {
            problems.Add("Thresholds are percentages between 0 and 100.");
        }
    }
}
