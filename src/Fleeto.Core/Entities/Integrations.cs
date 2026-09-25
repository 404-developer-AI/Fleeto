namespace Fleeto.Core.Entities;

/// <summary>The external products Fleeto talks to (0.4.0). Stored by name.</summary>
public enum IntegrationType
{
    /// <summary>Patch management. Fleeto keeps no patch engine of its own.</summary>
    Action1
}

/// <summary>What the last attempt to reach an integration did (0.4.0).</summary>
public enum IntegrationStatus
{
    /// <summary>Configured, not contacted yet.</summary>
    Unknown,

    /// <summary>The last attempt succeeded.</summary>
    Ok,

    /// <summary>The last attempt failed; <see cref="Integration.StatusMessage"/> says why.</summary>
    Failing
}

/// <summary>
/// The region an Action1 enterprise lives in (0.4.0). The account is registered in one region and the API answers only
/// there; <see cref="Europe"/> keeps patch data in the EU, which is what Fleeto uses unless the customer's Action1
/// account is elsewhere.
/// </summary>
public enum Action1Region
{
    Europe,
    NorthAmerica,
    NorthAmerica2,
    UnitedKingdom,
    Australia
}

/// <summary>
/// One external product an instance talks to (0.4.0). At most one row per <see cref="Type"/>: an instance uses one
/// Action1 enterprise, whose organizations map to clients through <see cref="IntegrationMapping"/>. The credentials are
/// ciphertext bound to the row (see ARCHITECTURE §5) and are never returned to the UI or the API.
/// </summary>
public class Integration
{
    public Guid Id { get; set; }
    public IntegrationType Type { get; set; }

    /// <summary>False keeps the configuration but stops every call to the product.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Action1: the region of the enterprise, which decides the base URL.</summary>
    public Action1Region? Region { get; set; }

    /// <summary>Credentials as JSON, encrypted and bound to this row. Write-only: the UI never reads it back.</summary>
    public string EncryptedCredentials { get; set; } = string.Empty;

    /// <summary>For display: which credential is in use, without its secret (Action1: the client id).</summary>
    public string CredentialName { get; set; } = string.Empty;

    public IntegrationStatus Status { get; set; } = IntegrationStatus.Unknown;

    /// <summary>Cause and next step of the last failure. Never holds a token or a secret.</summary>
    public string? StatusMessage { get; set; }

    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>
    /// Set by web when an admin asks for a connection test, cleared by the workers when they have run it. Web cannot
    /// reach the internet (only the workers are on the egress network), so every call to the product is made there.
    /// </summary>
    public DateTime? SyncRequestedAt { get; set; }

    /// <summary>The tenants of the account as the workers last read them, as JSON: id and name per tenant.</summary>
    public string TenantsJson { get; set; } = "[]";

    /// <summary>When the workers last read the tenants.</summary>
    public DateTime? TenantsUpdatedAt { get; set; }

    /// <summary>When the workers last read the patch state of every mapped tenant (0.4.0 step 2).</summary>
    public DateTime? PatchSyncedAt { get; set; }

    /// <summary>
    /// The product follows the clients and sites of Fleeto (0.6.0): a new client gets its own tenant (an Action1
    /// organization), every site of a mapped client an endpoint group with its endpoints, and renames and deletions go
    /// along. Off keeps every tenant as the admin maps it by hand.
    /// </summary>
    public bool FollowClients { get; set; }

    /// <summary>
    /// Cause and next step when following clients and sites last failed (0.6.0), such as credentials whose role may not
    /// manage organizations. Cleared by a pass without problems.
    /// </summary>
    public string? FollowMessage { get; set; }

    public List<IntegrationMapping> Mappings { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One tenant of the external product mapped to one Fleeto client (0.4.0): an Action1 organization maps to exactly one
/// client, and a client to at most one organization, so patch state of a machine can never land under another client.
/// Client-owned: deleting the client removes its mapping.
/// </summary>
public class IntegrationMapping
{
    public Guid Id { get; set; }
    public Guid IntegrationId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>The id of the tenant in the external product (Action1: the organization id).</summary>
    public string ExternalTenantId { get; set; } = string.Empty;

    /// <summary>The name of that tenant as the product last reported it, for display.</summary>
    public string ExternalTenantName { get; set; } = string.Empty;

    /// <summary>
    /// Where the installer of the product's own agent for this tenant is downloaded (0.4.0 step 3). The workers read it
    /// from the product when it hands it out and an admin can paste it from the product's console; empty means Fleeto
    /// cannot install that agent for this client, and says so instead of pushing a job that would fail.
    /// </summary>
    public string AgentInstallerUrl { get; set; } = string.Empty;

    /// <summary>When the workers last tried to read <see cref="AgentInstallerUrl"/> from the product.</summary>
    public DateTime? AgentInstallerReadAt { get; set; }

    /// <summary>
    /// The tenant name Fleeto last brought the tenant in step with (0.6.0). While <see cref="Integration.FollowClients"/> is
    /// on, the tenant is renamed when the name made of the client code and name (<c>[ACME] Acme Corporation</c>) differs
    /// from it. Set to the tenant's own name when the mapping is made, so a tenant mapped by hand gets that name too.
    /// </summary>
    public string SyncedName { get; set; } = string.Empty;

    /// <summary>
    /// The scheduled automations in the tenant that Fleeto does not manage (0.6.0), as a JSON array of
    /// <c>{"name": ..., "schedule": ...}</c> the workers read with the patch policies. Shown as a warning: an endpoint can be
    /// patched twice when such an automation also targets it.
    /// </summary>
    public string OtherAutomationsJson { get; set; } = "[]";

    /// <summary>When the workers last read <see cref="OtherAutomationsJson"/>; null while never read.</summary>
    public DateTime? OtherAutomationsReadAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A scheduled automation Fleeto keeps in the product for one patch policy in one tenant (0.6.0). Its targets are the
/// endpoints of the client whose effective patch policy is that policy. Kept by the workers only, and outliving its
/// client and patch policy on purpose: the row is how the workers find the automation to remove once either is gone, so it
/// has no foreign key to them.
/// </summary>
public class IntegrationAutomation
{
    public Guid Id { get; set; }
    public Guid IntegrationId { get; set; }

    /// <summary>The client the automation serves. Not a foreign key: the automation is removed after the client is gone.</summary>
    public Guid ClientId { get; set; }

    /// <summary>The tenant the automation lives in (Action1: the organization id).</summary>
    public string ExternalTenantId { get; set; } = string.Empty;

    /// <summary>The patch policy it is made from. Not a foreign key: the automation is removed after the policy is gone.</summary>
    public Guid PatchPolicyId { get; set; }

    /// <summary>The id of the automation in the product; null until it was created.</summary>
    public string? ExternalAutomationId { get; set; }

    /// <summary>A hash of what Fleeto last sent (settings and targets); a different one is written again.</summary>
    public string SyncedHash { get; set; } = string.Empty;

    public DateTime? SyncedAt { get; set; }

    /// <summary>Why the last attempt failed, in the product's words where it gave them; null after a success.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The endpoint group in the product that belongs to one site (0.6.0), while the integration follows clients and sites.
/// Fleeto keeps its members equal to the endpoints of the site the product reports. Client-owned: gone with the site.
/// </summary>
public class IntegrationSiteGroup
{
    public Guid Id { get; set; }
    public Guid IntegrationId { get; set; }
    public Guid ClientId { get; set; }
    public Guid SiteId { get; set; }

    /// <summary>The tenant the group lives in (Action1: the organization id), so it can be removed after the mapping is gone.</summary>
    public string ExternalTenantId { get; set; } = string.Empty;

    /// <summary>The id of the group in the product.</summary>
    public string ExternalGroupId { get; set; } = string.Empty;

    /// <summary>The site name the group last got from Fleeto; a different site name renames the group.</summary>
    public string SyncedName { get; set; } = string.Empty;

    /// <summary>A hash of the members Fleeto last put in the group; a different set is written again.</summary>
    public string MembersHash { get; set; } = string.Empty;

    /// <summary>When the members were last compared with the product, which happens at least once a day.</summary>
    public DateTime? MembersSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>What the product has to do for Fleeto that cannot be read from the current state (0.6.0).</summary>
public enum IntegrationOperationKind
{
    /// <summary>Create a tenant for a new client and map it, or map the unmapped tenant that has its name.</summary>
    CreateTenant,

    /// <summary>Remove the tenant of a deleted client. Action1 refuses while the organization still holds endpoints.</summary>
    DeleteTenant,

    /// <summary>Remove the endpoint group of a deleted site.</summary>
    DeleteGroup,

    /// <summary>
    /// Move an endpoint to the tenant of the client it belongs to in Fleeto: it was enrolled again under another client,
    /// while the product still has it in the tenant of the old one.
    /// </summary>
    MoveEndpoint
}

/// <summary>
/// One change the workers still have to make in the product (0.6.0), written by web in the same transaction as the change
/// in Fleeto and retried until it is done or an admin dismisses it. Not client-owned: a deletion outlives its client.
/// </summary>
public class IntegrationOperation
{
    public Guid Id { get; set; }
    public Guid IntegrationId { get; set; }
    public IntegrationOperationKind Kind { get; set; }

    /// <summary>
    /// The client a tenant is created for, or whose tenant an endpoint moves to; null for a deletion. Deleting the client drops the operation. Not called
    /// ClientId on purpose: the row is not client-owned, and the removal of a site's group must be recordable by a technician
    /// limited to clients, without a client on the row.
    /// </summary>
    public Guid? TargetClientId { get; set; }

    /// <summary>The tenant to delete, or that holds the group to delete.</summary>
    public string ExternalTenantId { get; set; } = string.Empty;

    /// <summary>The group to delete.</summary>
    public string ExternalGroupId { get; set; } = string.Empty;

    /// <summary>The endpoint to move, by its id in the product; it is moved out of <see cref="ExternalTenantId"/>.</summary>
    public string ExternalEndpointId { get; set; } = string.Empty;

    /// <summary>The name of the client or site, or the host name of the endpoint, for the admin and the audit log.</summary>
    public string Name { get; set; } = string.Empty;

    public int Attempts { get; set; }

    /// <summary>Cause and next step of the last failure, shown in Settings, Integrations.</summary>
    public string? LastError { get; set; }

    public DateTime NextAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
