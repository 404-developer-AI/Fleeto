namespace Fleeto.Core.Entities;

/// <summary>How often the schedule of a task starts it (0.6.0).</summary>
public enum TaskScheduleKind
{
    /// <summary>No schedule: the task starts only by its other triggers or by hand.</summary>
    None,
    Once,
    Daily,
    Weekly,
    Monthly
}

/// <summary>What a task does when its previous job on an endpoint has not ended yet (0.6.0).</summary>
public enum TaskOverlap
{
    /// <summary>The endpoint is left out of this run.</summary>
    Skip,

    /// <summary>The job of this run starts once the previous one has ended, within the validity of the run.</summary>
    Wait
}

/// <summary>Why a run of a task started (0.6.0).</summary>
public enum TaskTrigger
{
    Schedule,

    /// <summary>An alert of one of the task's chosen checks opened on the endpoint.</summary>
    CheckAlert,

    /// <summary>The endpoint was switched to managed.</summary>
    Managed,

    /// <summary>A user pressed Run now; the jobs carry that user as their initiator.</summary>
    Manual
}

/// <summary>What a step of a task does (0.6.0).</summary>
public enum TaskStepKind
{
    /// <summary>Runs a library script.</summary>
    Script,

    /// <summary>Restarts the endpoint and waits until its agent is back with a newer boot time.</summary>
    Restart
}

/// <summary>How the steps of a run ended on one endpoint (0.6.0).</summary>
public enum TaskTargetOutcome
{
    /// <summary>Every step passed.</summary>
    Succeeded,

    /// <summary>A step failed: the run stopped there, or went on past a step that may fail and ended with that failure.</summary>
    Failed,

    /// <summary>A job of the run was cancelled, so the run stopped there.</summary>
    Cancelled
}

/// <summary>Where one endpoint of a run stands (0.6.0).</summary>
public enum TaskTargetState
{
    /// <summary>Its job is not written yet: the random delay has not passed, or it waits for the previous job.</summary>
    Pending,

    /// <summary>The job is written; its own state tells the rest.</summary>
    Started,

    /// <summary>No job was written, with the reason.</summary>
    Skipped
}

/// <summary>
/// A library script that runs by itself (0.6.0, decided 2026-10-14): linked to clients, sites and endpoints like a monitoring
/// template, adding up over the levels, and started on a schedule, when an alert of a chosen check opens, when an endpoint
/// becomes managed, or by hand. ClientId null = global. Written by web only; the workers read it to start runs, and
/// fleeto-signer reads it again before it signs a job of a run Fleeto started.
/// </summary>
public class AutomationTask
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// The script of the first script step, kept equal to it by web so a release before steps (0.6.0-alpha.22) still runs on
    /// this schema; the steps (<see cref="AutomationTaskStep"/>) decide what runs.
    /// </summary>
    public Guid ScriptId { get; set; }

    /// <summary>The account of the first script step, kept as <see cref="ScriptId"/>.</summary>
    public JobRunAs RunAs { get; set; } = JobRunAs.Service;

    /// <summary>The class of the endpoints it runs on; it never runs on the other class, wherever it is linked.</summary>
    public CheckAppliesTo AppliesTo { get; set; } = CheckAppliesTo.All;

    public bool Enabled { get; set; } = true;

    public TaskScheduleKind ScheduleKind { get; set; } = TaskScheduleKind.None;

    /// <summary>IANA time zone of the schedule, for example Europe/Brussels.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>The first day the schedule may start it, in <see cref="TimeZone"/>.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Minutes after midnight of the start time, 0 to 1439.</summary>
    public int StartMinute { get; set; }

    /// <summary>The last day the schedule may start it, inclusive; null runs on.</summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>Every how many days, weeks or months, 1 to 365.</summary>
    public int Every { get; set; } = 1;

    /// <summary>The days of a weekly schedule, <see cref="Domain.WeekDays"/>.</summary>
    public Domain.WeekDays WeekDays { get; set; } = Domain.WeekDays.None;

    /// <summary>The day of a monthly schedule, 1 to 31; the last day of a shorter month.</summary>
    public int MonthDay { get; set; } = 1;

    /// <summary>Starts again every this many minutes after the start time until the end of that day; null starts once a day.</summary>
    public int? RepeatMinutes { get; set; }

    /// <summary>The checks whose alert, when it opens on an endpoint, starts the task there. Template checks only.</summary>
    public List<Guid> TriggerCheckIds { get; set; } = [];

    /// <summary>Starts on an endpoint when it is switched to managed.</summary>
    public bool OnManaged { get; set; }

    /// <summary>Each endpoint starts at a random moment within this many minutes after the run, 0 to 720.</summary>
    public int RandomDelayMinutes { get; set; }

    /// <summary>A job waits for an offline endpoint until the next run (at most 7 days) instead of expiring after an hour.</summary>
    public bool CatchUp { get; set; } = true;

    public TaskOverlap Overlap { get; set; } = TaskOverlap.Skip;

    /// <summary>A failed run opens an alert of kind Task on the endpoint.</summary>
    public bool AlertOnFailure { get; set; } = true;

    public AlertSeverity AlertSeverity { get; set; } = AlertSeverity.Warning;

    public Guid? CopiedFromId { get; set; }
    public string UpdatedByName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One step of a task (0.6.0, decided 2026-10-15): a library script or a restart of the endpoint. On an endpoint a step starts
/// only when the step before it ended, and a step that fails stops the run there unless it says the run may go on, or its exit
/// code counts as success. Written by web only; ClientId is the task's (trigger).
/// </summary>
public class AutomationTaskStep
{
    public Guid Id { get; set; }
    public Guid AutomationTaskId { get; set; }
    public Guid? ClientId { get; set; }

    /// <summary>1 for the first step, without gaps.</summary>
    public int Position { get; set; }

    public TaskStepKind Kind { get; set; }

    /// <summary>The library script of a script step; null for a restart.</summary>
    public Guid? ScriptId { get; set; }

    public JobRunAs RunAs { get; set; } = JobRunAs.Service;

    /// <summary>The next step starts also when this one fails (not when its job was cancelled).</summary>
    public bool ContinueOnFailure { get; set; }

    /// <summary>
    /// Exit codes besides 0 that counted as success of a script step (alpha.23). No longer written: the script says what its exit codes
    /// mean (0.6.0, <see cref="Script.SuccessExitCodes"/>); read only for a job that ended without an exit status. Dropped in a later
    /// release (expand/contract).
    /// </summary>
    public List<int> SuccessExitCodes { get; set; } = [];

    /// <summary>For a restart: how long Fleeto waits for the endpoint to come back before the step fails.</summary>
    public int? RestartWaitMinutes { get; set; }
}

/// <summary>A task linked to a client: it runs on every endpoint of the client (0.6.0).</summary>
public class ClientAutomationTask
{
    public Guid ClientId { get; set; }
    public Guid AutomationTaskId { get; set; }
    public DateTime CreatedAt { get; set; }

    public AutomationTask? AutomationTask { get; set; }
}

/// <summary>A task linked to a site (0.6.0).</summary>
public class SiteAutomationTask
{
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }
    public Guid AutomationTaskId { get; set; }
    public DateTime CreatedAt { get; set; }

    public AutomationTask? AutomationTask { get; set; }
}

/// <summary>A task linked to one endpoint (0.6.0).</summary>
public class EndpointAutomationTask
{
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }
    public Guid AutomationTaskId { get; set; }
    public DateTime CreatedAt { get; set; }

    public AutomationTask? AutomationTask { get; set; }
}

/// <summary>
/// A task linked to a monitoring template (0.6.0): it runs on every endpoint that gets the template, wherever the template is
/// linked. <see cref="ClientId"/> is the client of the template or the task, null when both are for every client; a task and a
/// template of two different clients are never linked (trigger <c>TR_MonitoringTemplateAutomationTasks_Client</c>).
/// </summary>
public class MonitoringTemplateAutomationTask
{
    public Guid MonitoringTemplateId { get; set; }
    public Guid AutomationTaskId { get; set; }
    public Guid? ClientId { get; set; }
    public DateTime CreatedAt { get; set; }

    public AutomationTask? AutomationTask { get; set; }
    public MonitoringTemplate? MonitoringTemplate { get; set; }
}

/// <summary>
/// Where the workers stand with one task (0.6.0): the occurrences of its schedule already handled, and how far they read the
/// alerts and the endpoints switched to managed. Written by the workers only; created when they first see the task, so
/// nothing that happened before a task existed starts it.
/// </summary>
public class AutomationTaskState
{
    public Guid AutomationTaskId { get; set; }

    /// <summary>The last occurrence of the schedule that was handled (started, or passed by without catching up).</summary>
    public DateTime? LastOccurrenceAt { get; set; }

    /// <summary>The next occurrence, for the task page.</summary>
    public DateTime? NextOccurrenceAt { get; set; }

    public DateTime AlertsSeenUntil { get; set; }
    public DateTime ManagedSeenUntil { get; set; }

    /// <summary>A hash of the schedule the occurrences above belong to; a changed schedule starts counting again.</summary>
    public string ScheduleHash { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One run of a task for one client (0.6.0): the moment it started, why, and per endpoint a <see cref="AutomationTaskRunTarget"/>.
/// A run of a global task linked to several clients has a row per client, so every row belongs to exactly one client.
/// </summary>
public class AutomationTaskRun
{
    public Guid Id { get; set; }
    public Guid AutomationTaskId { get; set; }
    public Guid ClientId { get; set; }
    public TaskTrigger Trigger { get; set; }

    /// <summary>The occurrence of the schedule, the moment the alert opened or the endpoint became managed, or the click.</summary>
    public DateTime OccurrenceAt { get; set; }

    /// <summary>Who or what started it: a user name, or "Fleeto (schedule)" and the like.</summary>
    public string InitiatedByName { get; set; } = string.Empty;

    /// <summary>The check whose alert started it, for a run of <see cref="TaskTrigger.CheckAlert"/>.</summary>
    public string? CheckName { get; set; }

    /// <summary>
    /// The user who pressed Run now, for a run of <see cref="TaskTrigger.Manual"/> (0.6.0, steps): fleeto-signer checks the
    /// permissions and access of this user again before it signs each step. Only web writes it (trigger).
    /// </summary>
    public Guid? InitiatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One endpoint of a run (0.6.0): when its first step is due, until when it may start, and the job of its current step once
/// written (<see cref="JobId"/> moves on with every step; the jobs of a run keep their step in <c>Job.TaskStepId</c>).
/// </summary>
public class AutomationTaskRunTarget
{
    public Guid RunId { get; set; }
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }
    public Guid AutomationTaskId { get; set; }

    /// <summary>The run plus this endpoint's share of the random delay.</summary>
    public DateTime DueAt { get; set; }

    /// <summary>The job is valid until then; a target still pending at that moment is skipped.</summary>
    public DateTime ValidUntil { get; set; }

    public TaskTargetState State { get; set; } = TaskTargetState.Pending;
    public Guid? JobId { get; set; }
    public string? Reason { get; set; }

    /// <summary>When the workers handled the end of the last job of the run on this endpoint.</summary>
    public DateTime? OutcomeHandledAt { get; set; }

    /// <summary>When the run ended on this endpoint (0.6.0, steps): the last step ended, or a step stopped the run.</summary>
    public DateTime? FinishedAt { get; set; }

    public TaskTargetOutcome? Outcome { get; set; }

    /// <summary>
    /// A step ended with an exit code its script lists as a warning (0.6.0): with <see cref="TaskTargetOutcome.Succeeded"/> the run
    /// succeeded with warnings. A column of its own, so a build that does not know it still reads the outcome.
    /// </summary>
    public bool HasWarning { get; set; }

    public DateTime UpdatedAt { get; set; }
}
