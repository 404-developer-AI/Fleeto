using System.Globalization;
using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Infrastructure.Services;
using Fleetify.Workers.Alerts;
using Fleetify.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleetify.Workers.Endpoints;

/// <summary>
/// Keeps endpoint online state honest and raises offline alerts, every 30 seconds.
/// <list type="bullet">
/// <item>An endpoint marked online whose last message is older than three heartbeat intervals of its policy is marked
/// offline. This covers a crashed gateway that could not mark its connections closed (never lie about endpoint state).</item>
/// <item>A managed endpoint offline for longer than its policy's <c>OfflineAlertAfterMinutes</c> gets an offline alert,
/// resolved when it comes back online, when it stops being managed or when the policy turns offline alerts off. No offline
/// alert opens while the endpoint is in maintenance; one still offline afterwards gets it on the next pass.</item>
/// </list>
/// </summary>
public sealed class EndpointHealthService : WorkerLoop
{
    public const string ResolvedReasonOnline = "The endpoint is online again";
    public const string ResolvedReasonDisabled = "Offline alerts are turned off in the policy of this site";

    /// <summary>Heartbeat interval bounds, identical to the clamp in AgentConfigBuilder.</summary>
    private const string EffectiveHeartbeatSeconds = """
        LEAST(300, GREATEST(10, COALESCE(
          (SELECT p."HeartbeatIntervalSeconds" FROM "SitePolicies" sp JOIN "Policies" p ON p."Id" = sp."PolicyId" WHERE sp."SiteId" = e."SiteId"),
          (SELECT p."HeartbeatIntervalSeconds" FROM "Policies" p WHERE p."IsDefault" LIMIT 1),
          30)))
        """;

    private const string MarkStaleOfflineSql = """
        UPDATE "Endpoints" e
        SET "IsOnline" = false, "UpdatedAt" = @now
        WHERE e."IsOnline" AND e."Source" = 'Agent'
          AND (e."LastSeenAt" IS NULL OR e."LastSeenAt" < @now - make_interval(secs => 3 *
        """ + " " + EffectiveHeartbeatSeconds + " " + """
        ))
        RETURNING e."Id" AS "Value"
        """;

    private const string EffectivePolicyJoin = """
        CROSS JOIN LATERAL (
          SELECT p."OfflineAlertAfterMinutes", p."OfflineAlertSeverity"
          FROM "Policies" p
          WHERE p."Id" = COALESCE(
            (SELECT sp."PolicyId" FROM "SitePolicies" sp WHERE sp."SiteId" = e."SiteId"),
            (SELECT d."Id" FROM "Policies" d WHERE d."IsDefault" LIMIT 1))
        ) pol
        """;

    private const string OfflineCandidatesSql = """
        SELECT e."Id" AS "EndpointId", e."ClientId" AS "ClientId", e."Hostname" AS "Hostname",
               COALESCE(e."LastSeenAt", e."EnrolledAt") AS "OfflineSince", pol."OfflineAlertSeverity" AS "Severity"
        FROM "Endpoints" e
        """ + " " + EffectivePolicyJoin + " " + """
        WHERE NOT e."IsOnline" AND e."Source" = 'Agent' AND e."Tier" = 'Managed' AND @allowsManaged
          AND pol."OfflineAlertAfterMinutes" > 0
          AND COALESCE(e."LastSeenAt", e."EnrolledAt") < @now - make_interval(mins => pol."OfflineAlertAfterMinutes")
          AND NOT EXISTS (SELECT 1 FROM "Alerts" a WHERE a."EndpointId" = e."Id" AND a."Kind" = 'Offline' AND a."State" <> 'Resolved')
          AND NOT
        """ + " " + MaintenanceSql.EndpointInMaintenance + " " + """
        ORDER BY e."Id"
        LIMIT 2000
        """;

    private const string ResolveOnlineSql = """
        UPDATE "Alerts" a
        SET "State" = 'Resolved', "ResolvedAt" = @now, "UpdatedAt" = @now, "ResolvedReason" = @reason
        FROM "Endpoints" e
        WHERE e."Id" = a."EndpointId" AND a."Kind" = 'Offline' AND a."State" <> 'Resolved' AND e."IsOnline"
        RETURNING a."Id" AS "Value"
        """;

    private const string ResolveDisabledSql = """
        UPDATE "Alerts" a
        SET "State" = 'Resolved', "ResolvedAt" = @now, "UpdatedAt" = @now, "ResolvedReason" = @reason
        FROM "Endpoints" e
        """ + " " + EffectivePolicyJoin + " " + """
        WHERE e."Id" = a."EndpointId" AND a."Kind" = 'Offline' AND a."State" <> 'Resolved' AND pol."OfflineAlertAfterMinutes" <= 0
        RETURNING a."Id" AS "Value"
        """;

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly LicenseService _licenses;
    private readonly AlertNotificationService _notifier;

    public EndpointHealthService(IFleetifyDbContextFactory dbFactory, INotificationBus bus, LicenseService licenses,
        AlertNotificationService notifier, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<EndpointHealthService> logger)
        : base("endpoint-health", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _licenses = licenses;
        _notifier = notifier;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await MarkStaleEndpointsOfflineAsync(cancellationToken);
        var opened = await UpdateOfflineAlertsAsync(cancellationToken);
        return opened >= 2000;
    }

    /// <summary>Marks endpoints offline whose agent stopped sending. Returns their ids.</summary>
    public async Task<IReadOnlyList<Guid>> MarkStaleEndpointsOfflineAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        await using (var db = _dbFactory.CreateSystem())
        {
            ids = await db.Database.SqlQueryRaw<Guid>(MarkStaleOfflineSql, new NpgsqlParameter("now", Time.GetUtcNow().UtcDateTime))
                .ToListAsync(cancellationToken);
        }

        if (ids.Count > 0)
        {
            Logger.LogInformation("Marked {Count} endpoint(s) offline: no message within three heartbeat intervals", ids.Count);
        }

        foreach (var id in ids)
        {
            await _bus.PublishAsync(NotificationChannels.EndpointStatus, id.ToString(), cancellationToken);
        }

        return ids;
    }

    /// <summary>Resolves offline alerts that no longer apply and opens new ones. Returns the number opened.</summary>
    public async Task<int> UpdateOfflineAlertsAsync(CancellationToken cancellationToken)
    {
        var license = await _licenses.GetStatusAsync(cancellationToken);
        var now = Time.GetUtcNow().UtcDateTime;
        var transitions = new List<AlertTransition>();

        await using (var db = _dbFactory.CreateSystem())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var resolved = await db.Database.SqlQueryRaw<Guid>(ResolveOnlineSql,
                new NpgsqlParameter("now", now), new NpgsqlParameter("reason", ResolvedReasonOnline)).ToListAsync(cancellationToken);
            resolved.AddRange(await AlertCleanup.ResolveNotManagedAsync(db, AlertKind.Offline, license, now, cancellationToken));
            resolved.AddRange(await db.Database.SqlQueryRaw<Guid>(ResolveDisabledSql,
                new NpgsqlParameter("now", now), new NpgsqlParameter("reason", ResolvedReasonDisabled)).ToListAsync(cancellationToken));
            transitions.AddRange(resolved.Distinct().Select(id => new AlertTransition(id, NotificationEvent.Resolved)));

            var candidates = await db.Database.SqlQueryRaw<OfflineCandidate>(OfflineCandidatesSql,
                    new NpgsqlParameter("now", now), new NpgsqlParameter("allowsManaged", license.AllowsManaged))
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                var since = DateTime.SpecifyKind(candidate.OfflineSince, DateTimeKind.Utc);
                var alert = new Alert
                {
                    Id = Guid.NewGuid(),
                    ClientId = candidate.ClientId,
                    EndpointId = candidate.EndpointId,
                    Kind = AlertKind.Offline,
                    Target = string.Empty,
                    Severity = Enum.TryParse<AlertSeverity>(candidate.Severity, out var severity) ? severity : AlertSeverity.Critical,
                    State = AlertState.Open,
                    Title = OfflineTitle(candidate.Hostname, since),
                    Detail = string.Empty,
                    OpenedAt = now,
                    UpdatedAt = now
                };
                db.Alerts.Add(alert);
                transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Opened));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another process opened one of these alerts first. Nothing was committed; the next pass starts over.
                Logger.LogInformation("Offline alert conflict with another writer; retrying on the next pass");
                return 0;
            }

            await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (candidates.Count > 0)
            {
                Logger.LogInformation("Opened {Count} offline alert(s)", candidates.Count);
            }
        }

        await AlertCleanup.PublishAsync(_bus, transitions.Select(t => t.AlertId), cancellationToken);
        return transitions.Count(t => t.Kind == NotificationEvent.Opened);
    }

    /// <summary>Branding §8: cause and next step.</summary>
    public static string OfflineTitle(string hostname, DateTime offlineSince) =>
        $"{hostname} has been offline since {offlineSince.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC. " +
        "Check that the endpoint is powered on and connected.";

    /// <summary>Row of <see cref="OfflineCandidatesSql"/>.</summary>
    public sealed class OfflineCandidate
    {
        public Guid EndpointId { get; set; }
        public Guid ClientId { get; set; }
        public string Hostname { get; set; } = string.Empty;
        public DateTime OfflineSince { get; set; }
        public string Severity { get; set; } = string.Empty;
    }
}
