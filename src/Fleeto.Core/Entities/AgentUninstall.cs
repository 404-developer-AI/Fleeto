namespace Fleeto.Core.Entities;

/// <summary>
/// The removal of the Fleeto agent and watchdog of a deleted endpoint (0.6.0). Deleting an endpoint with "Also uninstall the Fleeto
/// agent" removes the endpoint and all of its data at once, and leaves only this row: the fingerprints of its certificates, so the
/// gateway recognises the agent or the watchdog when it connects again, and the uninstall command fleeto-signer signed for it. The
/// gateway delivers the command on that one session and records that it was accepted; after <see cref="AgentUninstallRules.Lifetime"/>
/// nobody waits any more. Not client-owned: it outlives its endpoint and, when the client is deleted, its client.
/// </summary>
public class AgentUninstall
{
    public Guid Id { get; set; }

    /// <summary>The client the endpoint belonged to, for the list in Settings and the audit log; no foreign key.</summary>
    public Guid ClientId { get; set; }

    public string ClientCode { get; set; } = string.Empty;

    /// <summary>The deleted endpoint; no foreign key, it is gone.</summary>
    public Guid EndpointId { get; set; }

    public string Hostname { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of every agent and watchdog certificate that was valid when the endpoint was deleted.</summary>
    public List<string> Fingerprints { get; set; } = [];

    public Guid RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }

    /// <summary>Nobody waits for the endpoint after this; the signed command is valid until then.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Serialized UninstallCommand and its signature, set by fleeto-signer; null until it signed.</summary>
    public byte[]? Payload { get; set; }

    public byte[]? Signature { get; set; }
    public string? SigningKeyId { get; set; }
    public DateTime? SignedAt { get; set; }

    /// <summary>Why the signer refused it; the row is then of no use and an admin removes it.</summary>
    public string? RefusalReason { get; set; }

    /// <summary>When the agent or the watchdog accepted the command, and which of them.</summary>
    public DateTime? AcceptedAt { get; set; }

    public AgentComponent? AcceptedBy { get; set; }
}

/// <summary>Limits of <see cref="AgentUninstall"/> (0.6.0).</summary>
public static class AgentUninstallRules
{
    /// <summary>How long Fleeto waits for a deleted endpoint to connect again (decided 2026-10-13).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>How long an accepted removal stays in the list in Settings, so an admin sees it happened.</summary>
    public static readonly TimeSpan KeptAfterAccepted = TimeSpan.FromDays(7);

    /// <summary>
    /// The payload of the signing request web writes: the endpoint id and the id of the user who deleted it. The signer signs only a
    /// row that still names both, and nobody but web writes signing requests of this kind.
    /// </summary>
    public static byte[] Binding(Guid endpointId, Guid userId) => [.. endpointId.ToByteArray(), .. userId.ToByteArray()];
}
