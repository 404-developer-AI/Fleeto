namespace Fleeto.Core.Entities;

/// <summary>
/// Agent behaviour for the endpoints it applies to. Linked to a client, a site or one endpoint (0.6.0); the most specific
/// link wins. ClientId null = global.
/// </summary>
public class Policy
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The instance default policy applies to endpoints without a linked policy on any level. Exactly one, global.</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// The endpoints the policy is for (0.6.0): a policy for servers never applies to a workstation, and the other way round,
    /// wherever it is linked. The default policy is for every endpoint.
    /// </summary>
    public CheckAppliesTo AppliesTo { get; set; } = CheckAppliesTo.All;

    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int InventoryIntervalSeconds { get; set; } = 6 * 60 * 60;

    /// <summary>Minutes a managed endpoint may be offline before an offline alert opens. 0 disables it.</summary>
    public int OfflineAlertAfterMinutes { get; set; } = 10;

    public AlertSeverity OfflineAlertSeverity { get; set; } = AlertSeverity.Critical;

    /// <summary>
    /// Recurring maintenance windows (0.2.0) as a JSON array of <see cref="Domain.MaintenanceWindow"/>. Their occurrences are stored
    /// ahead in <see cref="MaintenanceWindowOccurrence"/>.
    /// </summary>
    public string MaintenanceWindowsJson { get; set; } = "[]";

    /// <summary>
    /// Four-eyes approval (0.2.0): jobs for the endpoints of sites with this policy run only library scripts whose current version
    /// a second admin approved. Off by default, recommended for servers.
    /// </summary>
    public bool ScriptApprovalRequired { get; set; }

    /// <summary>When the endpoints of the linked sites get a new agent release (0.2.1). Applies to agent-only endpoints too.</summary>
    public UpdateRing UpdateRing { get; set; } = UpdateRing.Standard;

    /// <summary>
    /// Output one job of these endpoints may send back (0.2.1), between <see cref="ScriptRules.MinOutputBytes"/> and
    /// <see cref="ScriptRules.MaxOutputBytes"/>. Output beyond it is dropped on the endpoint and the job is marked truncated.
    /// </summary>
    public long MaxOutputBytes { get; set; } = ScriptRules.DefaultMaxOutputBytes;

    /// <summary>
    /// Remote control on workstations (0.3.0): ask the signed-in user to allow a session first. Off by default; servers never ask.
    /// </summary>
    public bool RemoteConsentRequired { get; set; }

    /// <summary>Seconds the consent prompt waits for an answer; without one, access is granted (0.3.0).</summary>
    public int RemoteConsentTimeoutSeconds { get; set; } = Domain.RemoteSessionRules.DefaultConsentTimeoutSeconds;

    /// <summary>Remote control on workstations (0.3.0): a banner naming the technicians while a session runs. On by default; servers never show it.</summary>
    public bool RemoteBannerVisible { get; set; } = true;

    /// <summary>Remote control (0.3.0): clipboard synchronisation between technician and endpoint.</summary>
    public bool RemoteClipboardEnabled { get; set; } = true;

    /// <summary>A remote session without input from the technician closes after this many minutes (0.3.0).</summary>
    public int RemoteIdleTimeoutMinutes { get; set; } = Domain.RemoteSessionRules.DefaultIdleTimeoutMinutes;

    /// <summary>The largest file one transfer in a remote session may carry (0.3.0).</summary>
    public long RemoteMaxFileBytes { get; set; } = Domain.RemoteSessionRules.DefaultMaxFileBytes;

    /// <summary>Policy this one was copied from; the copy is independent.</summary>
    public Guid? CopiedFromId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One occurrence of a maintenance window of a policy, in UTC, stored ahead for <see cref="Domain.MaintenanceWindows.Horizon"/> by web
/// (when the policy is saved) and the workers (every hour). Read by the maintenance rule as a fourth source.
/// </summary>
public class MaintenanceWindowOccurrence
{
    public Guid PolicyId { get; set; }
    public int WindowIndex { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public CheckAppliesTo AppliesTo { get; set; }
    public string? Name { get; set; }
}

/// <summary>A named set of checks linked to clients, sites and endpoints. ClientId null = global.</summary>
public class MonitoringTemplate
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// The endpoints the template is for (0.6.0), on top of the class of each check: a template for servers runs none of its
    /// checks on a workstation.
    /// </summary>
    public CheckAppliesTo AppliesTo { get; set; } = CheckAppliesTo.All;
    public Guid? CopiedFromId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<CheckDefinition> Checks { get; set; } = new List<CheckDefinition>();
}

/// <summary>
/// One check. Owned by exactly one of a monitoring template (carrying the same ClientId as its template) or a single
/// endpoint (a check that exists only on that endpoint, carrying the endpoint's ClientId).
/// </summary>
public class CheckDefinition
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? MonitoringTemplateId { get; set; }

    /// <summary>Set for a check that exists only on this endpoint; null for a template check.</summary>
    public Guid? EndpointId { get; set; }
    public string Name { get; set; } = string.Empty;
    public CheckType Type { get; set; }
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Type-specific parameters as a JSON object of strings, e.g. {"service":"Spooler"} or {"drive":"C:"}.</summary>
    public string ParametersJson { get; set; } = "{}";

    public double? WarningThreshold { get; set; }
    public double? CriticalThreshold { get; set; }

    /// <summary>Consecutive non-OK results before an alert opens.</summary>
    public int FailuresBeforeAlert { get; set; } = 1;

    public CheckAppliesTo AppliesTo { get; set; } = CheckAppliesTo.All;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MonitoringTemplate? MonitoringTemplate { get; set; }
}

/// <summary>Link between a site and a monitoring template.</summary>
public class SiteMonitoringTemplate
{
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }
    public Guid MonitoringTemplateId { get; set; }
    public LinkSource Source { get; set; }
    public DateTime CreatedAt { get; set; }

    public MonitoringTemplate? MonitoringTemplate { get; set; }
}

/// <summary>An extra monitoring template linked to one endpoint, on top of the templates of its site.</summary>
public class EndpointMonitoringTemplate
{
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }
    public Guid MonitoringTemplateId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }

    public MonitoringTemplate? MonitoringTemplate { get; set; }
}

/// <summary>
/// Adjustments of one template check for one endpoint. The template stays linked: unset fields inherit its values, so a
/// later change to the template still applies to the fields that are not overridden.
/// </summary>
public class EndpointCheckOverride
{
    public Guid EndpointId { get; set; }
    public Guid CheckDefinitionId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>The check does not run on this endpoint.</summary>
    public bool Disabled { get; set; }

    public int? IntervalSeconds { get; set; }
    public int? FailuresBeforeAlert { get; set; }

    /// <summary>
    /// True when <see cref="WarningThreshold"/> and <see cref="CriticalThreshold"/> replace the template thresholds as a
    /// pair, null included. A flag rather than null-means-inherit, so "no warning threshold" can be an override too.
    /// </summary>
    public bool OverrideThresholds { get; set; }

    public double? WarningThreshold { get; set; }
    public double? CriticalThreshold { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    /// <summary>True when the row changes nothing and can be removed.</summary>
    public bool IsEmpty => !Disabled && IntervalSeconds is null && FailuresBeforeAlert is null && !OverrideThresholds;
}

/// <summary>
/// The policy linked to a client (0.6.0). It applies to every endpoint of the client whose site and endpoint have none.
/// One for every endpoint, or one for servers and one for workstations.
/// </summary>
public class ClientPolicy
{
    public Guid ClientId { get; set; }
    public Guid PolicyId { get; set; }

    /// <summary>
    /// The endpoints of the level this link is for (0.6.0): <see cref="CheckAppliesTo.All"/>, or one link for servers and one
    /// for workstations, so a client or site can hold a different one per class.
    /// </summary>
    public CheckAppliesTo AppliesTo { get; set; } = CheckAppliesTo.All;
    public LinkSource Source { get; set; }
    public DateTime CreatedAt { get; set; }

    public Policy? Policy { get; set; }
}

/// <summary>The policy linked to one endpoint (0.6.0). It wins over the policy of its site and client.</summary>
public class EndpointPolicy
{
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }
    public Guid PolicyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }

    public Policy? Policy { get; set; }
}

/// <summary>
/// A monitoring template linked to a whole client (0.6.0). Its checks apply to every endpoint of the client, together with
/// the templates of the site and the endpoint: templates add up, they never replace each other.
/// </summary>
public class ClientMonitoringTemplate
{
    public Guid ClientId { get; set; }
    public Guid MonitoringTemplateId { get; set; }
    public LinkSource Source { get; set; }
    public DateTime CreatedAt { get; set; }

    public MonitoringTemplate? MonitoringTemplate { get; set; }
}

/// <summary>The patch policy linked to a client (0.6.0): one for every endpoint, or one per class.</summary>
public class ClientPatchPolicy
{
    public Guid ClientId { get; set; }
    public Guid PatchPolicyId { get; set; }

    /// <summary>
    /// The endpoints of the level this link is for (0.6.0): <see cref="CheckAppliesTo.All"/>, or one link for servers and one
    /// for workstations, so a client or site can hold a different one per class.
    /// </summary>
    public CheckAppliesTo AppliesTo { get; set; } = CheckAppliesTo.All;
    public LinkSource Source { get; set; }
    public DateTime CreatedAt { get; set; }

    public PatchPolicy? PatchPolicy { get; set; }
}

/// <summary>The patch policy linked to a site (0.6.0). It wins over the one of the client; one for every endpoint, or one per class.</summary>
public class SitePatchPolicy
{
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }
    public Guid PatchPolicyId { get; set; }

    /// <summary>
    /// The endpoints of the level this link is for (0.6.0): <see cref="CheckAppliesTo.All"/>, or one link for servers and one
    /// for workstations, so a client or site can hold a different one per class.
    /// </summary>
    public CheckAppliesTo AppliesTo { get; set; } = CheckAppliesTo.All;
    public LinkSource Source { get; set; }
    public DateTime CreatedAt { get; set; }

    public PatchPolicy? PatchPolicy { get; set; }
}

/// <summary>The patch policy linked to one endpoint (0.6.0). It wins over the one of its site and client.</summary>
public class EndpointPatchPolicy
{
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }
    public Guid PatchPolicyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }

    public PatchPolicy? PatchPolicy { get; set; }
}

/// <summary>
/// The policy linked to a site: one for every endpoint, or one per class (0.6.0). Without one the policy of the client
/// applies, and without that the default policy.
/// </summary>
public class SitePolicy
{
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }
    public Guid PolicyId { get; set; }

    /// <summary>
    /// The endpoints of the level this link is for (0.6.0): <see cref="CheckAppliesTo.All"/>, or one link for servers and one
    /// for workstations, so a client or site can hold a different one per class.
    /// </summary>
    public CheckAppliesTo AppliesTo { get; set; } = CheckAppliesTo.All;
    public LinkSource Source { get; set; }
    public DateTime CreatedAt { get; set; }

    public Policy? Policy { get; set; }
}

/// <summary>
/// Blueprint of a client, used when creating one: the policy, patch policy and monitoring templates of the client itself
/// (0.6.0), and the sites with their own.
/// </summary>
public class ClientTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The policy of the client itself for every endpoint (0.6.0); null leaves the client without one.</summary>
    public Guid? PolicyId { get; set; }

    /// <summary>The policies of the client itself per class, instead of <see cref="PolicyId"/> (0.6.0).</summary>
    public Guid? ServerPolicyId { get; set; }

    public Guid? WorkstationPolicyId { get; set; }

    /// <summary>The patch policy of the client itself for every endpoint (0.6.0); null leaves the client without one.</summary>
    public Guid? PatchPolicyId { get; set; }

    /// <summary>The patch policies of the client itself per class, instead of <see cref="PatchPolicyId"/> (0.6.0).</summary>
    public Guid? ServerPatchPolicyId { get; set; }

    public Guid? WorkstationPatchPolicyId { get; set; }
    public Guid? CopiedFromId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ClientTemplateSite> Sites { get; set; } = new List<ClientTemplateSite>();

    /// <summary>The monitoring templates of the client itself (0.6.0).</summary>
    public ICollection<ClientTemplateMonitoringTemplate> MonitoringTemplates { get; set; } = new List<ClientTemplateMonitoringTemplate>();
}

public class ClientTemplateMonitoringTemplate
{
    public Guid ClientTemplateId { get; set; }
    public Guid MonitoringTemplateId { get; set; }
}

/// <summary>A site in a client template. Only global policies and monitoring templates can be linked.</summary>
public class ClientTemplateSite
{
    public Guid Id { get; set; }
    public Guid ClientTemplateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? PolicyId { get; set; }

    /// <summary>The policies of the site per class, instead of <see cref="PolicyId"/> (0.6.0).</summary>
    public Guid? ServerPolicyId { get; set; }

    public Guid? WorkstationPolicyId { get; set; }

    /// <summary>The patch policy of the site (0.6.0); null follows the client.</summary>
    public Guid? PatchPolicyId { get; set; }

    /// <summary>The patch policies of the site per class, instead of <see cref="PatchPolicyId"/> (0.6.0).</summary>
    public Guid? ServerPatchPolicyId { get; set; }

    public Guid? WorkstationPatchPolicyId { get; set; }

    public int SortOrder { get; set; }

    public ICollection<ClientTemplateSiteMonitoringTemplate> MonitoringTemplates { get; set; } =
        new List<ClientTemplateSiteMonitoringTemplate>();
}

public class ClientTemplateSiteMonitoringTemplate
{
    public Guid ClientTemplateSiteId { get; set; }
    public Guid MonitoringTemplateId { get; set; }
}
