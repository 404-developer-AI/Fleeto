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

    /// <summary>True when the product says installing this update may ask for a restart.</summary>
    public bool RebootNeeded { get; set; }

    /// <summary>When Fleeto last saw this update as missing.</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>What a deployment installs (0.4.0 step 3).</summary>
public enum PatchDeploymentScope
{
    /// <summary>Every update the product reports as missing on the endpoints of the deployment.</summary>
    AllMissing,

    /// <summary>Only the updates the technician chose.</summary>
    Specified
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
}
