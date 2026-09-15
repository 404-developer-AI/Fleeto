using System.Globalization;
using System.Text.Json;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>Parsing and validation of the type-specific parameters of a check definition, following <see cref="CheckCatalog"/>.</summary>
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
    /// Validates a definition before it is saved. Returns human-readable problems (cause and next step), empty when the definition
    /// is valid. The parameters are expected to be cleaned with <see cref="CheckCatalog.CleanParameters"/>.
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
        else if (definition.Type == CheckType.Script && definition.IntervalSeconds < ScriptRules.MinCheckIntervalSeconds)
        {
            problems.Add($"A script check runs at most once a minute. Set an interval of at least {ScriptRules.MinCheckIntervalSeconds} seconds.");
        }

        if (definition.FailuresBeforeAlert is < 1 or > 100)
        {
            problems.Add("Failures before alert must be between 1 and 100.");
        }

        if (!Enum.IsDefined(definition.Type))
        {
            problems.Add("Choose a check type.");
            return problems;
        }

        var info = CheckCatalog.Get(definition.Type);
        foreach (var spec in info.Parameters.Where(s => s.AppliesTo(definition.Type, parameters)))
        {
            parameters.TryGetValue(spec.Name, out var value);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (spec.Required)
                {
                    problems.Add($"Enter the {spec.Label.ToLowerInvariant()}.");
                }

                continue;
            }

            if (ValidateValue(spec, value) is { } problem)
            {
                problems.Add($"{spec.Label}: {problem}");
            }
        }

        ValidateThresholds(definition, info, parameters, problems);
        return problems;
    }

    private static string? ValidateValue(CheckParameterSpec spec, string value)
    {
        if (value.Length > spec.MaxLength)
        {
            return $"at most {spec.MaxLength} characters.";
        }

        switch (spec.Kind)
        {
            case ParameterKind.Integer:
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                    (spec.Min is { } min && number < min) || (spec.Max is { } max && number > max))
                {
                    return $"enter a whole number between {spec.Min ?? 0} and {spec.Max ?? int.MaxValue}.";
                }

                break;
            case ParameterKind.Choice:
                if (spec.Choices?.Any(c => c.Value == value) != true)
                {
                    return "choose one of the options.";
                }

                break;
            case ParameterKind.Boolean:
                if (value is not ("true" or "false"))
                {
                    return "choose yes or no.";
                }

                break;
        }

        return spec.Validate?.Invoke(value);
    }

    private static void ValidateThresholds(CheckDefinition definition, CheckTypeInfo info, IReadOnlyDictionary<string, string> parameters, List<string> problems)
    {
        var warning = definition.WarningThreshold;
        var critical = definition.CriticalThreshold;
        var unit = info.UnitFor(parameters);
        switch (info.ThresholdKindFor(parameters))
        {
            case ThresholdKind.ExitCode:
                if (warning is not null || critical is not null)
                {
                    problems.Add("A script check has no thresholds: its exit code sets the status.");
                }

                break;
            case ThresholdKind.Flag:
                if (critical is not null || warning is not (null or 1))
                {
                    problems.Add("This check has no thresholds: a problem is critical, or a warning when you choose so.");
                }

                break;
            case ThresholdKind.Reachability:
                if (warning is < 0 || critical is < 0)
                {
                    problems.Add("Response time thresholds cannot be negative.");
                }

                if (warning is not null && critical is not null && warning > critical)
                {
                    problems.Add("The warning threshold must be lower than the critical threshold.");
                }

                break;
            case ThresholdKind.HigherIsWorse:
                if (warning is null && critical is null)
                {
                    problems.Add(unit == "%" ? "Set a warning or critical threshold." : $"Set a warning or critical threshold in {unit}.");
                }

                if (warning is < 0 || critical is < 0 || (unit == "%" && (warning > 100 || critical > 100)))
                {
                    problems.Add(unit == "%" ? "Thresholds are percentages between 0 and 100." : "Thresholds cannot be negative.");
                }

                if (warning is not null && critical is not null && warning > critical)
                {
                    problems.Add("The warning threshold must be lower than the critical threshold.");
                }

                break;
            case ThresholdKind.LowerIsWorse:
                if (warning is null && critical is null)
                {
                    problems.Add(unit == "%" ? "Set a warning or critical threshold." : $"Set a warning or critical threshold in {unit}.");
                }

                if (unit == "%" && (warning is < 0 or > 100 || critical is < 0 or > 100))
                {
                    problems.Add("Thresholds are percentages between 0 and 100.");
                }

                if (warning is not null && critical is not null && warning < critical)
                {
                    problems.Add(definition.Type == CheckType.DiskFree
                        ? "For free disk space the warning threshold must be higher than the critical threshold."
                        : "The warning threshold must be higher than the critical threshold.");
                }

                break;
        }
    }
}
