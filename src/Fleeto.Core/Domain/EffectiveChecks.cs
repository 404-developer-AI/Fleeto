using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>Where a check of an endpoint comes from.</summary>
public enum CheckSource
{
    /// <summary>A monitoring template linked to the endpoint's site.</summary>
    SiteTemplate,
    /// <summary>A monitoring template linked to this endpoint only.</summary>
    EndpointTemplate,
    /// <summary>A check that exists only on this endpoint.</summary>
    Endpoint,
    /// <summary>A monitoring template linked to the endpoint's client (0.6.0).</summary>
    ClientTemplate
}

/// <summary>A check definition that may apply to an endpoint, with where it was found.</summary>
/// <param name="TemplateAppliesTo">The endpoints the monitoring template of a template check is for (0.6.0).</param>
public sealed record CheckCandidate(CheckDefinition Definition, CheckSource Source, string? TemplateName,
    CheckAppliesTo TemplateAppliesTo = CheckAppliesTo.All);

/// <summary>
/// A check as it runs on one endpoint: the definition with the endpoint's overrides applied. Thresholds and failures are
/// applied by the workers; interval and parameters go into the signed agent configuration.
/// </summary>
public sealed record EffectiveCheck(
    CheckDefinition Definition,
    CheckSource Source,
    string? TemplateName,
    EndpointCheckOverride? Override,
    int IntervalSeconds,
    double? WarningThreshold,
    double? CriticalThreshold,
    int FailuresBeforeAlert)
{
    public Guid Id => Definition.Id;
    public string Name => Definition.Name;
    public CheckType Type => Definition.Type;

    /// <summary>True when the check is switched off for this endpoint by an override.</summary>
    public bool DisabledOnEndpoint => Override?.Disabled == true;

    public bool IntervalOverridden => Override?.IntervalSeconds is not null;
    public bool ThresholdsOverridden => Override?.OverrideThresholds == true;
    public bool FailuresOverridden => Override?.FailuresBeforeAlert is not null;
    public bool HasOverrides => IntervalOverridden || ThresholdsOverridden || FailuresOverridden;

    /// <summary>A copy of the definition carrying the effective values, for validation and alert titles.</summary>
    public CheckDefinition ToEffectiveDefinition() => new()
    {
        Id = Definition.Id,
        ClientId = Definition.ClientId,
        MonitoringTemplateId = Definition.MonitoringTemplateId,
        EndpointId = Definition.EndpointId,
        Name = Definition.Name,
        Type = Definition.Type,
        IntervalSeconds = IntervalSeconds,
        ParametersJson = Definition.ParametersJson,
        WarningThreshold = WarningThreshold,
        CriticalThreshold = CriticalThreshold,
        FailuresBeforeAlert = FailuresBeforeAlert,
        AppliesTo = Definition.AppliesTo,
        Enabled = Definition.Enabled
    };
}

/// <summary>
/// The one rule for which checks run on an endpoint. Used by the configuration builder (signer), the check evaluation
/// (workers) and the endpoint page (web); its SQL twin is <c>EffectiveCheckResolver.AppliesSql</c>, kept equal by a test.
/// <list type="bullet">
/// <item>A template check applies when it is enabled, its template is linked to the endpoint's client, its site or the endpoint,
/// both the check and its template (0.6.0) match the endpoint class and it is not disabled on the endpoint.</item>
/// <item>An endpoint-only check applies when it is enabled; it runs whatever the class.</item>
/// <item>Either kind applies only when its type runs on the endpoint's platform (<see cref="CheckCatalog.IsSupported"/>), so a Windows
/// event log check in a template linked to a mixed site never reaches a Linux endpoint.</item>
/// </list>
/// Tier is not part of the rule: every caller applies <see cref="TierRules.EffectiveTier"/> itself.
/// </summary>
public static class EffectiveChecks
{
    public static bool MatchesClass(CheckAppliesTo appliesTo, EndpointClass endpointClass) =>
        appliesTo == CheckAppliesTo.All ||
        (appliesTo == CheckAppliesTo.Server && endpointClass == EndpointClass.Server) ||
        (appliesTo == CheckAppliesTo.Workstation && endpointClass == EndpointClass.Workstation);

    /// <summary>
    /// Resolves the checks of an endpoint, ordered by id so the configuration hash is deterministic. With
    /// <paramref name="includeDisabledOnEndpoint"/> the checks switched off by an override are included (for the endpoint
    /// page); they never run.
    /// </summary>
    public static IReadOnlyList<EffectiveCheck> Resolve(EndpointClass endpointClass, IEnumerable<CheckCandidate> candidates,
        IReadOnlyDictionary<Guid, EndpointCheckOverride> overrides, bool includeDisabledOnEndpoint = false, string? osPlatform = null)
    {
        var result = new Dictionary<Guid, EffectiveCheck>();
        // The widest link first, so a template linked to both the client and the endpoint reports the client as its source.
        foreach (var candidate in candidates.OrderBy(c => Rank(c.Source)))
        {
            var definition = candidate.Definition;
            if (!definition.Enabled || result.ContainsKey(definition.Id) || !Enum.IsDefined(definition.Type) ||
                !CheckCatalog.IsSupported(definition.Type, CheckParameters.Parse(definition.ParametersJson), osPlatform))
            {
                continue;
            }

            if (candidate.Source == CheckSource.Endpoint)
            {
                if (definition.EndpointId is null)
                {
                    continue;
                }

                result[definition.Id] = new EffectiveCheck(definition, CheckSource.Endpoint, null, null, definition.IntervalSeconds,
                    definition.WarningThreshold, definition.CriticalThreshold, definition.FailuresBeforeAlert);
                continue;
            }

            if (definition.MonitoringTemplateId is null || !MatchesClass(definition.AppliesTo, endpointClass) ||
                !MatchesClass(candidate.TemplateAppliesTo, endpointClass))
            {
                continue;
            }

            overrides.TryGetValue(definition.Id, out var adjustment);
            if (adjustment?.Disabled == true && !includeDisabledOnEndpoint)
            {
                continue;
            }

            result[definition.Id] = new EffectiveCheck(
                definition,
                candidate.Source,
                candidate.TemplateName,
                adjustment,
                adjustment?.IntervalSeconds ?? definition.IntervalSeconds,
                adjustment?.OverrideThresholds == true ? adjustment.WarningThreshold : definition.WarningThreshold,
                adjustment?.OverrideThresholds == true ? adjustment.CriticalThreshold : definition.CriticalThreshold,
                adjustment?.FailuresBeforeAlert ?? definition.FailuresBeforeAlert);
        }

        return result.Values.OrderBy(c => c.Id).ToList();
    }

    private static int Rank(CheckSource source) => source switch
    {
        CheckSource.ClientTemplate => 0,
        CheckSource.SiteTemplate => 1,
        CheckSource.EndpointTemplate => 2,
        _ => 3
    };
}
