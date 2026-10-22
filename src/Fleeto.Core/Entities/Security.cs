namespace Fleeto.Core.Entities;

// The security monitor (0.7.0): what web and the gateway see of attacks, and the addresses Fleeto refuses or always lets in.
// Instance-wide like instance health: no table here belongs to a client. Addresses are stored as CIDR text in canonical form
// ("203.0.113.7/32", "2001:db8:1:2::/64"), parsed once when a component loads them.

/// <summary>What web or the gateway counted for one address in one minute (0.7.0, security monitor). Stored by name.</summary>
public enum SecuritySignalKind
{
    /// <summary>A rate limiter refused a request.</summary>
    RateLimited,
    /// <summary>A public API call with a malformed, unknown, revoked or expired API key, or a wrong secret.</summary>
    ApiKeyInvalid,
    /// <summary>A password, two-factor or recovery code sign-in failed.</summary>
    SignInFailed,
    /// <summary>A two-factor code entered while setting up two-factor authentication was wrong.</summary>
    TwoFactorSetupFailed,
    /// <summary>A first-admin setup link that is unknown, expired or used.</summary>
    SetupTokenInvalid,
    /// <summary>An agent session with a certificate the instance never issued.</summary>
    AgentCertificateUnknown,
    /// <summary>An agent session with a revoked certificate.</summary>
    AgentCertificateRevoked,
    /// <summary>A connection to the agent port that never completed its TLS handshake.</summary>
    TlsHandshakeFailed,
    /// <summary>A request for a path Fleeto never serves but scanners always try (<see cref="Domain.ScannerVerdict.ScannerPath"/>).</summary>
    ScannerPath,
    /// <summary>A request for a path that only exists to leak secrets: <c>/.env</c>, <c>/.git/</c>, cloud credentials. Certain at once.</summary>
    SensitivePath,
    /// <summary>A request with the User-Agent of a scanning tool.</summary>
    ScannerAgent,
    /// <summary>An exploit attempt in the path or query: traversal, Log4Shell, injection.</summary>
    AttackPattern,
    /// <summary>Any other 404 for an anonymous caller, to recognise directory enumeration.</summary>
    NotFound,
    /// <summary>A request or connection refused because its address is blocked.</summary>
    Blocked
}

/// <summary>
/// The count of one kind of signal from one address in one minute, written once a minute by the component that saw it, never
/// per request (0.7.0, security monitor). A TimescaleDB hypertable where the extension is installed; kept 90 days.
/// </summary>
public class SecuritySignal
{
    public long Id { get; set; }
    public DateTime Minute { get; set; }

    /// <summary>web or gateway. A database trigger lets each role write only its own rows.</summary>
    public string Component { get; set; } = string.Empty;

    public SecuritySignalKind Kind { get; set; }

    /// <summary>The address as CIDR: an IPv4 address as /32, an IPv6 address as its /64, the unit one attacker controls.</summary>
    public string Network { get; set; } = string.Empty;

    /// <summary>What the signal was about, when that is not personal data: an API key id, an endpoint id. Never a user name or path.</summary>
    public string? Subject { get; set; }

    public int Count { get; set; }
}

/// <summary>Who decided that an address is blocked.</summary>
public enum IpBlockSource
{
    /// <summary>An admin, in Settings, Security. Holds on every port, the agent port included.</summary>
    Manual,
    /// <summary>The security monitor of the workers, after an attack. Never permanent.</summary>
    Automatic
}

/// <summary>
/// An address or network Fleeto refuses (0.7.0, security monitor): web answers 403 and the gateway closes the connection. An
/// address on <see cref="IpAllow"/> is never refused, whatever blocks it. At most one block per network.
/// </summary>
public class IpBlock
{
    public Guid Id { get; set; }

    /// <summary>CIDR in canonical form.</summary>
    public string Network { get; set; } = string.Empty;

    public IpBlockSource Source { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>When the block ends by itself; null blocks until an admin lifts it (only a manual block).</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>The user who added a manual block; null for an automatic one.</summary>
    public Guid? CreatedById { get; set; }

    public string CreatedByName { get; set; } = string.Empty;

    /// <summary>
    /// How often the security monitor blocked this network within 30 days of the previous time, which sets how long the next
    /// block lasts (<see cref="Domain.SecurityRules.BlockDuration"/>). 0 for a manual block.
    /// </summary>
    public int Times { get; set; }
}

/// <summary>
/// An address or network that is never blocked (0.7.0, security monitor): not by an admin's block, not by the security monitor
/// and not by a blocklist. For the office of the customer, a monitoring service, an integration that calls the public API.
/// </summary>
public class IpAllow
{
    public Guid Id { get; set; }

    /// <summary>CIDR in canonical form.</summary>
    public string Network { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedById { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
}

/// <summary>
/// A network a user signed in from successfully (0.7.0, security monitor): an IPv4 /24 or an IPv6 /64, with the first and last
/// time. A sign-in from a network a user never used is what the security monitor reports, and a network with a recent sign-in
/// is never blocked automatically. Deleted with the user.
/// </summary>
public class UserSignInNetwork
{
    public Guid UserId { get; set; }

    /// <summary>CIDR in canonical form.</summary>
    public string Network { get; set; } = string.Empty;

    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

/// <summary>What a security incident is about (0.7.0, security monitor step 2). Stored by name.</summary>
public enum SecurityIncidentKind
{
    BruteForce,
    PasswordSpraying,
    AccountAttack,
    AdminLockedOut,
    NewSignInNetwork,
    ApiKeyMisuse,
    AgentPortAttack,
    RevokedCertificate,
    Scanner,
    Enumeration,
    RateLimitFlood,
    PrivilegeChange,
    MassJobs,
    MassRemoteSessions,
    SigningRefusals,
    Posture
}

/// <summary>
/// Something the security monitor of the workers found (0.7.0, step 2), opened from <see cref="Domain.SecurityRules"/>. Not an
/// alert: it belongs to no client. At most one open incident per <see cref="Key"/>; a finding that comes back while it is open
/// counts up instead of opening another. It resolves by itself when its finding stays away (<see cref="Domain.SecurityRules.QuietPeriod"/>),
/// or by an admin with a comment. Resolved incidents are kept a year.
/// </summary>
public class SecurityIncident
{
    public const int MaxCommentLength = 1000;

    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public SecurityIncidentKind Kind { get; set; }
    public AlertSeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;

    /// <summary>The attacker network as CIDR, when the incident is about one.</summary>
    public string? Network { get; set; }

    /// <summary>The highest count the finding reached: attempts, requests, events.</summary>
    public int Count { get; set; }

    public DateTime OpenedAt { get; set; }

    /// <summary>The last pass that still found it.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>When the security monitor blocked the network for this incident, and until when.</summary>
    public DateTime? BlockedUntil { get; set; }

    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedByName { get; set; }
    public string? AcknowledgeComment { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>Who resolved it by hand; null when it resolved by itself.</summary>
    public string? ResolvedByName { get; set; }

    public string? ResolveComment { get; set; }
}

/// <summary>
/// One public blocklist the workers fetch (0.7.0, security monitor step 3), keyed by <see cref="Domain.IpFeeds"/>. An admin
/// switches it on or off; the workers keep its state. <see cref="Version"/> counts the times its entries were replaced, so web and
/// the gateway load the entries again only when it changed.
/// </summary>
public class IpFeed
{
    public string Key { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public int EntryCount { get; set; }

    /// <summary>Why the last attempt failed or its list was refused, with the next step; null when it worked.</summary>
    public string? Problem { get; set; }

    public long Version { get; set; }

    /// <summary>Set by web when an admin asks for a refresh; the workers clear it when they fetched the list.</summary>
    public DateTime? RefreshRequestedAt { get; set; }
}

/// <summary>One network of a blocklist, in canonical CIDR. Replaced as a whole when the list is fetched again.</summary>
public class IpFeedEntry
{
    public string Feed { get; set; } = string.Empty;
    public string Network { get; set; } = string.Empty;
}
