using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// The limits of patch deployments (0.4.0 step 3), shared by web, the workers and the tests.
///
/// A deployment is carried out by Action1, which Fleeto hands it to and follows, or (0.9.0) by the Fleeto agent with one signed
/// <see cref="Entities.JobType.PatchInstall"/> job per endpoint. The endpoint and update limits hold for a deployment a technician
/// starts with either engine; following a deployment by Fleeto lasts as long as its jobs are valid, not <see cref="FollowFor"/>.
/// </summary>
public static class PatchRules
{
    /// <summary>The name of an installation job by Fleeto, on the Jobs tab and in the audit log.</summary>
    public const string InstallJobName = "Install updates";

    /// <summary>
    /// The first agent that installs updates (0.9.0 step 3). The server writes no installation job for an older agent, and says why.
    /// </summary>
    public const string MinimumInstallAgentVersion = "0.9.0-alpha.1";

    /// <summary>How long an installation by Fleeto may run before it times out; the package manager is never killed at the timeout.</summary>
    public const int InstallTimeoutSeconds = 4 * 3600;

    /// <summary>The output an installation job may send: the output of apt, dnf or the Windows Update Agent is long.</summary>
    public const long InstallMaxOutputBytes = 1024 * 1024;

    /// <summary>The job of a deployment by Fleeto is valid this long: an endpoint that is off gets it when it comes back within a day.</summary>
    public static readonly TimeSpan InstallValidity = TimeSpan.FromHours(24);

    /// <summary>The prefix of the lines an installation job writes per update and for the restart (agent.proto, PatchInstallJob).</summary>
    public const string OutputPrefix = "fleeto-patch: ";

    /// <summary>Why an endpoint cannot get an installation job by Fleeto now; null when it can.</summary>
    public static string? InstallProblem(string? osPlatform, string? agentVersion) =>
        !PatchScanRules.PlatformScans(osPlatform)
            ? "Fleeto does not install updates on this operating system."
            : !SemanticVersion.TryParse(agentVersion, out _) || SemanticVersion.IsOlder(agentVersion, MinimumInstallAgentVersion)
                ? $"Update the agent to {MinimumInstallAgentVersion} or newer to install updates through Fleeto."
                : null;

    /// <summary>One line of an installation job about one update: its status, id and title.</summary>
    public sealed record InstallLine(string Status, string Id, string Title);

    /// <summary>
    /// Reads the lines an installation job writes (<see cref="OutputPrefix"/>): one per update and one for the restart. Other output (of
    /// apt, dnf or the Windows Update Agent) is ignored. Returns the update lines in order and the restart state, null when not reported.
    /// </summary>
    public static (IReadOnlyList<InstallLine> Updates, PatchTargetRestart? Restart) ParseInstallOutput(string? stdout)
    {
        var lines = new List<InstallLine>();
        PatchTargetRestart? restart = null;
        foreach (var raw in (stdout ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith(OutputPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[OutputPrefix.Length..];
            var parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "restart")
            {
                restart = parts[1] switch
                {
                    "not-required" => PatchTargetRestart.NotRequired,
                    "required" => PatchTargetRestart.Required,
                    "restarting" => PatchTargetRestart.Restarted,
                    _ => restart
                };
            }
            else if (parts.Length >= 2 && parts[0] is "installed" or "failed" or "not-missing" or "held" or "excluded" && lines.Count < 2000)
            {
                lines.Add(new InstallLine(parts[0], Cut(parts[1], 200), parts.Length == 3 ? Cut(parts[2], 300) : string.Empty));
            }
        }

        return (lines, restart);
    }

    /// <summary>The status of an update line in the words of the deployment history.</summary>
    public static string StatusWord(string status) => status switch
    {
        "installed" => "Succeeded",
        "failed" => "Failed",
        "not-missing" => "Already installed",
        "held" => "Held",
        "excluded" => "Excluded",
        _ => status
    };

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Endpoints one deployment may target. A larger selection is refused rather than silently cut.</summary>
    public const int MaxEndpointsPerDeployment = 200;

    /// <summary>Updates a technician may choose in one deployment; beyond it, deploying everything missing is the answer.</summary>
    public const int MaxUpdatesPerDeployment = 100;

    /// <summary>How often a running deployment is asked about. Action1 has no webhooks, so following it means polling.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long Fleeto follows a deployment. After this the deployment is abandoned: an endpoint that was offline the whole
    /// time never reports, and a row that stays open forever would poll forever.
    /// </summary>
    public static readonly TimeSpan FollowFor = TimeSpan.FromHours(24);

    /// <summary>
    /// How long the product keeps retrying an endpoint that it could not reach, in minutes. It matches
    /// <see cref="FollowFor"/>, so the product gives up at the same time Fleeto stops looking.
    /// </summary>
    public const int RetryMinutes = 1440;

    /// <summary>
    /// The message the product shows on a workstation before it restarts by itself. Fleeto sends its own wording so the
    /// text follows the tone of the rest of the product (branding §8).
    /// </summary>
    public const string RebootMessage =
        "Updates have been installed and this computer needs to restart to finish them. Save your work; it restarts automatically when the time is up.";

    /// <summary>
    /// Minutes the user gets before the product restarts the endpoint by itself. Action1 reads <c>timeout</c> in minutes
    /// (its OpenAPI document, RebootOptions); 0.4.0 sent seconds, which made a restart wait 30 hours.
    /// </summary>
    public const int RebootTimeoutMinutes = 30;
}
