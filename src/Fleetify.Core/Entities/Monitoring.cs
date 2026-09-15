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

    /// <summary>
    /// When a technician reset the check. Until a result ingested after this time arrives the state is "re-run requested":
    /// status Unknown without a value, never a made-up OK. Results ingested before it are ignored.
    /// </summary>
    public DateTime? ResetAt { get; set; }

    /// <summary>True while a reset waits for its first new result.</summary>
    public bool RerunRequested => ResetAt is { } reset && reset > LastResultAt;
}

/// <summary>
/// A technician's request to run one check now on one endpoint, optionally with a reset of its state. Written by web,
/// the reset is applied by the workers and the request is delivered to the agent by the gateway.
/// </summary>
public class CheckRunRequest
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public Guid CheckDefinitionId { get; set; }

    /// <summary>Resolve the open alerts of the check and set its state to "re-run requested" before it runs.</summary>
    public bool Reset { get; set; }

    public Guid RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }

    /// <summary>An agent that connects after this time does not receive the request.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime? ResetAppliedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }

    /// <summary>Set when the request ends without delivery. Null while pending or once delivered.</summary>
    public CheckRunRequestOutcome? Outcome { get; set; }
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

    /// <summary>
    /// While in the future the alert is on hold: no escalation or resolve emails, left out of open alert lists and counts.
    /// The alert itself stays real: it still resolves when its cause recovers. Cleared by the workers when it passes.
    /// </summary>
    public DateTime? HeldUntil { get; set; }

    public DateTime? HeldAt { get; set; }
    public Guid? HeldByUserId { get; set; }

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

/// <summary>
/// Hourly rollup of the results of one check and target on one endpoint (0.2.0, check history). Maintained by the workers in the
/// same transaction that evaluates the results, so every result is counted exactly once. The bucket is the start of the hour of the
/// agent's collection time when that is plausible (at most 7 days before and 5 minutes after ingest), otherwise of the ingest time.
/// Values that are errors or "no response" (-1 of a network check) are counted, never part of minimum, maximum or average.
/// </summary>
public class CheckResultHourly
{
    public Guid EndpointId { get; set; }
    public Guid CheckDefinitionId { get; set; }
    public string Target { get; set; } = string.Empty;
    public DateTime Bucket { get; set; }
    public Guid ClientId { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double SumValue { get; set; }
    public int ValueCount { get; set; }
    public int ErrorCount { get; set; }
    public int NoResponseCount { get; set; }
}

/// <summary>Daily rollup, same shape and rules as <see cref="CheckResultHourly"/>; the bucket is the start of the UTC day.</summary>
public class CheckResultDaily
{
    public Guid EndpointId { get; set; }
    public Guid CheckDefinitionId { get; set; }
    public string Target { get; set; } = string.Empty;
    public DateTime Bucket { get; set; }
    public Guid ClientId { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double SumValue { get; set; }
    public int ValueCount { get; set; }
    public int ErrorCount { get; set; }
    public int NoResponseCount { get; set; }
}
