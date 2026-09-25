using Fleeto.Core.Entities;

namespace Fleeto.Core.Interfaces;

/// <summary>
/// The set of clients the current caller may see. Every client-owned query is filtered on it by the
/// DbContext. System context (background services, the signer) sees every client.
/// </summary>
public interface IClientScope
{
    /// <summary>True for system context and for users without a client restriction.</summary>
    bool AllClients { get; }

    /// <summary>Allowed clients when <see cref="AllClients"/> is false. Empty means nothing is visible.</summary>
    IReadOnlyCollection<Guid> ClientIds { get; }
}

/// <summary>
/// Encrypts secrets at rest with envelope encryption: a data key per purpose, wrapped by the root key.
/// The associated data binds a ciphertext to where it is stored, so it cannot be moved to another row.
/// </summary>
public interface ISecretProtector
{
    string Protect(string purpose, string plaintext, string associatedData);
    string Unprotect(string purpose, string ciphertext, string associatedData);
    byte[] Protect(string purpose, ReadOnlySpan<byte> plaintext, string associatedData);
    byte[] Unprotect(string purpose, ReadOnlySpan<byte> ciphertext, string associatedData);
}

/// <summary>Cross-process notifications (PostgreSQL LISTEN/NOTIFY). Never the only copy of any data.</summary>
public interface INotificationBus
{
    /// <summary>Publishes a small payload (at most 8000 bytes) on a channel.</summary>
    Task PublishAsync(string channel, string payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to a channel. The handler also receives an empty payload with
    /// <see cref="NotificationBusEvents.Resync"/> after a reconnect, so subscribers can catch up from the
    /// database for anything they may have missed.
    /// </summary>
    IDisposable Subscribe(string channel, Func<string, CancellationToken, Task> handler);
}

public static class NotificationBusEvents
{
    /// <summary>Payload delivered to every subscriber after the listener reconnects.</summary>
    public const string Resync = "__resync__";
}

/// <summary>Channel names. Payloads are ids or tiny JSON objects, never data that exists nowhere else.</summary>
public static class NotificationChannels
{
    /// <summary>Payload: SigningRequest id. Raised by a database trigger on insert.</summary>
    public const string SigningRequests = "fleeto_signing_requests";

    /// <summary>Payload: SigningRequest id. Raised by a database trigger when a request completes.</summary>
    public const string SigningResults = "fleeto_signing_results";

    /// <summary>Payload: endpoint id. New check results were stored for this endpoint.</summary>
    public const string CheckResults = "fleeto_check_results";

    /// <summary>Payload: endpoint id. Online state, inventory or tier changed.</summary>
    public const string EndpointStatus = "fleeto_endpoint_status";

    /// <summary>Payload: endpoint id. A newer signed configuration is available.</summary>
    public const string EndpointConfig = "fleeto_endpoint_config";

    /// <summary>Payload: endpoint id. Certificates of this endpoint were revoked or the endpoint was deleted.</summary>
    public const string Revocations = "fleeto_revocations";

    /// <summary>Payload: alert id. An alert opened, changed or resolved.</summary>
    public const string Alerts = "fleeto_alerts";

    /// <summary>Payload: ConfigChangeEvent id. Raised by a database trigger on insert.</summary>
    public const string ConfigChanges = "fleeto_config_changes";

    /// <summary>Payload: EndpointEvent id. Raised by a database trigger on insert.</summary>
    public const string EndpointEvents = "fleeto_endpoint_events";

    /// <summary>Payload: OutboxEmail id. Raised by a database trigger on insert.</summary>
    public const string OutboxEmails = "fleeto_outbox_emails";

    /// <summary>Payload: endpoint id. A job of this endpoint was signed, cancelled or changed state (0.2.0).</summary>
    public const string Jobs = "fleeto_jobs";

    /// <summary>Payload: OutboxWebhook id. Raised by a database trigger on insert.</summary>
    public const string OutboxWebhooks = "fleeto_outbox_webhooks";

    /// <summary>
    /// Payload: CheckRunRequest id. Raised by a database trigger on insert (workers apply a reset) and when a reset was
    /// applied (the gateway delivers the request).
    /// </summary>
    public const string CheckRunRequests = "fleeto_check_run_requests";

    /// <summary>Payload: agent release version. A release was paused, resumed or released to all rings (0.2.1).</summary>
    public const string AgentReleases = "fleeto_agent_releases";

    /// <summary>
    /// Payload: the integration type. An admin asked for a connection test, or changed the credentials (0.4.0). Only the
    /// workers can reach an external product, so they do the call and write the result back.
    /// </summary>
    public const string Integrations = "fleeto_integrations";

    /// <summary>
    /// Payload: PatchDeployment id. A technician started a deployment, or the workers changed one (0.4.0 step 3). Web
    /// writes the request and follows the row; only the workers can reach the patch management product.
    /// </summary>
    public const string PatchDeployments = "fleeto_patch_deployments";

    /// <summary>
    /// Payload: SignInExchange id. Somebody is signing in with Microsoft Entra ID (0.5.0) and web wrote the authorization
    /// code; the workers exchange it, because only they can reach Microsoft. Raised again when the outcome is written, so
    /// the sign-in does not wait for its next poll.
    /// </summary>
    public const string SignIns = "fleeto_sign_ins";

    /// <summary>
    /// Payload: MicrosoftRequest id. An admin tested an app registration or searched the users of the tenant (0.5.0); only
    /// the workers can reach Microsoft, so they answer and web reads the answer from the row.
    /// </summary>
    public const string MicrosoftRequests = "fleeto_microsoft_requests";
}

/// <summary>Writes audit entries. The table is append-only; there is no update or delete.</summary>
public interface IAuditLog
{
    Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}

/// <summary>One audit record. <paramref name="Details"/> is serialized to JSON; never put secrets in it.</summary>
public sealed record AuditRecord(
    string Action,
    string TargetType,
    string TargetId,
    Guid? ClientId,
    AuditActorType ActorType,
    string ActorId,
    string ActorName,
    object? Details = null,
    string? IpAddress = null);

/// <summary>Well-known audit action names.</summary>
public static class AuditActions
{
    public const string LoginSucceeded = "user.login";
    public const string LoginFailed = "user.login_failed";
    public const string Logout = "user.logout";
    public const string TwoFactorEnabled = "user.2fa_enabled";
    public const string TwoFactorReset = "user.2fa_reset";
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserDeleted = "user.deleted";
    public const string RolesChanged = "user.roles_changed";
    public const string PasswordChanged = "user.password_changed";
    public const string FirstAdminCreated = "instance.first_admin_created";

    public const string ClientCreated = "client.created";
    public const string ClientUpdated = "client.updated";
    public const string ClientDeleted = "client.deleted";
    public const string ClientDetachedFromTemplate = "client.detached_from_template";
    public const string ClientTagsChanged = "client.tags_changed";
    /// <summary>The policy, patch policy or monitoring templates of a client changed (0.6.0).</summary>
    public const string ClientLinksChanged = "client.links_changed";
    public const string TagUpdated = "tag.updated";
    public const string TagDeleted = "tag.deleted";
    public const string SiteCreated = "site.created";
    public const string SiteUpdated = "site.updated";
    public const string SiteDeleted = "site.deleted";
    public const string SiteLinksChanged = "site.links_changed";

    public const string EnrollmentTokenCreated = "enrollment_token.created";
    public const string EnrollmentTokenRevoked = "enrollment_token.revoked";
    public const string EndpointEnrolled = "endpoint.enrolled";
    public const string EndpointTierChanged = "endpoint.tier_changed";
    public const string EndpointClassChanged = "endpoint.class_changed";
    public const string EndpointMoved = "endpoint.moved";
    public const string EndpointDeleted = "endpoint.deleted";
    public const string EndpointChecksChanged = "endpoint.checks_changed";
    /// <summary>The policy or patch policy of one endpoint changed (0.6.0).</summary>
    public const string EndpointLinksChanged = "endpoint.links_changed";
    public const string CheckRunRequested = "check.run_requested";
    public const string CheckReset = "check.reset";
    public const string NoteCreated = "note.created";
    public const string NoteUpdated = "note.updated";
    public const string NoteDeleted = "note.deleted";
    public const string MaintenanceStarted = "maintenance.started";
    public const string MaintenanceChanged = "maintenance.changed";
    public const string MaintenanceEnded = "maintenance.ended";
    public const string MaintenanceExpired = "maintenance.expired";
    public const string CertificateIssued = "certificate.issued";
    public const string CertificateRenewed = "certificate.renewed";
    public const string CertificateRecovered = "certificate.recovered";
    public const string ScriptCreated = "script.created";
    public const string ScriptChanged = "script.changed";
    public const string ScriptVersionSaved = "script.version_saved";
    public const string ScriptVersionApproved = "script.version_approved";
    public const string ScriptDeleted = "script.deleted";
    public const string JobCreated = "job.created";
    /// <summary>One entry per run on more than one endpoint, next to the entry per job (0.2.1).</summary>
    public const string JobBatchStarted = "job.batch_started";
    public const string JobSigned = "job.signed";
    public const string JobRefused = "job.refused";
    public const string JobCancelled = "job.cancelled";
    public const string EndpointEnrolledAgain = "endpoint.enrolled_again";
    public const string CertificateRevoked = "certificate.revoked";
    public const string ConfigSigned = "config.signed";
    public const string SigningKeyCreated = "signing_key.created";
    public const string CertificateAuthorityCreated = "certificate_authority.created";
    public const string SigningRefused = "signing.refused";

    public const string PolicyCreated = "policy.created";
    public const string PolicyUpdated = "policy.updated";
    public const string PolicyDeleted = "policy.deleted";
    public const string PatchPolicyCreated = "patch_policy.created";
    public const string PatchPolicyUpdated = "patch_policy.updated";
    public const string PatchPolicyDeleted = "patch_policy.deleted";
    public const string MonitoringTemplateCreated = "monitoring_template.created";
    public const string MonitoringTemplateUpdated = "monitoring_template.updated";
    public const string MonitoringTemplateDeleted = "monitoring_template.deleted";
    public const string ClientTemplateCreated = "client_template.created";
    public const string ClientTemplateUpdated = "client_template.updated";
    public const string ClientTemplateDeleted = "client_template.deleted";

    public const string AlertAcknowledged = "alert.acknowledged";
    public const string AlertResolvedManually = "alert.resolved_manually";
    public const string AlertHeld = "alert.held";
    public const string AlertHoldEnded = "alert.hold_ended";

    public const string AgentReleaseInstalled = "agent_release.installed";
    public const string AgentReleasePaused = "agent_release.paused";
    public const string AgentReleaseResumed = "agent_release.resumed";
    public const string AgentReleaseReleasedToAll = "agent_release.released_to_all";

    public const string ApiKeyCreated = "api_key.created";
    public const string ApiKeyRevoked = "api_key.revoked";
    public const string ApiKeyAuthenticationFailed = "api_key.authentication_failed";
    public const string ApiRequest = "api.request";

    public const string LicenseLoaded = "license.loaded";
    public const string SettingsChanged = "settings.changed";
    public const string CredentialChanged = "settings.credential_changed";
    public const string NotificationChannelChanged = "notification_channel.changed";
    public const string BackupStarted = "backup.started";

    /// <summary>A technician asked for updates to be deployed (0.4.0 step 3); one entry per client of the run.</summary>
    public const string PatchDeploymentStarted = "patch_deployment.started";

    /// <summary>The patch management product accepted the deployment and named it.</summary>
    public const string PatchDeploymentAccepted = "patch_deployment.accepted";

    /// <summary>The deployment ended: completed, refused by the product, or no longer followed.</summary>
    public const string PatchDeploymentEnded = "patch_deployment.ended";

    public const string IntegrationChanged = "integration.changed";
    public const string IntegrationRemoved = "integration.removed";
    public const string IntegrationMappingChanged = "integration.mapping_changed";

    /// <summary>The workers created a tenant in the product for a new client and mapped it (0.6.0).</summary>
    public const string IntegrationTenantCreated = "integration.tenant_created";

    /// <summary>The workers removed the tenant of a deleted client from the product (0.6.0).</summary>
    public const string IntegrationTenantDeleted = "integration.tenant_deleted";

    /// <summary>The workers moved an endpoint to the tenant of the client it belongs to in Fleeto (0.6.0).</summary>
    public const string IntegrationEndpointMoved = "integration.endpoint_moved";

    /// <summary>An admin dismissed a change the product still had to make (0.6.0).</summary>
    public const string IntegrationOperationDismissed = "integration.operation_dismissed";

    /// <summary>The Entra ID sign-in of the instance was configured, changed or turned off (0.5.0).</summary>
    public const string SignInConfigured = "sign_in.configured";

    /// <summary>A user was linked to an Entra ID account, or the link was removed (0.5.0).</summary>
    public const string UserLinked = "user.linked";
    public const string UserUnlinked = "user.unlinked";

    public const string RemoteSessionRequested = "remote_session.requested";
    public const string RemoteSessionSigned = "remote_session.signed";
    public const string RemoteSessionJoined = "remote_session.joined";
    public const string RemoteSessionLeft = "remote_session.left";
    public const string RemoteSessionRefused = "remote_session.refused";
}
