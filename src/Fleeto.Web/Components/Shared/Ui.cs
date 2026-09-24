using System.Globalization;
using System.Net;
using System.Text.Json;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Security;
using Fleeto.Web.Services;

namespace Fleeto.Web.Components.Shared;

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

    /// <summary>Names as prose: "Anna", "Anna and Bert", "Anna, Bert and Carl".</summary>
    public static string JoinList(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "Nobody",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
    };

    public static string UpdateRingLabel(UpdateRing ring) => ring switch
    {
        UpdateRing.Preview => "Preview ring (at once)",
        UpdateRing.Delayed => "Delayed ring (after 14 days)",
        _ => "Standard ring (after 7 days)"
    };

    public static string ServiceStateLabel(ComponentServiceState state) => state switch
    {
        ComponentServiceState.Running => "Running",
        ComponentServiceState.Stopped => "Stopped",
        ComponentServiceState.Starting => "Starting",
        ComponentServiceState.Stopping => "Stopping",
        ComponentServiceState.Disabled => "Disabled",
        ComponentServiceState.NotInstalled => "Not installed",
        _ => "Unknown"
    };

    public static string UpdateStateLabel(ComponentUpdateState state) => state switch
    {
        ComponentUpdateState.Downloading => "Downloading",
        ComponentUpdateState.Installing => "Installing",
        ComponentUpdateState.Installed => "Installed",
        ComponentUpdateState.Failed => "Update failed",
        _ => "Rolled back"
    };

    /// <summary>
    /// What the installer of a component waits for before it installs an offered release (0.2.2), or null when there is nothing to show: no wait
    /// reported, or the component already runs that release or a newer one.
    /// </summary>
    public static string? UpdateWaitText(EndpointComponentView component, string installedVersion, EndpointDetail endpoint, DateTime now,
        Func<DateTime?, string> absolute)
    {
        if (component.WaitReason is not { } reason || string.IsNullOrEmpty(component.WaitVersion) ||
            installedVersion.Length > 0 && !SemanticVersion.IsOlder(installedVersion, component.WaitVersion))
        {
            return null;
        }

        var version = component.WaitVersion;
        // The ring timing belongs to the current release only; a wait for another release shows no date.
        var ring = endpoint.ReleaseRing is { } timing && endpoint.ReleaseVersion == version ? timing : null;
        return reason switch
        {
            ComponentUpdateWait.UpdateRing when ring is { Paused: true } => $"Waiting: release {version} is paused",
            ComponentUpdateWait.UpdateRing when ring is not null && ring.AvailableAt > now =>
                $"Waiting for the update ring: release {version} reaches the {ring.Ring} ring on {absolute(ring.AvailableAt)}",
            ComponentUpdateWait.UpdateRing when ring is not null => $"Waiting for the update ring: release {version}, {ring.Ring} ring",
            ComponentUpdateWait.UpdateRing => $"Waiting for the update ring: release {version}",
            ComponentUpdateWait.NextAttempt when component.WaitUntil is { } until => $"Next attempt to install {version} after {absolute(until)}",
            ComponentUpdateWait.NextAttempt => $"Waiting for the next attempt to install {version}",
            ComponentUpdateWait.RandomDelay when component.WaitUntil is { } until =>
                $"Installs {version} after {absolute(until)}: a random delay spreads the downloads of many endpoints",
            ComponentUpdateWait.RandomDelay => $"Installs {version} after a random delay",
            ComponentUpdateWait.InstallerUpdate => $"Waiting until the agent runs {version}: the agent updates the watchdog after its own update",
            _ => $"Release {version} was rolled back on this endpoint and is not tried again"
        };
    }

    public static StatusKind UpdateStateKind(ComponentUpdateState state) => state switch
    {
        ComponentUpdateState.Installed => StatusKind.Ok,
        ComponentUpdateState.Failed or ComponentUpdateState.RolledBack => StatusKind.Warning,
        _ => StatusKind.Active
    };

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

    public static string CheckTypeLabel(CheckType type) => Enum.IsDefined(type) ? CheckCatalog.Get(type).Label : type.ToString();

    public static string AppliesToLabel(CheckAppliesTo appliesTo) => appliesTo switch
    {
        CheckAppliesTo.Workstation => "Workstations",
        CheckAppliesTo.Server => "Servers",
        _ => "All endpoints"
    };

    /// <summary>The unit a threshold of this check type is expressed in, given its parameters.</summary>
    public static string ThresholdUnit(CheckType type, IReadOnlyDictionary<string, string>? parameters = null) =>
        Enum.IsDefined(type) ? CheckCatalog.UnitOf(type, parameters ?? NoParameters) : string.Empty;

    private static readonly IReadOnlyDictionary<string, string> NoParameters = new Dictionary<string, string>();

    /// <summary>"warning 85%, critical 95%", or for a flag check what a problem counts as.</summary>
    public static string ThresholdsText(CheckType type, IReadOnlyDictionary<string, string> parameters, double? warning, double? critical)
    {
        if (!Enum.IsDefined(type))
        {
            return "-";
        }

        var kind = CheckCatalog.ThresholdKindOf(type, parameters);
        if (kind == ThresholdKind.Flag)
        {
            return warning >= 1 ? "Problem: warning" : "Problem: critical";
        }

        if (kind == ThresholdKind.ExitCode)
        {
            return "Exit code 1: warning, other: critical";
        }

        var unit = CheckCatalog.UnitOf(type, parameters);
        string Format(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + (unit == "%" ? "%" : " " + unit);
        var parts = new List<string>();
        if (kind == ThresholdKind.Reachability)
        {
            parts.Add("no response: critical");
        }

        if (warning is { } w)
        {
            parts.Add($"warning {Format(w)}");
        }

        if (critical is { } c)
        {
            parts.Add($"critical {Format(c)}");
        }

        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    /// <summary>A check result for the Checks tab: "85.2%", "Running", "No response", "12 ms", "34 days".</summary>
    public static string CheckValue(CheckType type, double? value, string target = "", IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (value is not { } v)
        {
            return "-";
        }

        parameters ??= NoParameters;
        var number = v.ToString("0.#", CultureInfo.InvariantCulture);
        if (type == CheckType.Http && target == CheckEvaluator.CertificateTarget)
        {
            return v < 0 ? "Expired" : $"{number} days left";
        }

        if (!Enum.IsDefined(type))
        {
            return number;
        }

        return CheckCatalog.ThresholdKindOf(type, parameters) switch
        {
            ThresholdKind.Flag => FlagText(type, parameters, v >= 1, v),
            ThresholdKind.Reachability => v < 0 ? "No response" : $"{number} ms",
            ThresholdKind.ExitCode => $"Exit code {v.ToString("0", CultureInfo.InvariantCulture)}",
            _ => CheckCatalog.UnitOf(type, parameters) switch
            {
                "%" => number + "%",
                "" => number,
                var unit => type == CheckType.CertificateExpiry && v < 0 ? "Expired" : $"{number} {unit}"
            }
        };
    }

    private static string FlagText(CheckType type, IReadOnlyDictionary<string, string> parameters, bool fine, double value) => type switch
    {
        CheckType.ServiceRunning => fine ? "Running" : "Not running",
        CheckType.ProcessRunning => fine ? $"{value.ToString("0", CultureInfo.InvariantCulture)} running" : "Not running",
        CheckType.PendingReboot => fine ? "No restart pending" : "Restart pending",
        CheckType.SecurityCenter => fine ? "On" : "Off",
        CheckType.File => CheckCatalog.ParameterOrDefault(type, parameters, "condition") == "missing"
            ? fine ? "Absent" : "Present"
            : fine ? "Present" : "Missing",
        _ => fine ? "OK" : "Problem"
    };

    /// <summary>
    /// A parameter as "Label: value" for lists, with choices shown by their label and a script by its name. Null for a parameter that is
    /// not shown (the language Fleeto copies from a script).
    /// </summary>
    public static string? ParameterText(CheckType type, string name, string value, IReadOnlyDictionary<string, string>? scriptNames = null)
    {
        if (type == CheckType.Script && name == CheckCatalog.ScriptLanguageParameter)
        {
            return null;
        }

        if (!Enum.IsDefined(type) || CheckCatalog.Get(type).Parameters.FirstOrDefault(p => p.Name == name) is not { } spec)
        {
            return $"{name}: {value}";
        }

        var shown = spec.Kind switch
        {
            ParameterKind.Choice => spec.Choices?.FirstOrDefault(c => c.Value == value)?.Label ?? value,
            ParameterKind.Boolean => value == "true" ? "yes" : "no",
            ParameterKind.Script => scriptNames?.GetValueOrDefault(value) ?? "not available",
            _ => value
        };
        return $"{spec.Label}: {shown}";
    }

    public static string JobStateLabel(JobState state, JobResult? result) => state switch
    {
        JobState.PendingSignature => "Waiting for signature",
        JobState.Queued => "Queued",
        JobState.Running => "Running",
        JobState.Succeeded => "Succeeded",
        JobState.Failed => result == JobResult.TimedOut ? "Timed out" : result == JobResult.FailedToStart ? "Could not start" : "Failed",
        JobState.Expired => "Expired",
        JobState.Refused => "Refused",
        JobState.Lost => "Result unknown",
        _ => "Cancelled"
    };

    public static StatusKind JobStateKind(JobState state) => state switch
    {
        JobState.Succeeded => StatusKind.Ok,
        JobState.Running or JobState.Queued or JobState.PendingSignature => StatusKind.Active,
        JobState.Failed or JobState.Refused or JobState.Lost => StatusKind.Error,
        _ => StatusKind.Neutral
    };

    /// <summary>How a deployment is named in the UI (0.4.0 step 3). It says what Action1 is doing, not what Fleeto hopes.</summary>
    public static string DeploymentStateLabel(PatchDeploymentState state) => state switch
    {
        PatchDeploymentState.Requested => "Starting",
        PatchDeploymentState.Running => "Running",
        PatchDeploymentState.Completed => "Finished",
        PatchDeploymentState.Failed => "Not started",
        _ => "No longer followed"
    };

    public static StatusKind DeploymentStateKind(PatchDeploymentState state) => state switch
    {
        PatchDeploymentState.Completed => StatusKind.Ok,
        PatchDeploymentState.Requested or PatchDeploymentState.Running => StatusKind.Active,
        PatchDeploymentState.Failed => StatusKind.Error,
        _ => StatusKind.Warning
    };

    public static string DeploymentTargetLabel(PatchDeploymentTargetState state) => state switch
    {
        PatchDeploymentTargetState.Pending => "Waiting",
        PatchDeploymentTargetState.Running => "Installing",
        PatchDeploymentTargetState.Succeeded => "Installed",
        PatchDeploymentTargetState.Failed => "Failed",
        _ => "Unknown"
    };

    public static StatusKind DeploymentTargetKind(PatchDeploymentTargetState state) => state switch
    {
        PatchDeploymentTargetState.Succeeded => StatusKind.Ok,
        PatchDeploymentTargetState.Running => StatusKind.Active,
        PatchDeploymentTargetState.Failed => StatusKind.Error,
        PatchDeploymentTargetState.Unknown => StatusKind.Warning,
        _ => StatusKind.Neutral
    };

    /// <summary>
    /// True when the agent connected from a private or special-use address (same LAN with local DNS, VPN): its public IP is then not
    /// known, so the Summary does not call the address public. Uses the same ranges as the webhook address policy.
    /// </summary>
    public static bool IsPrivateConnection(string? address) =>
        IPAddress.TryParse(address, out var ip) && !NetworkAddressPolicy.IsPublic(ip);

    /// <summary>The account a job runs under on the endpoint, named the same way everywhere in the UI.</summary>
    public static string JobRunAsLabel(JobRunAs runAs) => runAs switch
    {
        JobRunAs.LoggedOnUser => "The signed-in user",
        _ => "System (SYSTEM or root)"
    };

    /// <summary>
    /// The same choice inside a sentence: "as the signed-in user", with the account once the agent reported it, or else with the user the
    /// technician chose (0.2.2).
    /// </summary>
    public static string JobRunAsPhrase(JobRunAs runAs, string? account = null, string? chosenAccount = null) => runAs switch
    {
        JobRunAs.LoggedOnUser when !string.IsNullOrEmpty(account) => $"as the signed-in user {account}",
        JobRunAs.LoggedOnUser when !string.IsNullOrEmpty(chosenAccount) => $"as the signed-in user {chosenAccount}",
        JobRunAs.LoggedOnUser => "as the signed-in user",
        _ => "as SYSTEM or root"
    };

    /// <summary>A signed-in user in the run window: the account and its sessions, e.g. "CONTOSO\jan · console and 1 remote session".</summary>
    public static string SignedInUserLabel(SignedInUserInfo user)
    {
        var console = user.Sessions.Any(s => s.Console);
        var remote = user.Sessions.Count(s => !s.Console);
        var sessions = (console, remote) switch
        {
            (true, 0) => "console",
            (true, _) => $"console and {Count(remote, "remote session")}",
            (false, 0) => "signed in",
            _ => Count(remote, "remote session")
        };
        return $"{user.Account} · {sessions}";
    }

    public static string JobOutputLabel(JobOutputState state, bool truncated) => state switch
    {
        JobOutputState.None => "No output",
        JobOutputState.Receiving => "Receiving output",
        JobOutputState.Incomplete => "Output incomplete",
        _ => truncated ? "Output truncated" : "Output complete"
    };

    /// <summary>"1 endpoint" or "12 endpoints": a count with its noun, pluralised the simple way.</summary>
    public static string Count(int value, string noun) => value == 1 ? $"1 {noun}" : $"{value} {noun}s";

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

/// <summary>Which app registration the set-up guide of Settings explains (0.5.0).</summary>
public enum MicrosoftGuide
{
    SignIn,
    Email
}
