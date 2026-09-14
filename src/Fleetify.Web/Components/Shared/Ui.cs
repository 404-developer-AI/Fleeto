using System.Globalization;
using System.Text.Json;
using Fleetify.Core.Entities;

namespace Fleetify.Web.Components.Shared;

/// <summary>Status chip kinds, mapped to the branding §4 status colors.</summary>
public enum StatusKind
{
    /// <summary>Online / OK: primary tint.</summary>
    Ok,
    /// <summary>Running or active operations: strong primary tint.</summary>
    Active,
    /// <summary>Threshold breached, degraded: amber.</summary>
    Warning,
    /// <summary>Offline, error, failure: red. Reserved for genuine failure states.</summary>
    Error,
    /// <summary>Unknown, paused, never seen: neutral gray.</summary>
    Neutral
}

/// <summary>Display helpers shared by pages: labels in the fixed vocabulary (branding §6) and status kinds.</summary>
public static class Ui
{
    public static string TierLabel(EndpointTier tier) => tier == EndpointTier.Managed ? "Managed" : "Agent-only";

    public static StatusKind TierKind(EndpointTier tier) => tier == EndpointTier.Managed ? StatusKind.Active : StatusKind.Neutral;

    public static string ClassLabel(EndpointClass endpointClass) => endpointClass == EndpointClass.Server ? "Server" : "Workstation";

    public static StatusKind CheckKind(CheckStatus status) => status switch
    {
        CheckStatus.Ok => StatusKind.Ok,
        CheckStatus.Warning => StatusKind.Warning,
        CheckStatus.Critical => StatusKind.Error,
        _ => StatusKind.Neutral
    };

    public static StatusKind SeverityKind(AlertSeverity severity) => severity == AlertSeverity.Critical ? StatusKind.Error : StatusKind.Warning;

    public static StatusKind AlertStateKind(AlertState state) => state switch
    {
        AlertState.Open => StatusKind.Warning,
        AlertState.Acknowledged => StatusKind.Active,
        _ => StatusKind.Ok
    };

    public static string CheckTypeLabel(CheckType type) => type switch
    {
        CheckType.CpuUsage => "CPU usage",
        CheckType.MemoryUsage => "Memory usage",
        CheckType.DiskFree => "Free disk space",
        CheckType.ServiceRunning => "Service running",
        CheckType.Uptime => "Uptime",
        _ => type.ToString()
    };

    public static string AppliesToLabel(CheckAppliesTo appliesTo) => appliesTo switch
    {
        CheckAppliesTo.Workstation => "Workstations",
        CheckAppliesTo.Server => "Servers",
        _ => "All endpoints"
    };

    /// <summary>The unit a threshold of this check type is expressed in.</summary>
    public static string ThresholdUnit(CheckType type) => type switch
    {
        CheckType.Uptime => "days",
        CheckType.ServiceRunning => string.Empty,
        _ => "%"
    };

    public static string CheckValue(CheckType type, double? value)
    {
        if (value is null)
        {
            return "-";
        }

        return type switch
        {
            CheckType.ServiceRunning => value >= 1 ? "Running" : "Not running",
            CheckType.Uptime => value.Value.ToString("0.#", CultureInfo.InvariantCulture) + " days",
            _ => value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%"
        };
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return size.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string EventLabel(EndpointEventKind kind) => kind switch
    {
        EndpointEventKind.Connected => "Connected",
        EndpointEventKind.Disconnected => "Disconnected",
        EndpointEventKind.DuplicateIdentity => "Duplicate agent identity",
        _ => kind.ToString()
    };

    public static string PrettyJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public static string CssClass(StatusKind kind) => kind switch
    {
        StatusKind.Ok => "status-ok",
        StatusKind.Active => "status-active",
        StatusKind.Warning => "status-warning",
        StatusKind.Error => "status-error",
        _ => "status-neutral"
    };
}
