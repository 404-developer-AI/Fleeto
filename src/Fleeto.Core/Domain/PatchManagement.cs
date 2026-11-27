using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// Which patch management the instance works with (0.9.0, decided 2026-11-27, <c>PATCHING.md</c> §16): one choice for the whole
/// instance, made by an admin in Settings, Instance.
/// </summary>
public enum PatchManager
{
    /// <summary>
    /// Action1, as every instance before the choice existed: patch policies of engine Action1 become Action1 automations, Fleeto can
    /// install the Action1 agent, and patch policies of engine Fleeto patch the endpoints without the Action1 agent.
    /// </summary>
    Action1,

    /// <summary>
    /// Fleeto itself: every patch policy is of engine Fleeto, Fleeto never installs the Action1 agent, and the Action1 agents that
    /// remain are removed. The Action1 integration stays only to remove its agents and to read the state of endpoints that still
    /// have one.
    /// </summary>
    Fleeto
}

/// <summary>How Fleeto removes the Action1 agent from one endpoint when the instance patches with Fleeto.</summary>
public enum Action1RemovalWay
{
    /// <summary>The workers remove the endpoint from its Action1 organization, which makes Action1 uninstall its agent.</summary>
    Action1,

    /// <summary>A signed job that uninstalls the agent: Windows, managed, and Action1 does not report the endpoint.</summary>
    Job,

    /// <summary>Fleeto cannot remove it: somebody removes it in the Action1 console or on the endpoint.</summary>
    ByHand
}

/// <summary>What converting one patch policy of engine Action1 to engine Fleeto changes, for the confirmation and the audit log.</summary>
/// <param name="Dropped">The options only Action1 has that the policy loses, in words; empty when nothing is lost.</param>
/// <param name="Disabled">True when nothing the policy installed is left for Fleeto, so it is switched off rather than widened.</param>
public sealed record PatchPolicyConversion(IReadOnlyList<string> Dropped, bool Disabled);

/// <summary>
/// The rules of the instance-wide choice of patch management (0.9.0, <c>PATCHING.md</c> §16). The choice lives in one setting
/// (<see cref="SettingKey"/>); a missing or unknown value reads as <see cref="PatchManager.Action1"/>, the behaviour of every
/// instance before the choice existed, so nothing is taken away from an instance by a bad row.
/// </summary>
public static class PatchManagement
{
    /// <summary>The key of the setting, with the plain value <c>Action1</c> or <c>Fleeto</c>.</summary>
    public const string SettingKey = "patching.manager";

    /// <summary>Why Fleeto installs no Action1 agent while the instance patches with Fleeto: web, the workers and the signer say it.</summary>
    public const string InstallRefusal =
        "This instance patches with Fleeto, so Fleeto does not install the Action1 agent. An admin can choose Action1 in Settings, Instance.";

    /// <summary>Why a patch policy of engine Action1 or an "Action1 agent" check is refused while the instance patches with Fleeto.</summary>
    public const string Action1PolicyRefusal =
        "This instance patches with Fleeto, so a patch policy is always for patching by Fleeto. An admin can choose Action1 in Settings, Instance.";

    public const string Action1CheckRefusal =
        "This instance patches with Fleeto, so the Action1 agent check is not offered. An admin can choose Action1 in Settings, Instance.";

    /// <summary>Why the "Action1 agent" checks lose their result while the instance patches with Fleeto.</summary>
    public const string CheckForgottenReason = "This instance patches with Fleeto, so the Action1 agent is no longer checked.";

    /// <summary>The value of the setting.</summary>
    public static string ToValue(PatchManager manager) => manager == PatchManager.Fleeto ? "Fleeto" : "Action1";

    /// <summary>The choice a stored value means: only the exact value <c>Fleeto</c> is Fleeto.</summary>
    public static PatchManager Parse(string? value) => string.Equals(value, "Fleeto", StringComparison.Ordinal) ? PatchManager.Fleeto : PatchManager.Action1;

    /// <summary>The choice in words for the UI and the audit log.</summary>
    public static string Describe(PatchManager manager) => manager == PatchManager.Fleeto ? "Fleeto" : "Action1";

    /// <summary>
    /// How Fleeto removes the Action1 agent of one endpoint. <paramref name="action1Knows"/> is true when the Action1 integration
    /// exists and Fleeto knows both the organization and the Action1 id of the endpoint, from the patch state Action1 reported or
    /// from the agent and the organization mapped to the client. Otherwise only a signed job can remove it, which needs a managed
    /// Windows endpoint.
    /// </summary>
    public static Action1RemovalWay RemovalWay(bool action1Knows, bool managed, bool windows) =>
        action1Knows ? Action1RemovalWay.Action1 : managed && windows ? Action1RemovalWay.Job : Action1RemovalWay.ByHand;

    /// <summary>Why an endpoint needs its Action1 agent removed by hand, in words.</summary>
    public static string ByHandReason(bool managed, bool windows) =>
        !windows
            ? "Action1 does not report this endpoint, and Fleeto removes the Action1 agent by a job only on Windows. Remove it in the Action1 console or on the endpoint."
            : !managed
                ? "Action1 does not report this endpoint, and an agent-only endpoint gets no jobs. Remove it in the Action1 console or on the endpoint."
                : "Remove it in the Action1 console or on the endpoint.";

    /// <summary>
    /// Converts a patch policy of engine Action1 to engine Fleeto in place, and says what it lost. What both engines have (schedule,
    /// update types, severities, names, the wait after release, the restart and its message, the retry window, Windows Update) stays.
    /// What only Action1 has goes: update sources and vendors, approval in the Action1 console, installing the Action1 agent, and the
    /// update type Feature Updates, which Fleeto does not install. A filtered policy left without any filter installs every update when
    /// it only lost sources or vendors (Fleeto installs no third-party updates anyway); when it lost the update types it was limited
    /// to, it would install more than before, so it is also switched off. A policy that is already of engine Fleeto is not changed.
    /// </summary>
    public static PatchPolicyConversion ConvertToFleeto(PatchPolicy policy)
    {
        if (policy.Engine == PatchEngine.Fleeto)
        {
            return new PatchPolicyConversion([], false);
        }

        var dropped = new List<string>();
        if (policy.UpdateSources.Count > 0)
        {
            dropped.Add("update sources " + string.Join(", ", policy.UpdateSources));
        }

        if (policy.ExcludedVendors.Count > 0)
        {
            dropped.Add("excluded vendors " + string.Join(", ", policy.ExcludedVendors));
        }

        if (policy.RequireApproval)
        {
            dropped.Add("approval in the Action1 console (Fleeto installs without it)");
        }

        if (policy.AutoInstallAgent)
        {
            dropped.Add("installing the Action1 agent automatically");
        }

        var typesBefore = policy.UpdateTypes.Count;
        var types = policy.UpdateTypes.Where(t => !string.Equals(t, "Feature Updates", StringComparison.Ordinal)).ToList();
        if (types.Count != typesBefore)
        {
            dropped.Add("the update type Feature Updates");
        }

        policy.Engine = PatchEngine.Fleeto;
        policy.UpdateSources = [];
        policy.ExcludedVendors = [];
        policy.RequireApproval = false;
        policy.AutoInstallAgent = false;
        policy.UpdateTypes = types;

        var disabled = false;
        if (policy.Scope == PatchUpdateScope.Filtered && policy.UpdateTypes.Count == 0 && policy.Severities.Count == 0 &&
            policy.ExcludedNames.Count == 0)
        {
            // A filtered policy needs a filter, so it becomes "all updates" either way; switched off when that would widen it.
            policy.Scope = PatchUpdateScope.All;
            if (typesBefore > 0)
            {
                disabled = policy.Enabled;
                policy.Enabled = false;
                dropped.Add("its only filter, so it is switched off: switched on again it installs every update Fleeto offers");
            }
            else
            {
                dropped.Add("the filter, so it installs every update Fleeto offers");
            }
        }

        return new PatchPolicyConversion(dropped, disabled);
    }
}
