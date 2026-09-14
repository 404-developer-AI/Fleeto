namespace Fleetify.Core.Entities;

/// <summary>Agent behaviour for every endpoint of the sites it is linked to. ClientId null = global.</summary>
public class Policy
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The instance default policy applies to sites without a linked policy. Exactly one, global.</summary>
    public bool IsDefault { get; set; }

    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int InventoryIntervalSeconds { get; set; } = 6 * 60 * 60;

    /// <summary>Minutes a managed endpoint may be offline before an offline alert opens. 0 disables it.</summary>
    public int OfflineAlertAfterMinutes { get; set; } = 10;

    public AlertSeverity OfflineAlertSeverity { get; set; } = AlertSeverity.Critical;

    /// <summary>Policy this one was copied from; the copy is independent.</summary>
    public Guid? CopiedFromId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A named set of checks linked to sites. ClientId null = global.</summary>
public class MonitoringTemplate
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
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

/// <summary>The policy linked to a site. At most one per site; without one the default policy applies.</summary>
public class SitePolicy
{
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }
    public Guid PolicyId { get; set; }
    public LinkSource Source { get; set; }
    public DateTime CreatedAt { get; set; }

    public Policy? Policy { get; set; }
}

/// <summary>Blueprint of sites with their policy and monitoring templates, used when creating a client.</summary>
public class ClientTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? CopiedFromId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ClientTemplateSite> Sites { get; set; } = new List<ClientTemplateSite>();
}

/// <summary>A site in a client template. Only global policies and monitoring templates can be linked.</summary>
public class ClientTemplateSite
{
    public Guid Id { get; set; }
    public Guid ClientTemplateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? PolicyId { get; set; }
    public int SortOrder { get; set; }

    public ICollection<ClientTemplateSiteMonitoringTemplate> MonitoringTemplates { get; set; } =
        new List<ClientTemplateSiteMonitoringTemplate>();
}

public class ClientTemplateSiteMonitoringTemplate
{
    public Guid ClientTemplateSiteId { get; set; }
    public Guid MonitoringTemplateId { get; set; }
}
