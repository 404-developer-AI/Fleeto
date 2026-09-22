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

/// <summary>One update Action1 reports as missing on an endpoint (0.4.0 step 2).</summary>
public sealed record Action1MissingUpdate(string Id, string Name, string Vendor, string Version, string KbNumber, PatchSeverity Severity,
    bool RebootNeeded)
{
    public static Action1MissingUpdate? From(JsonElement item)
    {
        var id = Action1Client.Text(item, "id");
        var name = Action1Client.Text(item, "name");
        if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new Action1MissingUpdate(
            id,
            name,
            Action1Client.Text(item, "vendor"),
            VersionOf(item),
            Action1Client.Text(item, "kb_number"),
            ParseSeverity(Action1Client.Text(item, "security_severity")),
            Action1Client.Text(item, "reboot_needed") is { Length: > 0 } reboot &&
            !reboot.Equals("No", StringComparison.OrdinalIgnoreCase) && !reboot.Equals("Unknown", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The version Action1 would install. It reports the update itself with a list of versions, newest first, and some
    /// answers carry the version flat; both are read, because a deployment has to name the version of every package
    /// (0.4.0 step 3). An update without a version can still be shown, but it cannot be deployed by name.
    /// </summary>
    private static string VersionOf(JsonElement item)
    {
        if (Action1Client.Text(item, "version") is { Length: > 0 } flat)
        {
            return flat;
        }

        if (item.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
        {
            foreach (var version in versions.EnumerateArray())
            {
                if (Action1Client.Text(version, "version") is { Length: > 0 } nested)
                {
                    return nested;
                }
            }
        }

        return string.Empty;
    }

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
    IReadOnlyList<Action1Package> Packages, bool AutoReboot, string RebootMessage, int RebootTimeoutSeconds, int RetryMinutes)
{
    /// <summary>
    /// The body Action1 expects. The packages are an object per package, keyed by its id with the version as value, which
    /// is the shape Action1's own tooling builds; <c>default</c> means every update the endpoint misses.
    /// </summary>
    public JsonObject ToJson()
    {
        var packages = new JsonArray();
        if (Packages.Count == 0)
        {
            packages.Add(new JsonObject { ["default"] = "default" });
        }
        else
        {
            foreach (var package in Packages)
            {
                packages.Add(new JsonObject { [package.Id] = package.Version });
            }
        }

        var reboot = new JsonObject { ["auto_reboot"] = AutoReboot ? "yes" : "no" };
        if (AutoReboot)
        {
            reboot["show_message"] = "yes";
            reboot["message_text"] = RebootMessage;
            reboot["timeout"] = RebootTimeoutSeconds;
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
                ["params"] = new JsonObject
                {
                    ["display_summary"] = Summary,
                    ["scope"] = Packages.Count == 0 ? "All" : "Specified",
                    ["packages"] = packages,
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
