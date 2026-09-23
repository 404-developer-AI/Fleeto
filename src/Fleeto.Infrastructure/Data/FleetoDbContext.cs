using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
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
    public DbSet<Endpoint> Endpoints => Set<Endpoint>();
    public DbSet<AgentCertificate> AgentCertificates => Set<AgentCertificate>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<InventorySnapshot> InventorySnapshots => Set<InventorySnapshot>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<MonitoringTemplate> MonitoringTemplates => Set<MonitoringTemplate>();
    public DbSet<CheckDefinition> CheckDefinitions => Set<CheckDefinition>();
    public DbSet<SiteMonitoringTemplate> SiteMonitoringTemplates => Set<SiteMonitoringTemplate>();
    public DbSet<SitePolicy> SitePolicies => Set<SitePolicy>();
    public DbSet<MaintenanceWindowOccurrence> MaintenanceWindowOccurrences => Set<MaintenanceWindowOccurrence>();
    public DbSet<EndpointMonitoringTemplate> EndpointMonitoringTemplates => Set<EndpointMonitoringTemplate>();
    public DbSet<EndpointCheckOverride> EndpointCheckOverrides => Set<EndpointCheckOverride>();
    public DbSet<CheckRunRequest> CheckRunRequests => Set<CheckRunRequest>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Script> Scripts => Set<Script>();
    public DbSet<ScriptVersion> ScriptVersions => Set<ScriptVersion>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobOutputChunk> JobOutputChunks => Set<JobOutputChunk>();
    public DbSet<RemoteSession> RemoteSessions => Set<RemoteSession>();
    public DbSet<RemoteSessionParticipant> RemoteSessionParticipants => Set<RemoteSessionParticipant>();
    public DbSet<RemoteSessionAction> RemoteSessionActions => Set<RemoteSessionAction>();
    public DbSet<ClientTemplate> ClientTemplates => Set<ClientTemplate>();
    public DbSet<ClientTemplateSite> ClientTemplateSites => Set<ClientTemplateSite>();
    public DbSet<ClientTemplateSiteMonitoringTemplate> ClientTemplateSiteMonitoringTemplates => Set<ClientTemplateSiteMonitoringTemplate>();
    public DbSet<CheckResult> CheckResults => Set<CheckResult>();
    public DbSet<IngestBatch> IngestBatches => Set<IngestBatch>();
    public DbSet<CheckState> CheckStates => Set<CheckState>();
    public DbSet<CheckResultHourly> CheckResultsHourly => Set<CheckResultHourly>();
    public DbSet<CheckResultDaily> CheckResultsDaily => Set<CheckResultDaily>();
    public DbSet<Alert> Alerts => Set<Alert>();
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
    public DbSet<WorkerWatermark> WorkerWatermarks => Set<WorkerWatermark>();
    public DbSet<Integration> Integrations => Set<Integration>();

    /// <summary>Authorization codes of a sign-in with Entra ID that wait for the workers (0.5.0).</summary>
    public DbSet<SignInExchange> SignInExchanges => Set<SignInExchange>();
    public DbSet<EndpointPatchState> EndpointPatchStates => Set<EndpointPatchState>();
    public DbSet<EndpointMissingUpdate> EndpointMissingUpdates => Set<EndpointMissingUpdate>();
    public DbSet<PatchDeployment> PatchDeployments => Set<PatchDeployment>();
    public DbSet<PatchDeploymentUpdate> PatchDeploymentUpdates => Set<PatchDeploymentUpdate>();
    public DbSet<PatchDeploymentTarget> PatchDeploymentTargets => Set<PatchDeploymentTarget>();
    public DbSet<IntegrationMapping> IntegrationMappings => Set<IntegrationMapping>();

    // Evaluated per query by EF Core (context members become query parameters).
    private bool ScopeAllClients => _scope.AllClients;
    private Guid[] ScopeClientIds => _scope.ClientIds as Guid[] ?? _scope.ClientIds.ToArray();

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

        builder.Entity<Site>(entity =>
        {
            entity.Property(s => s.Name).HasMaxLength(100);
            entity.Property(s => s.Description).HasMaxLength(1000);
            entity.HasAlternateKey(s => new { s.Id, s.ClientId });
            entity.HasIndex(s => new { s.ClientId, s.Name }).IsUnique();
            MaintenanceColumns(entity);
            entity.HasOne<ClientTemplateSite>().WithMany().HasForeignKey(s => s.ClientTemplateSiteId).OnDelete(DeleteBehavior.SetNull);
            ClientOwned(entity);
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
            ClientOwned(entity);
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
            ClientOwned(entity);
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
            ClientOwned(entity);
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
            // The patch poller looks an endpoint up by the id its Action1 agent reports (0.4.0).
            entity.HasIndex(i => i.Action1AgentId).HasFilter("\"Action1AgentId\" <> ''");
            entity.Property(i => i.DisksJson).HasColumnType("jsonb");
            entity.Property(i => i.NetworkInterfacesJson).HasColumnType("jsonb");
            entity.Property(i => i.SoftwareJson).HasColumnType("jsonb");
            entity.Property(i => i.ServicesJson).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            ClientOwned(entity);
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
            entity.ToTable(table =>
            {
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
            // A client-specific policy is deleted with its client.
            entity.HasOne<Client>().WithMany().HasForeignKey(p => p.ClientId).OnDelete(DeleteBehavior.Cascade);
            GlobalOrClientOwned(entity);
        });

        builder.Entity<MonitoringTemplate>(entity =>
        {
            entity.Property(t => t.Name).HasMaxLength(100);
            entity.Property(t => t.Description).HasMaxLength(1000);
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
            GlobalOrClientOwned(entity);
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
            ClientOwned(entity);
        });

        builder.Entity<EndpointMonitoringTemplate>(entity =>
        {
            entity.HasKey(l => new { l.EndpointId, l.MonitoringTemplateId });
            EndpointChild(entity, l => new { l.EndpointId, l.ClientId });
            entity.HasOne(l => l.MonitoringTemplate).WithMany().HasForeignKey(l => l.MonitoringTemplateId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.MonitoringTemplateId);
            ClientOwned(entity);
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
            ClientOwned(entity);
        });

        builder.Entity<SitePolicy>(entity =>
        {
            entity.HasKey(l => l.SiteId);
            entity.Property(l => l.Source).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<Site>().WithOne(s => s.Policy)
                .HasForeignKey<SitePolicy>(l => new { l.SiteId, l.ClientId })
                .HasPrincipalKey<Site>(s => new { s.Id, s.ClientId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.Policy).WithMany().HasForeignKey(l => l.PolicyId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.PolicyId);
            ClientOwned(entity);
        });

        builder.Entity<ClientTemplate>(entity =>
        {
            entity.Property(t => t.Name).HasMaxLength(100);
            entity.Property(t => t.Description).HasMaxLength(1000);
            entity.HasIndex(t => t.Name).IsUnique();
            entity.HasMany(t => t.Sites).WithOne().HasForeignKey(s => s.ClientTemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ClientTemplateSite>(entity =>
        {
            entity.Property(s => s.Name).HasMaxLength(100);
            entity.Property(s => s.Description).HasMaxLength(1000);
            entity.HasIndex(s => new { s.ClientTemplateId, s.Name }).IsUnique();
            entity.HasOne<Policy>().WithMany().HasForeignKey(s => s.PolicyId).OnDelete(DeleteBehavior.SetNull);
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
            entity.HasIndex(r => new { r.EndpointId, r.CheckDefinitionId, r.Time });
            // Per-endpoint evaluation cursor in the workers: results with Id > cursor for one endpoint.
            entity.HasIndex(r => new { r.EndpointId, r.Id });
            entity.HasIndex(r => r.Id);
            ClientOwned(entity);
        });

        builder.Entity<IngestBatch>(entity =>
        {
            entity.HasKey(b => new { b.EndpointId, b.Sequence });
            entity.HasIndex(b => b.ReceivedAt);
            EndpointChild(entity, b => new { b.EndpointId, b.ClientId });
            ClientOwned(entity);
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
            ClientOwned(entity);
        });

        builder.Entity<CheckResultHourly>(entity =>
        {
            entity.ToTable("CheckResultsHourly");
            Rollup(entity);
            EndpointChild(entity, r => new { r.EndpointId, r.ClientId });
            ClientOwned(entity);
        });

        builder.Entity<CheckResultDaily>(entity =>
        {
            entity.ToTable("CheckResultsDaily");
            Rollup(entity);
            EndpointChild(entity, r => new { r.EndpointId, r.ClientId });
            ClientOwned(entity);
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
            ClientOwned(entity);
        });

        builder.Entity<Note>(entity =>
        {
            entity.Property(n => n.AuthorName).HasMaxLength(200);
            entity.Property(n => n.Body).HasMaxLength(Note.MaxBodyLength);
            EndpointChild(entity, n => new { n.EndpointId, n.ClientId });
            // Newest first per endpoint, keyset on (CreatedAt, Id).
            entity.HasIndex(n => new { n.EndpointId, n.CreatedAt, n.Id }).IsDescending(false, true, true);
            entity.ToTable(t => t.HasCheckConstraint("CK_Notes_Body", "char_length(\"Body\") BETWEEN 1 AND 20000"));
            ClientOwned(entity);
        });

        builder.Entity<Script>(entity =>
        {
            entity.Property(s => s.Name).HasMaxLength(100);
            entity.Property(s => s.Description).HasMaxLength(1000);
            entity.Property(s => s.Language).HasConversion<string>().HasMaxLength(20);
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
            // Public API: the job list across endpoints, newest first, with keyset pagination (0.2.1).
            entity.HasIndex(j => new { j.CreatedAt, j.Id }).IsDescending(true, true);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Jobs_Validity", "\"ValidUntil\" > \"CreatedAt\" AND \"ValidUntil\" <= \"CreatedAt\" + interval '7 days 5 minutes'");
                t.HasCheckConstraint("CK_Jobs_Signed", "\"State\" IN ('PendingSignature', 'Refused', 'Cancelled', 'Expired') OR \"Signature\" IS NOT NULL");
            });
            ClientOwned(entity);
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
            ClientOwned(entity);
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
            ClientOwned(entity);
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
            ClientOwned(entity);
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
            ClientOwned(entity);
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
            ClientOwned(entity);
        });

        builder.Entity<EndpointEvent>(entity =>
        {
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Detail).HasMaxLength(1000);
            entity.HasIndex(e => e.Id).HasFilter("\"ProcessedAt\" IS NULL").HasDatabaseName("IX_EndpointEvents_Unprocessed");
            entity.HasIndex(e => new { e.EndpointId, e.Time });
            EndpointChild(entity, e => new { e.EndpointId, e.ClientId });
            ClientOwned(entity);
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
            ClientOwned(entity);
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
            ClientOwned(entity);
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
            GlobalOrClientOwned(entity);
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
            // The dashboard and the client rollups count per client: how many endpoints have patch state, and how many of
            // those miss something.
            entity.HasIndex(p => new { p.ClientId, p.MissingCritical });
            EndpointChild(entity, p => new { p.EndpointId, p.ClientId });
            ClientOwned(entity);
        });

        builder.Entity<EndpointMissingUpdate>(entity =>
        {
            entity.Property(u => u.ExternalUpdateId).HasMaxLength(200);
            entity.Property(u => u.Name).HasMaxLength(300);
            entity.Property(u => u.Vendor).HasMaxLength(200);
            entity.Property(u => u.Version).HasMaxLength(100);
            entity.Property(u => u.KbNumber).HasMaxLength(20);
            entity.Property(u => u.Severity).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(u => new { u.EndpointId, u.Severity });
            EndpointChild(entity, u => new { u.EndpointId, u.ClientId });
            ClientOwned(entity);
        });

        builder.Entity<PatchDeployment>(entity =>
        {
            entity.Property(d => d.ExternalTenantId).HasMaxLength(200);
            entity.Property(d => d.ExternalDeploymentId).HasMaxLength(200);
            entity.Property(d => d.Scope).HasConversion<string>().HasMaxLength(20);
            entity.Property(d => d.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(d => d.StatusMessage).HasMaxLength(1000);
            entity.Property(d => d.RequestedByName).HasMaxLength(200);
            entity.Ignore(d => d.IsOpen);
            entity.HasAlternateKey(d => new { d.Id, d.ClientId });
            entity.HasOne<Client>().WithMany().HasForeignKey(d => d.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(d => d.Updates).WithOne().HasForeignKey(u => u.DeploymentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(d => d.Targets).WithOne().HasForeignKey(t => t.DeploymentId).OnDelete(DeleteBehavior.Cascade);
            // The workers pick up what still needs work; the UI reads the newest per client.
            entity.HasIndex(d => d.State).HasFilter("\"State\" IN ('Requested', 'Running')");
            entity.HasIndex(d => new { d.ClientId, d.RequestedAt }).IsDescending(false, true);
            entity.HasIndex(d => d.BatchId);
            ClientOwned(entity);
        });

        builder.Entity<PatchDeploymentUpdate>(entity =>
        {
            entity.Property(u => u.ExternalUpdateId).HasMaxLength(200);
            entity.Property(u => u.Name).HasMaxLength(300);
            entity.Property(u => u.Version).HasMaxLength(100);
            entity.HasIndex(u => u.DeploymentId);
            ClientOwned(entity);
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
            EndpointChild(entity, t => new { t.EndpointId, t.ClientId });
            ClientOwned(entity);
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

        builder.Entity<Integration>(entity =>
        {
            entity.Property(i => i.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(i => i.Region).HasConversion<string>().HasMaxLength(30);
            entity.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(i => i.EncryptedCredentials).HasMaxLength(8000);
            entity.Property(i => i.CredentialName).HasMaxLength(200);
            entity.Property(i => i.StatusMessage).HasMaxLength(1000);
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
            entity.HasOne<Client>().WithMany().HasForeignKey(m => m.ClientId).OnDelete(DeleteBehavior.Cascade);
            ClientOwned(entity);
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

        var allowed = _scope.ClientIds;
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
                if (isGlobalCapable)
                {
                    // Global templates and policies are instance-wide; restricted scopes may not change them.
                    throw new UnauthorizedAccessException("A client-restricted caller cannot change instance-wide data.");
                }

                continue;
            }

            if (!allowed.Contains(clientId.Value))
            {
                throw new UnauthorizedAccessException("The change touches a client outside the caller's scope.");
            }
        }
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

    private void ClientOwned<T>(EntityTypeBuilder<T> entity) where T : class
    {
        entity.HasQueryFilter(e => ScopeAllClients || ScopeClientIds.Contains(EF.Property<Guid>(e, "ClientId")));
    }

    private void GlobalOrClientOwned<T>(EntityTypeBuilder<T> entity) where T : class
    {
        entity.HasQueryFilter(e => ScopeAllClients || EF.Property<Guid?>(e, "ClientId") == null ||
                                   ScopeClientIds.Contains(EF.Property<Guid?>(e, "ClientId")!.Value));
    }

    private static void EndpointChild<T>(EntityTypeBuilder<T> entity, System.Linq.Expressions.Expression<Func<T, object?>> foreignKey)
        where T : class
    {
        entity.HasOne<Endpoint>().WithMany()
            .HasForeignKey(foreignKey)
            .HasPrincipalKey(e => new { e.Id, e.ClientId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
