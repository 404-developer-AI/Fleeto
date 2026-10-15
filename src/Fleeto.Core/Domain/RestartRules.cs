namespace Fleeto.Core.Domain;

/// <summary>
/// The restart step of a task (0.6.0, decided 2026-10-15): the agent restarts the endpoint with a fixed command and reports
/// success once it runs again with a newer boot time. Shared by web (the task editor), the workers (who can run it), fleeto-signer
/// (what it signs) and the tests.
/// </summary>
public static class RestartRules
{
    /// <summary>The first agent that restarts an endpoint for a task; an older one refuses the job type.</summary>
    public const string MinimumAgentVersion = "0.6.0-alpha.23";

    public const int MinWaitMinutes = 5;
    public const int MaxWaitMinutes = 240;
    public const int DefaultWaitMinutes = 30;

    /// <summary>The restart starts this long after the agent asked for it, so its start reaches the server and a user sees the notice.</summary>
    public static readonly TimeSpan Notice = TimeSpan.FromMinutes(1);

    /// <summary>Output of a restart job: one line from the agent.</summary>
    public const long MaxOutputBytes = 64 * 1024;

    public const string JobName = "Restart";

    /// <summary>Fleeto restarts Windows and Linux endpoints.</summary>
    public static bool PlatformSupported(string? osPlatform) =>
        string.Equals(osPlatform, "windows", StringComparison.OrdinalIgnoreCase) || string.Equals(osPlatform, "linux", StringComparison.OrdinalIgnoreCase);

    public static bool AgentSupports(string? agentVersion) =>
        SemanticVersion.TryParse(agentVersion, out _) && !SemanticVersion.IsOlder(agentVersion, MinimumAgentVersion);

    /// <summary>Why this endpoint cannot run a restart step, or null.</summary>
    public static string? Problem(string? osPlatform, string? agentVersion) =>
        !PlatformSupported(osPlatform) ? "Fleeto restarts only Windows and Linux endpoints."
        : !AgentSupports(agentVersion) ? "This endpoint's agent cannot restart for a task yet. Wait until it runs Fleeto 0.6.0 or later."
        : null;

    /// <summary>The time the agent waits for the endpoint to come back, from the step's minutes held inside the bounds.</summary>
    public static int TimeoutSeconds(int? waitMinutes) => Math.Clamp(waitMinutes ?? DefaultWaitMinutes, MinWaitMinutes, MaxWaitMinutes) * 60;
}
