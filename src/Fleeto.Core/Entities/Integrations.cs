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

    public DateTime CreatedAt { get; set; }
}
