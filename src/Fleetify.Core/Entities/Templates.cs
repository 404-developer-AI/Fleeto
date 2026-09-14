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

/// <summary>One check in a monitoring template. Carries the same ClientId as its template.</summary>
public class CheckDefinition
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public Guid MonitoringTemplateId { get; set; }
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
