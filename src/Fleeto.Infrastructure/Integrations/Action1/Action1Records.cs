using System.Text.Json;
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
            Action1Client.Text(item, "version"),
            Action1Client.Text(item, "kb_number"),
            ParseSeverity(Action1Client.Text(item, "security_severity")),
            Action1Client.Text(item, "reboot_needed") is { Length: > 0 } reboot &&
            !reboot.Equals("No", StringComparison.OrdinalIgnoreCase) && !reboot.Equals("Unknown", StringComparison.OrdinalIgnoreCase));
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
