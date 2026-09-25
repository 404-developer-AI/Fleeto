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

/// <summary>
/// The one rule for which policy and patch policy apply to an endpoint (0.6.0): a link on the endpoint wins over one on its
/// site, and a link on the site over one on its client. Without any link the default policy applies, and no patch policy.
/// <para>
/// The rule exists three times, kept equal by a test: here in C#, as the SQL fragments below for set-based statements in
/// the gateway and the workers, and as the EF Core query <c>EffectivePolicies.Query</c> for the services. Nothing else may
/// compose the rule on its own.
/// </para>
/// </summary>
public static class EffectivePolicyRules
{
    /// <summary>
    /// The id of the policy of endpoint <c>e</c> (alias of "Endpoints"). The subqueries use the alias <c>xp</c>, which callers
    /// must not use for anything else in the same scope.
    /// </summary>
    public const string PolicyIdSql = """
        COALESCE(
          (SELECT xp."PolicyId" FROM "EndpointPolicies" xp WHERE xp."EndpointId" = e."Id"),
          (SELECT xp."PolicyId" FROM "SitePolicies" xp WHERE xp."SiteId" = e."SiteId"),
          (SELECT xp."PolicyId" FROM "ClientPolicies" xp WHERE xp."ClientId" = e."ClientId"),
          (SELECT xp."Id" FROM "Policies" xp WHERE xp."IsDefault" LIMIT 1))
        """;

    /// <summary>The id of the patch policy of endpoint <c>e</c> (alias of "Endpoints"), or NULL. Subqueries use the alias <c>xq</c>.</summary>
    public const string PatchPolicyIdSql = """
        COALESCE(
          (SELECT xq."PatchPolicyId" FROM "EndpointPatchPolicies" xq WHERE xq."EndpointId" = e."Id"),
          (SELECT xq."PatchPolicyId" FROM "SitePatchPolicies" xq WHERE xq."SiteId" = e."SiteId"),
          (SELECT xq."PatchPolicyId" FROM "ClientPatchPolicies" xq WHERE xq."ClientId" = e."ClientId"))
        """;

    /// <summary>The C# form of the rule: the most specific link wins, then the fallback.</summary>
    public static Guid? Resolve(Guid? endpoint, Guid? site, Guid? client, Guid? fallback = null) => endpoint ?? site ?? client ?? fallback;

    /// <summary>The level the most specific link sits on.</summary>
    public static LinkLevel Level(Guid? endpoint, Guid? site, Guid? client) =>
        endpoint is not null ? LinkLevel.Endpoint : site is not null ? LinkLevel.Site : client is not null ? LinkLevel.Client : LinkLevel.None;
}
