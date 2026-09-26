using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>Where the policy or patch policy of an endpoint comes from (0.6.0).</summary>
public enum LinkLevel
{
    /// <summary>Nothing is linked: the default policy applies, or no patch policy at all.</summary>
    None,
    Client,
    Site,
    Endpoint
}

/// <summary>The policy and patch policy that apply to one endpoint, as <see cref="EffectivePolicyRules"/> resolves them.</summary>
public sealed class EffectivePolicyRow
{
    public Guid EndpointId { get; init; }
    public Guid ClientId { get; init; }
    public Guid SiteId { get; init; }

    /// <summary>The policy that applies; only null on an instance without a default policy, which setup never leaves.</summary>
    public Guid? PolicyId { get; init; }

    /// <summary>The patch policy that applies; null when none is linked on any level.</summary>
    public Guid? PatchPolicyId { get; init; }
}

/// <summary>One link as the rule sees it: its level, the class slot it sits in and the class its policy is for.</summary>
/// <param name="Slot">The class slot of the link on a client or site; <see cref="CheckAppliesTo.All"/> on an endpoint.</param>
/// <param name="PolicyAppliesTo">The endpoints the linked policy itself is for.</param>
public sealed record LevelLink(LinkLevel Level, CheckAppliesTo Slot, Guid PolicyId, CheckAppliesTo PolicyAppliesTo);

/// <summary>
/// The one rule for which policy and patch policy apply to an endpoint (0.6.0): a link on the endpoint wins over one on its
/// site, and a link on the site over one on its client. Without any link the default policy applies, and no patch policy.
/// <para>
/// A client or site holds one link for every endpoint or one per class (servers, workstations); a link counts for an
/// endpoint only when its slot and the policy itself are for the endpoint's class, so a policy for servers never reaches a
/// workstation, wherever it is linked. Within a level a slot for the class wins over one for every endpoint.
/// </para>
/// <para>
/// The rule exists three times, kept equal by a test: here in C#, as the SQL fragments below for set-based statements in
/// the gateway and the workers, and as the EF Core query <c>EffectivePolicies.Query</c> for the services. Nothing else may
/// compose the rule on its own.
/// </para>
/// </summary>
public static class EffectivePolicyRules
{
    /// <summary>The class of endpoint <c>e</c> in SQL, as stored: 'Server' or 'Workstation'.</summary>
    private const string ClassSql = """COALESCE(e."ClassOverride", e."DetectedClass")""";

    /// <summary>
    /// The id of the policy of endpoint <c>e</c> (alias of "Endpoints"). The subqueries use the aliases <c>xp</c> and
    /// <c>xq</c>, which callers must not use for anything else in the same scope.
    /// </summary>
    public const string PolicyIdSql = """
        COALESCE(
          (SELECT xp."PolicyId" FROM "EndpointPolicies" xp JOIN "Policies" xq ON xq."Id" = xp."PolicyId"
           WHERE xp."EndpointId" = e."Id" AND xq."AppliesTo" IN ('All',
        """ + ClassSql + """
        )),
          (SELECT xp."PolicyId" FROM "SitePolicies" xp JOIN "Policies" xq ON xq."Id" = xp."PolicyId"
           WHERE xp."SiteId" = e."SiteId" AND xp."AppliesTo" IN ('All',
        """ + ClassSql + """
        ) AND xq."AppliesTo" IN ('All',
        """ + ClassSql + """
        ) ORDER BY xp."AppliesTo" = 'All' LIMIT 1),
          (SELECT xp."PolicyId" FROM "ClientPolicies" xp JOIN "Policies" xq ON xq."Id" = xp."PolicyId"
           WHERE xp."ClientId" = e."ClientId" AND xp."AppliesTo" IN ('All',
        """ + ClassSql + """
        ) AND xq."AppliesTo" IN ('All',
        """ + ClassSql + """
        ) ORDER BY xp."AppliesTo" = 'All' LIMIT 1),
          (SELECT xp."Id" FROM "Policies" xp WHERE xp."IsDefault" LIMIT 1))
        """;

    /// <summary>The id of the patch policy of endpoint <c>e</c> (alias of "Endpoints"), or NULL. Subqueries use the aliases <c>xr</c> and <c>xs</c>.</summary>
    public const string PatchPolicyIdSql = """
        COALESCE(
          (SELECT xr."PatchPolicyId" FROM "EndpointPatchPolicies" xr JOIN "PatchPolicies" xs ON xs."Id" = xr."PatchPolicyId"
           WHERE xr."EndpointId" = e."Id" AND xs."AppliesTo" IN ('All',
        """ + ClassSql + """
        )),
          (SELECT xr."PatchPolicyId" FROM "SitePatchPolicies" xr JOIN "PatchPolicies" xs ON xs."Id" = xr."PatchPolicyId"
           WHERE xr."SiteId" = e."SiteId" AND xr."AppliesTo" IN ('All',
        """ + ClassSql + """
        ) AND xs."AppliesTo" IN ('All',
        """ + ClassSql + """
        ) ORDER BY xr."AppliesTo" = 'All' LIMIT 1),
          (SELECT xr."PatchPolicyId" FROM "ClientPatchPolicies" xr JOIN "PatchPolicies" xs ON xs."Id" = xr."PatchPolicyId"
           WHERE xr."ClientId" = e."ClientId" AND xr."AppliesTo" IN ('All',
        """ + ClassSql + """
        ) AND xs."AppliesTo" IN ('All',
        """ + ClassSql + """
        ) ORDER BY xr."AppliesTo" = 'All' LIMIT 1))
        """;

    /// <summary>True when something for <paramref name="appliesTo"/> is for an endpoint of <paramref name="endpointClass"/>.</summary>
    public static bool IsFor(CheckAppliesTo appliesTo, EndpointClass endpointClass) => EffectiveChecks.MatchesClass(appliesTo, endpointClass);

    /// <summary>
    /// The C# form of the rule: the most specific level with a link that counts for the class wins, within a level the slot
    /// for the class wins over the one for every endpoint; else <paramref name="fallback"/>.
    /// </summary>
    public static LevelLink? Resolve(EndpointClass endpointClass, IEnumerable<LevelLink> links) =>
        links.Where(l => IsFor(l.Slot, endpointClass) && IsFor(l.PolicyAppliesTo, endpointClass))
            .OrderByDescending(l => l.Level)
            .ThenBy(l => l.Slot == CheckAppliesTo.All)
            .FirstOrDefault();

    /// <summary>The slot of a class: the server or the workstation slot.</summary>
    public static CheckAppliesTo SlotOf(EndpointClass endpointClass) =>
        endpointClass == EndpointClass.Server ? CheckAppliesTo.Server : CheckAppliesTo.Workstation;
}
