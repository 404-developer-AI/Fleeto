using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleeto.Core.Domain;

/// <summary>A user with an active session on an endpoint, as the agent reports it (0.2.2). Personal data.</summary>
/// <param name="Id">The SID on Windows, the uid on Linux: what a job names to run as this user.</param>
/// <param name="Account">DOMAIN\name on Windows, the user name on Linux.</param>
public sealed record SignedInUserInfo(string Id, string Account, IReadOnlyList<UserSessionInfo> Sessions);

/// <param name="Id">The Windows session number or the systemd-logind session id.</param>
/// <param name="Console">The session on the local screen; otherwise a remote session.</param>
public sealed record UserSessionInfo(string Id, bool Console);

/// <summary>Rules for choosing the signed-in user a script runs as (0.2.2).</summary>
public static partial class SignedInUserRules
{
    public const int MaxUsers = 200;
    public const int MaxSessionsPerUser = 50;
    public const int MaxUserIdLength = 200;
    public const int MaxAccountLength = 256;

    /// <summary>The first agent that runs a script as a chosen user. An older agent ignores the choice, so it never gets such a job.</summary>
    public const string MinimumAgentVersion = "0.2.2-alpha.1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A SID (S-1-...) or a uid.</summary>
    public static bool IsValidUserId(string? id) => id is { Length: > 0 and <= MaxUserIdLength } && UserIdPattern().IsMatch(id);

    /// <summary>True when the agent version can run a script as a chosen user.</summary>
    public static bool AgentSupportsChosenUser(string? agentVersion) =>
        SemanticVersion.TryParse(agentVersion, out _) && !SemanticVersion.IsOlder(agentVersion, MinimumAgentVersion);

    /// <summary>Reads the stored list; empty for a missing or damaged value.</summary>
    public static IReadOnlyList<SignedInUserInfo> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SignedInUserInfo?>>(json, Json)?
                .Where(u => u is not null && IsValidUserId(u.Id))
                .Select(u => u! with { Sessions = u.Sessions ?? [] })
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^(S-1(-[0-9]{1,10}){1,15}|[0-9]{1,10})$", RegexOptions.CultureInvariant)]
    private static partial Regex UserIdPattern();
}
