namespace Fleetify.Core.Entities;

/// <summary>
/// One check result as received by the gateway. TimescaleDB hypertable on <see cref="Time"/> when the
/// extension is installed; a plain table otherwise.
/// </summary>
public class CheckResult
{
    public long Id { get; set; }

    /// <summary>Ingest time stamped by the gateway. Used for ordering; agent time is informational.</summary>
    public DateTime Time { get; set; }

    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public Guid CheckDefinitionId { get; set; }

    /// <summary>Instance of the check, e.g. the drive for a disk check; empty when not applicable.</summary>
    public string Target { get; set; } = string.Empty;

    public DateTime AgentTime { get; set; }
    public double Value { get; set; }
    public string Detail { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public long ConfigVersion { get; set; }
}

/// <summary>Deduplication of agent result batches: a batch sequence number is stored once per endpoint.</summary>
public class IngestBatch
{
    public Guid EndpointId { get; set; }
    public long Sequence { get; set; }
    public Guid ClientId { get; set; }
    public DateTime ReceivedAt { get; set; }
}

/// <summary>Current evaluated state of one check (and target) on one endpoint, maintained by the workers.</summary>
public class CheckState
{
    public Guid EndpointId { get; set; }
    public Guid CheckDefinitionId { get; set; }
    public string Target { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public CheckStatus Status { get; set; } = CheckStatus.Unknown;
    public double? Value { get; set; }
    public string Detail { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ConsecutiveNonOk { get; set; }
    public DateTime LastResultAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>An alert. At most one unresolved alert per endpoint, kind, check and target.</summary>
public class Alert
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public AlertKind Kind { get; set; }
    public Guid? CheckDefinitionId { get; set; }
    public string Target { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public AlertState State { get; set; } = AlertState.Open;

    /// <summary>Cause and next step, per branding §8: "SRV-DC01 has less than 10% free disk space on C:".</summary>
    public string Title { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedReason { get; set; }

    public Endpoint? Endpoint { get; set; }
}

/// <summary>Connection events written by the gateway and turned into alerts or history by the workers.</summary>
public class EndpointEvent
{
    public long Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public EndpointEventKind Kind { get; set; }
    public string Detail { get; set; } = string.Empty;
    public DateTime Time { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
