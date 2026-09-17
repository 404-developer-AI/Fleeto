namespace Fleeto.Core.Domain;

/// <summary>Limits and defaults of remote sessions (0.3.0), shared by web, signer, gateway and workers. The agent mirrors them.</summary>
public static class RemoteSessionRules
{
    /// <summary>The first watchdog that serves remote background sessions.</summary>
    public const string MinimumWatchdogVersion = "0.3.0-alpha.1";

    /// <summary>The first agent that serves remote control sessions (0.3.0 step 3, Windows).</summary>
    public const string MinimumAgentVersion = "0.3.0-alpha.5";

    /// <summary>The Windows session id that stands for the console session: whichever session is attached to the screen.</summary>
    public const int ConsoleWindowsSession = 0;

    /// <summary>How long a signed session token opens the relay.</summary>
    public static readonly TimeSpan TokenValidity = TimeSpan.FromSeconds(60);

    /// <summary>A token request older than this is refused by the signer: nobody waits for it any more.</summary>
    public static readonly TimeSpan MaxRequestAge = TimeSpan.FromSeconds(60);

    /// <summary>How long web waits for the signer.</summary>
    public static readonly TimeSpan SigningWait = TimeSpan.FromSeconds(20);

    /// <summary>How long the gateway waits for the endpoint to connect its side of the relay.</summary>
    public static readonly TimeSpan EndpointConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Length of the X25519 public keys of browser and endpoint.</summary>
    public const int PublicKeyBytes = 32;

    public const int MaxReasonLength = 500;

    /// <summary>A session without input from the technician closes after this long (policy, decided 2026-09-16).</summary>
    public const int DefaultIdleTimeoutMinutes = 30;

    public const int MinIdleTimeoutMinutes = 5;
    public const int MaxIdleTimeoutMinutes = 8 * 60;

    /// <summary>The warning before an idle session closes.</summary>
    public static readonly TimeSpan IdleWarning = TimeSpan.FromMinutes(2);

    /// <summary>The largest file one transfer may carry (policy, decided 2026-09-16).</summary>
    public const long DefaultMaxFileBytes = 10L * 1024 * 1024 * 1024;

    public const long MinMaxFileBytes = 1L * 1024 * 1024;
    public const long MaxMaxFileBytes = DefaultMaxFileBytes;

    /// <summary>Workstation consent prompt timeout bounds and default (policy, decided 2026-09-16).</summary>
    public const int DefaultConsentTimeoutSeconds = 30;

    public const int MinConsentTimeoutSeconds = 10;
    public const int MaxConsentTimeoutSeconds = 300;

    /// <summary>Remote sessions one endpoint serves at the same time; the gateway and the endpoint both hold it.</summary>
    public const int MaxSessionsPerEndpoint = 8;

    /// <summary>A participant that is not connected this long after it was requested is ended by the workers.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Remote session history is kept this long; the audit log keeps its own entries.</summary>
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(395);

    /// <summary>True when the watchdog version serves remote background sessions.</summary>
    public static bool WatchdogSupportsRemoteBackground(string? watchdogVersion) =>
        SemanticVersion.TryParse(watchdogVersion, out _) && !SemanticVersion.IsOlder(watchdogVersion, MinimumWatchdogVersion);

    /// <summary>True when the agent version serves remote control sessions.</summary>
    public static bool AgentSupportsRemoteControl(string? agentVersion) =>
        SemanticVersion.TryParse(agentVersion, out _) && !SemanticVersion.IsOlder(agentVersion, MinimumAgentVersion);

    /// <summary>
    /// The Windows sessions a remote control session can show: the console first, then every remote session of a signed-in user as the
    /// agent last reported it (0.2.2), by user.
    /// </summary>
    public static IReadOnlyList<RemoteWindowsSession> WindowsSessions(IReadOnlyList<SignedInUserInfo> users)
    {
        var sessions = new List<RemoteWindowsSession> { new(ConsoleWindowsSession, "Console", null) };
        foreach (var user in users.OrderBy(u => u.Account, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var session in user.Sessions)
            {
                if (!session.Console && int.TryParse(session.Id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                        out var id) && id > ConsoleWindowsSession && sessions.All(s => s.Id != id))
                {
                    sessions.Add(new RemoteWindowsSession(id, $"{user.Account} (session {id})", user.Account));
                }
            }
        }

        return sessions;
    }

    /// <summary>The idle timeout a policy may set, held inside the bounds.</summary>
    public static int IdleTimeoutMinutes(int minutes) => Math.Clamp(minutes, MinIdleTimeoutMinutes, MaxIdleTimeoutMinutes);

    /// <summary>The file size cap a policy may set, rounded to whole mebibytes and held inside the bounds.</summary>
    public static long MaxFileBytes(long bytes) => Math.Clamp(bytes, MinMaxFileBytes, MaxMaxFileBytes) / (1024 * 1024) * (1024 * 1024);

    /// <summary>A usable X25519 public key: 32 bytes, not all zero.</summary>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> key) => key.Length == PublicKeyBytes && key.IndexOfAnyExcept((byte)0) >= 0;
}

/// <summary>A Windows session a remote control session can show (0.3.0).</summary>
/// <param name="Id">0 for the console, otherwise the Windows session number.</param>
/// <param name="Account">The signed-in user of a remote session; null for the console.</param>
public sealed record RemoteWindowsSession(int Id, string Label, string? Account);
