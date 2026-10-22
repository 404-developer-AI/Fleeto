using System.Globalization;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>The signals of one kind from one attacker network, summed over the last 10, 30 and 60 minutes.</summary>
public sealed record SignalCount(SecuritySignalKind Kind, string Network, int Last10Minutes, int Last30Minutes, int LastHour);

/// <summary>A failed sign-in of the last hour from the audit log: the attacker network and the account it tried.</summary>
/// <param name="Account">The user id, or the typed email address for an account that does not exist.</param>
/// <param name="AccountName">What an incident may show: the display name of an existing user, or null for an unknown address.</param>
public sealed record FailedSignIn(DateTime Time, string Network, string Account, string? AccountName);

/// <summary>An audit entry of the last hour that the rules look at.</summary>
/// <param name="Endpoints">For <see cref="Interfaces.AuditActions.JobBatchStarted"/>: the number of endpoints of the batch.</param>
/// <param name="TargetIsAdmin">For <see cref="Interfaces.AuditActions.LockedOut"/>: whether the locked user is an admin.</param>
public sealed record SecurityAuditEvent(long Id, DateTime Time, string Action, string ActorId, string ActorName, string? Network,
    string TargetId, int? Endpoints = null, bool TargetIsAdmin = false);

/// <summary>A network a user with high privileges signed in from for the first time, while it had signed in before from others.</summary>
public sealed record FirstSignInFromNetwork(Guid UserId, string UserName, string Network, DateTime FirstSeen);

/// <summary>Facts about how the instance is set up, read once an hour.</summary>
public sealed record SecurityPosture(int ApiKeysWithoutExpiry, int UnusedApiKeys, int UnusedAdmins);

/// <summary>Everything one pass of the security monitor looks at.</summary>
public sealed record SecurityEvidence(DateTime Now, IReadOnlyList<SignalCount> Signals, IReadOnlyList<FailedSignIn> FailedSignIns,
    IReadOnlyList<SecurityAuditEvent> Events, IReadOnlyList<FirstSignInFromNetwork> NewNetworks, SecurityPosture? Posture);

/// <summary>
/// One thing the rules found. <see cref="Block"/> asks the workers to block <see cref="Network"/>, for at least
/// <see cref="MinimumBlock"/>; the workers decide whether the network may be blocked at all.
/// </summary>
public sealed record SecurityFinding(string Key, SecurityIncidentKind Kind, AlertSeverity Severity, string Title, string Detail,
    string? Network, int Count, bool Block = false, TimeSpan? MinimumBlock = null);

/// <summary>
/// The rules of the security monitor (0.7.0, step 2): pure, so every threshold is tested without a database. Each finding states
/// the cause and the next step; it names a network or an account, never a path or a typed email address. The thresholds are fixed
/// defaults: they sit far above what a person or an integration does by mistake and far below what an attack needs.
/// </summary>
public static class SecurityRules
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>An incident whose finding stays away this long resolves by itself.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromHours(24);

    /// <summary>A finding about the set-up of the instance resolves once it is fixed, after this long.</summary>
    public static readonly TimeSpan PostureQuietPeriod = TimeSpan.FromMinutes(15);

    /// <summary>A block within this long of the previous one counts as a repeat and lasts longer.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromDays(30);

    /// <summary>A network a user signed in from, or a managed endpoint connected from, within this long is never blocked automatically.</summary>
    public static readonly TimeSpan TrustedNetworkAge = TimeSpan.FromDays(30);

    /// <summary>The most automatic blocks active at once; beyond this the monitor reports and no longer blocks.</summary>
    public const int MaxAutomaticBlocks = 10_000;

    public const int BruteForceAttempts = 10;
    public const int SprayingAccounts = 5;
    public const int AccountAttackAttempts = 20;
    public const int AccountAttackNetworks = 5;
    public const int ApiKeyFailures = 20;
    public const int AgentPortFailures = 30;
    public const int ScannerPaths = 3;
    public const int NotFoundRequests = 50;
    public const int RateLimitRefusals = 200;
    public const int MassJobEndpoints = 100;
    public const int MassRemoteSessions = 10;
    public const int SigningRefusals = 5;

    /// <summary>Audit actions that change who can do what, or how the instance is secured: each one is reported.</summary>
    public static readonly IReadOnlyDictionary<string, string> PrivilegeChanges = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Interfaces.AuditActions.RolesChanged] = "changed the roles of a user",
        [Interfaces.AuditActions.RoleCreated] = "created a role",
        [Interfaces.AuditActions.RoleUpdated] = "changed the permissions of a role",
        [Interfaces.AuditActions.UserCreated] = "created a user",
        [Interfaces.AuditActions.TwoFactorReset] = "reset the two-factor authentication of a user",
        [Interfaces.AuditActions.ApiKeyCreated] = "created an API key",
        [Interfaces.AuditActions.SignInConfigured] = "changed the sign-in with Microsoft Entra ID",
        [Interfaces.AuditActions.UserLinked] = "linked a user to Microsoft Entra ID",
        [Interfaces.AuditActions.CredentialChanged] = "changed a credential of the instance",
        [Interfaces.AuditActions.IpAllowCreated] = "allowed an address, which is then never blocked",
    };

    /// <summary>
    /// How long the <paramref name="times"/>-th automatic block of a network within <see cref="RepeatWindow"/> lasts: an hour,
    /// then a day, then a week. Never permanent: an address can change hands.
    /// </summary>
    public static TimeSpan BlockDuration(int times) => times switch
    {
        <= 1 => TimeSpan.FromHours(1),
        2 => TimeSpan.FromHours(24),
        _ => TimeSpan.FromDays(7)
    };

    public static IReadOnlyList<SecurityFinding> Evaluate(SecurityEvidence evidence)
    {
        var findings = new List<SecurityFinding>();
        AddSignalFindings(evidence, findings);
        AddSignInFindings(evidence, findings);
        AddEventFindings(evidence, findings);
        AddPostureFindings(evidence.Posture, findings);
        return findings;
    }

    private static int Sum(IEnumerable<SignalCount> signals, string network, Func<SignalCount, int> window, params SecuritySignalKind[] kinds) =>
        signals.Where(s => s.Network == network && kinds.Contains(s.Kind)).Sum(window);

    private static void AddSignalFindings(SecurityEvidence evidence, List<SecurityFinding> findings)
    {
        foreach (var network in evidence.Signals.Select(s => s.Network).Distinct(StringComparer.Ordinal))
        {
            var s = evidence.Signals;
            var signIns = Sum(s, network, c => c.Last10Minutes, SecuritySignalKind.SignInFailed, SecuritySignalKind.TwoFactorSetupFailed,
                SecuritySignalKind.SetupTokenInvalid);
            if (signIns >= BruteForceAttempts)
            {
                findings.Add(new SecurityFinding($"brute-force:{network}", SecurityIncidentKind.BruteForce, AlertSeverity.Warning,
                    $"{Count(signIns, "failed sign-in")} from {network} in 10 minutes",
                    "Somebody is guessing passwords or codes. Lockout protects each account; Fleeto blocks the address. If it is your own " +
                    "office, allow the address in Settings, Security and check who mistyped.",
                    network, signIns, Block: true));
            }

            var apiKeys = Sum(s, network, c => c.Last10Minutes, SecuritySignalKind.ApiKeyInvalid);
            if (apiKeys >= ApiKeyFailures)
            {
                findings.Add(new SecurityFinding($"api-keys:{network}", SecurityIncidentKind.ApiKeyMisuse, AlertSeverity.Warning,
                    $"{Count(apiKeys, "public API call")} with an invalid API key from {network} in 10 minutes",
                    "Somebody is guessing API keys, or an integration uses a revoked or expired key. Fleeto blocks the address. If it is " +
                    "your integration, give it a valid key and allow its address in Settings, Security.",
                    network, apiKeys, Block: true));
            }

            var agentPort = Sum(s, network, c => c.Last10Minutes, SecuritySignalKind.AgentCertificateUnknown, SecuritySignalKind.TlsHandshakeFailed);
            if (agentPort >= AgentPortFailures)
            {
                findings.Add(new SecurityFinding($"agent-port:{network}", SecurityIncidentKind.AgentPortAttack, AlertSeverity.Warning,
                    $"{Count(agentPort, "refused connection")} to the agent gateway from {network} in 10 minutes",
                    "Connections without a certificate of this instance, or that never finish TLS: a scan of the agent port. Fleeto " +
                    "refuses the address for enrollment; endpoints with a valid certificate behind it keep working.",
                    network, agentPort, Block: true));
            }

            var revoked = Sum(s, network, c => c.LastHour, SecuritySignalKind.AgentCertificateRevoked);
            if (revoked > 0)
            {
                findings.Add(new SecurityFinding($"revoked-certificate:{network}", SecurityIncidentKind.RevokedCertificate, AlertSeverity.Critical,
                    $"A revoked agent certificate tried to connect from {network}",
                    "A certificate this instance revoked is still in use: an endpoint that was removed but still runs the agent, or a " +
                    "copied certificate. Check which endpoint used that address and uninstall the agent there.",
                    network, revoked));
            }

            var sensitive = Sum(s, network, c => c.Last10Minutes, SecuritySignalKind.SensitivePath, SecuritySignalKind.AttackPattern,
                SecuritySignalKind.ScannerAgent);
            var probes = Sum(s, network, c => c.Last10Minutes, SecuritySignalKind.ScannerPath);
            if (sensitive > 0 || probes >= ScannerPaths)
            {
                findings.Add(new SecurityFinding($"scanner:{network}", SecurityIncidentKind.Scanner, AlertSeverity.Warning,
                    $"A vulnerability scanner from {network}",
                    $"{Count(sensitive + probes, "request")} for paths Fleeto never serves, exploit attempts or a scanning tool in 10 " +
                    "minutes. Nothing was served; Fleeto blocks the address. No action is needed unless you run a scan yourself.",
                    network, sensitive + probes, Block: true, MinimumBlock: TimeSpan.FromHours(24)));
            }

            var notFound = Sum(s, network, c => c.Last10Minutes, SecuritySignalKind.NotFound);
            if (notFound >= NotFoundRequests)
            {
                findings.Add(new SecurityFinding($"enumeration:{network}", SecurityIncidentKind.Enumeration, AlertSeverity.Warning,
                    $"{Count(notFound, "request")} for pages that do not exist from {network} in 10 minutes",
                    "Somebody is listing the instance for hidden pages. Fleeto blocks the address.",
                    network, notFound, Block: true));
            }

            var limited = Sum(s, network, c => c.Last10Minutes, SecuritySignalKind.RateLimited);
            if (limited >= RateLimitRefusals)
            {
                findings.Add(new SecurityFinding($"rate-limit:{network}", SecurityIncidentKind.RateLimitFlood, AlertSeverity.Warning,
                    $"{Count(limited, "request")} over the rate limit from {network} in 10 minutes",
                    "The address keeps sending after it was told to wait. Fleeto blocks it. If it is your integration, slow it down and " +
                    "allow its address in Settings, Security.",
                    network, limited, Block: true));
            }
        }
    }

    private static void AddSignInFindings(SecurityEvidence evidence, List<SecurityFinding> findings)
    {
        var lastHalfHour = evidence.FailedSignIns.Where(f => f.Time >= evidence.Now.AddMinutes(-30)).ToList();
        foreach (var group in lastHalfHour.GroupBy(f => f.Network, StringComparer.Ordinal))
        {
            var accounts = group.Select(f => f.Account).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (accounts >= SprayingAccounts)
            {
                findings.Add(new SecurityFinding($"spraying:{group.Key}", SecurityIncidentKind.PasswordSpraying, AlertSeverity.Critical,
                    $"Sign-ins to {accounts} different accounts from {group.Key}",
                    "One address tries a few passwords on many accounts, which lockout of a single account does not stop. Fleeto blocks " +
                    "the address. Check that every account has a strong password and two-factor authentication.",
                    group.Key, accounts, Block: true));
            }
        }

        foreach (var group in evidence.FailedSignIns.Where(f => f.AccountName is not null).GroupBy(f => f.Account, StringComparer.OrdinalIgnoreCase))
        {
            var networks = group.Select(f => f.Network).Distinct(StringComparer.Ordinal).Count();
            if (group.Count() >= AccountAttackAttempts && networks >= AccountAttackNetworks)
            {
                var name = group.First().AccountName;
                findings.Add(new SecurityFinding($"account:{group.Key}", SecurityIncidentKind.AccountAttack, AlertSeverity.Critical,
                    $"The account of {name} is attacked from {networks} addresses",
                    $"{Count(group.Count(), "failed sign-in")} in an hour from {networks} addresses, too spread to block one. Lockout " +
                    "protects the account. Tell the user, and make sure the account has a strong password and two-factor authentication.",
                    null, group.Count()));
            }
        }

        foreach (var first in evidence.NewNetworks)
        {
            findings.Add(new SecurityFinding($"new-network:{first.UserId}:{first.Network}", SecurityIncidentKind.NewSignInNetwork, AlertSeverity.Warning,
                $"{first.UserName} signed in from a new network ({first.Network})",
                "An account that can control endpoints signed in from a network it never used. If that was not the user, reset the " +
                "password and two-factor authentication in Settings, Users and end its sessions.",
                first.Network, 1));
        }
    }

    private static void AddEventFindings(SecurityEvidence evidence, List<SecurityFinding> findings)
    {
        foreach (var e in evidence.Events)
        {
            if (e.Action == Interfaces.AuditActions.LockedOut && e.TargetIsAdmin)
            {
                findings.Add(new SecurityFinding($"locked-out:{e.Id}", SecurityIncidentKind.AdminLockedOut, AlertSeverity.Warning,
                    $"The admin account of {e.ActorName} was locked",
                    $"Five failed sign-ins locked the account for 15 minutes{From(e.Network)}. If the admin did not mistype, somebody " +
                    "is trying the account: check the audit log for the addresses.",
                    e.Network, 1));
            }
            else if (PrivilegeChanges.TryGetValue(e.Action, out var what))
            {
                findings.Add(new SecurityFinding($"change:{e.Id}", SecurityIncidentKind.PrivilegeChange, AlertSeverity.Warning,
                    $"{e.ActorName} {what}",
                    $"At {e.Time.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC{From(e.Network)}. If you expected it, acknowledge " +
                    "the incident; if not, check the audit log and the account of the user who did it.",
                    e.Network, 1));
            }
            else if (e.Action == Interfaces.AuditActions.JobBatchStarted && e.Endpoints >= MassJobEndpoints)
            {
                findings.Add(new SecurityFinding($"mass-jobs:{e.Id}", SecurityIncidentKind.MassJobs, AlertSeverity.Critical,
                    $"{e.ActorName} ran a script on {e.Endpoints} endpoints at once",
                    "A script on this many endpoints is what an attacker with a stolen account would do. If it was planned, acknowledge " +
                    "the incident; if not, cancel the jobs on the Jobs page and lock the account in Settings, Users.",
                    e.Network, e.Endpoints!.Value));
            }
        }

        foreach (var group in evidence.Events.Where(e => e.Action == Interfaces.AuditActions.RemoteSessionRequested)
                     .GroupBy(e => e.ActorId, StringComparer.Ordinal))
        {
            if (group.Count() >= MassRemoteSessions)
            {
                findings.Add(new SecurityFinding($"remote-sessions:{group.Key}", SecurityIncidentKind.MassRemoteSessions, AlertSeverity.Warning,
                    $"{group.First().ActorName} opened {group.Count()} remote sessions in an hour",
                    "More remote sessions than a technician usually opens. If it is planned work, acknowledge the incident; if not, " +
                    "check the endpoints in the audit log and lock the account.",
                    null, group.Count()));
            }
        }

        var refused = evidence.Events.Count(e => e.Action == Interfaces.AuditActions.SigningRefused && e.Time >= evidence.Now.AddMinutes(-10));
        if (refused >= SigningRefusals)
        {
            findings.Add(new SecurityFinding("signing-refused", SecurityIncidentKind.SigningRefusals, AlertSeverity.Critical,
                $"fleeto-signer refused {refused} requests in 10 minutes",
                "Web asked to sign jobs or sessions the rules do not allow. One refusal is a user without a permission; many in a row can " +
                "mean web is compromised. Check signing.refused in the audit log.",
                null, refused));
        }
    }

    private static void AddPostureFindings(SecurityPosture? posture, List<SecurityFinding> findings)
    {
        if (posture is null)
        {
            return;
        }

        if (posture.ApiKeysWithoutExpiry > 0)
        {
            findings.Add(new SecurityFinding("posture:api-keys-without-expiry", SecurityIncidentKind.Posture, AlertSeverity.Warning,
                $"{Count(posture.ApiKeysWithoutExpiry, "API key")} never {(posture.ApiKeysWithoutExpiry == 1 ? "expires" : "expire")}",
                "A leaked key without an expiry date works forever. Create a key with an expiry date in Settings, API keys, switch the " +
                "integration over and revoke the old one.",
                null, posture.ApiKeysWithoutExpiry));
        }

        if (posture.UnusedApiKeys > 0)
        {
            findings.Add(new SecurityFinding("posture:unused-api-keys", SecurityIncidentKind.Posture, AlertSeverity.Warning,
                $"{Count(posture.UnusedApiKeys, "API key")} unused for 90 days",
                "A key nobody uses only adds risk. Revoke it in Settings, API keys.",
                null, posture.UnusedApiKeys));
        }

        if (posture.UnusedAdmins > 0)
        {
            findings.Add(new SecurityFinding("posture:unused-admins", SecurityIncidentKind.Posture, AlertSeverity.Warning,
                $"{Count(posture.UnusedAdmins, "admin")} did not sign in for 90 days",
                "An admin account nobody uses is an easy target. Remove the admin role or delete the user in Settings, Users.",
                null, posture.UnusedAdmins));
        }
    }

    /// <summary>The quiet period after which an incident of this kind resolves by itself.</summary>
    public static TimeSpan QuietPeriodOf(SecurityIncidentKind kind) => kind == SecurityIncidentKind.Posture ? PostureQuietPeriod : QuietPeriod;

    private static string From(string? network) => network is null ? string.Empty : $", from {network}";

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
