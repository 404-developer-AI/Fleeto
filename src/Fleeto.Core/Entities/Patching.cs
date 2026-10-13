namespace Fleeto.Core.Entities;

/// <summary>
/// How severe the vendor of an update calls it (0.4.0). Action1 reports these words; anything it does not recognise is
/// <see cref="Unspecified"/>.
/// </summary>
public enum PatchSeverity
{
    Unspecified,
    Low,
    Moderate,
    Important,
    Critical
}

/// <summary>
/// Whether the patch management product still looks after an endpoint (0.4.0). Action1 gives 200 endpoints per enterprise
/// for free and puts everything above that quota on inactive, which stops patching it.
/// </summary>
public enum PatchCoverage
{
    /// <summary>The product patches this endpoint.</summary>
    Active,

    /// <summary>The product knows the endpoint but does not patch it, so its state is not to be trusted.</summary>
    Inactive
}

/// <summary>
/// The patch state of one managed endpoint as the patch management product last reported it (0.4.0). One row per
/// endpoint, replaced on every sync: Fleeto keeps no patch history of its own, the product has it.
/// <para>
/// The row exists only for an endpoint Fleeto could match to a record in the product, on the id its agent reads from the
/// endpoint. An endpoint without a row has no patch state, which the UI says plainly instead of implying it is compliant.
/// </para>
/// </summary>
public class EndpointPatchState
{
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>The id of the endpoint in the product, which is also what its agent reports locally.</summary>
    public string ExternalEndpointId { get; set; } = string.Empty;

    /// <summary>The tenant of the product this endpoint belongs to (Action1: the organization id).</summary>
    public string ExternalTenantId { get; set; } = string.Empty;

    public PatchCoverage Coverage { get; set; } = PatchCoverage.Active;

    /// <summary>Missing updates the vendor calls critical.</summary>
    public int MissingCritical { get; set; }

    /// <summary>Missing updates of every other severity.</summary>
    public int MissingOther { get; set; }

    /// <summary>True when the product says the endpoint waits for a restart to finish its updates.</summary>
    public bool RebootRequired { get; set; }

    /// <summary>When the product last had contact with the endpoint. Null when it never did.</summary>
    public DateTime? ProductLastSeenAt { get; set; }

    /// <summary>The version of the product's own agent on the endpoint, for support questions.</summary>
    public string ProductAgentVersion { get; set; } = string.Empty;

    /// <summary>When Fleeto last read this state.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// The automation of the endpoint's patch policy that targets it in the product (0.6.0), by the id of its
    /// <see cref="IntegrationAutomation"/> row; null while no automation of Fleeto targets the endpoint. Set by the workers
    /// when the automation was written with the endpoint among its targets.
    /// </summary>
    public Guid? AutomationId { get; set; }

    /// <summary>
    /// Since when an automation of the same patch policy targets the endpoint (0.6.0). The "Scheduled patching" check counts
    /// from here while no scheduled deployment succeeded on the endpoint yet; a different patch policy starts it again.
    /// </summary>
    public DateTime? ScheduledSince { get; set; }

    /// <summary>The patch policy of <see cref="AutomationId"/>, kept so a change of policy can restart <see cref="ScheduledSince"/>.</summary>
    public Guid? ScheduledPolicyId { get; set; }

    /// <summary>True when the endpoint misses nothing the product knows about.</summary>
    public bool IsCompliant => MissingCritical == 0 && MissingOther == 0;
}

/// <summary>
/// One update the product reports as missing on an endpoint (0.4.0). Replaced per endpoint on every sync; the detail is
/// read only for endpoints that are not compliant, so a compliant endpoint has no rows.
/// </summary>
public class EndpointMissingUpdate
{
    public Guid Id { get; set; }
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>The id of the update in the product, so a deployment can name exactly this one.</summary>
    public string ExternalUpdateId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>The KB number on Windows, empty for everything else.</summary>
    public string KbNumber { get; set; } = string.Empty;

    public PatchSeverity Severity { get; set; } = PatchSeverity.Unspecified;

    /// <summary>
    /// Kept only so the release before 0.6.0 still runs on this schema (expand/contract); nothing reads it. Action1 answers
    /// "Possibly" or "Unknown" for every update, which tells a technician nothing.
    /// </summary>
    public bool RebootNeeded { get; set; }

    /// <summary>The version on the endpoint now (0.6.0), empty when the product does not know it or it is not installed.</summary>
    public string InstalledVersion { get; set; } = string.Empty;

    /// <summary>When the vendor released the version the product would install (0.6.0).</summary>
    public DateOnly? ReleaseDate { get; set; }

    /// <summary>The product's word for the kind of update (0.6.0), such as Security Updates, Feature Updates or Drivers.</summary>
    public string UpdateType { get; set; } = string.Empty;

    /// <summary>
    /// The approval of the update in the product (0.6.0): New, Approved or Declined. Only shown: a deployment started from
    /// Fleeto installs what it names regardless of it.
    /// </summary>
    public string ApprovalStatus { get; set; } = string.Empty;

    /// <summary>The CVEs the update remediates (0.6.0).</summary>
    public List<string> Cves { get; set; } = [];

    /// <summary>When Fleeto last saw this update as missing.</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>What a deployment installs (0.4.0 step 3).</summary>
public enum PatchDeploymentScope
{
    /// <summary>Every update the product reports as missing on the endpoints of the deployment.</summary>
    AllMissing,

    /// <summary>Only the updates the technician chose.</summary>
    Specified,

    /// <summary>
    /// What a patch policy installs, on its schedule (0.6.0): a run of the automation Fleeto keeps for the policy, found in
    /// the product rather than started by a technician.
    /// </summary>
    PatchPolicy
}

/// <summary>
/// Where a deployment stands (0.4.0 step 3). Fleeto owns the first two states; after that the patch management product
/// does the work and Fleeto follows it.
/// </summary>
public enum PatchDeploymentState
{
    /// <summary>Written by web. The workers have not handed it to the product yet.</summary>
    Requested,

    /// <summary>The product accepted it and is working through the endpoints.</summary>
    Running,

    /// <summary>Every endpoint reached an end state.</summary>
    Completed,

    /// <summary>The product refused the deployment; nothing was installed. <see cref="PatchDeployment.StatusMessage"/> says why.</summary>
    Failed,

    /// <summary>The product never finished within <see cref="Fleeto.Core.Domain.PatchRules.FollowFor"/>; Fleeto stopped following it.</summary>
    Abandoned
}

/// <summary>What the product reports for one endpoint of a deployment (0.4.0 step 3).</summary>
public enum PatchDeploymentTargetState
{
    /// <summary>Handed to the product, not started on this endpoint yet.</summary>
    Pending,

    Running,
    Succeeded,

    /// <summary>The product could not install the updates on this endpoint.</summary>
    Failed,

    /// <summary>The deployment ended without the product saying what happened here.</summary>
    Unknown
}

/// <summary>
/// One deployment of updates, started by a technician and carried out by the patch management product (0.4.0 step 3).
///
/// A deployment belongs to one client, because the product runs it per tenant: a selection that spans clients becomes one
/// deployment per client, sharing a <see cref="BatchId"/>. Fleeto writes the row, the workers hand it to the product
/// (only they can reach it) and follow it until every endpoint has an answer.
/// </summary>
public class PatchDeployment
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>Deployments started in one action share this id, also across clients.</summary>
    public Guid BatchId { get; set; }

    /// <summary>The tenant of the product this deployment runs in (Action1: the organization id).</summary>
    public string ExternalTenantId { get; set; } = string.Empty;

    /// <summary>The id the product gave the deployment, empty until it accepted it.</summary>
    public string ExternalDeploymentId { get; set; } = string.Empty;

    public PatchDeploymentScope Scope { get; set; }

    /// <summary>True when the product may restart an endpoint by itself to finish the updates.</summary>
    public bool AutoReboot { get; set; }

    public PatchDeploymentState State { get; set; } = PatchDeploymentState.Requested;

    /// <summary>Cause and next step when something went wrong. Never holds a token or a secret.</summary>
    public string? StatusMessage { get; set; }

    public Guid RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }

    /// <summary>When the product accepted the deployment.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When every endpoint had reached an end state, or Fleeto gave up following.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>When the workers last asked the product how it is going.</summary>
    public DateTime? PolledAt { get; set; }

    /// <summary>
    /// The patch policy whose automation ran this deployment (0.6.0), for <see cref="PatchDeploymentScope.PatchPolicy"/>
    /// only. Not a foreign key: the history stays when the policy is deleted, with its name in <see cref="RequestedByName"/>.
    /// </summary>
    public Guid? PatchPolicyId { get; set; }

    /// <summary>The id of the automation in the product that ran it (0.6.0); empty for a deployment a technician started.</summary>
    public string ExternalAutomationId { get; set; } = string.Empty;

    /// <summary>
    /// Until when Fleeto follows the deployment (0.6.0); null follows it for <see cref="Fleeto.Core.Domain.PatchRules.FollowFor"/>
    /// after it started. A scheduled one follows the retry window of its patch policy, which can be longer than a day.
    /// </summary>
    public DateTime? FollowUntil { get; set; }

    public List<PatchDeploymentUpdate> Updates { get; set; } = [];
    public List<PatchDeploymentTarget> Targets { get; set; } = [];

    /// <summary>True while the workers still have work to do for this deployment.</summary>
    public bool IsOpen => State is PatchDeploymentState.Requested or PatchDeploymentState.Running;
}

/// <summary>
/// One update a deployment installs (0.4.0 step 3). Only for <see cref="PatchDeploymentScope.Specified"/>: the product
/// needs the id and the version of every package, and Fleeto keeps the name so the history stays readable when the update
/// is no longer missing anywhere.
/// </summary>
public class PatchDeploymentUpdate
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>The id of the update in the product (Action1: the package id).</summary>
    public string ExternalUpdateId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The version of the package the product must install.</summary>
    public string Version { get; set; } = string.Empty;
}

/// <summary>One endpoint of a deployment, with what the product reports for it (0.4.0 step 3).</summary>
public class PatchDeploymentTarget
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }

    /// <summary>The id of the endpoint in the product, as it was when the deployment started.</summary>
    public string ExternalEndpointId { get; set; } = string.Empty;

    /// <summary>The host name when the deployment started, so the history reads the same after a rename.</summary>
    public string Hostname { get; set; } = string.Empty;

    public PatchDeploymentTargetState State { get; set; } = PatchDeploymentTargetState.Pending;

    /// <summary>What the product said about this endpoint, when it said anything.</summary>
    public string? Message { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>When the workers last read the history of this endpoint from the product (0.6.0); null while never read.</summary>
    public DateTime? StepsReadAt { get; set; }
}

/// <summary>
/// One line of the history the product keeps for one endpoint of a deployment (0.6.0): Action1's "Automation History",
/// such as "Deploy Update, Success, Installing 2026-09 .NET Framework Security Update (KB5126052)". Replaced as a whole
/// every time the workers read it, and deleted with its endpoint of the deployment.
/// </summary>
public class PatchDeploymentStep
{
    public Guid Id { get; set; }
    public Guid TargetId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>The order the product listed the lines in, from 0.</summary>
    public int Position { get; set; }

    /// <summary>When it happened, in UTC; null when the product gave no time Fleeto could read.</summary>
    public DateTime? Time { get; set; }

    /// <summary>What the product calls the operation, such as "Deploy Update"; empty for lines the product adds itself.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>The status word of the product: Pending, Stopped, Running, Success, Warning or Error.</summary>
    public string Status { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;
}

/// <summary>What a technician asks the workers to do about the patch management of one endpoint (0.6.0).</summary>
public enum PatchRequestKind
{
    /// <summary>Read the patch state, missing updates, scheduled deployments and next run of the endpoint from the product now.</summary>
    Sync,

    /// <summary>Remove the endpoint from the product, which also uninstalls the product's agent on it.</summary>
    RemoveAgent
}

/// <summary>
/// A request of a technician about the patch management of one endpoint (0.6.0). Web writes it, because only the workers
/// can reach the product; the workers carry it out and write the outcome on the row, which the Patches tab shows. Kept a
/// day after it completed, so the outcome can be read, and gone with its endpoint.
/// </summary>
public class PatchRequest
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public PatchRequestKind Kind { get; set; }

    /// <summary>The tenant and the endpoint in the product when the request was made, for <see cref="PatchRequestKind.RemoveAgent"/>.</summary>
    public string ExternalTenantId { get; set; } = string.Empty;

    public string ExternalEndpointId { get; set; } = string.Empty;

    public Guid RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }

    /// <summary>When the workers finished it; null while it waits.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Cause and next step when it failed; null when it succeeded or still waits.</summary>
    public string? Failure { get; set; }
}

/// <summary>Where the Action1 agent of one endpoint stands (0.6.0), as the workers last judged it.</summary>
public enum Action1AgentStatus
{
    /// <summary>Action1 reports the endpoint in the organization of its client.</summary>
    Installed,

    /// <summary>No Action1 agent, and its patch policy does not install one by itself.</summary>
    Missing,

    /// <summary>An Action1 agent runs on the endpoint, but Action1 does not report it in the organization of its client.</summary>
    NotReported,

    /// <summary>
    /// An installation is on its way: Fleeto waits for the endpoint to come online, for the job, for the Fleeto agent to
    /// report the new agent, or for Action1 to report the endpoint.
    /// </summary>
    Installing,

    /// <summary>The last installation failed; Fleeto tries again at <see cref="Action1AgentInstallState.NextAttemptAt"/>.</summary>
    Failed,

    /// <summary>Every automatic attempt failed; Fleeto waits until a technician installs the agent by hand.</summary>
    Stopped,

    /// <summary>
    /// The agent was removed through Fleeto, so Fleeto does not install it again by itself until a technician installs it
    /// by hand.
    /// </summary>
    Removed
}

/// <summary>
/// The Action1 agent of one managed Windows endpoint whose client is mapped to Action1 (0.6.0): whether Action1 reports
/// the endpoint, and the automatic installation a patch policy asks for, with its attempts and the reason of the last
/// failure. Written by the workers only; the Patches tab, the patch policy and the "Action1 agent" check read it.
/// </summary>
public class Action1AgentInstallState
{
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }
    public Action1AgentStatus Status { get; set; }

    /// <summary>Failed automatic attempts since the last success or the last installation by hand.</summary>
    public int Attempts { get; set; }

    /// <summary>The newest installation job of the endpoint, started by Fleeto or by a technician.</summary>
    public Guid? JobId { get; set; }

    /// <summary>When that job succeeded; the Fleeto agent should report the new Action1 agent within hours after it.</summary>
    public DateTime? JobSucceededAt { get; set; }

    /// <summary>When the Fleeto agent first reported the Action1 agent after the job; Action1 should report the endpoint soon after.</summary>
    public DateTime? AgentReportedAt { get; set; }

    /// <summary>When Fleeto next tries by itself after <see cref="Action1AgentStatus.Failed"/>.</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>Cause and next step of the last failure, in the words of the UI; null when nothing failed.</summary>
    public string? Failure { get; set; }

    /// <summary>The newest removal through Fleeto that this row took into account (a request or a job), so it counts once.</summary>
    public Guid? RemovalId { get; set; }

    /// <summary>When the "Action1 agent" checks of the endpoint last got a result, and what it said.</summary>
    public DateTime? CheckWrittenAt { get; set; }

    public string? CheckDetail { get; set; }

    public DateTime UpdatedAt { get; set; }
}
