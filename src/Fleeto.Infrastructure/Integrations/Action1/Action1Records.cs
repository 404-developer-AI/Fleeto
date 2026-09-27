using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Integrations.Action1;

/// <summary>
/// One endpoint as Action1 reports it (0.4.0 step 2). Only what Fleeto uses is read; a field Action1 leaves out or fills
/// with something unexpected gives the safe value, never an error, because one odd record must not stop a whole sync.
/// </summary>
public sealed record Action1Endpoint(string Id, string OrganizationId, string Name, string DeviceName, string Serial, string Platform,
    string AgentVersion, DateTime? LastSeenAt, PatchCoverage Coverage, bool RebootRequired, int MissingCritical, int MissingOther)
{
    public static Action1Endpoint? From(JsonElement item, string organizationId)
    {
        var id = Action1Client.Text(item, "id");
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var organization = Action1Client.Text(item, "organization_id") is { Length: > 0 } fromItem ? fromItem : organizationId;
        var (critical, other) = MissingCounts(item);
        return new Action1Endpoint(
            id,
            organization,
            Action1Client.Text(item, "name"),
            Action1Client.Text(item, "device_name"),
            Action1Client.Text(item, "serial"),
            Action1Client.Text(item, "platform"),
            Action1Client.Text(item, "agent_version"),
            Action1Api.ParseTime(Action1Client.Text(item, "last_seen")),
            Action1Client.Text(item, "subscription_status").Equals("Inactive", StringComparison.OrdinalIgnoreCase)
                ? PatchCoverage.Inactive
                : PatchCoverage.Active,
            Flag(item, "reboot_required"),
            critical,
            other);
    }

    private static (int Critical, int Other) MissingCounts(JsonElement item)
    {
        if (!item.TryGetProperty("missing_updates", out var missing) || missing.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }

        return (Count(missing, "critical"), Count(missing, "other"));
    }

    private static int Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0
            ? number
            : 0;

    /// <summary>Action1 writes a flag as a boolean or as "Yes"/"No" depending on the field; both are read.</summary>
    private static bool Flag(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetString() is { } text &&
                                    (text.Equals("Yes", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }
}

/// <summary>
/// One update Action1 reports as missing on an endpoint (0.4.0 step 2). Action1 describes the update at the top and the
/// version it would install in <c>versions[0]</c>, which carries the severity, type, release date, approval and CVEs
/// (its schema <c>MissingUpdateSoftwareRepositoryPackage</c>; found in 0.6.0, when every severity read as unspecified
/// because Fleeto looked for it at the top). Each of those is read from the version first and from the top second.
/// </summary>
/// <param name="InstalledVersion">The version on the endpoint now (Action1: <c>version_outdated</c>), empty when not installed.</param>
/// <param name="UpdateType">Action1's word for the kind of update: Security Updates, Feature Updates, Drivers and so on.</param>
/// <param name="ApprovalStatus">New, Approved or Declined in Action1. Only shown: a deployment from Fleeto installs regardless.</param>
/// <param name="Cves">The CVEs this version remediates.</param>
public sealed record Action1MissingUpdate(string Id, string Name, string Vendor, string Version, string KbNumber, PatchSeverity Severity,
    string InstalledVersion, DateOnly? ReleaseDate, string UpdateType, string ApprovalStatus, IReadOnlyList<string> Cves)
{
    public static Action1MissingUpdate? From(JsonElement item)
    {
        var id = Action1Client.Text(item, "id");
        var name = Action1Client.Text(item, "name");
        if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name))
        {
            return null;
        }

        var version = FirstVersion(item);
        string Field(string field) => Action1Client.Text(version, field) is { Length: > 0 } nested ? nested : Action1Client.Text(item, field);

        return new Action1MissingUpdate(
            id,
            name,
            Action1Client.Text(item, "vendor"),
            Action1Client.Text(item, "version") is { Length: > 0 } flat ? flat : Action1Client.Text(version, "version"),
            Action1Client.Text(item, "kb_number"),
            ParseSeverity(Field("security_severity")),
            Field("version_outdated"),
            DateOnly.TryParseExact(Field("release_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var released)
                ? released
                : null,
            Field("update_type"),
            Field("approval_status"),
            ParseCves(Field("security_CVE")));
    }

    /// <summary>
    /// The version Action1 would install: it reports the update with a list of versions, newest first. A deployment has to
    /// name the version of every package (0.4.0 step 3), so an update without one can be shown but not deployed by name.
    /// </summary>
    private static JsonElement FirstVersion(JsonElement item)
    {
        if (item.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
        {
            foreach (var version in versions.EnumerateArray())
            {
                if (version.ValueKind == JsonValueKind.Object)
                {
                    return version;
                }
            }
        }

        return default;
    }

    /// <summary>Action1 lists the CVEs as one string, "CVE-2024-0222, CVE-2024-0223"; anything that is not a CVE id is dropped.</summary>
    public static IReadOnlyList<string> ParseCves(string? value) =>
        [.. (value ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase) && c.Length <= 30)
            .Select(c => c.ToUpperInvariant())
            .Distinct()];

    /// <summary>
    /// Action1 uses the words of the vendor: Critical, Important, Moderate, Low, Unspecified, and "Other" for what it
    /// cannot place. Anything Fleeto does not know becomes <see cref="PatchSeverity.Unspecified"/>, never something worse
    /// or better than it is.
    /// </summary>
    public static PatchSeverity ParseSeverity(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "critical" => PatchSeverity.Critical,
        "important" => PatchSeverity.Important,
        "moderate" => PatchSeverity.Moderate,
        "low" => PatchSeverity.Low,
        _ => PatchSeverity.Unspecified
    };
}

/// <summary>
/// A deployment of updates Fleeto asks Action1 to run now (0.4.0 step 3). Action1 calls it a policy instance: an
/// automation that runs once, on the endpoints it names.
/// </summary>
/// <param name="Name">The name of the deployment in the Action1 console; it names Fleeto so an admin knows where it came from.</param>
/// <param name="Summary">One line about what is installed, shown by Action1 next to the deployment.</param>
/// <param name="EndpointIds">The Action1 ids of the endpoints, never host names.</param>
/// <param name="Packages">The packages to install with their version, empty when every missing update goes.</param>
/// <param name="AutoReboot">True lets Action1 restart the endpoint by itself to finish the updates.</param>
public sealed record Action1Deployment(string Name, string Summary, IReadOnlyList<string> EndpointIds,
    IReadOnlyList<Action1Package> Packages, bool AutoReboot, string RebootMessage, int RebootTimeoutMinutes, int RetryMinutes)
{
    /// <summary>
    /// The body Action1 expects (its published examples "Deploy Updates (specified updates)" and "(all updates)"). Chosen
    /// updates are an object per package, keyed by its id with the version as value. Every missing update is scope
    /// <c>All</c> with <c>require_update_approval</c> "no": Action1 defaults that to "yes", which installs only updates an
    /// admin approved in the Action1 console and answered "No updates are applicable" on an endpoint missing seven (found
    /// testing 0.6.0). Chosen updates never pass Action1's approval either, so both scopes install what the technician saw.
    /// </summary>
    public JsonObject ToJson()
    {
        var reboot = new JsonObject { ["auto_reboot"] = AutoReboot ? "yes" : "no" };
        if (AutoReboot)
        {
            reboot["show_message"] = "yes";
            reboot["message_text"] = RebootMessage;
            reboot["timeout"] = RebootTimeoutMinutes;
        }

        return new JsonObject
        {
            ["name"] = Name,
            ["retry_minutes"] = RetryMinutes.ToString(CultureInfo.InvariantCulture),
            ["endpoints"] = new JsonArray([.. EndpointIds.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["type"] = "Endpoint" })]),
            ["actions"] = new JsonArray(new JsonObject
            {
                ["name"] = "Deploy Update",
                ["template_id"] = "deploy_update",
                ["params"] = Packages.Count == 0
                    ? new JsonObject
                    {
                        ["display_summary"] = Summary,
                        ["scope"] = "All",
                        ["require_update_approval"] = "no",
                        ["reboot_options"] = reboot
                    }
                    : new JsonObject
                    {
                        ["display_summary"] = Summary,
                        ["scope"] = "Specified",
                        ["packages"] = new JsonArray([.. Packages.Select(p => (JsonNode)new JsonObject { [p.Id] = p.Version })]),
                        ["reboot_options"] = reboot
                    }
            })
        };
    }
}

/// <summary>One package of a deployment: the id Action1 knows the update by, and the version to install.</summary>
public sealed record Action1Package(string Id, string Version);

/// <summary>
/// What Action1 reports for one endpoint of a deployment (0.4.0 step 3). Action1 words its status in its own vocabulary
/// and documents no list of values, so a word Fleeto does not know becomes <see cref="PatchDeploymentTargetState.Unknown"/>
/// with the word itself as the message: showing what Action1 said beats guessing what it meant.
/// </summary>
public sealed record Action1EndpointResult(string EndpointId, PatchDeploymentTargetState State, string Status)
{
    public static Action1EndpointResult? From(JsonElement item)
    {
        var id = Action1Client.Text(item, "endpoint_id") is { Length: > 0 } endpoint ? endpoint : Action1Client.Text(item, "id");
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var status = Action1Client.Text(item, "status") is { Length: > 0 } text ? text : Action1Client.Text(item, "state");
        return new Action1EndpointResult(id, ParseState(status), status);
    }

    public static PatchDeploymentTargetState ParseState(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "succeeded" or "success" or "completed" or "complete" or "ok" or "installed" => PatchDeploymentTargetState.Succeeded,
        "failed" or "failure" or "error" or "cancelled" or "canceled" => PatchDeploymentTargetState.Failed,
        "running" or "in progress" or "in_progress" or "inprogress" or "started" or "executing" => PatchDeploymentTargetState.Running,
        "pending" or "scheduled" or "queued" or "waiting" or "not started" or "new" => PatchDeploymentTargetState.Pending,
        _ => PatchDeploymentTargetState.Unknown
    };
}

/// <summary>
/// One line of Action1's history for one endpoint of a deployment (0.6.0), from
/// <c>GET /automations/instances/{org}/{instance}/endpoint-results/{endpoint}/details</c>: what the console shows as its
/// "Automation History". Texts are kept as Action1 wrote them, cut to what Fleeto stores.
/// </summary>
public sealed record Action1DeploymentStep(DateTime? Time, string Operation, string Status, string Details)
{
    public static Action1DeploymentStep From(JsonElement item) => new(
        Action1Api.ParseTime(Action1Client.Text(item, "time")),
        Cut(Action1Client.Text(item, "action_name"), 200),
        Cut(Action1Client.Text(item, "status"), 20),
        Cut(Action1Client.Text(item, "description"), 2000));

    private static string Cut(string value, int max)
    {
        var clean = value.Trim();
        return clean.Length <= max ? clean : clean[..max];
    }
}

/// <summary>
/// One member of an Action1 endpoint group (0.6.0). <paramref name="Manual"/> is false for an endpoint the filters of
/// the group brought in; Fleeto only removes members that were added by hand, which is how it adds them.
/// </summary>
public sealed record Action1GroupMember(string EndpointId, bool Manual);
