using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>Who can patch an endpoint (0.9.0, <c>PATCHING.md</c> §2).</summary>
public enum EndpointPatchEngine
{
    /// <summary>Nobody: the reason is a <see cref="PatchEngineReason"/>.</summary>
    None,

    /// <summary>Action1: its agent runs on the endpoint or Action1 reports the endpoint. Fleeto never patches it itself.</summary>
    Action1,

    /// <summary>The Fleeto agent, with the Windows Update Agent, apt or dnf of the endpoint.</summary>
    Fleeto
}

/// <summary>Why an endpoint has no patch engine.</summary>
public enum PatchEngineReason
{
    /// <summary>It has one.</summary>
    None,

    /// <summary>Discovered through an integration, without a Fleeto agent.</summary>
    NoAgent,

    /// <summary>Agent-only, or every endpoint behaves as agent-only after the license grace period.</summary>
    AgentOnly,

    /// <summary>Synology DSM updates itself; the agent never touches its packages.</summary>
    UpdatesItself,

    /// <summary>An operating system Fleeto does not patch.</summary>
    NotSupported
}

/// <summary>The engine of one endpoint, with the reason when it has none.</summary>
public readonly record struct PatchEngineResult(EndpointPatchEngine Engine, PatchEngineReason Reason)
{
    public static readonly PatchEngineResult Action1 = new(EndpointPatchEngine.Action1, PatchEngineReason.None);
    public static readonly PatchEngineResult Fleeto = new(EndpointPatchEngine.Fleeto, PatchEngineReason.None);

    public static PatchEngineResult Without(PatchEngineReason reason) => new(EndpointPatchEngine.None, reason);
}

/// <summary>
/// The one rule for which engine can patch an endpoint (0.9.0, decided 2026-11-04, <c>PATCHING.md</c> §2), in this order:
/// <list type="number">
/// <item>an endpoint without a Fleeto agent, or one that is (or behaves as) agent-only, has none;</item>
/// <item><b>Action1</b> when the agent reports an Action1 agent on the endpoint, or Action1 reports the endpoint (a patch state
/// with an Action1 id), so Fleeto never patches an endpoint that Action1 may patch as well;</item>
/// <item>otherwise <b>Fleeto</b> on Windows, Windows Server, Ubuntu, Debian, AlmaLinux and Proxmox VE, as
/// <see cref="EndOfLife.ProductOf"/> recognises them; Synology DSM updates itself, anything else is not supported.</item>
/// </list>
/// <para>
/// The engine of the endpoint's patch policy (<see cref="PatchPolicy.Engine"/>) only decides what Fleeto <i>does</i>: a Fleeto
/// policy on an endpoint whose engine is Action1 installs nothing. Whether the agent is new enough to scan and install is a separate
/// check of the step that needs it, not part of this rule, so the rule holds in plain SQL.
/// </para>
/// <para>
/// The rule exists three times, kept equal by a test: here in C#, as <see cref="EngineSql"/> for set-based statements, and as the
/// EF Core query <c>PatchEngines.Query</c> for the services. Nothing else may compose the rule on its own.
/// </para>
/// </summary>
public static class PatchEngineRules
{
    /// <summary>The C# form of the rule. <paramref name="effectiveTier"/> is the tier after the license (<see cref="TierRules.EffectiveTier"/>).</summary>
    public static PatchEngineResult EngineFor(EndpointSource source, EndpointTier effectiveTier, string? osPlatform, string? osName,
        string? action1AgentId, bool reportedByAction1)
    {
        if (source != EndpointSource.Agent)
        {
            return PatchEngineResult.Without(PatchEngineReason.NoAgent);
        }

        if (effectiveTier != EndpointTier.Managed)
        {
            return PatchEngineResult.Without(PatchEngineReason.AgentOnly);
        }

        if (!string.IsNullOrEmpty(action1AgentId) || reportedByAction1)
        {
            return PatchEngineResult.Action1;
        }

        return EndOfLife.ProductOf(osPlatform, osName) switch
        {
            EndOfLifeProduct.Windows or EndOfLifeProduct.WindowsServer or EndOfLifeProduct.Ubuntu or EndOfLifeProduct.Debian or
                EndOfLifeProduct.AlmaLinux or EndOfLifeProduct.ProxmoxVe => PatchEngineResult.Fleeto,
            EndOfLifeProduct.SynologyDsm => PatchEngineResult.Without(PatchEngineReason.UpdatesItself),
            _ => PatchEngineResult.Without(PatchEngineReason.NotSupported)
        };
    }

    /// <summary>The engine of <paramref name="endpoint"/> with its inventory and patch state (either may be null).</summary>
    public static PatchEngineResult EngineFor(Endpoint endpoint, LicenseStatus license, InventorySnapshot? inventory, EndpointPatchState? patchState) =>
        EngineFor(endpoint.Source, TierRules.EffectiveTier(endpoint.Tier, license), endpoint.OsPlatform, endpoint.OsName,
            inventory?.Action1AgentId, ReportedByAction1(patchState));

    /// <summary>True when the patch state comes from Action1: only Action1 fills its external id.</summary>
    public static bool ReportedByAction1(EndpointPatchState? patchState) => patchState is { ExternalEndpointId.Length: > 0 };

    /// <summary>The reason in words, for the Patches tab and the "not patched" list. Null when the endpoint has an engine.</summary>
    public static string? Describe(PatchEngineReason reason) => reason switch
    {
        PatchEngineReason.NoAgent => "No Fleeto agent on this endpoint.",
        PatchEngineReason.AgentOnly => "Agent-only. Switch the endpoint to managed to patch it.",
        PatchEngineReason.UpdatesItself => "DSM updates itself.",
        PatchEngineReason.NotSupported => "Fleeto does not patch this operating system.",
        _ => null
    };

    /// <summary>
    /// The engine of endpoint <c>e</c> (alias of "Endpoints") in SQL, as the names of <see cref="EndpointPatchEngine"/>:
    /// 'None', 'Action1' or 'Fleeto'. The statement must supply the boolean parameter <c>@allowsManaged</c>
    /// (<see cref="LicenseStatus.AllowsManaged"/>). The subqueries use the aliases <c>xi</c> and <c>xt</c>, which callers must
    /// not use for anything else in the same scope.
    /// </summary>
    public const string EngineSql = """
        (CASE
          WHEN e."Source" <> 'Agent' OR e."Tier" <> 'Managed' OR NOT @allowsManaged THEN 'None'
          WHEN EXISTS (SELECT 1 FROM "InventorySnapshots" xi WHERE xi."EndpointId" = e."Id" AND xi."Action1AgentId" <> '')
            OR EXISTS (SELECT 1 FROM "EndpointPatchStates" xt WHERE xt."EndpointId" = e."Id" AND xt."ExternalEndpointId" <> '') THEN 'Action1'
          WHEN e."OsPlatform" = 'windows' AND (btrim(e."OsName") ILIKE '%server%' OR btrim(e."OsName") ILIKE 'windows%') THEN 'Fleeto'
          WHEN e."OsPlatform" = 'linux' AND starts_with(btrim(e."OsName"), '
        """ + RemoteSessionRules.ProxmoxOsPrefix + """
        ') THEN 'Fleeto'
          WHEN e."OsPlatform" = 'linux' AND starts_with(btrim(e."OsName"), '
        """ + RemoteSessionRules.SynologyOsPrefix + """
        ') THEN 'None'
          WHEN e."OsPlatform" = 'linux' AND (btrim(e."OsName") ILIKE 'ubuntu%' OR btrim(e."OsName") ILIKE 'debian%'
            OR btrim(e."OsName") ILIKE 'almalinux%') THEN 'Fleeto'
          ELSE 'None'
        END)
        """;
}
