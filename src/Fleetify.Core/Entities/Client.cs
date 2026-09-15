namespace Fleetify.Core.Entities;

/// <summary>
/// A customer of the IT team using Fleeto. The tenant boundary inside an instance: every client-owned
/// table carries its own ClientId (ARCHITECTURE.md §2, Client scoping rule).
/// </summary>
public class Client
{
    public Guid Id { get; set; }

    /// <summary>Short, unique, uppercase code such as <c>ACME</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The client template this client follows, if any. Linked, not copied.</summary>
    public Guid? ClientTemplateId { get; set; }

    /// <summary>Maintenance mode: active while started and the end is unset or in the future (MaintenanceRules).</summary>
    public DateTime? MaintenanceStartedAt { get; set; }

    /// <summary>Null means until turned off.</summary>
    public DateTime? MaintenanceEndsAt { get; set; }

    public Guid? MaintenanceStartedByUserId { get; set; }
    public string? MaintenanceStartedByName { get; set; }
    public string? MaintenanceReason { get; set; }

    public Domain.MaintenancePeriod Maintenance =>
        new(MaintenanceStartedAt, MaintenanceEndsAt, MaintenanceStartedByName, MaintenanceReason);

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Site> Sites { get; set; } = new List<Site>();
}

/// <summary>A group of endpoints within a client; where a policy and monitoring templates are linked.</summary>
public class Site
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The client template site this site was created from; null for manual or detached sites.</summary>
    public Guid? ClientTemplateSiteId { get; set; }

    /// <summary>Maintenance mode: active while started and the end is unset or in the future (MaintenanceRules).</summary>
    public DateTime? MaintenanceStartedAt { get; set; }

    /// <summary>Null means until turned off.</summary>
    public DateTime? MaintenanceEndsAt { get; set; }

    public Guid? MaintenanceStartedByUserId { get; set; }
    public string? MaintenanceStartedByName { get; set; }
    public string? MaintenanceReason { get; set; }

    public Domain.MaintenancePeriod Maintenance =>
        new(MaintenanceStartedAt, MaintenanceEndsAt, MaintenanceStartedByName, MaintenanceReason);

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Client? Client { get; set; }
    public ICollection<Endpoint> Endpoints { get; set; } = new List<Endpoint>();
    public ICollection<SiteMonitoringTemplate> MonitoringTemplates { get; set; } = new List<SiteMonitoringTemplate>();
    public SitePolicy? Policy { get; set; }
}

/// <summary>A machine with a Fleeto agent (or, later, a hypervisor object from an integration).</summary>
public class Endpoint
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid SiteId { get; set; }

    public string Hostname { get; set; } = string.Empty;

    /// <summary>Class derived from the OS (server edition or not).</summary>
    public EndpointClass DetectedClass { get; set; }

    /// <summary>Manual override by a technician; null means the detected class applies.</summary>
    public EndpointClass? ClassOverride { get; set; }

    public EndpointTier Tier { get; set; } = EndpointTier.AgentOnly;
    public EndpointSource Source { get; set; } = EndpointSource.Agent;

    public string OsPlatform { get; set; } = string.Empty;
    public string OsName { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;

    /// <summary>True while the agent has a live connection to the gateway.</summary>
    public bool IsOnline { get; set; }

    /// <summary>Server time of the last message from the agent (never agent time).</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>True while the watchdog has a live connection to the gateway (0.2.1). <see cref="IsOnline"/> stays the agent.</summary>
    public bool WatchdogOnline { get; set; }

    /// <summary>Version the watchdog reported when it last connected; empty until it ever connected.</summary>
    public string WatchdogVersion { get; set; } = string.Empty;

    /// <summary>Server time of the last message from the watchdog.</summary>
    public DateTime? WatchdogLastSeenAt { get; set; }

    public DateTime EnrolledAt { get; set; }

    /// <summary>Version of the latest signed configuration issued for this endpoint.</summary>
    public long ConfigVersion { get; set; }

    /// <summary>Configuration version the agent last confirmed as applied.</summary>
    public long AppliedConfigVersion { get; set; }

    /// <summary>
    /// Personal data (GDPR): the address the gateway saw for the latest agent connection (behind the host proxy taken from
    /// the PROXY protocol header). Only the latest value is kept.
    /// </summary>
    public string? PublicIpAddress { get; set; }

    public DateTime? PublicIpSeenAt { get; set; }

    /// <summary>Maintenance mode: active while started and the end is unset or in the future (MaintenanceRules).</summary>
    public DateTime? MaintenanceStartedAt { get; set; }

    /// <summary>Null means until turned off.</summary>
    public DateTime? MaintenanceEndsAt { get; set; }

    public Guid? MaintenanceStartedByUserId { get; set; }
    public string? MaintenanceStartedByName { get; set; }
    public string? MaintenanceReason { get; set; }

    public Domain.MaintenancePeriod Maintenance =>
        new(MaintenanceStartedAt, MaintenanceEndsAt, MaintenanceStartedByName, MaintenanceReason);

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Site? Site { get; set; }
    public InventorySnapshot? Inventory { get; set; }

    /// <summary>The class that applies: the override when set, otherwise the detected class.</summary>
    public EndpointClass EffectiveClass => ClassOverride ?? DetectedClass;
}

/// <summary>
/// One issued agent certificate (enrollment or renewal). The gateway accepts a client certificate only when
/// its fingerprint is present here, not revoked and not expired: an allow list, so a deleted endpoint
/// (whose rows cascade away) can never reconnect.
/// </summary>
public class AgentCertificate
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }

    /// <summary>Lowercase hex SHA-256 of the certificate DER.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the SubjectPublicKeyInfo DER; renewals must keep the same key.</summary>
    public string PublicKeyFingerprint { get; set; } = string.Empty;

    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>Which service holds the key (0.2.1). One watchdog certificate is valid per endpoint at a time.</summary>
    public AgentComponent Role { get; set; } = AgentComponent.Agent;

    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevokedReason { get; set; }
}

/// <summary>Enrollment token for a site. Only the SHA-256 of the secret is stored.</summary>
public class EnrollmentToken
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid SiteId { get; set; }

    /// <summary>
    /// Set for an "enroll again" token (0.2.0): the agent that enrolls with it takes over this existing endpoint (its checks,
    /// alerts, notes and history) instead of creating a new one; the endpoint's earlier certificates are revoked.
    /// </summary>
    public Guid? EndpointId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }

    /// <summary>Maximum number of enrollments; null means unlimited until expiry. Default 1 (single use).</summary>
    public int? MaxUses { get; set; } = 1;

    public int UseCount { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsUsable(DateTime now) =>
        RevokedAt is null && ExpiresAt > now && (MaxUses is null || UseCount < MaxUses);
}

/// <summary>Latest inventory reported by the agent. One row per endpoint.</summary>
public class InventorySnapshot
{
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>Server time the inventory was received.</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>Hash reported by the agent; unchanged inventory is not resent.</summary>
    public string Hash { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string CpuModel { get; set; } = string.Empty;
    public int CpuCores { get; set; }
    public int CpuLogicalProcessors { get; set; }
    public long MemoryTotalBytes { get; set; }
    public DateTime? BootTime { get; set; }
    public string Domain { get; set; } = string.Empty;

    /// <summary>Personal data (GDPR): the user name of the interactive session, if any.</summary>
    public string LoggedOnUser { get; set; } = string.Empty;

    /// <summary>JSON array of disks: mount, filesystem, totalBytes, freeBytes.</summary>
    public string DisksJson { get; set; } = "[]";

    /// <summary>JSON array of network interfaces: name, macAddress, ipAddresses.</summary>
    public string NetworkInterfacesJson { get; set; } = "[]";

    /// <summary>JSON array of installed software: name, version, publisher, installDate.</summary>
    public string SoftwareJson { get; set; } = "[]";

    /// <summary>JSON array of services: name, displayName, startType, state. For picking a service in a check (0.2.0).</summary>
    public string ServicesJson { get; set; } = "[]";
}
