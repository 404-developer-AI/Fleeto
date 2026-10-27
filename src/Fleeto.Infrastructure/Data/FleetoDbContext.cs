using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fleeto.Infrastructure.Data;

/// <summary>
/// The instance database. Client isolation is enforced here in two ways (ARCHITECTURE.md §2):
/// <list type="bullet">
/// <item>Global query filters on the ClientId that every client-owned table carries.</item>
/// <item>A write guard in SaveChanges that refuses rows outside the caller's client scope.</item>
/// </list>
/// Consistency of the denormalized ClientId is enforced by the database itself: composite foreign keys for
/// required ClientIds, constraint triggers for nullable ones (migration DatabaseRules).
/// <para>
/// Create one context per operation through <see cref="IFleetoDbContextFactory"/>; a Blazor circuit must never
/// share a context between concurrent events.
/// </para>
/// </summary>
public class FleetoDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    private readonly IClientScope _scope;

    public FleetoDbContext(DbContextOptions<FleetoDbContext> options, IClientScope? scope = null)
        : base(options)
    {
        _scope = scope ?? SystemClientScope.Instance;
    }

    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ClientTag> ClientTags => Set<ClientTag>();
    public DbSet<Endpoint> Endpoints => Set<Endpoint>();
    public DbSet<AgentCertificate> AgentCertificates => Set<AgentCertificate>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<InventorySnapshot> InventorySnapshots => Set<InventorySnapshot>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<MonitoringTemplate> MonitoringTemplates => Set<MonitoringTemplate>();
    public DbSet<CheckDefinition> CheckDefinitions => Set<CheckDefinition>();
    public DbSet<SiteMonitoringTemplate> SiteMonitoringTemplates => Set<SiteMonitoringTemplate>();
    public DbSet<SitePolicy> SitePolicies => Set<SitePolicy>();
    public DbSet<ClientPolicy> ClientPolicies => Set<ClientPolicy>();
    public DbSet<EndpointPolicy> EndpointPolicies => Set<EndpointPolicy>();
    public DbSet<ClientMonitoringTemplate> ClientMonitoringTemplates => Set<ClientMonitoringTemplate>();
    public DbSet<PatchPolicy> PatchPolicies => Set<PatchPolicy>();
    public DbSet<ClientPatchPolicy> ClientPatchPolicies => Set<ClientPatchPolicy>();
    public DbSet<SitePatchPolicy> SitePatchPolicies => Set<SitePatchPolicy>();
    public DbSet<EndpointPatchPolicy> EndpointPatchPolicies => Set<EndpointPatchPolicy>();
    public DbSet<MaintenanceWindowOccurrence> MaintenanceWindowOccurrences => Set<MaintenanceWindowOccurrence>();
    public DbSet<EndpointMonitoringTemplate> EndpointMonitoringTemplates => Set<EndpointMonitoringTemplate>();
    public DbSet<EndpointCheckOverride> EndpointCheckOverrides => Set<EndpointCheckOverride>();
    public DbSet<CheckRunRequest> CheckRunRequests => Set<CheckRunRequest>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<StorageScan> StorageScans => Set<StorageScan>();
    public DbSet<StorageScanRequest> StorageScanRequests => Set<StorageScanRequest>();
    public DbSet<Script> Scripts => Set<Script>();
    public DbSet<ScriptVersion> ScriptVersions => Set<ScriptVersion>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobOutputChunk> JobOutputChunks => Set<JobOutputChunk>();
    public DbSet<AutomationTask> AutomationTasks => Set<AutomationTask>();
    public DbSet<AutomationTaskStep> AutomationTaskSteps => Set<AutomationTaskStep>();
    public DbSet<ClientAutomationTask> ClientAutomationTasks => Set<ClientAutomationTask>();
    public DbSet<SiteAutomationTask> SiteAutomationTasks => Set<SiteAutomationTask>();
    public DbSet<EndpointAutomationTask> EndpointAutomationTasks => Set<EndpointAutomationTask>();
    public DbSet<MonitoringTemplateAutomationTask> MonitoringTemplateAutomationTasks => Set<MonitoringTemplateAutomationTask>();
    public DbSet<AutomationTaskState> AutomationTaskStates => Set<AutomationTaskState>();
    public DbSet<AutomationTaskRun> AutomationTaskRuns => Set<AutomationTaskRun>();
    public DbSet<AutomationTaskRunTarget> AutomationTaskRunTargets => Set<AutomationTaskRunTarget>();
    public DbSet<RemoteSession> RemoteSessions => Set<RemoteSession>();
    public DbSet<RemoteSessionParticipant> RemoteSessionParticipants => Set<RemoteSessionParticipant>();
    public DbSet<RemoteSessionAction> RemoteSessionActions => Set<RemoteSessionAction>();
    public DbSet<ClientTemplate> ClientTemplates => Set<ClientTemplate>();
    public DbSet<ClientTemplateSite> ClientTemplateSites => Set<ClientTemplateSite>();
    public DbSet<ClientTemplateSiteMonitoringTemplate> ClientTemplateSiteMonitoringTemplates => Set<ClientTemplateSiteMonitoringTemplate>();
    public DbSet<ClientTemplateMonitoringTemplate> ClientTemplateMonitoringTemplates => Set<ClientTemplateMonitoringTemplate>();
    public DbSet<CheckResult> CheckResults => Set<CheckResult>();
    public DbSet<IngestBatch> IngestBatches => Set<IngestBatch>();
    public DbSet<CheckState> CheckStates => Set<CheckState>();
    public DbSet<CheckResultHourly> CheckResultsHourly => Set<CheckResultHourly>();
    public DbSet<CheckResultDaily> CheckResultsDaily => Set<CheckResultDaily>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AlertActivity> AlertActivities => Set<AlertActivity>();
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();
    public DbSet<EndpointEvent> EndpointEvents => Set<EndpointEvent>();
    public DbSet<SigningRequest> SigningRequests => Set<SigningRequest>();
    public DbSet<EndpointConfig> EndpointConfigs => Set<EndpointConfig>();
    public DbSet<InstanceSigningKey> InstanceSigningKeys => Set<InstanceSigningKey>();
    public DbSet<CertificateAuthority> CertificateAuthorities => Set<CertificateAuthority>();
    public DbSet<ConfigChangeEvent> ConfigChangeEvents => Set<ConfigChangeEvent>();
    public DbSet<InstanceSettings> InstanceSettings => Set<InstanceSettings>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<DataKey> DataKeys => Set<DataKey>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<SetupToken> SetupTokens => Set<SetupToken>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<AgentRelease> AgentReleases => Set<AgentRelease>();
    public DbSet<EndpointComponentState> EndpointComponentStates => Set<EndpointComponentState>();
    public DbSet<ApiKeyClient> ApiKeyClients => Set<ApiKeyClient>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationChannelClient> NotificationChannelClients => Set<NotificationChannelClient>();
    public DbSet<OutboxEmail> OutboxEmails => Set<OutboxEmail>();
    public DbSet<OutboxWebhook> OutboxWebhooks => Set<OutboxWebhook>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<InstanceHealthSample> InstanceHealthSamples => Set<InstanceHealthSample>();
    public DbSet<InstanceHealthIssue> InstanceHealthIssues => Set<InstanceHealthIssue>();
    public DbSet<WorkerWatermark> WorkerWatermarks => Set<WorkerWatermark>();

    // The security monitor (0.7.0): instance-wide, no client.
    public DbSet<SecuritySignal> SecuritySignals => Set<SecuritySignal>();
    public DbSet<IpBlock> IpBlocks => Set<IpBlock>();
    public DbSet<IpAllow> IpAllows => Set<IpAllow>();
    public DbSet<UserSignInNetwork> UserSignInNetworks => Set<UserSignInNetwork>();
    public DbSet<SecurityIncident> SecurityIncidents => Set<SecurityIncident>();
    public DbSet<IpFeed> IpFeeds => Set<IpFeed>();
    public DbSet<IpFeedEntry> IpFeedEntries => Set<IpFeedEntry>();

    // Security advisories (0.8.0): instance-wide, no client.
    public DbSet<AdvisorySource> AdvisorySources => Set<AdvisorySource>();
    public DbSet<Advisory> Advisories => Set<Advisory>();
    public DbSet<AdvisoryHandling> AdvisoryHandlings => Set<AdvisoryHandling>();

    // Vulnerabilities on Linux endpoints (0.8.0): the feeds are instance-wide, the findings belong to an endpoint.
    public DbSet<VulnerabilityFeed> VulnerabilityFeeds => Set<VulnerabilityFeed>();
    public DbSet<VulnerabilityFeedRecord> VulnerabilityFeedRecords => Set<VulnerabilityFeedRecord>();
    public DbSet<VulnerabilityCve> VulnerabilityCves => Set<VulnerabilityCve>();
    public DbSet<EndpointVulnerability> EndpointVulnerabilities => Set<EndpointVulnerability>();
    public DbSet<EndpointVulnerabilitySummary> EndpointVulnerabilitySummaries => Set<EndpointVulnerabilitySummary>();
    public DbSet<VulnerabilityCveCount> VulnerabilityCveCounts => Set<VulnerabilityCveCount>();
    public DbSet<VulnerabilityExploit> VulnerabilityExploits => Set<VulnerabilityExploit>();

    // The performance monitor (0.7.0): instance-wide, no client, no personal data.
    public DbSet<PerfMinute> PerfMinutes => Set<PerfMinute>();
    public DbSet<PerfHour> PerfHours => Set<PerfHour>();
    public DbSet<PerfRuntimeMinute> PerfRuntimeMinutes => Set<PerfRuntimeMinute>();
    public DbSet<PerfHostMinute> PerfHostMinutes => Set<PerfHostMinute>();
    public DbSet<PerfEvent> PerfEvents => Set<PerfEvent>();
    public DbSet<PerfStatementSnapshot> PerfStatementSnapshots => Set<PerfStatementSnapshot>();
    public DbSet<PerfDatabaseSnapshot> PerfDatabaseSnapshots => Set<PerfDatabaseSnapshot>();
    public DbSet<PerfTableSnapshot> PerfTableSnapshots => Set<PerfTableSnapshot>();
    public DbSet<PerfMarker> PerfMarkers => Set<PerfMarker>();
    public DbSet<PerfFingerprint> PerfFingerprints => Set<PerfFingerprint>();
    public DbSet<PerfProbe> PerfProbes => Set<PerfProbe>();
    public DbSet<PerformanceSettingsRow> PerformanceSettings => Set<PerformanceSettingsRow>();
    public DbSet<Integration> Integrations => Set<Integration>();

    /// <summary>Authorization codes of a sign-in with Entra ID that wait for the workers (0.5.0).</summary>
    public DbSet<SignInExchange> SignInExchanges => Set<SignInExchange>();
    public DbSet<MicrosoftRequest> MicrosoftRequests => Set<MicrosoftRequest>();
    public DbSet<EndpointPatchState> EndpointPatchStates => Set<EndpointPatchState>();
    public DbSet<EndpointMissingUpdate> EndpointMissingUpdates => Set<EndpointMissingUpdate>();
    public DbSet<PatchDeployment> PatchDeployments => Set<PatchDeployment>();
    public DbSet<PatchDeploymentUpdate> PatchDeploymentUpdates => Set<PatchDeploymentUpdate>();
    public DbSet<PatchDeploymentTarget> PatchDeploymentTargets => Set<PatchDeploymentTarget>();
    public DbSet<PatchDeploymentStep> PatchDeploymentSteps => Set<PatchDeploymentStep>();
    public DbSet<PatchRequest> PatchRequests => Set<PatchRequest>();
    public DbSet<IntegrationMapping> IntegrationMappings => Set<IntegrationMapping>();
    public DbSet<Action1AgentInstallState> Action1AgentInstallStates => Set<Action1AgentInstallState>();
    public DbSet<AgentUninstall> AgentUninstalls => Set<AgentUninstall>();
    public DbSet<IntegrationSiteGroup> IntegrationSiteGroups => Set<IntegrationSiteGroup>();
    public DbSet<IntegrationAutomation> IntegrationAutomations => Set<IntegrationAutomation>();
    public DbSet<IntegrationOperation> IntegrationOperations => Set<IntegrationOperation>();

    /// <summary>Groups of users and what they give access to (0.6.0).</summary>
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<UserGroupMember> UserGroupMembers => Set<UserGroupMember>();
    public DbSet<UserGroupRule> UserGroupRules => Set<UserGroupRule>();

    /// <summary>The permissions of every role (0.6.0).</summary>
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    // Evaluated per query by EF Core (context members become query parameters).
    private bool ScopeAllClients => _scope.AllClients;
    private Guid[] ScopeClientIds => _scope.ClientIds as Guid[] ?? _scope.ClientIds.ToArray();
    private Guid[] ScopeFullClientIds => _scope.FullClientIds as Guid[] ?? _scope.FullClientIds.ToArray();
    private Guid[] ScopeSiteIds => _scope.SiteIds as Guid[] ?? _scope.SiteIds.ToArray();
    private Guid[] ScopeFullSiteIds => _scope.FullSiteIds as Guid[] ?? _scope.FullSiteIds.ToArray();
    private Guid[] ScopeEndpointIds => _scope.EndpointIds as Guid[] ?? _scope.EndpointIds.ToArray();
    private string[] ScopeEndpointIdTexts => _scope.EndpointIds.Select(id => id.ToString()).ToArray();

    /// <summary>
    /// Set by a service that checked that a caller limited by its groups may change an instance-wide template, policy or
    /// script (0.6.0: it has access to everything it is used on). Without it such a caller cannot change instance-wide rows.
    /// </summary>
    private bool _instanceWideWritesAllowed;

    /// <summary>See <see cref="_instanceWideWritesAllowed"/>. Call only after that check.</summary>
    public void AllowInstanceWideWrites() => _instanceWideWritesAllowed = true;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.DisplayName).HasMaxLength(200);
            // Sign-in with Entra ID (0.5.0): one Entra account belongs to at most one user of this instance.
            entity.Property(u => u.EntraTenantId).HasMaxLength(255);
            entity.Property(u => u.EntraAccount).HasMaxLength(320);
            entity.HasIndex(u => u.EntraObjectId).IsUnique();
            entity.Ignore(u => u.IsLinkedToEntra);
        });

        // Roles with their permissions (0.6.0): instance-wide like the groups.
        builder.Entity<ApplicationRole>(entity =>
        {
            entity.Property(r => r.Description).HasMaxLength(Core.Domain.RoleRules.MaxDescriptionLength);
        });

        builder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(p => new { p.RoleId, p.Permission });
            entity.Property(p => p.Permission).HasMaxLength(Core.Domain.RoleRules.MaxPermissionLength);
            entity.HasOne<ApplicationRole>().WithMany().HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
            // The signer asks which users hold a permission.
            entity.HasIndex(p => p.Permission);
        });

        // Groups of users (0.6.0): instance-wide, managed by admins, never filtered by the scope they define.
        builder.Entity<UserGroup>(entity =>
        {
            entity.Property(g => g.Name).HasMaxLength(Core.Domain.UserAccessRules.MaxNameLength);
            entity.Property(g => g.NormalizedName).HasMaxLength(Core.Domain.UserAccessRules.MaxNameLength);
            entity.Property(g => g.Description).HasMaxLength(Core.Domain.UserAccessRules.MaxDescriptionLength);
            entity.HasIndex(g => g.NormalizedName).IsUnique();
            // At most one group is the default one the update put every user in.
            entity.HasIndex(g => g.IsDefault).IsUnique().HasFilter("\"IsDefault\"");
            entity.HasMany(g => g.Members).WithOne().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(g => g.Rules).WithOne().HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserGroupMember>(entity =>
        {
            entity.HasKey(m => new { m.GroupId, m.UserId });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(m => m.UserId);
        });

        builder.Entity<UserGroupRule>(entity =>
        {
            entity.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
            // A rule goes with its target: a deleted client, site or endpoint is no longer named anywhere.
            entity.HasOne<Client>().WithMany().HasForeignKey(r => r.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Site>().WithMany().HasForeignKey(r => r.SiteId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Endpoint>().WithMany().HasForeignKey(r => r.EndpointId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(r => new { r.GroupId, r.Kind, r.ClientId, r.SiteId, r.EndpointId }).IsUnique().AreNullsDistinct(false);
            entity.HasIndex(r => r.ClientId);
            entity.HasIndex(r => r.SiteId);
            entity.HasIndex(r => r.EndpointId);
            entity.ToTable(t => t.HasCheckConstraint("CK_UserGroupRules_Target", "num_nonnulls(\"ClientId\", \"SiteId\", \"EndpointId\") = 1"));
        });

        builder.Entity<Client>(entity =>
        {
            entity.Property(c => c.Code).HasMaxLength(16);
            entity.Property(c => c.Name).HasMaxLength(200);
            entity.HasIndex(c => c.Code).IsUnique();
            MaintenanceColumns(entity);
            entity.HasMany(c => c.Sites).WithOne(s => s.Client).HasForeignKey(s => s.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ClientTemplate>().WithMany().HasForeignKey(c => c.ClientTemplateId).OnDelete(DeleteBehavior.SetNull);
            entity.HasQueryFilter(c => ScopeAllClients || ScopeClientIds.Contains(c.Id));
        });

        // Tags (0.6.0): instance-wide names and colors; the link to a client is client-owned.
        builder.Entity<Tag>(entity =>
        {
            entity.Property(t => t.Name).HasMaxLength(Core.Domain.TagRules.MaxLength);
            entity.Property(t => t.NormalizedName).HasMaxLength(Core.Domain.TagRules.MaxLength);
            entity.Property(t => t.Color).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(t => t.NormalizedName).IsUnique();
        });

        builder.Entity<ClientTag>(entity =>
        {
            entity.HasKey(l => new { l.ClientId, l.TagId });
            entity.HasOne<Client>().WithMany().HasForeignKey(l => l.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.Tag).WithMany().HasForeignKey(l => l.TagId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.TagId);
            ClientOwned(entity);
        });

        builder.Entity<Site>(entity =>
        {
            entity.Property(s => s.Name).HasMaxLength(100);
            entity.Property(s => s.Description).HasMaxLength(1000);
            entity.HasAlternateKey(s => new { s.Id, s.ClientId });
            entity.HasIndex(s => new { s.ClientId, s.Name }).IsUnique();
            MaintenanceColumns(entity);
            entity.HasOne<ClientTemplateSite>().WithMany().HasForeignKey(s => s.ClientTemplateSiteId).OnDelete(DeleteBehavior.SetNull);
            SiteOwned(entity, "Id");
        });

        builder.Entity<Endpoint>(entity =>
        {
            entity.Property(e => e.Hostname).HasMaxLength(255);
            entity.Property(e => e.OsPlatform).HasMaxLength(20);
            entity.Property(e => e.OsName).HasMaxLength(200);
            entity.Property(e => e.OsVersion).HasMaxLength(100);
            entity.Property(e => e.Architecture).HasMaxLength(20);
            entity.Property(e => e.AgentVersion).HasMaxLength(50);
            entity.Property(e => e.DetectedClass).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.ClassOverride).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Tier).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.PublicIpAddress).HasMaxLength(64);
            entity.Property(e => e.SignedInUsersJson).HasColumnType("jsonb");
            entity.Property(e => e.WatchdogVersion).HasMaxLength(50).HasDefaultValue(string.Empty);
            entity.Ignore(e => e.EffectiveClass);
            MaintenanceColumns(entity);
            entity.HasAlternateKey(e => new { e.Id, e.ClientId });
            entity.HasOne(e => e.Site).WithMany(s => s.Endpoints)
                .HasForeignKey(e => new { e.SiteId, e.ClientId })
                .HasPrincipalKey(s => new { s.Id, s.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Inventory).WithOne()
                .HasForeignKey<InventorySnapshot>(i => new { i.EndpointId, i.ClientId })
                .HasPrincipalKey<Endpoint>(e => new { e.Id, e.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.ClientId, e.SiteId });
            entity.HasIndex(e => e.Tier);
            entity.HasIndex(e => e.IsOnline);
            entity.HasIndex(e => e.Hostname);
            EndpointOwned(entity, "Id");
        });

        builder.Entity<AgentCertificate>(entity =>
        {
            entity.Property(c => c.Fingerprint).HasMaxLength(64);
            entity.Property(c => c.PublicKeyFingerprint).HasMaxLength(64);
            entity.Property(c => c.SerialNumber).HasMaxLength(64);
            entity.Property(c => c.RevokedReason).HasMaxLength(500);
            entity.Property(c => c.Role).HasConversion<string>().HasMaxLength(20).HasDefaultValue(AgentComponent.Agent).HasSentinel((AgentComponent)(-1));
            entity.HasIndex(c => c.Fingerprint).IsUnique();
            entity.HasIndex(c => c.EndpointId);
            EndpointChild(entity, c => new { c.EndpointId, c.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<EnrollmentToken>(entity =>
        {
            entity.Property(t => t.Name).HasMaxLength(100);
            entity.Property(t => t.TokenHash).HasMaxLength(64);
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasOne<Site>().WithMany()
                .HasForeignKey(t => new { t.SiteId, t.ClientId })
                .HasPrincipalKey(s => new { s.Id, s.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Endpoint>().WithMany()
                .HasForeignKey(t => new { t.EndpointId, t.ClientId })
                .HasPrincipalKey(e => new { e.Id, e.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            SiteOwned(entity);
        });

        builder.Entity<InventorySnapshot>(entity =>
        {
            entity.HasKey(i => i.EndpointId);
            entity.Property(i => i.Hash).HasMaxLength(64);
            entity.Property(i => i.Manufacturer).HasMaxLength(200);
            entity.Property(i => i.Model).HasMaxLength(200);
            entity.Property(i => i.SerialNumber).HasMaxLength(200);
            entity.Property(i => i.CpuModel).HasMaxLength(200);
            entity.Property(i => i.Domain).HasMaxLength(255);
            entity.Property(i => i.LoggedOnUser).HasMaxLength(255);
            entity.Property(i => i.Action1AgentId).HasMaxLength(64).HasDefaultValue(string.Empty);
            entity.Property(i => i.Desktop).HasMaxLength(20).HasDefaultValue(string.Empty);
            entity.Property(i => i.WindowsUpdate).HasMaxLength(20).HasDefaultValue(string.Empty);
            // The patch poller looks an endpoint up by the id its Action1 agent reports (0.4.0).
            entity.HasIndex(i => i.Action1AgentId).HasFilter("\"Action1AgentId\" <> ''");
            entity.Property(i => i.DisksJson).HasColumnType("jsonb");
            entity.Property(i => i.NetworkInterfacesJson).HasColumnType("jsonb");
            entity.Property(i => i.SoftwareJson).HasColumnType("jsonb");
            entity.Property(i => i.PackageInfoJson).HasColumnType("jsonb");
            entity.Property(i => i.ServicesJson).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            EndpointOwned(entity);
        });

        builder.Entity<MaintenanceWindowOccurrence>(entity =>
        {
            entity.HasKey(o => new { o.PolicyId, o.WindowIndex, o.StartsAt });
            entity.Property(o => o.AppliesTo).HasConversion<string>().HasMaxLength(20);
            entity.Property(o => o.Name).HasMaxLength(100);
            entity.HasIndex(o => new { o.StartsAt, o.EndsAt });
            entity.HasOne<Policy>().WithMany().HasForeignKey(o => o.PolicyId).OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(t => t.HasCheckConstraint("CK_MaintenanceWindowOccurrences_Span", "\"EndsAt\" > \"StartsAt\""));
        });

        builder.Entity<Policy>(entity =>
        {
            entity.Property(p => p.Name).HasMaxLength(100);
            entity.Property(p => p.Description).HasMaxLength(1000);
            entity.Property(p => p.OfflineAlertSeverity).HasConversion<string>().HasMaxLength(20);
            entity.Property(p => p.MaintenanceWindowsJson).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            entity.Property(p => p.UpdateRing).HasConversion<string>().HasMaxLength(20).HasDefaultValue(UpdateRing.Standard).HasSentinel((UpdateRing)(-1));
            entity.Property(p => p.MaxOutputBytes).HasDefaultValue(ScriptRules.DefaultMaxOutputBytes).HasSentinel(0L);
            // Remote session settings (0.3.0). Sentinels outside the valid range, so a value equal to the default is still written.
            // The booleans have no database default in the model (a default of true would swallow false); migration RemoteSessions
            // fills existing rows with the defaults.
            entity.Property(p => p.RemoteConsentTimeoutSeconds).HasDefaultValue(RemoteSessionRules.DefaultConsentTimeoutSeconds).HasSentinel(0);
            entity.Property(p => p.RemoteIdleTimeoutMinutes).HasDefaultValue(RemoteSessionRules.DefaultIdleTimeoutMinutes).HasSentinel(0);
            entity.Property(p => p.RemoteMaxFileBytes).HasDefaultValue(RemoteSessionRules.DefaultMaxFileBytes).HasSentinel(0L);
            // Storage scans (0.6.0): 0 is a valid value (no scheduled scans), so the sentinel is -1.
            entity.Property(p => p.StorageScanIntervalHours).HasDefaultValue(StorageRules.DefaultScanIntervalHours).HasSentinel(-1);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Policies_StorageScanIntervalHours",
                    $"\"StorageScanIntervalHours\" IN ({string.Join(", ", StorageRules.ScanIntervalChoices)})");
                table.HasCheckConstraint(
                    "CK_Policies_MaxOutputBytes",
                    $"\"MaxOutputBytes\" BETWEEN {ScriptRules.MinOutputBytes} AND {ScriptRules.MaxOutputBytes}");
                table.HasCheckConstraint("CK_Policies_RemoteConsentTimeoutSeconds",
                    $"\"RemoteConsentTimeoutSeconds\" BETWEEN {RemoteSessionRules.MinConsentTimeoutSeconds} AND {RemoteSessionRules.MaxConsentTimeoutSeconds}");
                table.HasCheckConstraint("CK_Policies_RemoteIdleTimeoutMinutes",
                    $"\"RemoteIdleTimeoutMinutes\" BETWEEN {RemoteSessionRules.MinIdleTimeoutMinutes} AND {RemoteSessionRules.MaxIdleTimeoutMinutes}");
                table.HasCheckConstraint("CK_Policies_RemoteMaxFileBytes",
                    $"\"RemoteMaxFileBytes\" BETWEEN {RemoteSessionRules.MinMaxFileBytes} AND {RemoteSessionRules.MaxMaxFileBytes}");
            });
            entity.HasIndex(p => new { p.ClientId, p.Name }).IsUnique().AreNullsDistinct(false);
            entity.HasIndex(p => p.IsDefault).IsUnique().HasFilter("\"IsDefault\"");
            entity.Property(p => p.AppliesTo).HasConversion<string>().HasMaxLength(20).HasDefaultValue(CheckAppliesTo.All).HasSentinel(CheckAppliesTo.All);
            // The default policy is for every endpoint: an endpoint must always have a policy.
            entity.ToTable(t => t.HasCheckConstraint("CK_Policies_DefaultForAll", "NOT \"IsDefault\" OR \"AppliesTo\" = 'All'"));
            // A client-specific policy is deleted with its client.
            entity.HasOne<Client>().WithMany().HasForeignKey(p => p.ClientId).OnDelete(DeleteBehavior.Cascade);
            GlobalOrClientOwned(entity);
        });

        builder.Entity<MonitoringTemplate>(entity =>
        {
            entity.Property(t => t.Name).HasMaxLength(100);
            entity.Property(t => t.Description).HasMaxLength(1000);
            entity.Property(t => t.AppliesTo).HasConversion<string>().HasMaxLength(20).HasDefaultValue(CheckAppliesTo.All).HasSentinel(CheckAppliesTo.All);
            // Templates saved before 0.6.0 can be linked everywhere, as they could then.
            entity.Property(t => t.ForClients).HasDefaultValue(true).HasSentinel(true);
            entity.Property(t => t.ForSites).HasDefaultValue(true).HasSentinel(true);
            entity.Property(t => t.ForEndpoints).HasDefaultValue(true).HasSentinel(true);
            entity.ToTable(t => t.HasCheckConstraint("CK_MonitoringTemplates_Levels", "\"ForClients\" OR \"ForSites\" OR \"ForEndpoints\""));
            entity.HasIndex(t => new { t.ClientId, t.Name }).IsUnique().AreNullsDistinct(false);
            entity.HasMany(t => t.Checks).WithOne(c => c.MonitoringTemplate).HasForeignKey(c => c.MonitoringTemplateId).OnDelete(DeleteBehavior.Cascade);
            // A client-specific monitoring template is deleted with its client.
            entity.HasOne<Client>().WithMany().HasForeignKey(t => t.ClientId).OnDelete(DeleteBehavior.Cascade);
            GlobalOrClientOwned(entity);
        });

        builder.Entity<CheckDefinition>(entity =>
        {
            entity.Property(c => c.Name).HasMaxLength(100);
            entity.Property(c => c.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(c => c.AppliesTo).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.OperatingSystem).HasConversion<string>().HasMaxLength(20)
                .HasDefaultValue(CheckOperatingSystem.All).HasSentinel(CheckOperatingSystem.All);
            entity.Property(c => c.ParametersJson).HasColumnType("jsonb");
            // An endpoint-only check: the composite key keeps its ClientId equal to the endpoint's. Template checks are
            // kept consistent by trigger TR_CheckDefinitions_Client.
            entity.HasOne<Endpoint>().WithMany()
                .HasForeignKey(c => new { c.EndpointId, c.ClientId })
                .HasPrincipalKey(e => new { e.Id, e.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_CheckDefinitions_Owner", "num_nonnulls(\"MonitoringTemplateId\", \"EndpointId\") = 1");
                t.HasCheckConstraint("CK_CheckDefinitions_EndpointClient", "\"EndpointId\" IS NULL OR \"ClientId\" IS NOT NULL");
            });
            CheckDefinitionOwned(entity);
        });

        builder.Entity<SiteMonitoringTemplate>(entity =>
        {
            entity.HasKey(l => new { l.SiteId, l.MonitoringTemplateId });
            entity.Property(l => l.Source).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<Site>().WithMany(s => s.MonitoringTemplates)
                .HasForeignKey(l => new { l.SiteId, l.ClientId })
                .HasPrincipalKey(s => new { s.Id, s.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.MonitoringTemplate).WithMany().HasForeignKey(l => l.MonitoringTemplateId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.MonitoringTemplateId);
            SiteOwned(entity);
        });

        builder.Entity<EndpointMonitoringTemplate>(entity =>
        {
            entity.HasKey(l => new { l.EndpointId, l.MonitoringTemplateId });
            EndpointChild(entity, l => new { l.EndpointId, l.ClientId });
            entity.HasOne(l => l.MonitoringTemplate).WithMany().HasForeignKey(l => l.MonitoringTemplateId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.MonitoringTemplateId);
            EndpointOwned(entity);
        });

        builder.Entity<EndpointCheckOverride>(entity =>
        {
            entity.HasKey(o => new { o.EndpointId, o.CheckDefinitionId });
            entity.Ignore(o => o.IsEmpty);
            EndpointChild(entity, o => new { o.EndpointId, o.ClientId });
            entity.HasOne<CheckDefinition>().WithMany().HasForeignKey(o => o.CheckDefinitionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(o => o.CheckDefinitionId);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EndpointCheckOverrides_Interval",
                    "\"IntervalSeconds\" IS NULL OR \"IntervalSeconds\" BETWEEN 10 AND 2678400");
                t.HasCheckConstraint("CK_EndpointCheckOverrides_Failures",
                    "\"FailuresBeforeAlert\" IS NULL OR \"FailuresBeforeAlert\" BETWEEN 1 AND 100");
            });
            EndpointOwned(entity);
        });

        builder.Entity<SitePolicy>(entity =>
        {
            entity.HasKey(l => new { l.SiteId, l.AppliesTo });
            entity.Property(l => l.Source).HasConversion<string>().HasMaxLength(20);
            ClassSlot(entity);
            entity.HasOne<Site>().WithMany(s => s.Policies)
                .HasForeignKey(l => new { l.SiteId, l.ClientId })
                .HasPrincipalKey(s => new { s.Id, s.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.Policy).WithMany().HasForeignKey(l => l.PolicyId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.PolicyId);
            SiteOwned(entity);
        });

        // Links on client and endpoint level (0.6.0): the same client as their parent through the composite key, and a
        // policy of another client is refused by trigger (migration LinksOnEveryLevel).
        builder.Entity<ClientPolicy>(entity =>
        {
            entity.HasKey(l => new { l.ClientId, l.AppliesTo });
            entity.Property(l => l.Source).HasConversion<string>().HasMaxLength(20);
            ClassSlot(entity);
            entity.HasOne<Client>().WithMany(c => c.Policies).HasForeignKey(l => l.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.Policy).WithMany().HasForeignKey(l => l.PolicyId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.PolicyId);
            ClientOwned(entity);
        });

        builder.Entity<EndpointPolicy>(entity =>
        {
            entity.HasKey(l => l.EndpointId);
            EndpointChild(entity, l => new { l.EndpointId, l.ClientId });
            entity.HasOne(l => l.Policy).WithMany().HasForeignKey(l => l.PolicyId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.PolicyId);
            EndpointOwned(entity);
        });

        builder.Entity<ClientMonitoringTemplate>(entity =>
        {
            entity.HasKey(l => new { l.ClientId, l.MonitoringTemplateId });
            entity.Property(l => l.Source).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<Client>().WithMany(c => c.MonitoringTemplates).HasForeignKey(l => l.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.MonitoringTemplate).WithMany().HasForeignKey(l => l.MonitoringTemplateId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.MonitoringTemplateId);
            ClientOwned(entity);
        });

        builder.Entity<PatchPolicy>(entity =>
        {
            entity.Property(p => p.Name).HasMaxLength(100);
            entity.Property(p => p.Description).HasMaxLength(1000);
            entity.Property(p => p.ScheduleKind).HasConversion<string>().HasMaxLength(20);
            entity.Property(p => p.MonthWeekday).HasConversion<string>().HasMaxLength(10);
            entity.Property(p => p.Scope).HasConversion<string>().HasMaxLength(20);
            entity.Property(p => p.AppliesTo).HasConversion<string>().HasMaxLength(20).HasDefaultValue(CheckAppliesTo.All).HasSentinel(CheckAppliesTo.All);
            entity.Property(p => p.RebootMessageText).HasMaxLength(PatchPolicyRules.MaxRebootMessageLength);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_PatchPolicies_WeekDays", "\"WeekDays\" BETWEEN 0 AND 127");
                table.HasCheckConstraint("CK_PatchPolicies_MonthDay", "\"MonthDay\" BETWEEN 1 AND 31");
                table.HasCheckConstraint("CK_PatchPolicies_MonthWeek", "\"MonthWeek\" BETWEEN 1 AND 4");
                table.HasCheckConstraint("CK_PatchPolicies_StartMinute", "\"StartMinute\" BETWEEN 0 AND 1439");
                table.HasCheckConstraint("CK_PatchPolicies_InstallDelayDays",
                    $"\"InstallDelayDays\" BETWEEN 0 AND {PatchPolicyRules.MaxInstallDelayDays}");
                table.HasCheckConstraint("CK_PatchPolicies_RebootTimeoutMinutes",
                    $"\"RebootTimeoutMinutes\" BETWEEN {PatchPolicyRules.MinRebootTimeoutMinutes} AND {PatchPolicyRules.MaxRebootTimeoutMinutes}");
                table.HasCheckConstraint("CK_PatchPolicies_RetryHours",
                    $"\"RetryHours\" BETWEEN {PatchPolicyRules.MinRetryHours} AND {PatchPolicyRules.MaxRetryHours}");
            });
            entity.HasIndex(p => new { p.ClientId, p.Name }).IsUnique().AreNullsDistinct(false);
            // A client-specific patch policy is deleted with its client.
            entity.HasOne<Client>().WithMany().HasForeignKey(p => p.ClientId).OnDelete(DeleteBehavior.Cascade);
            GlobalOrClientOwned(entity);
        });

        builder.Entity<ClientPatchPolicy>(entity =>
        {
            entity.HasKey(l => new { l.ClientId, l.AppliesTo });
            entity.Property(l => l.Source).HasConversion<string>().HasMaxLength(20);
            ClassSlot(entity);
            entity.HasOne<Client>().WithMany(c => c.PatchPolicies).HasForeignKey(l => l.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.PatchPolicy).WithMany().HasForeignKey(l => l.PatchPolicyId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.PatchPolicyId);
            ClientOwned(entity);
        });

        builder.Entity<SitePatchPolicy>(entity =>
        {
            entity.HasKey(l => new { l.SiteId, l.AppliesTo });
            entity.Property(l => l.Source).HasConversion<string>().HasMaxLength(20);
            ClassSlot(entity);
            entity.HasOne<Site>().WithMany(s => s.PatchPolicies)
                .HasForeignKey(l => new { l.SiteId, l.ClientId })
                .HasPrincipalKey(s => new { s.Id, s.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.PatchPolicy).WithMany().HasForeignKey(l => l.PatchPolicyId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.PatchPolicyId);
            SiteOwned(entity);
        });

        builder.Entity<EndpointPatchPolicy>(entity =>
        {
            entity.HasKey(l => l.EndpointId);
            EndpointChild(entity, l => new { l.EndpointId, l.ClientId });
            entity.HasOne(l => l.PatchPolicy).WithMany().HasForeignKey(l => l.PatchPolicyId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.PatchPolicyId);
            EndpointOwned(entity);
        });

        builder.Entity<ClientTemplate>(entity =>
        {
            entity.Property(t => t.Name).HasMaxLength(100);
            entity.Property(t => t.Description).HasMaxLength(1000);
            entity.HasIndex(t => t.Name).IsUnique();
            entity.HasMany(t => t.Sites).WithOne().HasForeignKey(s => s.ClientTemplateId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Policy>().WithMany().HasForeignKey(t => t.PolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Policy>().WithMany().HasForeignKey(t => t.ServerPolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Policy>().WithMany().HasForeignKey(t => t.WorkstationPolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<PatchPolicy>().WithMany().HasForeignKey(t => t.PatchPolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<PatchPolicy>().WithMany().HasForeignKey(t => t.ServerPatchPolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<PatchPolicy>().WithMany().HasForeignKey(t => t.WorkstationPatchPolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(t => t.MonitoringTemplates).WithOne().HasForeignKey(l => l.ClientTemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ClientTemplateMonitoringTemplate>(entity =>
        {
            entity.HasKey(l => new { l.ClientTemplateId, l.MonitoringTemplateId });
            entity.HasOne<MonitoringTemplate>().WithMany().HasForeignKey(l => l.MonitoringTemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ClientTemplateSite>(entity =>
        {
            entity.Property(s => s.Name).HasMaxLength(100);
            entity.Property(s => s.Description).HasMaxLength(1000);
            entity.HasIndex(s => new { s.ClientTemplateId, s.Name }).IsUnique();
            entity.HasOne<Policy>().WithMany().HasForeignKey(s => s.PolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Policy>().WithMany().HasForeignKey(s => s.ServerPolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Policy>().WithMany().HasForeignKey(s => s.WorkstationPolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<PatchPolicy>().WithMany().HasForeignKey(s => s.PatchPolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<PatchPolicy>().WithMany().HasForeignKey(s => s.ServerPatchPolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<PatchPolicy>().WithMany().HasForeignKey(s => s.WorkstationPatchPolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(s => s.MonitoringTemplates).WithOne().HasForeignKey(l => l.ClientTemplateSiteId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ClientTemplateSiteMonitoringTemplate>(entity =>
        {
            entity.HasKey(l => new { l.ClientTemplateSiteId, l.MonitoringTemplateId });
            entity.HasOne<MonitoringTemplate>().WithMany().HasForeignKey(l => l.MonitoringTemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CheckResult>(entity =>
        {
            // Hypertable-friendly key: every unique constraint on a hypertable must include the time column.
            // No foreign keys: ingest speed and compressed chunks. The gateway takes ClientId from the endpoint row,
            // and the workers purge results of deleted endpoints (documented exception in ARCHITECTURE.md §2).
            entity.HasKey(r => new { r.Time, r.Id });
            entity.Property(r => r.Id).UseIdentityAlwaysColumn();
            entity.Property(r => r.Target).HasMaxLength(256);
            entity.Property(r => r.Detail).HasMaxLength(1000);
            entity.Property(r => r.Error).HasMaxLength(1000);
            entity.Property(r => r.ProcessesJson).HasColumnType("jsonb");
            entity.HasIndex(r => new { r.EndpointId, r.CheckDefinitionId, r.Time });
            // The few results with a process list, for the check history (0.6.0).
            entity.HasIndex(r => new { r.EndpointId, r.CheckDefinitionId, r.Time }, "IX_CheckResults_ProcessLists")
                .HasFilter("\"ProcessesJson\" IS NOT NULL");
            // Per-endpoint evaluation cursor in the workers: results with Id > cursor for one endpoint.
            entity.HasIndex(r => new { r.EndpointId, r.Id });
            entity.HasIndex(r => r.Id);
            EndpointOwned(entity);
        });

        builder.Entity<IngestBatch>(entity =>
        {
            entity.HasKey(b => new { b.EndpointId, b.Sequence });
            entity.HasIndex(b => b.ReceivedAt);
            EndpointChild(entity, b => new { b.EndpointId, b.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<CheckState>(entity =>
        {
            entity.HasKey(s => new { s.EndpointId, s.CheckDefinitionId, s.Target });
            entity.Property(s => s.Target).HasMaxLength(256);
            entity.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.Detail).HasMaxLength(1000);
            entity.Property(s => s.Error).HasMaxLength(1000);
            entity.Ignore(s => s.RerunRequested);
            entity.HasOne<CheckDefinition>().WithMany().HasForeignKey(s => s.CheckDefinitionId).OnDelete(DeleteBehavior.Cascade);
            EndpointChild(entity, s => new { s.EndpointId, s.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<CheckResultHourly>(entity =>
        {
            entity.ToTable("CheckResultsHourly");
            Rollup(entity);
            EndpointChild(entity, r => new { r.EndpointId, r.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<CheckResultDaily>(entity =>
        {
            entity.ToTable("CheckResultsDaily");
            Rollup(entity);
            EndpointChild(entity, r => new { r.EndpointId, r.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<CheckRunRequest>(entity =>
        {
            entity.Property(r => r.RequestedByName).HasMaxLength(200);
            entity.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(30);
            EndpointChild(entity, r => new { r.EndpointId, r.ClientId });
            entity.HasOne<CheckDefinition>().WithMany().HasForeignKey(r => r.CheckDefinitionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(r => new { r.EndpointId, r.RequestedAt });
            entity.HasIndex(r => new { r.RequestedByUserId, r.RequestedAt });
            entity.HasIndex(r => r.RequestedAt);
            entity.HasIndex(r => r.ExpiresAt).HasFilter("\"DeliveredAt\" IS NULL AND \"Outcome\" IS NULL").HasDatabaseName("IX_CheckRunRequests_Pending");
            EndpointOwned(entity);
        });

        builder.Entity<StorageScan>(entity =>
        {
            entity.Property(s => s.Volume).HasMaxLength(StorageRules.MaxVolumeLength);
            entity.Property(s => s.Filesystem).HasMaxLength(50);
            entity.Property(s => s.Method).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.Error).HasMaxLength(1000);
            entity.Property(s => s.FoldersJson).HasColumnType("jsonb");
            entity.Property(s => s.FilesJson).HasColumnType("jsonb");
            EndpointChild(entity, s => new { s.EndpointId, s.ClientId });
            // A report sent twice is stored once.
            entity.HasIndex(s => new { s.EndpointId, s.AgentScanId }).IsUnique();
            // The latest scans of a volume, and the history of a folder.
            entity.HasIndex(s => new { s.EndpointId, s.Volume, s.ReceivedAt });
            entity.HasIndex(s => s.ReceivedAt);
            EndpointOwned(entity);
        });

        builder.Entity<StorageScanRequest>(entity =>
        {
            entity.Property(r => r.Reason).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.RequestedByName).HasMaxLength(200);
            EndpointChild(entity, r => new { r.EndpointId, r.ClientId });
            entity.HasIndex(r => new { r.EndpointId, r.RequestedAt });
            entity.HasIndex(r => r.RequestedAt);
            entity.HasIndex(r => r.ExpiresAt).HasFilter("\"DeliveredAt\" IS NULL").HasDatabaseName("IX_StorageScanRequests_Pending");
            EndpointOwned(entity);
        });

        builder.Entity<Note>(entity =>
        {
            entity.Property(n => n.AuthorName).HasMaxLength(200);
            entity.Property(n => n.Body).HasMaxLength(Note.MaxBodyLength);
            EndpointChild(entity, n => new { n.EndpointId, n.ClientId });
            // Newest first per endpoint, keyset on (CreatedAt, Id).
            entity.HasIndex(n => new { n.EndpointId, n.CreatedAt, n.Id }).IsDescending(false, true, true);
            entity.ToTable(t => t.HasCheckConstraint("CK_Notes_Body", "char_length(\"Body\") BETWEEN 1 AND 20000"));
            EndpointOwned(entity);
        });

        builder.Entity<Script>(entity =>
        {
            entity.Property(s => s.Name).HasMaxLength(100);
            entity.Property(s => s.Description).HasMaxLength(1000);
            entity.Property(s => s.Language).HasConversion<string>().HasMaxLength(20);
            entity.Ignore(s => s.ExitCodes);
            // A default in the database, so the build before exit code rules still inserts scripts (expand/contract).
            entity.Property(s => s.SuccessExitCodes).HasDefaultValueSql("'{}'");
            entity.Property(s => s.InfoExitCodes).HasDefaultValueSql("'{}'");
            entity.Property(s => s.WarningExitCodes).HasDefaultValueSql("'{}'");
            entity.Property(s => s.ErrorExitCodes).HasDefaultValueSql("'{}'");
            // What the exit codes mean (0.6.0): a code in at most one list, at most 100 codes together.
            entity.ToTable(t => t.HasCheckConstraint("CK_Scripts_ExitCodes",
                "NOT (\"SuccessExitCodes\" && \"InfoExitCodes\") AND NOT (\"SuccessExitCodes\" && \"WarningExitCodes\") AND " +
                "NOT (\"SuccessExitCodes\" && \"ErrorExitCodes\") AND NOT (\"InfoExitCodes\" && \"WarningExitCodes\") AND " +
                "NOT (\"InfoExitCodes\" && \"ErrorExitCodes\") AND NOT (\"WarningExitCodes\" && \"ErrorExitCodes\") AND " +
                "cardinality(\"SuccessExitCodes\") + cardinality(\"InfoExitCodes\") + cardinality(\"WarningExitCodes\") + " +
                "cardinality(\"ErrorExitCodes\") <= " + ScriptExitCodes.MaxCodes));
            entity.HasIndex(s => new { s.ClientId, s.Name }).IsUnique().AreNullsDistinct(false);
            entity.HasMany(s => s.Versions).WithOne().HasForeignKey(v => v.ScriptId).OnDelete(DeleteBehavior.Cascade);
            // A client-specific script is deleted with its client.
            entity.HasOne<Client>().WithMany().HasForeignKey(s => s.ClientId).OnDelete(DeleteBehavior.Cascade);
            GlobalOrClientOwned(entity);
        });

        builder.Entity<ScriptVersion>(entity =>
        {
            entity.Property(v => v.Body).HasMaxLength(ScriptRules.MaxBodyLength);
            entity.Property(v => v.Sha256).HasMaxLength(64);
            entity.Property(v => v.AuthorName).HasMaxLength(200);
            entity.Property(v => v.ApprovedByName).HasMaxLength(200);
            entity.Property(v => v.ApprovedSha256).HasMaxLength(64);
            entity.Ignore(v => v.IsApproved);
            entity.HasIndex(v => new { v.ScriptId, v.Number }).IsUnique();
            entity.ToTable(t => t.HasCheckConstraint("CK_ScriptVersions_Approval",
                "(\"ApprovedAt\" IS NULL) = (\"ApprovedByUserId\" IS NULL) AND (\"ApprovedByUserId\" IS NULL OR \"ApprovedByUserId\" <> \"AuthorUserId\")"));
            GlobalOrClientOwned(entity);
        });

        builder.Entity<Job>(entity =>
        {
            entity.Property(j => j.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(j => j.ScriptName).HasMaxLength(100);
            entity.Property(j => j.Language).HasConversion<string>().HasMaxLength(20);
            entity.Property(j => j.ScriptSha256).HasMaxLength(64);
            entity.Property(j => j.InitiatedByName).HasMaxLength(200);
            entity.Property(j => j.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(j => j.RunAs).HasConversion<string>().HasMaxLength(20).HasDefaultValue(JobRunAs.Service).HasSentinel((JobRunAs)(-1));
            entity.Property(j => j.RunAsUserId).HasMaxLength(SignedInUserRules.MaxUserIdLength);
            entity.Property(j => j.RunAsChosenAccount).HasMaxLength(SignedInUserRules.MaxAccountLength);
            entity.Property(j => j.RunAsAccount).HasMaxLength(256);
            entity.Property(j => j.RefusalReason).HasMaxLength(500);
            entity.Property(j => j.SigningKeyId).HasMaxLength(64);
            entity.Property(j => j.Result).HasConversion<string>().HasMaxLength(20);
            entity.Property(j => j.ExitStatus).HasConversion<string>().HasMaxLength(20);
            entity.ToTable(t => t.HasCheckConstraint("CK_Jobs_ExitStatus",
                "\"ExitStatus\" IS NULL OR (\"CompletedAt\" IS NOT NULL AND \"Result\" = 'Exited')"));
            entity.Property(j => j.Error).HasMaxLength(1000);
            entity.Property(j => j.OutputState).HasConversion<string>().HasMaxLength(20);
            entity.Property(j => j.StdoutSha256).HasMaxLength(64);
            entity.Property(j => j.StderrSha256).HasMaxLength(64);
            entity.HasAlternateKey(j => new { j.Id, j.ClientId });
            EndpointChild(entity, j => new { j.EndpointId, j.ClientId });
            entity.HasOne<Script>().WithMany().HasForeignKey(j => j.ScriptId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ScriptVersion>().WithMany().HasForeignKey(j => j.ScriptVersionId).OnDelete(DeleteBehavior.SetNull);
            // Newest first per endpoint; delivery and maintenance by state.
            entity.HasIndex(j => new { j.EndpointId, j.CreatedAt }).IsDescending(false, true);
            entity.HasIndex(j => new { j.State, j.EndpointId }).HasFilter("\"State\" IN ('PendingSignature', 'Queued', 'Running')");
            entity.HasIndex(j => j.BatchId);
            // Tasks (0.6.0): the jobs of a run, and the last job of a task on an endpoint (overlap, signing interval).
            entity.HasOne<AutomationTask>().WithMany().HasForeignKey(j => j.TaskId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<AutomationTaskRun>().WithMany().HasForeignKey(j => j.TaskRunId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(j => j.TaskRunId).HasFilter("\"TaskRunId\" IS NOT NULL");
            // A step of a task runs at most once per run and endpoint (0.6.0, steps), whatever the workers retry.
            entity.HasIndex(j => new { j.TaskRunId, j.EndpointId, j.TaskStepId }).IsUnique()
                .HasFilter("\"TaskStepId\" IS NOT NULL").HasDatabaseName("IX_Jobs_TaskStep");
            entity.HasIndex(j => new { j.TaskId, j.EndpointId, j.CreatedAt }).HasFilter("\"TaskId\" IS NOT NULL");
            // Public API: the job list across endpoints, newest first, with keyset pagination (0.2.1).
            entity.HasIndex(j => new { j.CreatedAt, j.Id }).IsDescending(true, true);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Jobs_Validity", "\"ValidUntil\" > \"CreatedAt\" AND \"ValidUntil\" <= \"CreatedAt\" + interval '7 days 5 minutes'");
                t.HasCheckConstraint("CK_Jobs_Signed", "\"State\" IN ('PendingSignature', 'Refused', 'Cancelled', 'Expired') OR \"Signature\" IS NOT NULL");
            });
            EndpointOwned(entity);
        });

        builder.Entity<AutomationTask>(entity =>
        {
            entity.Property(t => t.Name).HasMaxLength(Core.Domain.AutomationTasks.MaxNameLength);
            entity.Property(t => t.Description).HasMaxLength(Core.Domain.AutomationTasks.MaxDescriptionLength);
            entity.Property(t => t.RunAs).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.AppliesTo).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.ScheduleKind).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.TimeZone).HasMaxLength(64);
            entity.Property(t => t.WeekDays).HasConversion<int>();
            entity.Property(t => t.Overlap).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.AlertSeverity).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.UpdatedByName).HasMaxLength(200);
            entity.HasIndex(t => new { t.ClientId, t.Name }).IsUnique().AreNullsDistinct(false);
            entity.HasIndex(t => t.ScriptId);
            // A task needs its script: deleting a script a task runs is refused until the task uses another one. Checked at the end
            // of the statement, so deleting a client with its scripts and tasks still works.
            entity.HasOne<Script>().WithMany().HasForeignKey(t => t.ScriptId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<Client>().WithMany().HasForeignKey(t => t.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_AutomationTasks_StartMinute", "\"StartMinute\" BETWEEN 0 AND 1439");
                t.HasCheckConstraint("CK_AutomationTasks_Every", "\"Every\" BETWEEN 1 AND 365");
                t.HasCheckConstraint("CK_AutomationTasks_WeekDays", "\"WeekDays\" BETWEEN 0 AND 127");
                t.HasCheckConstraint("CK_AutomationTasks_MonthDay", "\"MonthDay\" BETWEEN 1 AND 31");
                t.HasCheckConstraint("CK_AutomationTasks_Repeat", "\"RepeatMinutes\" IS NULL OR \"RepeatMinutes\" BETWEEN 15 AND 720");
                t.HasCheckConstraint("CK_AutomationTasks_Delay", "\"RandomDelayMinutes\" BETWEEN 0 AND 720");
                t.HasCheckConstraint("CK_AutomationTasks_Dates", "\"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
                t.HasCheckConstraint("CK_AutomationTasks_TriggerChecks", "cardinality(\"TriggerCheckIds\") <= 20");
            });
            GlobalOrClientOwned(entity);
        });

        builder.Entity<AutomationTaskStep>(entity =>
        {
            entity.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.RunAs).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(s => new { s.AutomationTaskId, s.Position }).IsUnique();
            entity.HasIndex(s => s.ScriptId);
            entity.HasOne<AutomationTask>().WithMany().HasForeignKey(s => s.AutomationTaskId).OnDelete(DeleteBehavior.Cascade);
            // As for the task: a script a step runs cannot be deleted, checked at the end of the statement.
            entity.HasOne<Script>().WithMany().HasForeignKey(s => s.ScriptId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<Client>().WithMany().HasForeignKey(s => s.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_AutomationTaskSteps_Position", "\"Position\" >= 1");
                t.HasCheckConstraint("CK_AutomationTaskSteps_Kind",
                    "(\"Kind\" = 'Script' AND \"ScriptId\" IS NOT NULL AND \"RestartWaitMinutes\" IS NULL) OR " +
                    "(\"Kind\" = 'Restart' AND \"ScriptId\" IS NULL AND \"RestartWaitMinutes\" BETWEEN 5 AND 240 AND cardinality(\"SuccessExitCodes\") = 0)");
                t.HasCheckConstraint("CK_AutomationTaskSteps_ExitCodes",
                    "cardinality(\"SuccessExitCodes\") <= 10 AND NOT (0 = ANY (\"SuccessExitCodes\"))");
            });
            GlobalOrClientOwned(entity);
        });

        builder.Entity<ClientAutomationTask>(entity =>
        {
            entity.HasKey(l => new { l.ClientId, l.AutomationTaskId });
            entity.HasOne<Client>().WithMany().HasForeignKey(l => l.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.AutomationTask).WithMany().HasForeignKey(l => l.AutomationTaskId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.AutomationTaskId);
            ClientOwned(entity);
        });

        builder.Entity<SiteAutomationTask>(entity =>
        {
            entity.HasKey(l => new { l.SiteId, l.AutomationTaskId });
            entity.HasOne<Site>().WithMany()
                .HasForeignKey(l => new { l.SiteId, l.ClientId })
                .HasPrincipalKey(s => new { s.Id, s.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.AutomationTask).WithMany().HasForeignKey(l => l.AutomationTaskId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.AutomationTaskId);
            SiteOwned(entity);
        });

        builder.Entity<EndpointAutomationTask>(entity =>
        {
            entity.HasKey(l => new { l.EndpointId, l.AutomationTaskId });
            EndpointChild(entity, l => new { l.EndpointId, l.ClientId });
            entity.HasOne(l => l.AutomationTask).WithMany().HasForeignKey(l => l.AutomationTaskId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.AutomationTaskId);
            EndpointOwned(entity);
        });

        builder.Entity<MonitoringTemplateAutomationTask>(entity =>
        {
            entity.HasKey(l => new { l.MonitoringTemplateId, l.AutomationTaskId });
            entity.HasOne(l => l.MonitoringTemplate).WithMany().HasForeignKey(l => l.MonitoringTemplateId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.AutomationTask).WithMany().HasForeignKey(l => l.AutomationTaskId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Client>().WithMany().HasForeignKey(l => l.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.AutomationTaskId);
            GlobalOrClientOwned(entity);
        });

        builder.Entity<AutomationTaskState>(entity =>
        {
            entity.HasKey(s => s.AutomationTaskId);
            entity.Property(s => s.ScheduleHash).HasMaxLength(200);
            entity.HasOne<AutomationTask>().WithMany().HasForeignKey(s => s.AutomationTaskId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AutomationTaskRun>(entity =>
        {
            entity.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.InitiatedByName).HasMaxLength(200);
            entity.Property(r => r.CheckName).HasMaxLength(100);
            entity.HasAlternateKey(r => new { r.Id, r.ClientId });
            entity.HasOne<AutomationTask>().WithMany().HasForeignKey(r => r.AutomationTaskId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Client>().WithMany().HasForeignKey(r => r.ClientId).OnDelete(DeleteBehavior.Cascade);
            // Newest first per task; a scheduled occurrence runs once per client, whatever the workers retry.
            entity.HasIndex(r => new { r.AutomationTaskId, r.CreatedAt }).IsDescending(false, true);
            entity.HasIndex(r => new { r.AutomationTaskId, r.ClientId, r.OccurrenceAt }).IsUnique()
                .HasFilter("\"Trigger\" = 'Schedule'").HasDatabaseName("IX_AutomationTaskRuns_Occurrence");
            ClientOwned(entity);
        });

        builder.Entity<AutomationTaskRunTarget>(entity =>
        {
            entity.HasKey(t => new { t.RunId, t.EndpointId });
            entity.Property(t => t.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.Reason).HasMaxLength(500);
            entity.HasOne<AutomationTaskRun>().WithMany()
                .HasForeignKey(t => new { t.RunId, t.ClientId })
                .HasPrincipalKey(r => new { r.Id, r.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            EndpointChild(entity, t => new { t.EndpointId, t.ClientId });
            entity.HasOne<Job>().WithMany().HasForeignKey(t => t.JobId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(t => t.DueAt).HasFilter("\"State\" = 'Pending'").HasDatabaseName("IX_AutomationTaskRunTargets_Pending");
            entity.HasIndex(t => t.JobId).HasFilter("\"JobId\" IS NOT NULL AND \"OutcomeHandledAt\" IS NULL")
                .HasDatabaseName("IX_AutomationTaskRunTargets_Outcome");
            entity.HasIndex(t => new { t.AutomationTaskId, t.EndpointId });
            entity.Property(t => t.Outcome).HasConversion<string>().HasMaxLength(20);
            // The runs still busy on an endpoint, for the overlap of a task with steps (0.6.0).
            entity.HasIndex(t => new { t.EndpointId, t.AutomationTaskId, t.State }).HasFilter("\"State\" = 'Started' AND \"FinishedAt\" IS NULL")
                .HasDatabaseName("IX_AutomationTaskRunTargets_Busy");
            EndpointOwned(entity);
        });

        builder.Entity<JobOutputChunk>(entity =>
        {
            entity.HasKey(c => new { c.JobId, c.Stream, c.Sequence });
            entity.Property(c => c.Stream).HasConversion<string>().HasMaxLength(10);
            entity.HasOne<Job>().WithMany()
                .HasForeignKey(c => new { c.JobId, c.ClientId })
                .HasPrincipalKey(j => new { j.Id, j.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(c => c.ReceivedAt);
            entity.ToTable(t => t.HasCheckConstraint("CK_JobOutputChunks_Size", "octet_length(\"Data\") BETWEEN 1 AND 65536"));
            JobOutputOwned(entity);
        });

        // Remote sessions (0.3.0): the session, one row per technician's connection, and the actions of a background session.
        builder.Entity<RemoteSession>(entity =>
        {
            entity.Property(r => r.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(r => r.Component).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.StartedByName).HasMaxLength(200);
            entity.Property(r => r.Reason).HasMaxLength(RemoteSessionRules.MaxReasonLength);
            entity.Property(r => r.EndReason).HasMaxLength(500);
            entity.HasAlternateKey(r => new { r.Id, r.ClientId });
            EndpointChild(entity, r => new { r.EndpointId, r.ClientId });
            entity.HasIndex(r => new { r.EndpointId, r.CreatedAt }).IsDescending(false, true);
            entity.HasIndex(r => r.CreatedAt);
            entity.HasIndex(r => r.Id).HasFilter("\"EndedAt\" IS NULL").HasDatabaseName("IX_RemoteSessions_Open");
            entity.ToTable(t => t.HasCheckConstraint("CK_RemoteSessions_WindowsSession",
                "(\"Kind\" = 'RemoteControl' AND \"WindowsSessionId\" >= 0) OR (\"Kind\" <> 'RemoteControl' AND \"WindowsSessionId\" IS NULL)"));
            EndpointOwned(entity);
        });

        builder.Entity<RemoteSessionParticipant>(entity =>
        {
            entity.Property(p => p.UserName).HasMaxLength(200);
            entity.Property(p => p.Reason).HasMaxLength(RemoteSessionRules.MaxReasonLength);
            entity.Property(p => p.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(p => p.SigningKeyId).HasMaxLength(64);
            entity.Property(p => p.IpAddress).HasMaxLength(64);
            entity.Property(p => p.EndReason).HasMaxLength(500);
            entity.HasOne<RemoteSession>().WithMany()
                .HasForeignKey(p => new { p.SessionId, p.ClientId })
                .HasPrincipalKey(r => new { r.Id, r.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            EndpointChild(entity, p => new { p.EndpointId, p.ClientId });
            entity.HasIndex(p => p.SessionId);
            entity.HasIndex(p => new { p.State, p.CreatedAt })
                .HasFilter("\"State\" IN ('Requested', 'Signed', 'Connecting', 'Connected')")
                .HasDatabaseName("IX_RemoteSessionParticipants_Active");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_RemoteSessionParticipants_BrowserKey", "octet_length(\"BrowserPublicKey\") = 32");
                // The relay only opens with a signed token.
                t.HasCheckConstraint("CK_RemoteSessionParticipants_Signed", "\"ConnectingAt\" IS NULL OR \"TokenSignature\" IS NOT NULL");
            });
            EndpointOwned(entity);
        });

        builder.Entity<RemoteSessionAction>(entity =>
        {
            entity.Property(a => a.Id).UseIdentityAlwaysColumn();
            entity.Property(a => a.Action).HasMaxLength(50);
            entity.Property(a => a.Target).HasMaxLength(1000);
            entity.Property(a => a.Detail).HasMaxLength(1000);
            entity.HasOne<RemoteSession>().WithMany()
                .HasForeignKey(a => new { a.SessionId, a.ClientId })
                .HasPrincipalKey(r => new { r.Id, r.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<RemoteSessionParticipant>().WithMany().HasForeignKey(a => a.ParticipantId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(a => new { a.SessionId, a.Time });
            entity.HasIndex(a => a.Time);
            EndpointOwned(entity);
        });

        builder.Entity<Alert>(entity =>
        {
            entity.Property(a => a.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(a => a.Severity).HasConversion<string>().HasMaxLength(20);
            entity.Property(a => a.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(a => a.Target).HasMaxLength(256);
            entity.Property(a => a.Title).HasMaxLength(500);
            entity.Property(a => a.Detail).HasMaxLength(2000);
            entity.Property(a => a.ResolvedReason).HasMaxLength(500);
            entity.HasOne(a => a.Endpoint).WithMany()
                .HasForeignKey(a => new { a.EndpointId, a.ClientId })
                .HasPrincipalKey(e => new { e.Id, e.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CheckDefinition>().WithMany().HasForeignKey(a => a.CheckDefinitionId).OnDelete(DeleteBehavior.SetNull);
            // Deduplication: one unresolved alert per endpoint, kind, check and target. Check alerts whose definition was
            // deleted (CheckDefinitionId set to null) are excluded, so deleting definitions never collides; a trigger
            // resolves those alerts (migration AlertAndCursorIndexes).
            entity.HasIndex(a => new { a.EndpointId, a.Kind, a.CheckDefinitionId, a.Target })
                .IsUnique().AreNullsDistinct(false)
                .HasFilter("\"State\" <> 'Resolved' AND NOT (\"Kind\" = 'Check' AND \"CheckDefinitionId\" IS NULL)");
            entity.HasIndex(a => new { a.State, a.Severity });
            entity.HasIndex(a => new { a.ClientId, a.State });
            entity.HasIndex(a => a.OpenedAt);
            entity.HasIndex(a => a.HeldUntil).HasFilter("\"HeldUntil\" IS NOT NULL AND \"State\" <> 'Resolved'").HasDatabaseName("IX_Alerts_Held");
            EndpointOwned(entity);
        });

        // What users did with an alert, with their comment (0.6.0).
        builder.Entity<AlertActivity>(entity =>
        {
            entity.Property(a => a.Id).UseIdentityAlwaysColumn();
            entity.Property(a => a.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(a => a.Comment).HasMaxLength(AlertActivity.MaxCommentLength);
            entity.Property(a => a.UserName).HasMaxLength(200);
            entity.HasOne<Alert>().WithMany().HasForeignKey(a => a.AlertId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(a => new { a.AlertId, a.Id });
            EndpointChild(entity, a => new { a.EndpointId, a.ClientId });
            EndpointOwned(entity);
        });

        // Periods of maintenance mode (0.6.0), written by TR_*_MaintenanceRecord.
        builder.Entity<MaintenanceRecord>(entity =>
        {
            entity.Property(m => m.Id).UseIdentityAlwaysColumn();
            entity.Property(m => m.StartedByName).HasMaxLength(200);
            entity.Property(m => m.Reason).HasMaxLength(MaintenanceRules.MaxReasonLength);
            entity.HasOne<Client>().WithMany().HasForeignKey(m => m.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Site>().WithMany()
                .HasForeignKey(m => new { m.SiteId, m.ClientId })
                .HasPrincipalKey(s => new { s.Id, s.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Endpoint>().WithMany()
                .HasForeignKey(m => new { m.EndpointId, m.ClientId })
                .HasPrincipalKey(e => new { e.Id, e.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(m => new { m.ClientId, m.StartedAt });
            entity.HasIndex(m => new { m.SiteId, m.StartedAt }).HasFilter("\"SiteId\" IS NOT NULL");
            entity.HasIndex(m => new { m.EndpointId, m.StartedAt }).HasFilter("\"EndpointId\" IS NOT NULL");
            // A user with part of a client sees the maintenance of the client, of the sites it reaches and of its endpoints: the
            // same maintenance that keeps those endpoints from raising alerts.
            entity.HasQueryFilter(m => ScopeAllClients || ScopeFullClientIds.Contains(m.ClientId) ||
                                       (m.SiteId == null && m.EndpointId == null && ScopeClientIds.Contains(m.ClientId)) ||
                                       (m.EndpointId == null && m.SiteId != null && ScopeSiteIds.Contains(m.SiteId.Value)) ||
                                       (m.EndpointId != null && ScopeEndpointIds.Contains(m.EndpointId.Value)));
        });

        builder.Entity<EndpointEvent>(entity =>
        {
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Detail).HasMaxLength(1000);
            entity.HasIndex(e => e.Id).HasFilter("\"ProcessedAt\" IS NULL").HasDatabaseName("IX_EndpointEvents_Unprocessed");
            entity.HasIndex(e => new { e.EndpointId, e.Time });
            EndpointChild(entity, e => new { e.EndpointId, e.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<SigningRequest>(entity =>
        {
            entity.Property(r => r.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(r => r.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.RequestedBy).HasMaxLength(200);
            entity.Property(r => r.RefusalReason).HasMaxLength(1000);
            entity.HasIndex(r => r.CreatedAt).HasFilter("\"State\" = 'Pending'").HasDatabaseName("IX_SigningRequests_Pending");
            entity.HasIndex(r => r.CreatedAt);
            GlobalOrClientOwned(entity);
        });

        builder.Entity<EndpointConfig>(entity =>
        {
            entity.HasKey(c => c.EndpointId);
            entity.Property(c => c.KeyId).HasMaxLength(16);
            entity.Property(c => c.ContentHash).HasMaxLength(64);
            EndpointChild(entity, c => new { c.EndpointId, c.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<InstanceSigningKey>(entity =>
        {
            entity.HasKey(k => k.Id);
            entity.Property(k => k.Id).HasMaxLength(16);
        });

        builder.Entity<CertificateAuthority>(entity =>
        {
            entity.Property(c => c.Fingerprint).HasMaxLength(64);
            entity.HasIndex(c => c.Fingerprint).IsUnique();
        });

        builder.Entity<ConfigChangeEvent>(entity =>
        {
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.Scope).HasConversion<string>().HasMaxLength(30);
            entity.HasIndex(e => e.Id).HasFilter("\"ProcessedAt\" IS NULL").HasDatabaseName("IX_ConfigChangeEvents_Unprocessed");
        });

        builder.Entity<InstanceSettings>(entity =>
        {
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.Fqdn).HasMaxLength(255);
            entity.Property(s => s.AgentHostName).HasMaxLength(255);
            entity.Property(s => s.WebBaseUrl).HasMaxLength(500);
            entity.ToTable(t => t.HasCheckConstraint("CK_InstanceSettings_SingleRow", "\"Id\" = 1"));
        });

        builder.Entity<Setting>(entity =>
        {
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasMaxLength(200);
        });

        builder.Entity<DataKey>(entity =>
        {
            entity.Property(k => k.Purpose).HasMaxLength(100);
            entity.Property(k => k.RootKeyId).HasMaxLength(16);
            entity.HasIndex(k => k.Purpose).IsUnique().HasFilter("\"RetiredAt\" IS NULL").HasDatabaseName("IX_DataKeys_ActivePurpose");
        });

        builder.Entity<License>(entity =>
        {
            entity.Property(l => l.Serial).HasMaxLength(100);
            entity.Property(l => l.CustomerName).HasMaxLength(200);
            entity.Property(l => l.Fqdn).HasMaxLength(255);
            entity.Property(l => l.KeyId).HasMaxLength(16);
            entity.HasIndex(l => l.IsActive).IsUnique().HasFilter("\"IsActive\"").HasDatabaseName("IX_Licenses_Active");
        });

        builder.Entity<SetupToken>(entity =>
        {
            entity.Property(t => t.TokenHash).HasMaxLength(64);
            entity.HasIndex(t => t.TokenHash).IsUnique();
        });

        builder.Entity<AgentRelease>(entity =>
        {
            entity.HasKey(r => r.Version);
            entity.Property(r => r.Version).HasMaxLength(50);
            entity.Property(r => r.ManifestSha256).HasMaxLength(64);
            entity.Property(r => r.PausedByName).HasMaxLength(200);
            entity.Property(r => r.ReleasedToAllByName).HasMaxLength(200);
            entity.HasIndex(r => r.IsCurrent).IsUnique().HasFilter("\"IsCurrent\"").HasDatabaseName("IX_AgentReleases_Current");
        });

        builder.Entity<EndpointComponentState>(entity =>
        {
            entity.HasKey(c => new { c.EndpointId, c.Component });
            entity.Property(c => c.Component).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.InstalledVersion).HasMaxLength(50);
            entity.Property(c => c.ServiceState).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.ServiceDetail).HasMaxLength(500);
            entity.Property(c => c.UpdateVersion).HasMaxLength(50);
            entity.Property(c => c.UpdateState).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.UpdateDetail).HasMaxLength(500);
            entity.Property(c => c.WaitVersion).HasMaxLength(50);
            entity.Property(c => c.WaitReason).HasConversion<string>().HasMaxLength(20);
            EndpointChild(entity, c => new { c.EndpointId, c.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<ApiKey>(entity =>
        {
            entity.Property(k => k.Name).HasMaxLength(ApiKey.MaxNameLength);
            entity.Property(k => k.SecretHash).HasMaxLength(64);
            entity.Property(k => k.CreatedByName).HasMaxLength(200);
            entity.Property(k => k.RevokedByName).HasMaxLength(200);
            entity.Property(k => k.AllClients).HasDefaultValue(true).ValueGeneratedNever();
            entity.HasIndex(k => k.CreatedAt);
            entity.HasMany(k => k.Clients).WithOne().HasForeignKey(c => c.ApiKeyId).OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(t => t.HasCheckConstraint("CK_ApiKeys_SecretHash", "\"SecretHash\" ~ '^[0-9a-f]{64}$'"));
        });

        builder.Entity<ApiKeyClient>(entity =>
        {
            entity.HasKey(c => new { c.ApiKeyId, c.ClientId });
            entity.HasIndex(c => c.ClientId);
            entity.HasOne<Client>().WithMany().HasForeignKey(c => c.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditEntry>(entity =>
        {
            entity.Property(a => a.Id).UseIdentityAlwaysColumn();
            entity.Property(a => a.ActorType).HasConversion<string>().HasMaxLength(20);
            entity.Property(a => a.ActorId).HasMaxLength(100);
            entity.Property(a => a.ActorName).HasMaxLength(200);
            entity.Property(a => a.Action).HasMaxLength(100);
            entity.Property(a => a.TargetType).HasMaxLength(50);
            entity.Property(a => a.TargetId).HasMaxLength(100);
            entity.Property(a => a.DetailsJson).HasColumnType("jsonb");
            entity.Property(a => a.IpAddress).HasMaxLength(64);
            entity.HasIndex(a => a.Time);
            entity.HasIndex(a => new { a.ClientId, a.Time });
            entity.HasIndex(a => new { a.TargetType, a.TargetId });
            // The security monitor (0.7.0) reads the last hour of a few actions every minute.
            entity.HasIndex(a => new { a.Action, a.Time });
            // A limited scope sees no instance-wide entries (who changed settings, users or a deleted client), and in a client it
            // sees part of only the entries about its endpoints (0.6.0).
            entity.HasQueryFilter(a => ScopeAllClients ||
                                       (a.ClientId != null && (ScopeFullClientIds.Contains(a.ClientId.Value) ||
                                                               (a.TargetType == "Endpoint" && ScopeEndpointIdTexts.Contains(a.TargetId)))));
        });

        builder.Entity<NotificationChannel>(entity =>
        {
            entity.Property(c => c.Name).HasMaxLength(100);
            entity.Property(c => c.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.Recipients).HasMaxLength(2000);
            entity.Property(c => c.MinimumSeverity).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.WebhookFormat).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.WebhookHost).HasMaxLength(255);
            entity.Property(c => c.EncryptedWebhook).HasMaxLength(8000);
            entity.Property(c => c.AllClients).HasDefaultValue(true).ValueGeneratedNever();
            entity.Property(c => c.InstanceHealth).HasDefaultValue(false);
            entity.ToTable(t => t.HasCheckConstraint("CK_NotificationChannels_Type",
                "(\"Type\" = 'Email' AND \"Recipients\" <> '' AND \"EncryptedWebhook\" IS NULL) OR " +
                "(\"Type\" = 'Webhook' AND \"EncryptedWebhook\" IS NOT NULL AND \"WebhookFormat\" IS NOT NULL)"));
            entity.HasMany(c => c.Clients).WithOne().HasForeignKey(c => c.NotificationChannelId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<EndpointPatchState>(entity =>
        {
            entity.HasKey(p => p.EndpointId);
            entity.Property(p => p.ExternalEndpointId).HasMaxLength(64);
            entity.Property(p => p.ExternalTenantId).HasMaxLength(200);
            entity.Property(p => p.Coverage).HasConversion<string>().HasMaxLength(20);
            entity.Property(p => p.ProductAgentVersion).HasMaxLength(50);
            entity.Ignore(p => p.IsCompliant);
            // The scheduled patching check and the Patches tab find the automation of an endpoint (0.6.0).
            entity.HasIndex(p => p.AutomationId).HasFilter("\"AutomationId\" IS NOT NULL");
            // The dashboard and the client rollups count per client: how many endpoints have patch state, and how many of
            // those miss something.
            entity.HasIndex(p => new { p.ClientId, p.MissingCritical });
            EndpointChild(entity, p => new { p.EndpointId, p.ClientId });
            EndpointOwned(entity);
        });

        // Not client-owned on purpose (0.6.0): the row outlives its endpoint and client, and the gateway finds it by fingerprint.
        builder.Entity<AgentUninstall>(entity =>
        {
            entity.Property(u => u.ClientCode).HasMaxLength(20);
            entity.Property(u => u.Hostname).HasMaxLength(255);
            entity.Property(u => u.RequestedByName).HasMaxLength(200);
            entity.Property(u => u.SigningKeyId).HasMaxLength(16);
            entity.Property(u => u.RefusalReason).HasMaxLength(500);
            entity.Property(u => u.AcceptedBy).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(u => u.ExpiresAt);
        });

        builder.Entity<Action1AgentInstallState>(entity =>
        {
            entity.HasKey(a => a.EndpointId);
            entity.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(a => a.Failure).HasMaxLength(1000);
            entity.Property(a => a.CheckDetail).HasMaxLength(1000);
            // The patch policy page counts per client and status; the workers find the attempts that are due.
            entity.HasIndex(a => new { a.ClientId, a.Status });
            entity.HasIndex(a => a.NextAttemptAt).HasFilter("\"NextAttemptAt\" IS NOT NULL");
            EndpointChild(entity, a => new { a.EndpointId, a.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<EndpointMissingUpdate>(entity =>
        {
            entity.Property(u => u.ExternalUpdateId).HasMaxLength(200);
            entity.Property(u => u.Name).HasMaxLength(300);
            entity.Property(u => u.Vendor).HasMaxLength(200);
            entity.Property(u => u.Version).HasMaxLength(100);
            entity.Property(u => u.KbNumber).HasMaxLength(20);
            entity.Property(u => u.InstalledVersion).HasMaxLength(100);
            entity.Property(u => u.UpdateType).HasMaxLength(50);
            entity.Property(u => u.ApprovalStatus).HasMaxLength(20);
            entity.Property(u => u.Severity).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(u => new { u.EndpointId, u.Severity });
            EndpointChild(entity, u => new { u.EndpointId, u.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<PatchDeployment>(entity =>
        {
            entity.Property(d => d.ExternalTenantId).HasMaxLength(200);
            entity.Property(d => d.ExternalDeploymentId).HasMaxLength(200);
            entity.Property(d => d.Scope).HasConversion<string>().HasMaxLength(20);
            entity.Property(d => d.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(d => d.StatusMessage).HasMaxLength(1000);
            entity.Property(d => d.RequestedByName).HasMaxLength(200);
            entity.Property(d => d.ExternalAutomationId).HasMaxLength(200).HasDefaultValue(string.Empty);
            entity.Ignore(d => d.IsOpen);
            // A run of an automation becomes one scheduled deployment, however often the workers find it (0.6.0).
            entity.HasIndex(d => new { d.ClientId, d.ExternalDeploymentId }).IsUnique().HasFilter("\"ExternalAutomationId\" <> ''");
            entity.HasAlternateKey(d => new { d.Id, d.ClientId });
            entity.HasOne<Client>().WithMany().HasForeignKey(d => d.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(d => d.Updates).WithOne().HasForeignKey(u => u.DeploymentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(d => d.Targets).WithOne().HasForeignKey(t => t.DeploymentId).OnDelete(DeleteBehavior.Cascade);
            // The workers pick up what still needs work; the UI reads the newest per client.
            entity.HasIndex(d => d.State).HasFilter("\"State\" IN ('Requested', 'Running')");
            entity.HasIndex(d => new { d.ClientId, d.RequestedAt }).IsDescending(false, true);
            entity.HasIndex(d => d.BatchId);
            PatchDeploymentOwned(entity);
        });

        builder.Entity<PatchDeploymentUpdate>(entity =>
        {
            entity.Property(u => u.ExternalUpdateId).HasMaxLength(200);
            entity.Property(u => u.Name).HasMaxLength(300);
            entity.Property(u => u.Version).HasMaxLength(100);
            entity.HasIndex(u => u.DeploymentId);
            PatchDeploymentUpdateOwned(entity);
        });

        builder.Entity<PatchDeploymentTarget>(entity =>
        {
            entity.Property(t => t.ExternalEndpointId).HasMaxLength(64);
            entity.Property(t => t.Hostname).HasMaxLength(255);
            entity.Property(t => t.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.Message).HasMaxLength(1000);
            // One row per endpoint in a deployment: a re-delivered result updates it instead of adding another.
            entity.HasIndex(t => new { t.DeploymentId, t.EndpointId }).IsUnique();
            entity.HasIndex(t => new { t.EndpointId, t.UpdatedAt }).IsDescending(false, true);
            entity.HasAlternateKey(t => new { t.Id, t.ClientId });
            EndpointChild(entity, t => new { t.EndpointId, t.ClientId });
            EndpointOwned(entity);
        });

        // The product's history per endpoint of a deployment (0.6.0): same client as its endpoint, gone with it.
        builder.Entity<PatchDeploymentStep>(entity =>
        {
            entity.Property(s => s.Operation).HasMaxLength(200);
            entity.Property(s => s.Status).HasMaxLength(20);
            entity.Property(s => s.Details).HasMaxLength(2000);
            entity.HasIndex(s => new { s.TargetId, s.Position }).IsUnique();
            entity.HasOne<PatchDeploymentTarget>().WithMany()
                .HasForeignKey(s => new { s.TargetId, s.ClientId })
                .HasPrincipalKey(t => new { t.Id, t.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            PatchDeploymentStepOwned(entity);
        });

        // What a technician asked about the patch management of an endpoint (0.6.0): gone with the endpoint.
        builder.Entity<PatchRequest>(entity =>
        {
            entity.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.ExternalTenantId).HasMaxLength(200);
            entity.Property(r => r.ExternalEndpointId).HasMaxLength(64);
            entity.Property(r => r.RequestedByName).HasMaxLength(200);
            entity.Property(r => r.Failure).HasMaxLength(1000);
            // The workers pick up what waits; the Patches tab reads the newest of an endpoint.
            entity.HasIndex(r => r.RequestedAt).HasFilter("\"CompletedAt\" IS NULL").HasDatabaseName("IX_PatchRequests_Pending");
            entity.HasIndex(r => new { r.EndpointId, r.RequestedAt });
            EndpointChild(entity, r => new { r.EndpointId, r.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<SignInExchange>(entity =>
        {
            entity.Property(e => e.EncryptedRequest).HasMaxLength(8000);
            entity.Property(e => e.EncryptedClaims).HasMaxLength(8000);
            entity.Property(e => e.RedirectUri).HasMaxLength(500);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            // The workers pick up what is waiting; retention removes what an abandoned browser left behind.
            entity.HasIndex(e => new { e.State, e.CreatedAt });
        });

        builder.Entity<MicrosoftRequest>(entity =>
        {
            entity.Property(r => r.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(r => r.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.Query).HasMaxLength(100);
            entity.Property(r => r.ResultJson).HasMaxLength(64000);
            entity.Property(r => r.FailureReason).HasMaxLength(500);
            // The workers pick up what is waiting and delete what nobody read.
            entity.HasIndex(r => new { r.State, r.CreatedAt });
        });

        builder.Entity<Integration>(entity =>
        {
            entity.Property(i => i.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(i => i.Region).HasConversion<string>().HasMaxLength(30);
            entity.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(i => i.EncryptedCredentials).HasMaxLength(8000);
            entity.Property(i => i.CredentialName).HasMaxLength(200);
            entity.Property(i => i.StatusMessage).HasMaxLength(1000);
            entity.Property(i => i.FollowMessage).HasMaxLength(1000);
            entity.Property(i => i.TenantsJson).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            // One enterprise per product per instance (0.4.0): its organizations map to clients.
            entity.HasIndex(i => i.Type).IsUnique();
            entity.ToTable(t => t.HasCheckConstraint("CK_Integrations_Action1",
                "\"Type\" <> 'Action1' OR (\"Region\" IS NOT NULL AND \"EncryptedCredentials\" <> '')"));
            entity.HasMany(i => i.Mappings).WithOne().HasForeignKey(m => m.IntegrationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<IntegrationMapping>(entity =>
        {
            entity.Property(m => m.ExternalTenantId).HasMaxLength(200);
            entity.Property(m => m.ExternalTenantName).HasMaxLength(200);
            entity.Property(m => m.AgentInstallerUrl).HasMaxLength(500);
            // A tenant belongs to one client, and a client to one tenant of that integration: patch state can never land
            // under another client, and a client's compliance is never composed from two organizations.
            entity.HasIndex(m => new { m.IntegrationId, m.ExternalTenantId }).IsUnique();
            entity.HasIndex(m => new { m.IntegrationId, m.ClientId }).IsUnique();
            entity.Property(m => m.SyncedName).HasMaxLength(200);
            entity.Property(m => m.OtherAutomationsJson).HasColumnType("jsonb").HasDefaultValue("[]");
            entity.HasOne<Client>().WithMany().HasForeignKey(m => m.ClientId).OnDelete(DeleteBehavior.Cascade);
            ClientOwned(entity);
        });

        // The endpoint group of a site (0.6.0): same client as its site, gone with it or with the integration.
        builder.Entity<IntegrationSiteGroup>(entity =>
        {
            entity.Property(g => g.ExternalTenantId).HasMaxLength(200);
            entity.Property(g => g.ExternalGroupId).HasMaxLength(200);
            entity.Property(g => g.SyncedName).HasMaxLength(200);
            entity.Property(g => g.MembersHash).HasMaxLength(64);
            entity.HasIndex(g => new { g.IntegrationId, g.SiteId }).IsUnique();
            entity.HasIndex(g => g.ClientId);
            entity.HasOne<Integration>().WithMany().HasForeignKey(g => g.IntegrationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Site>().WithMany()
                .HasForeignKey(g => new { g.SiteId, g.ClientId })
                .HasPrincipalKey(s => new { s.Id, s.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            SiteOwned(entity);
        });

        // The automation of a patch policy in a tenant (0.6.0). No foreign key to the client or the patch policy: the
        // workers remove the automation in the product after either is gone, and need the row to find it.
        builder.Entity<IntegrationAutomation>(entity =>
        {
            entity.Property(a => a.ExternalTenantId).HasMaxLength(200);
            entity.Property(a => a.ExternalAutomationId).HasMaxLength(200);
            entity.Property(a => a.SyncedHash).HasMaxLength(64);
            entity.Property(a => a.LastError).HasMaxLength(1000);
            entity.HasIndex(a => new { a.IntegrationId, a.ClientId, a.PatchPolicyId }).IsUnique();
            entity.HasIndex(a => a.PatchPolicyId);
            entity.HasOne<Integration>().WithMany().HasForeignKey(a => a.IntegrationId).OnDelete(DeleteBehavior.Cascade);
            ClientOwned(entity);
        });

        // Changes the product still has to make (0.6.0). A tenant waiting to be created goes with its client; a deletion
        // has no client left and stays until it is done or dismissed.
        builder.Entity<IntegrationOperation>(entity =>
        {
            entity.Property(o => o.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(o => o.ExternalTenantId).HasMaxLength(200);
            entity.Property(o => o.ExternalGroupId).HasMaxLength(200);
            entity.Property(o => o.ExternalEndpointId).HasMaxLength(200);
            entity.Property(o => o.Name).HasMaxLength(200);
            entity.Property(o => o.LastError).HasMaxLength(1000);
            entity.HasIndex(o => new { o.IntegrationId, o.NextAttemptAt });
            entity.HasIndex(o => o.TargetClientId);
            entity.HasOne<Integration>().WithMany().HasForeignKey(o => o.IntegrationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Client>().WithMany().HasForeignKey(o => o.TargetClientId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<NotificationChannelClient>(entity =>
        {
            entity.HasKey(c => new { c.NotificationChannelId, c.ClientId });
            entity.HasIndex(c => c.ClientId);
            entity.HasOne<Client>().WithMany().HasForeignKey(c => c.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OutboxWebhook>(entity =>
        {
            entity.Property(w => w.Category).HasMaxLength(50);
            entity.Property(w => w.LastError).HasMaxLength(1000);
            entity.HasIndex(w => w.NextAttemptAt).HasFilter("\"SentAt\" IS NULL").HasDatabaseName("IX_OutboxWebhooks_Pending");
            entity.HasIndex(w => new { w.NotificationChannelId, w.CreatedAt });
            entity.HasOne<NotificationChannel>().WithMany().HasForeignKey(w => w.NotificationChannelId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OutboxEmail>(entity =>
        {
            entity.Property(e => e.ToAddress).HasMaxLength(320);
            entity.Property(e => e.Subject).HasMaxLength(500);
            entity.Property(e => e.Category).HasMaxLength(50);
            entity.Property(e => e.LastError).HasMaxLength(1000);
            entity.HasIndex(e => e.NextAttemptAt).HasFilter("\"SentAt\" IS NULL").HasDatabaseName("IX_OutboxEmails_Pending");
            // What was sent to one address lately, for the flood limit (0.6.0).
            entity.HasIndex(e => new { e.ToAddress, e.CreatedAt });
        });

        // The security monitor (0.7.0): instance-wide, no client. Networks are canonical CIDR text (IpNetworks.Format).
        builder.Entity<SecuritySignal>(entity =>
        {
            // Hypertable-friendly key, like PerfMinutes: the minute and an identity.
            entity.HasKey(s => new { s.Minute, s.Id });
            entity.Property(s => s.Id).UseIdentityByDefaultColumn();
            entity.Property(s => s.Component).HasMaxLength(20);
            entity.Property(s => s.Kind).HasConversion<string>().HasMaxLength(40);
            entity.Property(s => s.Network).HasMaxLength(50);
            entity.Property(s => s.Subject).HasMaxLength(Security.SecuritySignalRecorder.MaxSubjectLength);
            entity.HasIndex(s => new { s.Kind, s.Minute });
            entity.HasIndex(s => new { s.Network, s.Minute });
        });

        builder.Entity<IpBlock>(entity =>
        {
            entity.Property(b => b.Network).HasMaxLength(50);
            entity.Property(b => b.Source).HasConversion<string>().HasMaxLength(20);
            entity.Property(b => b.Reason).HasMaxLength(500);
            entity.Property(b => b.CreatedByName).HasMaxLength(256);
            entity.HasIndex(b => b.Network).IsUnique();
            entity.HasIndex(b => b.ExpiresAt);
        });

        builder.Entity<IpAllow>(entity =>
        {
            entity.Property(a => a.Network).HasMaxLength(50);
            entity.Property(a => a.Description).HasMaxLength(500);
            entity.Property(a => a.CreatedByName).HasMaxLength(256);
            entity.HasIndex(a => a.Network).IsUnique();
        });

        builder.Entity<SecurityIncident>(entity =>
        {
            entity.Property(i => i.Key).HasMaxLength(200);
            entity.Property(i => i.Kind).HasConversion<string>().HasMaxLength(40);
            entity.Property(i => i.Severity).HasConversion<string>().HasMaxLength(20);
            entity.Property(i => i.Title).HasMaxLength(500);
            entity.Property(i => i.Detail).HasMaxLength(2000);
            entity.Property(i => i.Network).HasMaxLength(50);
            entity.Property(i => i.AcknowledgedByName).HasMaxLength(256);
            entity.Property(i => i.AcknowledgeComment).HasMaxLength(SecurityIncident.MaxCommentLength);
            entity.Property(i => i.ResolvedByName).HasMaxLength(256);
            entity.Property(i => i.ResolveComment).HasMaxLength(SecurityIncident.MaxCommentLength);
            // At most one open incident per key, so a concurrent pass cannot open it twice.
            entity.HasIndex(i => i.Key).IsUnique().HasFilter("\"ResolvedAt\" IS NULL").HasDatabaseName("IX_SecurityIncidents_OpenKey");
            entity.HasIndex(i => i.ResolvedAt);
            entity.HasIndex(i => i.OpenedAt);
        });

        builder.Entity<IpFeed>(entity =>
        {
            entity.HasKey(f => f.Key);
            entity.Property(f => f.Key).HasMaxLength(50);
            entity.Property(f => f.Problem).HasMaxLength(1000);
        });

        builder.Entity<IpFeedEntry>(entity =>
        {
            entity.HasKey(e => new { e.Feed, e.Network });
            entity.Property(e => e.Feed).HasMaxLength(50);
            entity.Property(e => e.Network).HasMaxLength(50);
            entity.HasOne<IpFeed>().WithMany().HasForeignKey(e => e.Feed).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AdvisorySource>(entity =>
        {
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasMaxLength(50);
            entity.Property(s => s.Problem).HasMaxLength(1000);
        });

        builder.Entity<Advisory>(entity =>
        {
            entity.Property(a => a.Source).HasMaxLength(50);
            entity.Property(a => a.SourceId).HasMaxLength(AdvisoryRules.MaxSourceIdLength);
            entity.Property(a => a.Language).HasMaxLength(10);
            entity.Property(a => a.Title).HasMaxLength(AdvisoryRules.MaxTitleLength);
            entity.Property(a => a.Summary).HasMaxLength(AdvisoryRules.MaxSummaryLength);
            entity.Property(a => a.Link).HasMaxLength(AdvisoryRules.MaxLinkLength);
            entity.HasOne<AdvisorySource>().WithMany().HasForeignKey(a => a.Source).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(a => new { a.Source, a.SourceId }).IsUnique();
            entity.HasIndex(a => a.PublishedAt);
        });

        builder.Entity<AdvisoryHandling>(entity =>
        {
            entity.HasKey(h => h.AdvisoryId);
            entity.Property(h => h.UserName).HasMaxLength(256);
            entity.Property(h => h.Comment).HasMaxLength(AdvisoryRules.MaxCommentLength);
            entity.HasOne<Advisory>().WithOne().HasForeignKey<AdvisoryHandling>(h => h.AdvisoryId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<VulnerabilityFeed>(entity =>
        {
            entity.HasKey(f => f.Key);
            entity.Property(f => f.Key).HasMaxLength(50);
            entity.Property(f => f.Problem).HasMaxLength(1000);
            entity.Property(f => f.ETag).HasMaxLength(200);
            entity.Property(f => f.LastModified).HasMaxLength(100);
            entity.Property(f => f.Scope).HasMaxLength(4000);
        });

        builder.Entity<VulnerabilityFeedRecord>(entity =>
        {
            entity.Property(r => r.Id).UseIdentityAlwaysColumn();
            entity.Property(r => r.Feed).HasMaxLength(50);
            entity.Property(r => r.Distribution).HasMaxLength(20);
            entity.Property(r => r.Release).HasMaxLength(20);
            entity.Property(r => r.Package).HasMaxLength(200);
            entity.Property(r => r.Cve).HasMaxLength(30);
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.FixedVersion).HasMaxLength(200);
            entity.Property(r => r.ProService).HasMaxLength(30);
            entity.Property(r => r.Severity).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.SourceSeverity).HasMaxLength(50);
            entity.Property(r => r.Cvss).HasPrecision(3, 1);
            entity.Property(r => r.Advisory).HasMaxLength(50);
            entity.Property(r => r.KernelPattern).HasMaxLength(500);
            entity.Property(r => r.ModuleStreams).HasMaxLength(500);
            entity.HasOne<VulnerabilityFeed>().WithMany().HasForeignKey(r => r.Feed).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(r => new { r.Distribution, r.Release, r.Package });
            entity.HasIndex(r => r.Feed);
        });

        builder.Entity<VulnerabilityCve>(entity =>
        {
            entity.HasKey(c => c.Cve);
            entity.Property(c => c.Cve).HasMaxLength(30);
            entity.Property(c => c.UbuntuPriority).HasConversion<string>().HasMaxLength(20);
        });

        builder.Entity<VulnerabilityExploit>(entity =>
        {
            entity.HasKey(x => x.Cve);
            entity.Property(x => x.Cve).HasMaxLength(30);
            // The exploited CVEs are few (about 1,800 of 395,000), and the counts read them by this index.
            entity.HasIndex(x => x.Cve).HasFilter("\"Exploited\"").HasDatabaseName("IX_VulnerabilityExploits_Exploited");
        });

        builder.Entity<EndpointVulnerability>(entity =>
        {
            entity.Property(v => v.Id).UseIdentityAlwaysColumn();
            entity.Property(v => v.Cve).HasMaxLength(30);
            entity.Property(v => v.Package).HasMaxLength(200);
            entity.Property(v => v.InstalledVersion).HasMaxLength(200);
            entity.Property(v => v.FixedVersion).HasMaxLength(200);
            entity.Property(v => v.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(v => v.Severity).HasConversion<string>().HasMaxLength(20);
            entity.Property(v => v.SourceSeverity).HasMaxLength(50);
            entity.Property(v => v.Cvss).HasPrecision(3, 1);
            entity.Property(v => v.Advisory).HasMaxLength(50);
            entity.Property(v => v.EndReason).HasConversion<string>().HasMaxLength(20);
            // At most one open finding per CVE and package of an endpoint, so a concurrent pass cannot open it twice.
            entity.HasIndex(v => new { v.EndpointId, v.Cve, v.Package }).IsUnique().HasFilter("\"EndedAt\" IS NULL")
                .HasDatabaseName("IX_EndpointVulnerabilities_Open");
            // The endpoints one CVE affects, in every client or in one (step 2).
            entity.HasIndex(v => new { v.Cve, v.ClientId }).HasFilter("\"EndedAt\" IS NULL").HasDatabaseName("IX_EndpointVulnerabilities_Cve");
            entity.HasIndex(v => v.EndedAt);
            EndpointChild(entity, v => new { v.EndpointId, v.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<VulnerabilityCveCount>(entity =>
        {
            entity.HasKey(c => new { c.ClientId, c.SiteId, c.Cve });
            entity.Property(c => c.Cve).HasMaxLength(30);
            entity.HasOne<Site>().WithMany()
                .HasForeignKey(c => new { c.SiteId, c.ClientId })
                .HasPrincipalKey(s => new { s.Id, s.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            WholeSiteOwned(entity);
        });

        builder.Entity<EndpointVulnerabilitySummary>(entity =>
        {
            entity.HasKey(s => s.EndpointId);
            entity.Property(s => s.Coverage).HasConversion<string>().HasMaxLength(30);
            entity.Property(s => s.Distribution).HasMaxLength(50);
            entity.Property(s => s.Release).HasMaxLength(50);
            entity.Property(s => s.PackageLists).HasMaxLength(20);
            entity.Property(s => s.InventoryHash).HasMaxLength(64);
            entity.Property(s => s.FeedVersion).HasMaxLength(500);
            entity.HasIndex(s => s.ClientId);
            EndpointChild(entity, s => new { s.EndpointId, s.ClientId });
            EndpointOwned(entity);
        });

        builder.Entity<UserSignInNetwork>(entity =>
        {
            entity.HasKey(n => new { n.UserId, n.Network });
            entity.Property(n => n.Network).HasMaxLength(50);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(n => new { n.Network, n.LastSeen });
        });

        // Instance health (0.6.0): instance-wide, no client, no personal data.
        builder.Entity<InstanceHealthSample>(entity =>
        {
            entity.Property(s => s.DetailJson).HasColumnType("jsonb");
            entity.HasIndex(s => s.Time);
        });

        builder.Entity<InstanceHealthIssue>(entity =>
        {
            entity.Property(i => i.Key).HasMaxLength(100);
            entity.Property(i => i.Component).HasConversion<string>().HasMaxLength(20);
            entity.Property(i => i.Severity).HasConversion<string>().HasMaxLength(20);
            entity.Property(i => i.Title).HasMaxLength(500);
            entity.Property(i => i.Detail).HasMaxLength(2000);
            // At most one open issue per key, so a concurrent pass cannot open it twice.
            entity.HasIndex(i => i.Key).IsUnique().HasFilter("\"ResolvedAt\" IS NULL").HasDatabaseName("IX_InstanceHealthIssues_OpenKey");
            entity.HasIndex(i => i.ResolvedAt);
        });

        builder.Entity<BackupRun>(entity =>
        {
            entity.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.ObjectKey).HasMaxLength(500);
            entity.Property(r => r.Error).HasMaxLength(2000);
            entity.HasIndex(r => r.StartedAt);
        });

        builder.Entity<WorkerWatermark>(entity =>
        {
            entity.HasKey(w => w.Name);
            entity.Property(w => w.Name).HasMaxLength(100);
        });

        ConfigurePerformance(builder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceClientScopeOnWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceClientScopeOnWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Refuses to add, change or delete a client-owned row outside the caller's scope, including a row whose
    /// ClientId would be moved to a client outside the scope.
    /// </summary>
    private void EnforceClientScopeOnWrites()
    {
        if (_scope.AllClients)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            Guid? clientId = entry.Entity switch
            {
                Client c => c.Id,
                _ => entry.Metadata.FindProperty("ClientId") is { } property ? (Guid?)entry.Property(property.Name).CurrentValue : null
            };

            var isGlobalCapable = entry.Metadata.FindProperty("ClientId")?.IsNullable == true;
            if (clientId is null)
            {
                // Global templates and policies are instance-wide; a limited scope changes them only after its service
                // checked it may (0.6.0). An audit entry without a client is a record, not instance-wide data.
                if (isGlobalCapable && entry.Entity is not AuditEntry && !_instanceWideWritesAllowed)
                {
                    throw new UnauthorizedAccessException("A client-restricted caller cannot change instance-wide data.");
                }

                continue;
            }

            if (!MayWrite(entry, clientId.Value))
            {
                throw new UnauthorizedAccessException("The change touches a client outside the caller's scope.");
            }
        }
    }

    /// <summary>
    /// Whether a limited scope may write this row (0.6.0): a row of an endpoint needs the endpoint, a row of a site the whole
    /// site, any other row of a client the whole client. A few records an endpoint action writes on client level (the audit
    /// entry, the signing request, a deployment and its updates, whose targets are checked per endpoint) need the client only.
    /// </summary>
    private bool MayWrite(EntityEntry entry, Guid clientId)
    {
        Guid? endpointId = entry.Entity switch
        {
            Endpoint e => e.Id,
            _ => entry.Metadata.FindProperty("EndpointId") is { } property ? (Guid?)entry.Property(property.Name).CurrentValue : null
        };
        if (endpointId is { } endpoint)
        {
            return _scope.SeesEndpoint(clientId, endpoint);
        }

        Guid? siteId = entry.Entity switch
        {
            Site s => s.Id,
            _ => entry.Metadata.FindProperty("SiteId") is { } property ? (Guid?)entry.Property(property.Name).CurrentValue : null
        };
        if (siteId is { } site)
        {
            return _scope.HasWholeSite(clientId, site);
        }

        // A run of a task started by hand (0.6.0) is written per client; its targets are checked per endpoint.
        return entry.Entity is AuditEntry or SigningRequest or PatchDeployment or PatchDeploymentUpdate or AutomationTaskRun
            ? _scope.SeesClient(clientId)
            : _scope.HasWholeClient(clientId);
    }

    /// <summary>Key, lengths, the check foreign key and the retention index of an hourly or daily rollup.</summary>
    private static void Rollup<T>(EntityTypeBuilder<T> entity) where T : class
    {
        entity.HasKey("EndpointId", "CheckDefinitionId", "Target", "Bucket");
        entity.Property<string>("Target").HasMaxLength(256);
        entity.HasIndex("Bucket");
        entity.HasOne<CheckDefinition>().WithMany().HasForeignKey("CheckDefinitionId").OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>Maintenance mode columns shared by clients, sites and endpoints (MaintenanceRules).</summary>
    private static void MaintenanceColumns<T>(EntityTypeBuilder<T> entity) where T : class
    {
        entity.Property<string?>("MaintenanceStartedByName").HasMaxLength(200);
        entity.Property<string?>("MaintenanceReason").HasMaxLength(Core.Domain.MaintenanceRules.MaxReasonLength);
        entity.Ignore("Maintenance");
        // The workers find maintenance that expired since their last pass (MaintenanceExpiryService).
        entity.HasIndex("MaintenanceEndsAt").HasFilter("\"MaintenanceEndsAt\" IS NOT NULL");
        entity.ToTable(t => t.HasCheckConstraint($"CK_{typeof(T).Name}s_Maintenance",
            "\"MaintenanceEndsAt\" IS NULL OR \"MaintenanceStartedAt\" IS NOT NULL"));
    }

    /// <summary>The class slot of a link on client or site level (0.6.0): every endpoint, servers or workstations.</summary>
    private static void ClassSlot<T>(EntityTypeBuilder<T> entity) where T : class =>
        entity.Property<CheckAppliesTo>("AppliesTo").HasConversion<string>().HasMaxLength(20).HasDefaultValue(CheckAppliesTo.All)
            .HasSentinel(CheckAppliesTo.All);

    private void ClientOwned<T>(EntityTypeBuilder<T> entity) where T : class
    {
        entity.HasQueryFilter(e => ScopeAllClients || ScopeClientIds.Contains(EF.Property<Guid>(e, "ClientId")));
    }

    private void GlobalOrClientOwned<T>(EntityTypeBuilder<T> entity) where T : class
    {
        entity.HasQueryFilter(e => ScopeAllClients || EF.Property<Guid?>(e, "ClientId") == null ||
                                   ScopeClientIds.Contains(EF.Property<Guid?>(e, "ClientId")!.Value));
    }

    /// <summary>A row of a site (0.6.0): visible with the whole client, or with the site, wholly or in part.</summary>
    private void SiteOwned<T>(EntityTypeBuilder<T> entity, string siteIdProperty = "SiteId") where T : class
    {
        entity.HasQueryFilter(e => ScopeAllClients || ScopeFullClientIds.Contains(EF.Property<Guid>(e, "ClientId")) ||
                                   ScopeSiteIds.Contains(EF.Property<Guid>(e, siteIdProperty)));
    }

    /// <summary>
    /// A count over the endpoints of a site (0.8.0): visible only with the whole client or the whole site, never with part of it, so it
    /// never counts endpoints the caller cannot see.
    /// </summary>
    private void WholeSiteOwned<T>(EntityTypeBuilder<T> entity) where T : class
    {
        entity.HasQueryFilter(e => ScopeAllClients || ScopeFullClientIds.Contains(EF.Property<Guid>(e, "ClientId")) ||
                                   ScopeFullSiteIds.Contains(EF.Property<Guid>(e, "SiteId")));
    }

    /// <summary>A row of an endpoint (0.6.0): visible with the whole client, or with the endpoint.</summary>
    private void EndpointOwned<T>(EntityTypeBuilder<T> entity, string endpointIdProperty = "EndpointId") where T : class
    {
        entity.HasQueryFilter(e => ScopeAllClients || ScopeFullClientIds.Contains(EF.Property<Guid>(e, "ClientId")) ||
                                   ScopeEndpointIds.Contains(EF.Property<Guid>(e, endpointIdProperty)));
    }

    /// <summary>A check of a template is visible with its client (or everywhere when global); a check of one endpoint with the endpoint.</summary>
    private void CheckDefinitionOwned(EntityTypeBuilder<CheckDefinition> entity)
    {
        entity.HasQueryFilter(c => ScopeAllClients || c.ClientId == null ||
                                   (c.EndpointId == null
                                       ? ScopeClientIds.Contains(c.ClientId.Value)
                                       : ScopeFullClientIds.Contains(c.ClientId.Value) || ScopeEndpointIds.Contains(c.EndpointId.Value)));
    }

    private void JobOutputOwned(EntityTypeBuilder<JobOutputChunk> entity)
    {
        entity.HasQueryFilter(c => ScopeAllClients || ScopeFullClientIds.Contains(c.ClientId) ||
                                   Set<Job>().Any(j => j.Id == c.JobId && ScopeEndpointIds.Contains(j.EndpointId)));
    }

    /// <summary>A deployment is visible with its whole client, or with one of the endpoints it targets.</summary>
    private void PatchDeploymentOwned(EntityTypeBuilder<PatchDeployment> entity)
    {
        entity.HasQueryFilter(d => ScopeAllClients || ScopeFullClientIds.Contains(d.ClientId) ||
                                   Set<PatchDeploymentTarget>().Any(t => t.DeploymentId == d.Id && ScopeEndpointIds.Contains(t.EndpointId)));
    }

    private void PatchDeploymentUpdateOwned(EntityTypeBuilder<PatchDeploymentUpdate> entity)
    {
        entity.HasQueryFilter(u => ScopeAllClients || ScopeFullClientIds.Contains(u.ClientId) ||
                                   Set<PatchDeploymentTarget>().Any(t => t.DeploymentId == u.DeploymentId && ScopeEndpointIds.Contains(t.EndpointId)));
    }

    private void PatchDeploymentStepOwned(EntityTypeBuilder<PatchDeploymentStep> entity)
    {
        entity.HasQueryFilter(s => ScopeAllClients || ScopeFullClientIds.Contains(s.ClientId) ||
                                   Set<PatchDeploymentTarget>().Any(t => t.Id == s.TargetId && ScopeEndpointIds.Contains(t.EndpointId)));
    }

    private static void EndpointChild<T>(EntityTypeBuilder<T> entity, System.Linq.Expressions.Expression<Func<T, object?>> foreignKey)
        where T : class
    {
        entity.HasOne<Endpoint>().WithMany()
            .HasForeignKey(foreignKey)
            .HasPrincipalKey(e => new { e.Id, e.ClientId })
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// The performance monitor (0.7.0, PERFORMANCE.md §6). The minute table becomes a hypertable in migration PerformanceMonitor
    /// where TimescaleDB is installed; its key holds the minute, as a hypertable needs, and an identity, because a component that
    /// restarts within a minute writes that minute twice and the reader adds the rows up.
    /// </summary>
    private static void ConfigurePerformance(ModelBuilder builder)
    {
        builder.Entity<PerfMinute>(entity =>
        {
            entity.HasKey(m => new { m.Minute, m.Id });
            entity.Property(m => m.Id).UseIdentityByDefaultColumn();
            entity.Property(m => m.Component).HasMaxLength(20);
            entity.Property(m => m.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(m => m.Key).HasMaxLength(PerfKeys.MaxKeyLength);
            entity.Property(m => m.SubKey).HasMaxLength(PerfKeys.MaxSubKeyLength);
            entity.HasIndex(m => new { m.Kind, m.Minute });
        });

        builder.Entity<PerfHour>(entity =>
        {
            entity.Property(h => h.Component).HasMaxLength(20);
            entity.Property(h => h.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(h => h.Key).HasMaxLength(PerfKeys.MaxKeyLength);
            entity.Property(h => h.SubKey).HasMaxLength(PerfKeys.MaxSubKeyLength);
            entity.HasIndex(h => new { h.Hour, h.Kind });
        });

        builder.Entity<PerfRuntimeMinute>(entity =>
        {
            entity.Property(r => r.Component).HasMaxLength(20);
            entity.Property(r => r.Version).HasMaxLength(50);
            entity.Property(r => r.Level).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(r => new { r.Minute, r.Component });
        });

        builder.Entity<PerfHostMinute>(entity => entity.HasKey(h => h.Minute));

        builder.Entity<PerfEvent>(entity =>
        {
            entity.Property(e => e.Component).HasMaxLength(20);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Key).HasMaxLength(PerfKeys.MaxKeyLength);
            entity.Property(e => e.SubKey).HasMaxLength(PerfKeys.MaxSubKeyLength);
            entity.Property(e => e.Statements).HasMaxLength(200);
            entity.HasIndex(e => e.Time);
        });

        builder.Entity<PerfFingerprint>(entity =>
        {
            entity.HasKey(f => new { f.Component, f.Hash });
            entity.Property(f => f.Component).HasMaxLength(20);
            entity.Property(f => f.Hash).HasMaxLength(16);
            entity.Property(f => f.Sql).HasMaxLength(SqlFingerprint.MaxLength);
            entity.HasIndex(f => f.LastSeen);
            entity.HasIndex(f => f.Hash);
        });

        builder.Entity<PerfStatementSnapshot>(entity =>
        {
            entity.Property(s => s.Query).HasMaxLength(SqlFingerprint.MaxLength);
            entity.HasIndex(s => new { s.Time, s.QueryId });
        });

        builder.Entity<PerfDatabaseSnapshot>(entity =>
        {
            entity.HasKey(s => s.Time);
            entity.Property(s => s.StatementsProblem).HasMaxLength(500);
        });

        builder.Entity<PerfTableSnapshot>(entity =>
        {
            entity.HasKey(t => new { t.Time, t.Table });
            entity.Property(t => t.Table).HasMaxLength(200);
        });

        builder.Entity<PerfMarker>(entity =>
        {
            entity.Property(m => m.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(m => m.Label).HasMaxLength(200);
            entity.HasIndex(m => m.Time);
        });

        builder.Entity<PerfProbe>(entity =>
        {
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(p => p.ItemsJson).HasColumnType("jsonb");
            entity.Property(p => p.Problem).HasMaxLength(PerfProbe.MaxProblemLength);
            entity.HasIndex(p => new { p.Kind, p.RequestedAt });
        });

        builder.Entity<PerformanceSettingsRow>(entity =>
        {
            entity.ToTable("PerformanceSettings");
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.Json).HasColumnType("jsonb");
        });
    }
}
