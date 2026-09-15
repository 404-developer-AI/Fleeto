using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Fleeto.Web.Api;

// The response contract of the public API (/api/v1), documented in MD-Files/API.md. These types are separate from the entities and the
// UI records on purpose: a rename inside Fleeto must never change a field name or value that integrations depend on. Field names are
// camelCase (the ASP.NET Core web defaults, as in the webhook payload); values of enumerations are snake_case strings.

[JsonConverter(typeof(JsonStringEnumConverter<ApiEndpointClass>))]
public enum ApiEndpointClass
{
    [JsonStringEnumMemberName("workstation")] Workstation,
    [JsonStringEnumMemberName("server")] Server
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiEndpointTier>))]
public enum ApiEndpointTier
{
    [JsonStringEnumMemberName("agent_only")] AgentOnly,
    [JsonStringEnumMemberName("managed")] Managed
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiEndpointSource>))]
public enum ApiEndpointSource
{
    [JsonStringEnumMemberName("agent")] Agent,
    [JsonStringEnumMemberName("integration")] Integration
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiMaintenanceSource>))]
public enum ApiMaintenanceSource
{
    [JsonStringEnumMemberName("endpoint")] Endpoint,
    [JsonStringEnumMemberName("site")] Site,
    [JsonStringEnumMemberName("client")] Client,
    [JsonStringEnumMemberName("policy_window")] PolicyWindow
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiAlertKind>))]
public enum ApiAlertKind
{
    [JsonStringEnumMemberName("check")] Check,
    [JsonStringEnumMemberName("offline")] Offline,
    [JsonStringEnumMemberName("duplicate_identity")] DuplicateIdentity,
    [JsonStringEnumMemberName("agent_stopped")] AgentStopped,
    [JsonStringEnumMemberName("watchdog_stopped")] WatchdogStopped
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiAlertSeverity>))]
public enum ApiAlertSeverity
{
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("critical")] Critical
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiAlertState>))]
public enum ApiAlertState
{
    [JsonStringEnumMemberName("open")] Open,
    [JsonStringEnumMemberName("acknowledged")] Acknowledged,
    [JsonStringEnumMemberName("resolved")] Resolved
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiCheckStatus>))]
public enum ApiCheckStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("critical")] Critical,
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("not_run_yet")] NotRunYet,
    [JsonStringEnumMemberName("rerun_requested")] RerunRequested,
    [JsonStringEnumMemberName("disabled")] Disabled
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiCheckSource>))]
public enum ApiCheckSource
{
    [JsonStringEnumMemberName("site_template")] SiteTemplate,
    [JsonStringEnumMemberName("endpoint_template")] EndpointTemplate,
    [JsonStringEnumMemberName("endpoint")] Endpoint
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiJobState>))]
public enum ApiJobState
{
    [JsonStringEnumMemberName("pending_signature")] PendingSignature,
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("expired")] Expired,
    [JsonStringEnumMemberName("refused")] Refused,
    [JsonStringEnumMemberName("lost")] Lost,
    [JsonStringEnumMemberName("cancelled")] Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiJobResult>))]
public enum ApiJobResult
{
    [JsonStringEnumMemberName("exited")] Exited,
    [JsonStringEnumMemberName("timed_out")] TimedOut,
    [JsonStringEnumMemberName("refused")] Refused,
    [JsonStringEnumMemberName("failed_to_start")] FailedToStart,
    [JsonStringEnumMemberName("interrupted")] Interrupted
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiJobOutputState>))]
public enum ApiJobOutputState
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("receiving")] Receiving,
    [JsonStringEnumMemberName("complete")] Complete,
    [JsonStringEnumMemberName("incomplete")] Incomplete
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiScriptLanguage>))]
public enum ApiScriptLanguage
{
    [JsonStringEnumMemberName("powershell")] PowerShell,
    [JsonStringEnumMemberName("batch")] Batch,
    [JsonStringEnumMemberName("sh")] Shell,
    [JsonStringEnumMemberName("bash")] Bash
}

/// <summary>One page of a list. Pass <see cref="NextCursor"/> as <c>cursor</c> to get the next page; null means this is the last page.</summary>
public sealed record ApiPage<T>(IReadOnlyList<T> Items, string? NextCursor);

[Description("A maintenance of a client or site that is active now.")]
public sealed record ApiMaintenance(DateTime StartedAt, DateTime? EndsAt, string? StartedBy, string? Reason);

[Description("The maintenance that applies to an endpoint now, and where it comes from.")]
public sealed record ApiEndpointMaintenance(ApiMaintenanceSource Source, DateTime StartedAt, DateTime? EndsAt, string? StartedBy, string? Reason,
    string? PolicyName);

public sealed record ApiClient(Guid Id, string Code, string Name, int SiteCount, int EndpointCount, ApiMaintenance? Maintenance, DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ApiSite(Guid Id, Guid ClientId, string Name, string? Description, int EndpointCount, ApiMaintenance? Maintenance, DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ApiOperatingSystem(string Platform, string Name, string Version);

public sealed record ApiEndpoint(
    Guid Id,
    Guid ClientId,
    Guid SiteId,
    string Hostname,
    ApiEndpointClass Class,
    ApiEndpointClass DetectedClass,
    bool ClassOverridden,
    ApiEndpointTier Tier,
    ApiEndpointTier EffectiveTier,
    ApiEndpointSource Source,
    bool Online,
    DateTime? LastSeenAt,
    ApiOperatingSystem Os,
    string Architecture,
    string AgentVersion,
    DateTime EnrolledAt,
    string? PublicIpAddress,
    DateTime? PublicIpSeenAt,
    int OpenAlertCount,
    int HeldAlertCount,
    ApiEndpointMaintenance? Maintenance,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ApiCpu(string Model, int Cores, int LogicalProcessors);

public sealed record ApiDisk(string Mount, string Filesystem, long TotalBytes, long FreeBytes);

public sealed record ApiNetworkInterface(string Name, string MacAddress, IReadOnlyList<string> IpAddresses);

public sealed record ApiSoftware(string Name, string Version, string Publisher, string InstallDate);

public sealed record ApiService(string Name, string DisplayName, string StartType, string State);

public sealed record ApiInventory(Guid EndpointId, DateTime ReceivedAt, string Manufacturer, string Model, string SerialNumber, ApiCpu Cpu,
    long MemoryTotalBytes, DateTime? BootTime, string Domain, string LoggedOnUser, IReadOnlyList<ApiDisk> Disks,
    IReadOnlyList<ApiNetworkInterface> NetworkInterfaces, IReadOnlyList<ApiSoftware> Software, IReadOnlyList<ApiService> Services);

public sealed record ApiCheck(
    Guid CheckId,
    string Name,
    string Type,
    string Target,
    ApiCheckStatus Status,
    double? Value,
    string Detail,
    string Error,
    DateTime? LastResultAt,
    int IntervalSeconds,
    ApiCheckSource Source,
    string? MonitoringTemplateName,
    bool Adjusted,
    Guid? OpenAlertId,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record ApiEndpointChecks(Guid EndpointId, bool ConfigurationPending, IReadOnlyList<ApiCheck> Items);

public sealed record ApiAlert(
    Guid Id,
    Guid ClientId,
    Guid EndpointId,
    ApiAlertKind Kind,
    Guid? CheckId,
    string Target,
    ApiAlertSeverity Severity,
    ApiAlertState State,
    bool OnHold,
    DateTime? HeldUntil,
    string Title,
    string Detail,
    DateTime OpenedAt,
    DateTime UpdatedAt,
    DateTime? AcknowledgedAt,
    DateTime? ResolvedAt,
    string? ResolvedReason);

public sealed record ApiJobScript(Guid? Id, Guid? VersionId, string Name, int VersionNumber, ApiScriptLanguage Language, string Sha256);

public sealed record ApiJobOutputSummary(ApiJobOutputState State, bool Truncated, long Bytes);

public sealed record ApiJob(
    Guid Id,
    Guid BatchId,
    Guid ClientId,
    Guid EndpointId,
    string Type,
    ApiJobScript Script,
    ApiJobState State,
    ApiJobResult? Result,
    int? ExitCode,
    string? Problem,
    string InitiatedBy,
    DateTime CreatedAt,
    DateTime ValidUntil,
    DateTime? DeliveredAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    ApiJobOutputSummary Output);

public sealed record ApiJobOutput(Guid JobId, ApiJobOutputState State, string Stdout, long StdoutBytes, string Stderr, long StderrBytes, bool Shortened,
    bool Truncated);

public sealed record ApiNote(Guid Id, Guid EndpointId, string AuthorName, string Body, DateTime CreatedAt, DateTime? EditedAt);
