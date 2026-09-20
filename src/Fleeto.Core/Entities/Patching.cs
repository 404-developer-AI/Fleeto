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
