namespace Fleeto.Core.Entities;

/// <summary>
/// Work queue for fleeto-signer. Written by the gateway or workers, processed by the signer, which
/// re-checks every rule against the database before signing (ARCHITECTURE.md §5, The signer).
/// </summary>
public class SigningRequest
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public SigningRequestKind Kind { get; set; }

    /// <summary>Endpoint id for renewals and configs; null for enrollment and gateway certificates.</summary>
    public Guid? SubjectId { get; set; }

    /// <summary>Kind-specific input, e.g. a serialized enrollment request.</summary>
    public byte[] Payload { get; set; } = [];

    public string RequestedBy { get; set; } = string.Empty;
    public SigningRequestState State { get; set; } = SigningRequestState.Pending;

    /// <summary>Kind-specific output, e.g. a serialized enrollment response.</summary>
    public byte[]? Result { get; set; }

    public string? RefusalReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Latest signed configuration of an endpoint.</summary>
public class EndpointConfig
{
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }
    public long Version { get; set; }

    /// <summary>Serialized <c>AgentConfig</c> protobuf.</summary>
    public byte[] Payload { get; set; } = [];

    public byte[] Signature { get; set; } = [];
    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// Hex SHA-256 of the configuration content without version and issue time. The signer skips issuing a new
    /// version when the content has not changed.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

/// <summary>The instance signing key. The private key is encrypted under the signer key.</summary>
public class InstanceSigningKey
{
    /// <summary>First 16 hex characters of the SHA-256 of the public key.</summary>
    public string Id { get; set; } = string.Empty;

    public byte[] PublicKey { get; set; } = [];
    public byte[] EncryptedPrivateKey { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? RetiredAt { get; set; }
}

/// <summary>The internal CA. The private key is encrypted under the signer key.</summary>
public class CertificateAuthority
{
    public Guid Id { get; set; }
    public byte[] CertificateDer { get; set; } = [];

    /// <summary>Lowercase hex SHA-256 of the CA certificate DER; embedded in agent install commands.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public byte[] EncryptedPrivateKey { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RetiredAt { get; set; }
}

/// <summary>Durable record that configuration inputs changed; the workers fan it out to endpoints.</summary>
public class ConfigChangeEvent
{
    public long Id { get; set; }
    public ConfigChangeScope Scope { get; set; }
    public Guid? ScopeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
