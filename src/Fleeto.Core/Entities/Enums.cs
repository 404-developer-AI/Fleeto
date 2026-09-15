namespace Fleeto.Core.Entities;

/// <summary>Endpoint class, derived from the OS edition and overridable by a technician.</summary>
public enum EndpointClass
{
    Workstation,
    Server
}

/// <summary>License tier of an endpoint. See CLAUDE.md, Licensing.</summary>
public enum EndpointTier
{
    AgentOnly,
    Managed
}

/// <summary>Where an endpoint comes from: an enrolled agent or an integration (hypervisor inventory).</summary>
public enum EndpointSource
{
    Agent,
    Integration
}

/// <summary>
/// Check types. Stored by name, so members are only ever appended. What each type measures, its parameters and on which
/// platforms it runs is described once in <see cref="Domain.CheckCatalog"/>.
/// </summary>
public enum CheckType
{
    /// <summary>Average CPU usage in percent over the sample window.</summary>
    CpuUsage,
    /// <summary>Physical memory in use, in percent.</summary>
    MemoryUsage,
    /// <summary>Free disk space in percent, per drive or for every fixed drive.</summary>
    DiskFree,
    /// <summary>1 when the named service is running, 0 otherwise.</summary>
    ServiceRunning,
    /// <summary>Days since the last boot.</summary>
    Uptime,
    /// <summary>Average round-trip time of ICMP echo requests to a host in milliseconds; -1 when the host does not reply (0.2.0).</summary>
    Ping,
    /// <summary>Time to open a TCP connection in milliseconds; -1 when the port cannot be reached (0.2.0).</summary>
    TcpPort,
    /// <summary>Response time of an HTTP(S) URL in milliseconds, -1 when it fails; target "certificate": days until the TLS certificate expires (0.2.0).</summary>
    Http,
    /// <summary>Number of running processes with the name; 0 is a problem (0.2.0).</summary>
    ProcessRunning,
    /// <summary>1 when no restart is pending, 0 when the endpoint needs a restart (0.2.0).</summary>
    PendingReboot,
    /// <summary>A file or folder: exists (1/0), missing (1/0), size in MB, or hours since the last change (0.2.0).</summary>
    File,
    /// <summary>Days until a local certificate expires, one result per certificate (0.2.0).</summary>
    CertificateExpiry,
    /// <summary>Windows: number of matching events in an event log within a window (0.2.0).</summary>
    EventLog,
    /// <summary>Windows: 1 when antivirus or firewall protection is on, 0 otherwise (0.2.0).</summary>
    SecurityCenter,
    /// <summary>The exit code of a library script: 0 OK, 1 warning, any other code critical (0.2.0).</summary>
    Script
}

/// <summary>Which endpoint class a check definition applies to.</summary>
public enum CheckAppliesTo
{
    All,
    Workstation,
    Server
}

/// <summary>Evaluated state of one check on one endpoint.</summary>
public enum CheckStatus
{
    Ok,
    Warning,
    Critical,
    /// <summary>The check could not run (error reported by the agent) or has no result yet.</summary>
    Unknown
}

public enum AlertSeverity
{
    Warning,
    Critical
}

public enum AlertState
{
    Open,
    Acknowledged,
    Resolved
}

/// <summary>How a check run request ended without being delivered to the agent.</summary>
public enum CheckRunRequestOutcome
{
    /// <summary>The agent did not connect before the request expired.</summary>
    Expired,
    /// <summary>The check no longer applies to the endpoint.</summary>
    NotApplicable,
    /// <summary>The endpoint is no longer managed.</summary>
    NotManaged
}

public enum AlertKind
{
    Check,
    Offline,
    DuplicateIdentity,
    /// <summary>The watchdog is online but the agent is not: its service is stopped, or it runs without connecting (0.2.1).</summary>
    AgentStopped,
    /// <summary>The agent is online but its watchdog is not (0.2.1).</summary>
    WatchdogStopped
}

/// <summary>The Fleeto services on an endpoint (0.2.1). Also the role of an agent certificate. Stored by name.</summary>
public enum AgentComponent
{
    Agent,
    Watchdog
}

/// <summary>
/// When the endpoints of a site get a new agent release, counted from the moment the instance installed it (decided 2026-09-15). An
/// admin can pause a release or release it to every ring at once.
/// </summary>
public enum UpdateRing
{
    /// <summary>At once.</summary>
    Preview,
    /// <summary>7 days after the instance installed the release.</summary>
    Standard,
    /// <summary>14 days after the instance installed the release.</summary>
    Delayed
}

/// <summary>State of a Fleeto service on an endpoint, as reported by the other service (0.2.1).</summary>
public enum ComponentServiceState
{
    Unknown,
    Running,
    Stopped,
    Starting,
    Stopping,
    Disabled,
    NotInstalled
}

/// <summary>Progress of installing a component, as reported by the service that installs it (0.2.1).</summary>
public enum ComponentUpdateState
{
    Downloading,
    Installing,
    Installed,
    Failed,
    RolledBack
}

/// <summary>How a link between a site and a policy or monitoring template came to exist.</summary>
public enum LinkSource
{
    /// <summary>Linked by a technician on the site.</summary>
    Manual,
    /// <summary>Maintained by the client template the site was created from.</summary>
    ClientTemplate
}

public enum SigningRequestKind
{
    /// <summary>Enrollment: certificate signing request plus enrollment token. Creates the endpoint.</summary>
    AgentEnrollment,
    /// <summary>Certificate renewal over an existing mTLS connection.</summary>
    AgentRenewal,
    /// <summary>Short-lived server certificate for the gateway.</summary>
    GatewayCertificate,
    /// <summary>Signed agent configuration (tier, policy, checks) for one endpoint.</summary>
    AgentConfig,
    /// <summary>Renewal of an expired, never revoked agent certificate within the recovery grace period (0.2.0).</summary>
    AgentRecovery,
    /// <summary>A job for one endpoint (0.2.0). SubjectId is the job id; only web may request it.</summary>
    Job,
    /// <summary>
    /// Certificate for the watchdog of an endpoint (0.2.1), requested by the gateway for a live agent session. SubjectId is the endpoint
    /// id, the payload the CSR of the watchdog key.
    /// </summary>
    WatchdogCertificate
}

public enum SigningRequestState
{
    Pending,
    Completed,
    Refused,
    Failed
}

public enum EndpointEventKind
{
    Connected,
    Disconnected,
    DuplicateIdentity
}

public enum ConfigChangeScope
{
    /// <summary>Every endpoint of the instance.</summary>
    Instance,
    Client,
    Site,
    Endpoint,
    Policy,
    MonitoringTemplate,
    /// <summary>Every managed endpoint with a script check that uses the script (0.2.0).</summary>
    Script
}

public enum NotificationChannelType
{
    Email,
    /// <summary>An HTTPS POST per notification (0.2.0).</summary>
    Webhook
}

/// <summary>The body of a webhook request.</summary>
public enum WebhookFormat
{
    /// <summary>Fleeto's own JSON, signed with the channel's signing secret.</summary>
    Generic,
    /// <summary>A Slack incoming webhook message.</summary>
    Slack,
    /// <summary>An adaptive card for a Microsoft Teams workflow ("When a Teams webhook request is received").</summary>
    Teams
}

public enum BackupKind
{
    Nightly,
    Manual,
    PreUpdate
}

public enum BackupRunStatus
{
    Running,
    Succeeded,
    Failed
}

public enum AuditActorType
{
    User,
    ApiKey,
    System,
    Agent
}
