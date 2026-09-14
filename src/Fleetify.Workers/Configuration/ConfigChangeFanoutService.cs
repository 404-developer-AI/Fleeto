using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleetify.Workers.Configuration;

/// <summary>
/// Fans configuration changes out to endpoints: every unprocessed <see cref="ConfigChangeEvent"/> becomes one
/// AgentConfig signing request per affected agent endpoint, inserted set-based in the same transaction that marks the
/// event processed. The signer then builds and signs each configuration from the database (it skips unchanged ones).
/// Woken by <c>fleetify_config_changes</c>, and polls every 30 seconds so a lost notification only delays work.
/// </summary>
public sealed class ConfigChangeFanoutService : WorkerLoop
{
    /// <summary>Events handled per pass before the loop yields.</summary>
    internal const int EventsPerPass = 100;

    /// <summary>Serializes fan-out across worker processes, so two passes cannot both see "no pending request".</summary>
    private const long FanoutLockKey = 0x466C65657446616E; // "FleetFan"

    // One statement per scope. The dedupe on pending requests only counts requests created at or after the event:
    // a request created earlier may already be in the signer's hands (locked, building from data read before this
    // change committed), and skipping it could lose the change. The workers role cannot see row locks, so time decides.
    private const string InsertPrefix = """
        INSERT INTO "SigningRequests" ("Id", "ClientId", "Kind", "SubjectId", "Payload", "RequestedBy", "State", "CreatedAt")
        SELECT gen_random_uuid(), e."ClientId", 'AgentConfig', e."Id", ''::bytea, 'workers', 'Pending', @now
        FROM "Endpoints" e
        WHERE e."Source" = 'Agent'
          AND NOT EXISTS (
            SELECT 1 FROM "SigningRequests" r
            WHERE r."Kind" = 'AgentConfig' AND r."State" = 'Pending' AND r."SubjectId" = e."Id" AND r."CreatedAt" >= @eventCreatedAt)
        """;

    private const string InsertInstance = InsertPrefix;
    private const string InsertClient = InsertPrefix + """ AND e."ClientId" = @scopeId""";
    private const string InsertSite = InsertPrefix + """ AND e."SiteId" = @scopeId""";
    private const string InsertEndpoint = InsertPrefix + """ AND e."Id" = @scopeId""";

    private const string InsertPolicyLinked = InsertPrefix + """
         AND EXISTS (SELECT 1 FROM "SitePolicies" sp WHERE sp."SiteId" = e."SiteId" AND sp."PolicyId" = @scopeId)
        """;

    // The default policy applies to sites without a linked policy. A deleted policy lost its links through the
    // cascade, so its former sites are now exactly among the sites without a link: the same statement covers both.
    private const string InsertPolicyDefaultOrDeleted = InsertPrefix + """
         AND (NOT EXISTS (SELECT 1 FROM "SitePolicies" sp WHERE sp."SiteId" = e."SiteId")
              OR EXISTS (SELECT 1 FROM "SitePolicies" sp WHERE sp."SiteId" = e."SiteId" AND sp."PolicyId" = @scopeId))
        """;

    private const string InsertMonitoringTemplate = InsertPrefix + """
         AND (EXISTS (SELECT 1 FROM "SiteMonitoringTemplates" l WHERE l."SiteId" = e."SiteId" AND l."MonitoringTemplateId" = @scopeId)
              OR EXISTS (SELECT 1 FROM "EndpointMonitoringTemplates" el WHERE el."EndpointId" = e."Id" AND el."MonitoringTemplateId" = @scopeId))
        """;

    // A deleted monitoring template lost its site and endpoint links; only managed endpoints carry checks, so they are a safe superset.
    private const string InsertMonitoringTemplateDeleted = InsertPrefix + """ AND e."Tier" = 'Managed'""";

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private IDisposable? _subscription;

    public ConfigChangeFanoutService(IFleetifyDbContextFactory dbFactory, INotificationBus bus, WorkerHeartbeat heartbeat,
        TimeProvider time, ILogger<ConfigChangeFanoutService> logger)
        : base("config-fanout", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.ConfigChanges, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken) =>
        await ProcessPendingAsync(cancellationToken) >= EventsPerPass;

    /// <summary>Processes up to <see cref="EventsPerPass"/> events in id order. Returns the number processed.</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        while (processed < EventsPerPass && await ProcessNextAsync(cancellationToken))
        {
            processed++;
        }

        return processed;
    }

    private async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({FanoutLockKey})", cancellationToken);

        var change = (await db.ConfigChangeEvents
                .FromSql($"""SELECT * FROM "ConfigChangeEvents" WHERE "ProcessedAt" IS NULL ORDER BY "Id" LIMIT 1 FOR UPDATE SKIP LOCKED""")
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .FirstOrDefault();
        if (change is null)
        {
            return false;
        }

        var now = Time.GetUtcNow().UtcDateTime;
        var sql = await SelectStatementAsync(db, change, cancellationToken);
        var parameters = new List<NpgsqlParameter>
        {
            new("now", now),
            new("eventCreatedAt", DateTime.SpecifyKind(change.CreatedAt, DateTimeKind.Utc))
        };
        if (sql != InsertInstance && sql != InsertMonitoringTemplateDeleted)
        {
            parameters.Add(new NpgsqlParameter("scopeId", change.ScopeId!.Value));
        }

        var inserted = await db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
        await db.ConfigChangeEvents.Where(e => e.Id == change.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ProcessedAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        Logger.LogInformation("Configuration change {EventId} ({Scope}) requested {Count} agent configuration(s)",
            change.Id, change.Scope, inserted);
        return true;
    }

    private async Task<string> SelectStatementAsync(FleetifyDbContext db, ConfigChangeEvent change, CancellationToken cancellationToken)
    {
        if (change.Scope != ConfigChangeScope.Instance && change.ScopeId is null)
        {
            // A scoped event without an id is a bug in the writer; the safe answer is every endpoint.
            Logger.LogWarning("Configuration change {EventId} has scope {Scope} without a scope id; treating it as instance-wide",
                change.Id, change.Scope);
            return InsertInstance;
        }

        switch (change.Scope)
        {
            case ConfigChangeScope.Instance:
                return InsertInstance;
            case ConfigChangeScope.Client:
                return InsertClient;
            case ConfigChangeScope.Site:
                return InsertSite;
            case ConfigChangeScope.Endpoint:
                return InsertEndpoint;
            case ConfigChangeScope.Policy:
            {
                var policy = await db.Policies.AsNoTracking()
                    .Where(p => p.Id == change.ScopeId)
                    .Select(p => new { p.IsDefault })
                    .FirstOrDefaultAsync(cancellationToken);
                return policy is null || policy.IsDefault ? InsertPolicyDefaultOrDeleted : InsertPolicyLinked;
            }
            case ConfigChangeScope.MonitoringTemplate:
            {
                var exists = await db.MonitoringTemplates.AsNoTracking().AnyAsync(t => t.Id == change.ScopeId, cancellationToken);
                return exists ? InsertMonitoringTemplate : InsertMonitoringTemplateDeleted;
            }
            default:
                Logger.LogWarning("Configuration change {EventId} has unknown scope {Scope}; treating it as instance-wide", change.Id, change.Scope);
                return InsertInstance;
        }
    }
}
