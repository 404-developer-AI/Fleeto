using Fleeto.Core.Entities;

namespace Fleeto.Core.Interfaces;

/// <summary>
/// The clients, sites and endpoints the current caller may see. Every client-owned query is filtered on it by the
/// DbContext. System context (background services, the signer) sees every client.
/// <para>
/// From 0.6.0 a user sees what the groups it is a member of give it, which can be part of a client: some of its sites, or
/// some of its endpoints. A client is then <i>partial</i>: it is in <see cref="ClientIds"/> but not in
/// <see cref="FullClientIds"/>, and only the sites in <see cref="SiteIds"/> and the endpoints in <see cref="EndpointIds"/>
/// of it are visible. A scope that never sees part of a client (system, API keys) leaves the last three at their default.
/// </para>
/// </summary>
public interface IClientScope
{
    /// <summary>True for system context, admins and users that see every client without exception.</summary>
    bool AllClients { get; }

    /// <summary>Clients the caller sees, wholly or in part, when <see cref="AllClients"/> is false. Empty means nothing is visible.</summary>
    IReadOnlyCollection<Guid> ClientIds { get; }

    /// <summary>Clients the caller sees with every site and endpoint, the ones created later included.</summary>
    IReadOnlyCollection<Guid> FullClientIds => ClientIds;

    /// <summary>Sites of partial clients the caller sees with every endpoint, the ones enrolled later included.</summary>
    IReadOnlyCollection<Guid> FullSiteIds => [];

    /// <summary>Sites of partial clients the caller sees, wholly or in part (<see cref="FullSiteIds"/> included).</summary>
    IReadOnlyCollection<Guid> SiteIds => [];

    /// <summary>Endpoints of partial clients the caller sees, those of <see cref="FullSiteIds"/> included.</summary>
    IReadOnlyCollection<Guid> EndpointIds => [];

    /// <summary>A client created later is visible to the caller: it sees every client, or is in a group for all clients.</summary>
    bool IncludesNewClients => AllClients;
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

    /// <summary>Payload: StorageScanRequest id. Raised by a database trigger on insert; the gateway delivers the request (0.6.0).</summary>
    public const string StorageScanRequests = "fleeto_storage_scan_requests";

    /// <summary>
    /// Payload: endpoint id. A user asked for a fresh inventory (Endpoints.InventoryRequestedAt); the gateway asks the live agent
    /// for it (0.6.0).
    /// </summary>
    public const string InventoryRequests = "fleeto_inventory_requests";

    /// <summary>Payload: empty. A setting that instance health reads changed (the backups, 0.6.0); the workers measure again.</summary>
    public const string InstanceHealth = "fleeto_instance_health";

    /// <summary>Payload: empty. The settings of the performance monitor changed (0.7.0); every component reads them again.</summary>
    public const string Performance = "fleeto_performance";

    /// <summary>
    /// Payload: user id. What a user may reach changed (0.6.0): its groups, the rules of one of them, or its role. Web closes
    /// the open pages of that user; the gateway ends the remote session connections web marked as revoked.
    /// </summary>
    public const string UserAccess = "fleeto_user_access";

    /// <summary>Payload: endpoint id. The gateway stored a storage scan of the endpoint; the workers evaluate folder growth (0.6.0).</summary>
    public const string StorageScans = "fleeto_storage_scans";

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
    /// Payload: PatchRequest id. Raised by a database trigger on insert, when a technician asks to read an endpoint from
    /// the patch management product again or to remove its agent (0.6.0), and by the workers when they finished it.
    /// </summary>
    public const string PatchRequests = "fleeto_patch_requests";

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

    /// <summary>
    /// Payload: AutomationTask id. A user changed a task or started it by hand, or the workers started a run of it (0.6.0); the
    /// workers look at its schedule again and the task page reloads its runs.
    /// </summary>
    public const string Tasks = "fleeto_tasks";
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

    /// <summary>Groups of users (0.6.0): created, changed (name, rules or members) and deleted.</summary>
    public const string UserGroupCreated = "user_group.created";
    public const string UserGroupUpdated = "user_group.updated";
    public const string UserGroupDeleted = "user_group.deleted";

    /// <summary>Roles (0.6.0): a custom role created, changed (name, description or permissions) and deleted.</summary>
    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleDeleted = "role.deleted";

    /// <summary>The groups of one user were set from Settings, Users (0.6.0).</summary>
    public const string UserGroupsChanged = "user.groups_changed";

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
    /// <summary>A technician asked an endpoint to scan its storage now (0.6.0).</summary>
    public const string StorageScanRequested = "storage.scan_requested";
    /// <summary>A user copied the storage summary of an endpoint as an image or downloaded its PDF report (0.7.0); never the paths.</summary>
    public const string StorageReportExported = "storage.report_exported";
    /// <summary>A user copied the process list of a CPU or memory usage result as an image (0.7.0); never the processes or users.</summary>
    public const string ProcessListExported = "check.process_list_exported";
    /// <summary>An admin copied or downloaded the instance health diagnostics (0.6.0).</summary>
    public const string InstanceDiagnosticsExported = "instance.diagnostics_exported";
    /// <summary>An admin changed the settings of the performance monitor (0.7.0); the entry holds the old and new values.</summary>
    public const string PerformanceSettingsChanged = "performance.settings_changed";
    /// <summary>An admin copied or downloaded the performance report (0.7.0).</summary>
    public const string PerformanceExported = "performance.exported";
    /// <summary>An admin started Diagnose of the performance monitor (0.7.0 step 2); the entry holds its end.</summary>
    public const string PerformanceDiagnoseStarted = "performance.diagnose_started";
    /// <summary>An admin stopped Diagnose before its end.</summary>
    public const string PerformanceDiagnoseStopped = "performance.diagnose_stopped";
    /// <summary>An admin put a marker on the timeline of the performance monitor; the entry holds its length, never its text.</summary>
    public const string PerformanceMarkerAdded = "performance.marker_added";
    /// <summary>An admin asked for the generic plan of a stored statement.</summary>
    public const string PerformancePlanned = "performance.planned";
    /// <summary>An admin ran ANALYZE on one table from the performance monitor.</summary>
    public const string PerformanceTableAnalyzed = "performance.table_analyzed";
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
    public const string TaskCreated = "task.created";
    public const string TaskUpdated = "task.updated";
    public const string TaskDeleted = "task.deleted";
    public const string TaskRunStarted = "task.run_started";
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

    /// <summary>A technician asked to remove the endpoint from the patch management product and uninstall its agent (0.6.0).</summary>
    public const string PatchAgentRemovalRequested = "patch_agent.removal_requested";

    /// <summary>The workers removed the endpoint from the product, or the product refused (0.6.0).</summary>
    public const string PatchAgentRemovalEnded = "patch_agent.removal_ended";

    /// <summary>An endpoint was deleted with "Also uninstall the Fleeto agent" (0.6.0); the gateway waits for it to connect.</summary>
    public const string AgentUninstallRequested = "agent.uninstall_requested";

    /// <summary>fleeto-signer signed the uninstall command of a deleted endpoint (0.6.0).</summary>
    public const string AgentUninstallSigned = "agent.uninstall_signed";

    /// <summary>The agent or the watchdog of a deleted endpoint accepted its uninstall command (0.6.0).</summary>
    public const string AgentUninstallAccepted = "agent.uninstall_accepted";

    /// <summary>An admin stopped waiting for a deleted endpoint to uninstall (0.6.0).</summary>
    public const string AgentUninstallDismissed = "agent.uninstall_dismissed";

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

    /// <summary>A library script was sent to run in a remote background terminal (0.6.0): the script, its version and hash, never its body.</summary>
    public const string RemoteSessionScript = "remote_session.script";
}
