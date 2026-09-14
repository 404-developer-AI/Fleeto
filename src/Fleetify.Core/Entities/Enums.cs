namespace Fleetify.Core.Entities;

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

/// <summary>Check types supported by the agent in 0.1.0.</summary>
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
    Uptime
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
    DuplicateIdentity
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
    AgentConfig
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
    MonitoringTemplate
}

public enum NotificationChannelType
{
    Email
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
