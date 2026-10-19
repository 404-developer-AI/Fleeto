using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Integrations.Action1;

/// <summary>A scheduled automation as Action1 lists it (0.6.0).</summary>
/// <param name="Settings">The schedule in Action1's own notation, such as <c>ENABLED WEEKLY:Wed AT:20-00-00</c>.</param>
/// <param name="NextRunAt">When Action1 plans the next run, as it writes it (no time zone); null when it plans none.</param>
/// <param name="LastRunAt">When it last ran; null when it never did.</param>
/// <param name="LastInstanceId">The id of the run Action1 started last (an automation instance); empty when it never ran.</param>
public sealed record Action1AutomationInfo(string Id, string Name, string Settings, DateTime? NextRunAt = null, DateTime? LastRunAt = null,
    string LastInstanceId = "")
{
    public static Action1AutomationInfo? From(JsonElement item) =>
        Action1Client.Text(item, "id") is { Length: > 0 } id
            ? new Action1AutomationInfo(id, Action1Client.Text(item, "name"), Action1Client.Text(item, "settings"),
                Action1Api.ParseTime(Action1Client.Text(item, "next_run")), Action1Api.ParseTime(Action1Client.Text(item, "last_run")),
                LastInstance(item))
            : null;

    /// <summary>
    /// The id of the last run. Action1 documents <c>last_instance</c> as a link; an object with an <c>id</c> or <c>self</c> is
    /// read as well, since the scheduled rounds of the test VPS never matched the documented form (found 2026-10-19).
    /// </summary>
    private static string LastInstance(JsonElement item)
    {
        if (!item.TryGetProperty("last_instance", out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => LastSegment(value.GetString()),
            JsonValueKind.Object => Action1Client.Text(value, "id") is { Length: > 0 } id
                ? LastSegment(id)
                : LastSegment(Action1Client.Text(value, "self")),
            _ => string.Empty
        };
    }

    /// <summary>Action1 links the last run as <c>/API/automations/instances/{instance-id}</c>; the id is the last segment.</summary>
    public static string LastSegment(string? link)
    {
        var trimmed = (link ?? string.Empty).Trim().TrimEnd('/');
        var id = trimmed.Length == 0 ? string.Empty : trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return id.Length is > 0 and <= 200 && !id.Contains('{') ? id : string.Empty;
    }
}

/// <summary>
/// One run of an automation as Action1 lists it under <c>automations/instances</c> (0.7.0): a scheduled round or a
/// deployment started now. Used to find the round of a patch policy when its automation names no <c>last_instance</c>.
/// </summary>
/// <param name="AutomationId">The automation it is a run of, the last segment of its <c>schedule</c> link; empty when not named.</param>
/// <param name="StartedAt">When it started, as Action1 writes it (no time zone); null when not named.</param>
public sealed record Action1AutomationRun(string Id, string AutomationId, DateTime? StartedAt)
{
    public static Action1AutomationRun? From(JsonElement item) =>
        Action1AutomationInfo.LastSegment(Action1Client.Text(item, "id")) is { Length: > 0 } id
            ? new Action1AutomationRun(id, Action1AutomationInfo.LastSegment(Action1Client.Text(item, "schedule")),
                Action1Api.ParseTime(Action1Client.Text(item, "start_time")))
            : null;

    /// <summary>
    /// True when this is a run of the automation: named by its <c>schedule</c> link, or, when that is missing, by its id, which
    /// Action1 builds from the id of the automation and the start time (<c>{automation-id}_2026-10-19_13-04-06</c>).
    /// </summary>
    public bool IsRunOf(string automationId) =>
        AutomationId.Length > 0
            ? string.Equals(AutomationId, automationId, StringComparison.Ordinal)
            : Id.StartsWith(automationId + "_", StringComparison.Ordinal);
}

/// <summary>
/// The scheduled automation Fleeto keeps for one patch policy in one organization (0.6.0): a "Deploy Update" action on the
/// endpoints whose effective patch policy it is. The body follows Action1's OpenAPI document 3.1, schemas
/// <c>AutomationSchedulePayload</c> and <c>DeployUpdate</c>; every option maps one-to-one on <see cref="PatchPolicy"/>.
/// </summary>
/// <param name="EndpointIds">The Action1 ids of the endpoints, never host names. Action1 needs at least one.</param>
public sealed record Action1Automation(PatchPolicy Policy, IReadOnlyList<string> EndpointIds)
{
    /// <summary>Automation names start with this, so an admin in the Action1 console sees where one came from.</summary>
    public const string NamePrefix = "Fleeto: ";

    public string Name => Cut(NamePrefix + Policy.Name, 200);

    /// <summary>
    /// The schedule in Action1's notation: <c>ENABLED|DISABLED</c>, the frequency and the start time. Action1 has no daily
    /// schedule; every day is a weekly one with all seven days.
    /// </summary>
    public string Settings
    {
        get
        {
            var frequency = Policy.ScheduleKind switch
            {
                PatchScheduleKind.MonthlyDay => $"MONTHLY:{Policy.MonthDay}",
                PatchScheduleKind.MonthlyWeekday => $"MONTHLYWEEK:{Policy.MonthWeek}:{Day(Policy.MonthWeekday)}",
                _ => "WEEKLY:" + string.Join(",", Order.Where(d => (Policy.WeekDays & PatchPolicyRules.Flag(d)) != 0).Select(Day))
            };
            var at = $"AT:{Policy.StartMinute / 60:00}-{Policy.StartMinute % 60:00}-00";
            return $"{(Policy.Enabled ? "ENABLED" : "DISABLED")} {frequency} {at}";
        }
    }

    public JsonObject ToJson()
    {
        var parameters = new JsonObject
        {
            ["display_summary"] = Policy.Scope == PatchUpdateScope.All ? "All updates" : "Updates matching filters",
            ["scope"] = Policy.Scope == PatchUpdateScope.All ? "All" : "MatchingFilters",
            ["require_update_approval"] = Policy.RequireApproval ? "yes" : "no"
        };
        if (!Policy.RequireApproval)
        {
            parameters["automatic_install_delay_days"] = Policy.InstallDelayDays;
        }

        if (Policy.Scope == PatchUpdateScope.Filtered)
        {
            var filters = new JsonArray();
            AddFilter(filters, "update_sources", Policy.UpdateSources, "include");
            AddFilter(filters, "update_types", Policy.UpdateTypes, "include");
            AddFilter(filters, "update_security_severities", Policy.Severities, "include");
            AddFilter(filters, "update_names", Policy.ExcludedNames, "exclude");
            AddFilter(filters, "update_vendors", Policy.ExcludedVendors, "exclude");
            parameters["filters"] = filters;
        }

        var reboot = new JsonObject { ["auto_reboot"] = Policy.AutoReboot ? "yes" : "no" };
        if (Policy.AutoReboot)
        {
            reboot["show_message"] = Policy.RebootMessage ? "yes" : "no";
            if (Policy.RebootMessage)
            {
                reboot["message_text"] = string.IsNullOrWhiteSpace(Policy.RebootMessageText) ? PatchPolicyRules.DefaultRebootMessage : Policy.RebootMessageText;
            }

            // Minutes, per Action1's RebootOptions schema.
            reboot["timeout"] = Policy.RebootTimeoutMinutes;
        }

        parameters["reboot_options"] = reboot;

        return new JsonObject
        {
            ["name"] = Name,
            ["settings"] = Settings,
            ["settings_timezone"] = Policy.EndpointLocalTime ? "LOCALTIME" : "UTC",
            ["retry_minutes"] = (Policy.RetryHours * 60).ToString(CultureInfo.InvariantCulture),
            ["endpoints"] = new JsonArray([.. EndpointIds.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["type"] = "Endpoint" })]),
            ["actions"] = new JsonArray(new JsonObject
            {
                ["name"] = "Deploy Update",
                ["template_id"] = "deploy_update",
                ["params"] = parameters
            })
        };
    }

    private static readonly DayOfWeek[] Order =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>The weekday as Action1 writes it: Sun, Mon, Tue, Wed, Thu, Fri, Sat.</summary>
    private static string Day(DayOfWeek day) => day.ToString()[..3];

    private static void AddFilter(JsonArray filters, string name, IReadOnlyCollection<string> values, string operation)
    {
        if (values.Count > 0)
        {
            filters.Add(new JsonObject
            {
                ["name"] = name,
                ["values"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v))]),
                ["operator"] = operation
            });
        }
    }

    private static string Cut(string value, int length) => value.Length <= length ? value : value[..length];
}
