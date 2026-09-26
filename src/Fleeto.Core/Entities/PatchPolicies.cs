namespace Fleeto.Core.Entities;

/// <summary>When a patch policy runs (0.6.0). Exactly the schedules an Action1 automation offers, nothing else.</summary>
public enum PatchScheduleKind
{
    /// <summary>At the start time on the chosen days of the week (<c>WEEKLY:Mon,Thu</c>). Every day is all seven.</summary>
    Weekly,

    /// <summary>At the start time on one day of every month (<c>MONTHLY:15</c>).</summary>
    MonthlyDay,

    /// <summary>At the start time on the n-th weekday of every month, such as the second Tuesday (<c>MONTHLYWEEK:2:Tue</c>).</summary>
    MonthlyWeekday
}

/// <summary>Which updates a patch policy installs (0.6.0).</summary>
public enum PatchUpdateScope
{
    /// <summary>Every update the product finds missing (<c>scope: All</c>).</summary>
    All,

    /// <summary>Only the updates that match the filters of the policy (<c>scope: MatchingFilters</c>).</summary>
    Filtered
}

/// <summary>
/// A patch policy (0.6.0): when and how updates are installed on the endpoints it applies to. Fleeto hands it to the patch
/// management product as a scheduled automation per client (Action1: one automation per organization that uses it), so
/// every option here is one the product itself offers. Linked, not copied: a change reaches every automation made from it.
/// ClientId null = global.
/// </summary>
public class PatchPolicy
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// The endpoints the patch policy is for: a patch policy for servers never applies to a workstation, and the other way
    /// round, wherever it is linked.
    /// </summary>
    public CheckAppliesTo AppliesTo { get; set; } = CheckAppliesTo.All;

    /// <summary>Off keeps the automations in the product but stops them from running (<c>DISABLED</c>).</summary>
    public bool Enabled { get; set; } = true;

    public PatchScheduleKind ScheduleKind { get; set; } = PatchScheduleKind.Weekly;

    /// <summary>The days of <see cref="PatchScheduleKind.Weekly"/>.</summary>
    public Domain.WeekDays WeekDays { get; set; } = Domain.WeekDays.Wednesday;

    /// <summary>The day of the month of <see cref="PatchScheduleKind.MonthlyDay"/>, 1 to 31.</summary>
    public int MonthDay { get; set; } = 1;

    /// <summary>Which week of the month for <see cref="PatchScheduleKind.MonthlyWeekday"/>, 1 to 4.</summary>
    public int MonthWeek { get; set; } = 2;

    /// <summary>The weekday of <see cref="PatchScheduleKind.MonthlyWeekday"/>.</summary>
    public DayOfWeek MonthWeekday { get; set; } = DayOfWeek.Wednesday;

    /// <summary>The start time in minutes after midnight.</summary>
    public int StartMinute { get; set; } = 20 * 60;

    /// <summary>True runs at the start time in each endpoint's own time zone (<c>LOCALTIME</c>), false in UTC.</summary>
    public bool EndpointLocalTime { get; set; } = true;

    public PatchUpdateScope Scope { get; set; } = PatchUpdateScope.All;

    /// <summary>Filter: update sources to include (<see cref="Domain.PatchPolicyRules.UpdateSources"/>); empty means every source.</summary>
    public List<string> UpdateSources { get; set; } = [];

    /// <summary>Filter: update types to include (<see cref="Domain.PatchPolicyRules.UpdateTypes"/>); empty means every type.</summary>
    public List<string> UpdateTypes { get; set; } = [];

    /// <summary>Filter: security severities to include (<see cref="Domain.PatchPolicyRules.Severities"/>); empty means every severity.</summary>
    public List<string> Severities { get; set; } = [];

    /// <summary>Filter: update names to leave out, with * as a wildcard (<c>Mozilla*</c>).</summary>
    public List<string> ExcludedNames { get; set; } = [];

    /// <summary>Filter: vendors to leave out, with * as a wildcard.</summary>
    public List<string> ExcludedVendors { get; set; } = [];

    /// <summary>
    /// True installs only updates approved in the product's own console (<c>require_update_approval: yes</c>); false every
    /// update that is not declined there.
    /// </summary>
    public bool RequireApproval { get; set; }

    /// <summary>Days after its release before an update is installed; only when <see cref="RequireApproval"/> is off.</summary>
    public int InstallDelayDays { get; set; }

    /// <summary>The product restarts the endpoint by itself when an update needs it.</summary>
    public bool AutoReboot { get; set; }

    /// <summary>A signed-in user sees a message before the restart.</summary>
    public bool RebootMessage { get; set; } = true;

    public string? RebootMessageText { get; set; }

    /// <summary>Minutes between the message and the restart.</summary>
    public int RebootTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Hours the product keeps trying endpoints that were off or offline at the start time (<c>retry_minutes</c>). Action1
    /// advises no longer than the time between two runs.
    /// </summary>
    public int RetryHours { get; set; } = 24;

    /// <summary>Patch policy this one was copied from; the copy is independent.</summary>
    public Guid? CopiedFromId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
