using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fleetify.Infrastructure.Data;

/// <summary>
/// The instance database. Client isolation is enforced here in two ways (ARCHITECTURE.md §2):
/// <list type="bullet">
/// <item>Global query filters on the ClientId that every client-owned table carries.</item>
/// <item>A write guard in SaveChanges that refuses rows outside the caller's client scope.</item>
/// </list>
/// Consistency of the denormalized ClientId is enforced by the database itself: composite foreign keys for
/// required ClientIds, constraint triggers for nullable ones (migration DatabaseRules).
/// <para>
/// Create one context per operation through <see cref="IFleetifyDbContextFactory"/>; a Blazor circuit must never
/// share a context between concurrent events.
/// </para>
/// </summary>
public class FleetifyDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    private readonly IClientScope _scope;

    public FleetifyDbContext(DbContextOptions<FleetifyDbContext> options, IClientScope? scope = null)
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
    public DbSet<ClientTemplate> ClientTemplates => Set<ClientTemplate>();
    public DbSet<ClientTemplateSite> ClientTemplateSites => Set<ClientTemplateSite>();
    public DbSet<ClientTemplateSiteMonitoringTemplate> ClientTemplateSiteMonitoringTemplates => Set<ClientTemplateSiteMonitoringTemplate>();
    public DbSet<CheckResult> CheckResults => Set<CheckResult>();
    public DbSet<IngestBatch> IngestBatches => Set<IngestBatch>();
    public DbSet<CheckState> CheckStates => Set<CheckState>();
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
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<OutboxEmail> OutboxEmails => Set<OutboxEmail>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<WorkerWatermark> WorkerWatermarks => Set<WorkerWatermark>();

    // Evaluated per query by EF Core (context members become query parameters).
    private bool ScopeAllClients => _scope.AllClients;
    private Guid[] ScopeClientIds => _scope.ClientIds as Guid[] ?? _scope.ClientIds.ToArray();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.DisplayName).HasMaxLength(200);
        });

        builder.Entity<Client>(entity =>
        {
            entity.Property(c => c.Code).HasMaxLength(16);
            entity.Property(c => c.Name).HasMaxLength(200);
            entity.HasIndex(c => c.Code).IsUnique();
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
            entity.Ignore(e => e.EffectiveClass);
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
            entity.Property(i => i.DisksJson).HasColumnType("jsonb");
            entity.Property(i => i.NetworkInterfacesJson).HasColumnType("jsonb");
            entity.Property(i => i.SoftwareJson).HasColumnType("jsonb");
            ClientOwned(entity);
        });

        builder.Entity<Policy>(entity =>
        {
            entity.Property(p => p.Name).HasMaxLength(100);
            entity.Property(p => p.Description).HasMaxLength(1000);
            entity.Property(p => p.OfflineAlertSeverity).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(p => new { p.ClientId, p.Name }).IsUnique().AreNullsDistinct(false);
            entity.HasIndex(p => p.IsDefault).IsUnique().HasFilter("\"IsDefault\"");
            GlobalOrClientOwned(entity);
        });

        builder.Entity<MonitoringTemplate>(entity =>
        {
            entity.Property(t => t.Name).HasMaxLength(100);
            entity.Property(t => t.Description).HasMaxLength(1000);
            entity.HasIndex(t => new { t.ClientId, t.Name }).IsUnique().AreNullsDistinct(false);
            entity.HasMany(t => t.Checks).WithOne(c => c.MonitoringTemplate).HasForeignKey(c => c.MonitoringTemplateId).OnDelete(DeleteBehavior.Cascade);
            GlobalOrClientOwned(entity);
        });

        builder.Entity<CheckDefinition>(entity =>
        {
            entity.Property(c => c.Name).HasMaxLength(100);
            entity.Property(c => c.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(c => c.AppliesTo).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.ParametersJson).HasColumnType("jsonb");
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
            entity.HasOne<CheckDefinition>().WithMany().HasForeignKey(s => s.CheckDefinitionId).OnDelete(DeleteBehavior.Cascade);
            EndpointChild(entity, s => new { s.EndpointId, s.ClientId });
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
