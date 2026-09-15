namespace Fleetify.Core.Entities;

/// <summary>Identity of the instance. Exactly one row, created by <c>fleetify-tool migrate</c>.</summary>
public class InstanceSettings
{
    public int Id { get; set; } = 1;
    public Guid InstanceId { get; set; }

    /// <summary>The customer's FQDN, e.g. <c>rmm.customer.example</c>.</summary>
    public string Fqdn { get; set; } = string.Empty;

    /// <summary>Host name agents connect to, normally <c>agents.&lt;fqdn&gt;</c>.</summary>
    public string AgentHostName { get; set; } = string.Empty;

    public int AgentPort { get; set; } = 443;

    /// <summary>Public base URL of the web UI, e.g. <c>https://rmm.customer.example</c>.</summary>
    public string WebBaseUrl { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

/// <summary>Key/value settings. Values with <see cref="IsEncrypted"/> are ciphertext from the secret protector.</summary>
public class Setting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsEncrypted { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>A data key (DEK) wrapped by the root key.</summary>
public class DataKey
{
    public Guid Id { get; set; }
    public string Purpose { get; set; } = string.Empty;

    /// <summary>nonce (12) | tag (16) | ciphertext (32), AES-256-GCM under the root key.</summary>
    public byte[] WrappedKey { get; set; } = [];

    /// <summary>First 16 hex characters of the SHA-256 of the root key that wraps this key.</summary>
    public string RootKeyId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime? RetiredAt { get; set; }
}

/// <summary>A loaded license document. At most one active.</summary>
public class License
{
    public Guid Id { get; set; }
    public string Serial { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Fqdn { get; set; } = string.Empty;
    public int ManagedEndpointCount { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string KeyId { get; set; } = string.Empty;

    /// <summary>The signed document, encrypted with the secret protector.</summary>
    public string EncryptedDocument { get; set; } = string.Empty;

    public bool IsActive { get; set; }
    public DateTime LoadedAt { get; set; }
    public Guid? LoadedByUserId { get; set; }
}

/// <summary>One-time link for creating the first admin. Only the hash is stored.</summary>
public class SetupToken
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Append-only audit trail. No update or delete path in code or database grants.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime Time { get; set; }
    public Guid? ClientId { get; set; }
    public AuditActorType ActorType { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;

    /// <summary>JSON object with action-specific details. Never secrets.</summary>
    public string DetailsJson { get; set; } = "{}";

    public string? IpAddress { get; set; }
}

/// <summary>Where alert notifications go.</summary>
public class NotificationChannel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public NotificationChannelType Type { get; set; } = NotificationChannelType.Email;

    /// <summary>Email channels: comma-separated email addresses. Empty for webhooks.</summary>
    public string Recipients { get; set; } = string.Empty;

    /// <summary>Webhook channels: the body format.</summary>
    public WebhookFormat? WebhookFormat { get; set; }

    /// <summary>Webhook channels: the host name of the URL, for display. The URL itself can carry a secret.</summary>
    public string? WebhookHost { get; set; }

    /// <summary>
    /// Webhook channels: URL and signing secret as JSON, encrypted and bound to the channel id. Never returned to the UI.
    /// </summary>
    public string? EncryptedWebhook { get; set; }

    public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.Warning;
    public bool NotifyOnResolve { get; set; } = true;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Routing (0.2.0): true sends alerts of every client, also clients created later; false only alerts of the clients
    /// in <see cref="Clients"/>.
    /// </summary>
    public bool AllClients { get; set; } = true;

    public List<NotificationChannelClient> Clients { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A client whose alerts a channel with <see cref="NotificationChannel.AllClients"/> false receives.</summary>
public class NotificationChannelClient
{
    public Guid NotificationChannelId { get; set; }
    public Guid ClientId { get; set; }
}

/// <summary>
/// Webhook outbox: one request per channel and notification, delivered by the workers with retry and backoff. The id is
/// sent as the delivery id, the same on every attempt, so a receiver can drop duplicates.
/// </summary>
public class OutboxWebhook
{
    public Guid Id { get; set; }
    public Guid NotificationChannelId { get; set; }
    public string Category { get; set; } = string.Empty;

    /// <summary>The request body, built when the notification was queued.</summary>
    public string Payload { get; set; } = string.Empty;

    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Email outbox. Written by web and workers, delivered by the workers with retry and backoff.</summary>
public class OutboxEmail
{
    public Guid Id { get; set; }
    public string ToAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string TextBody { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>One backup attempt.</summary>
public class BackupRun
{
    public Guid Id { get; set; }
    public BackupKind Kind { get; set; }
    public BackupRunStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? Error { get; set; }
}

/// <summary>Progress markers for workers that catch up from the database.</summary>
public class WorkerWatermark
{
    public string Name { get; set; } = string.Empty;
    public long Value { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Role names. See CLAUDE.md, Product scope: admin, technician, read-only.</summary>
public static class FleetifyRoles
{
    public const string Admin = "admin";
    public const string Technician = "technician";
    public const string ReadOnly = "read-only";

    public static readonly IReadOnlyList<string> All = [Admin, Technician, ReadOnly];
}
