using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// Turning automatic Windows Update off from a patch policy (0.7.0). Action1 has this option only in its console, so the Fleeto
/// agent does it on the endpoint itself: it sets the Windows Update group policy value <c>NoAutoUpdate</c> to 1 under
/// <c>HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU</c> only when that value is missing, remembers that it did, and
/// removes it again when the configuration no longer asks for it. A group policy that sets the value wins: the agent never
/// overwrites it and reports that a group policy decides.
/// </summary>
public static class WindowsUpdateRules
{
    /// <summary>The agent turned automatic Windows Update off.</summary>
    public const string StateTurnedOff = "turned_off";

    /// <summary>The configuration asks for it, but a group policy (or somebody else) sets Windows Update, so the agent left it.</summary>
    public const string StatePolicy = "policy";

    /// <summary>The configuration asks for it, but the Action1 agent is not installed, so Windows Update stays on.</summary>
    public const string StateNoAction1 = "no_action1";

    /// <summary>The configuration does not ask for it; the agent left Windows Update as it is (and undid its own change).</summary>
    public const string StateUntouched = "untouched";

    /// <summary>The first agent version that turns automatic Windows Update off.</summary>
    public const string MinimumAgentVersion = "0.7.0";

    /// <summary>The state as stored: a value the agent may report, or empty (an older agent, Linux, or unreadable).</summary>
    public static string NormalizeState(string? state) =>
        state is StateTurnedOff or StatePolicy or StateNoAction1 or StateUntouched ? state : string.Empty;

    /// <summary>
    /// True when the configuration of the endpoint asks the agent to turn automatic Windows Update off: a managed Windows endpoint
    /// whose patch policy is enabled and asks for it, and that Action1 reports in the organization of its client, so Action1 really
    /// patches it. An endpoint Action1 does not cover keeps Windows Update, or it would get no updates at all.
    /// </summary>
    public static bool Applies(EndpointTier effectiveTier, string? osPlatform, PatchPolicy? patchPolicy, Action1AgentStatus? action1) =>
        effectiveTier == EndpointTier.Managed &&
        string.Equals(osPlatform, "windows", StringComparison.OrdinalIgnoreCase) &&
        patchPolicy is { Enabled: true, TurnOffWindowsUpdate: true } &&
        action1 == Action1AgentStatus.Installed;

    /// <summary>
    /// Automatic Windows Update in words for the Patches tab: <paramref name="requested"/> is true when the patch policy of the
    /// endpoint is enabled and asks for it, <paramref name="state"/> what the agent last reported. Null when there is nothing to show.
    /// </summary>
    public static string? Describe(bool requested, string? state) => (requested, NormalizeState(state)) switch
    {
        (_, StateTurnedOff) when !requested => "Fleeto turns it back on: the patch policy no longer asks for it.",
        (false, _) => null,
        (_, StateTurnedOff) => "Turned off by Fleeto. Only the rounds of the patch policy install updates.",
        (_, StatePolicy) => "Set by a group policy, so Fleeto left it. Change that group policy to let Action1 decide when updates install.",
        (_, StateNoAction1) => "Not turned off: the Action1 agent is not installed on this endpoint, which would then get no updates at all.",
        (_, StateUntouched) => "Not turned off yet. Fleeto does it once Action1 reports this endpoint.",
        _ => $"Not reported. The Fleeto agent turns automatic Windows Update off from version {MinimumAgentVersion}."
    };
}
