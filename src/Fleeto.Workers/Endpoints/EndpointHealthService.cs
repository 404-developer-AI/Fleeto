using System.Globalization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleeto.Workers.Endpoints;

/// <summary>
/// Keeps endpoint online state honest and raises offline alerts, every 30 seconds.
/// <list type="bullet">
/// <item>An endpoint (or its watchdog) marked online whose last message is older than three heartbeat intervals of its policy is marked
/// offline. This covers a crashed gateway that could not mark its connections closed (never lie about endpoint state).</item>
/// <item>A managed endpoint whose agent and watchdog are both offline for longer than its policy's <c>OfflineAlertAfterMinutes</c> gets an
/// offline alert, resolved when the agent or the watchdog comes back, when it stops being managed or when the policy turns offline alerts
/// off. No offline alert opens while the endpoint is in maintenance; one still offline afterwards gets it on the next pass.</item>
/// <item>Watchdog (0.2.1): when the watchdog is online but the agent is not, the alert is "Agent service stopped" instead, with the state
/// of the agent service the watchdog reports; when the agent is online but a watchdog that connected before is not, the alert is
/// "Watchdog stopped". Same delay, maintenance and managed rules.</item>
/// </list>
/// </summary>
public sealed class EndpointHealthService : WorkerLoop
{
    public const string ResolvedReasonOnline = "The endpoint is online again";
    public const string ResolvedReasonDisabled = "Offline alerts are turned off in the policy of this site";
    public const string ResolvedReasonWatchdogOnline = "The watchdog of the endpoint is online; a stopped agent is reported as Agent service stopped";
    public const string ResolvedReasonAgentOnline = "The agent is online again";
    public const string ResolvedReasonWatchdogBack = "The watchdog is online again";
    public const string ResolvedReasonBothOffline = "The agent and the watchdog are both offline; the offline alert applies";

    /// <summary>Heartbeat interval bounds, identical to the clamp in AgentConfigBuilder.</summary>
    private const string EffectiveHeartbeatSeconds = """
        LEAST(300, GREATEST(10, COALESCE(
          (SELECT hp."HeartbeatIntervalSeconds" FROM "Policies" hp WHERE hp."Id" =
        """ + " " + EffectivePolicyRules.PolicyIdSql + " " + """
          ), 30)))
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

    private const string MarkStaleWatchdogsOfflineSql = """
        UPDATE "Endpoints" e
        SET "WatchdogOnline" = false, "UpdatedAt" = @now
        WHERE e."WatchdogOnline"
          AND (e."WatchdogLastSeenAt" IS NULL OR e."WatchdogLastSeenAt" < @now - make_interval(secs => 3 *
        """ + " " + EffectiveHeartbeatSeconds + " " + """
        ))
        RETURNING e."Id" AS "Value"
        """;

    private const string EffectivePolicyJoin = """
        CROSS JOIN LATERAL (
          SELECT p."OfflineAlertAfterMinutes", p."OfflineAlertSeverity"
          FROM "Policies" p
          WHERE p."Id" =
        """ + " " + EffectivePolicyRules.PolicyIdSql + " " + """
        ) pol
        """;

    private const string OfflineCandidatesSql = """
        SELECT e."Id" AS "EndpointId", e."ClientId" AS "ClientId", e."Hostname" AS "Hostname",
               COALESCE(e."LastSeenAt", e."EnrolledAt") AS "OfflineSince", pol."OfflineAlertSeverity" AS "Severity"
        FROM "Endpoints" e
        """ + " " + EffectivePolicyJoin + " " + """
        WHERE NOT e."IsOnline" AND NOT e."WatchdogOnline" AND e."Source" = 'Agent' AND e."Tier" = 'Managed' AND @allowsManaged
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

    private const string ResolveWatchdogOnlineSql = """
        UPDATE "Alerts" a
        SET "State" = 'Resolved', "ResolvedAt" = @now, "UpdatedAt" = @now, "ResolvedReason" = @reason
        FROM "Endpoints" e
        WHERE e."Id" = a."EndpointId" AND a."Kind" = 'Offline' AND a."State" <> 'Resolved' AND NOT e."IsOnline" AND e."WatchdogOnline"
        RETURNING a."Id" AS "Value"
        """;

    /// <summary>Resolves service alerts of kind @kind whose condition no longer holds; each statement below adds the condition.</summary>
    private const string ResolveServiceAlertSql = """
        UPDATE "Alerts" a
        SET "State" = 'Resolved', "ResolvedAt" = @now, "UpdatedAt" = @now, "ResolvedReason" = @reason
        FROM "Endpoints" e
        WHERE e."Id" = a."EndpointId" AND a."Kind" = @kind AND a."State" <> 'Resolved' AND
        """;

    private const string ReturningAlertIdSql = """
         RETURNING a."Id" AS "Value"
        """;

    private const string ResolveWhenAgentOnlineSql = ResolveServiceAlertSql + """ e."IsOnline" """ + ReturningAlertIdSql;

    private const string ResolveWhenWatchdogOnlineSql = ResolveServiceAlertSql + """ e."WatchdogOnline" """ + ReturningAlertIdSql;

    private const string ResolveWhenBothOfflineSql = ResolveServiceAlertSql + """ NOT e."IsOnline" AND NOT e."WatchdogOnline" """ + ReturningAlertIdSql;

    private const string ResolveWhenAgentOfflineSql = ResolveServiceAlertSql + """ NOT e."IsOnline" """ + ReturningAlertIdSql;

    private const string ServiceCandidatesSelect = """
        SELECT e."Id" AS "EndpointId", e."ClientId" AS "ClientId", e."Hostname" AS "Hostname",
               COALESCE(cs."ServiceState", 'Unknown') AS "ServiceState", COALESCE(cs."ServiceDetail", '') AS "ServiceDetail",
               pol."OfflineAlertSeverity" AS "Severity",
        """;

    private const string AgentStoppedCandidatesSql = ServiceCandidatesSelect + """
               COALESCE(e."LastSeenAt", e."EnrolledAt") AS "Since"
        FROM "Endpoints" e
        LEFT JOIN "EndpointComponentStates" cs ON cs."EndpointId" = e."Id" AND cs."Component" = 'Agent'
        """ + " " + EffectivePolicyJoin + " " + """
        WHERE NOT e."IsOnline" AND e."WatchdogOnline" AND e."Source" = 'Agent' AND e."Tier" = 'Managed' AND @allowsManaged
          AND pol."OfflineAlertAfterMinutes" > 0
          AND COALESCE(e."LastSeenAt", e."EnrolledAt") < @now - make_interval(mins => pol."OfflineAlertAfterMinutes")
          AND NOT EXISTS (SELECT 1 FROM "Alerts" a WHERE a."EndpointId" = e."Id" AND a."Kind" = 'AgentStopped' AND a."State" <> 'Resolved')
          AND NOT
        """ + " " + MaintenanceSql.EndpointInMaintenance + " " + """
        ORDER BY e."Id"
        LIMIT 2000
        """;

    private const string WatchdogStoppedCandidatesSql = ServiceCandidatesSelect + """
               COALESCE(e."WatchdogLastSeenAt", e."EnrolledAt") AS "Since"
        FROM "Endpoints" e
        LEFT JOIN "EndpointComponentStates" cs ON cs."EndpointId" = e."Id" AND cs."Component" = 'Watchdog'
        """ + " " + EffectivePolicyJoin + " " + """
        WHERE e."IsOnline" AND NOT e."WatchdogOnline" AND e."WatchdogVersion" <> '' AND e."Source" = 'Agent' AND e."Tier" = 'Managed' AND @allowsManaged
          AND pol."OfflineAlertAfterMinutes" > 0
          AND COALESCE(e."WatchdogLastSeenAt", e."EnrolledAt") < @now - make_interval(mins => pol."OfflineAlertAfterMinutes")
          AND NOT EXISTS (SELECT 1 FROM "Alerts" a WHERE a."EndpointId" = e."Id" AND a."Kind" = 'WatchdogStopped' AND a."State" <> 'Resolved')
          AND NOT
        """ + " " + MaintenanceSql.EndpointInMaintenance + " " + """
        ORDER BY e."Id"
        LIMIT 2000
        """;

    private const string ResolveDisabledSql = """
        UPDATE "Alerts" a
        SET "State" = 'Resolved', "ResolvedAt" = @now, "UpdatedAt" = @now, "ResolvedReason" = @reason
        FROM "Endpoints" e
        """ + " " + EffectivePolicyJoin + " " + """
        WHERE e."Id" = a."EndpointId" AND a."Kind" = @kind AND a."State" <> 'Resolved' AND pol."OfflineAlertAfterMinutes" <= 0
        RETURNING a."Id" AS "Value"
        """;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly LicenseService _licenses;
    private readonly AlertNotificationService _notifier;

    public EndpointHealthService(IFleetoDbContextFactory dbFactory, INotificationBus bus, LicenseService licenses,
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
        var serviceAlerts = await UpdateServiceAlertsAsync(cancellationToken);
        return opened >= 2000 || serviceAlerts >= 2000;
    }

    /// <summary>Marks endpoints offline whose agent stopped sending. Returns their ids.</summary>
    public async Task<IReadOnlyList<Guid>> MarkStaleEndpointsOfflineAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        await using (var db = _dbFactory.CreateSystem())
        {
            ids = await db.Database.SqlQueryRaw<Guid>(MarkStaleOfflineSql, new NpgsqlParameter("now", Time.GetUtcNow().UtcDateTime))
                .ToListAsync(cancellationToken);
            var watchdogs = await db.Database.SqlQueryRaw<Guid>(MarkStaleWatchdogsOfflineSql, new NpgsqlParameter("now", Time.GetUtcNow().UtcDateTime))
                .ToListAsync(cancellationToken);
            if (watchdogs.Count > 0)
            {
                Logger.LogInformation("Marked the watchdog of {Count} endpoint(s) offline: no message within three heartbeat intervals", watchdogs.Count);
                ids = ids.Union(watchdogs).ToList();
            }
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
            resolved.AddRange(await db.Database.SqlQueryRaw<Guid>(ResolveWatchdogOnlineSql,
                new NpgsqlParameter("now", now), new NpgsqlParameter("reason", ResolvedReasonWatchdogOnline)).ToListAsync(cancellationToken));
            resolved.AddRange(await AlertCleanup.ResolveNotManagedAsync(db, AlertKind.Offline, license, now, cancellationToken));
            resolved.AddRange(await db.Database.SqlQueryRaw<Guid>(ResolveDisabledSql,
                new NpgsqlParameter("now", now), new NpgsqlParameter("reason", ResolvedReasonDisabled),
                new NpgsqlParameter("kind", nameof(AlertKind.Offline))).ToListAsync(cancellationToken));
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

    /// <summary>
    /// Resolves and opens the "Agent service stopped" and "Watchdog stopped" alerts (0.2.1). Returns the number opened.
    /// </summary>
    public async Task<int> UpdateServiceAlertsAsync(CancellationToken cancellationToken)
    {
        var license = await _licenses.GetStatusAsync(cancellationToken);
        var now = Time.GetUtcNow().UtcDateTime;
        var transitions = new List<AlertTransition>();
        var openedCount = 0;

        await using (var db = _dbFactory.CreateSystem())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var resolved = new List<Guid>();
            foreach (var (kind, sql, reason) in new (AlertKind, string, string)[]
                     {
                         (AlertKind.AgentStopped, ResolveWhenAgentOnlineSql, ResolvedReasonAgentOnline),
                         (AlertKind.AgentStopped, ResolveWhenBothOfflineSql, ResolvedReasonBothOffline),
                         (AlertKind.WatchdogStopped, ResolveWhenWatchdogOnlineSql, ResolvedReasonWatchdogBack),
                         (AlertKind.WatchdogStopped, ResolveWhenAgentOfflineSql, ResolvedReasonBothOffline)
                     })
            {
                resolved.AddRange(await db.Database.SqlQueryRaw<Guid>(sql,
                    new NpgsqlParameter("now", now), new NpgsqlParameter("reason", reason), new NpgsqlParameter("kind", kind.ToString()))
                    .ToListAsync(cancellationToken));
            }

            foreach (var kind in new[] { AlertKind.AgentStopped, AlertKind.WatchdogStopped })
            {
                resolved.AddRange(await AlertCleanup.ResolveNotManagedAsync(db, kind, license, now, cancellationToken));
                resolved.AddRange(await db.Database.SqlQueryRaw<Guid>(ResolveDisabledSql,
                    new NpgsqlParameter("now", now), new NpgsqlParameter("reason", ResolvedReasonDisabled), new NpgsqlParameter("kind", kind.ToString()))
                    .ToListAsync(cancellationToken));
            }

            transitions.AddRange(resolved.Distinct().Select(id => new AlertTransition(id, NotificationEvent.Resolved)));

            foreach (var (kind, sql) in new[] { (AlertKind.AgentStopped, AgentStoppedCandidatesSql), (AlertKind.WatchdogStopped, WatchdogStoppedCandidatesSql) })
            {
                var candidates = await db.Database.SqlQueryRaw<ServiceCandidate>(sql,
                        new NpgsqlParameter("now", now), new NpgsqlParameter("allowsManaged", license.AllowsManaged))
                    .ToListAsync(cancellationToken);
                foreach (var candidate in candidates)
                {
                    var state = Enum.TryParse<ComponentServiceState>(candidate.ServiceState, out var parsed) ? parsed : ComponentServiceState.Unknown;
                    var since = DateTime.SpecifyKind(candidate.Since, DateTimeKind.Utc);
                    var alert = new Alert
                    {
                        Id = Guid.NewGuid(),
                        ClientId = candidate.ClientId,
                        EndpointId = candidate.EndpointId,
                        Kind = kind,
                        Target = string.Empty,
                        // A stopped agent means no monitoring: as severe as offline. A stopped watchdog leaves monitoring working.
                        Severity = kind == AlertKind.WatchdogStopped ? AlertSeverity.Warning
                            : Enum.TryParse<AlertSeverity>(candidate.Severity, out var severity) ? severity : AlertSeverity.Critical,
                        State = AlertState.Open,
                        Title = kind == AlertKind.AgentStopped ? AgentStoppedTitle(candidate.Hostname, state, since) : WatchdogStoppedTitle(candidate.Hostname, state, since),
                        Detail = candidate.ServiceDetail.Length <= 2000 ? candidate.ServiceDetail : candidate.ServiceDetail[..2000],
                        OpenedAt = now,
                        UpdatedAt = now
                    };
                    db.Alerts.Add(alert);
                    transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Opened));
                    openedCount++;
                }
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                Logger.LogInformation("Service alert conflict with another writer; retrying on the next pass");
                return 0;
            }

            await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (openedCount > 0)
            {
                Logger.LogInformation("Opened {Count} agent or watchdog service alert(s)", openedCount);
            }
        }

        await AlertCleanup.PublishAsync(_bus, transitions.Select(t => t.AlertId), cancellationToken);
        return openedCount;
    }

    /// <summary>Branding §8: cause and next step, from the state of the agent service the watchdog reports.</summary>
    public static string AgentStoppedTitle(string hostname, ComponentServiceState state, DateTime since) => state switch
    {
        ComponentServiceState.Stopped or ComponentServiceState.Stopping =>
            $"The Fleeto agent service on {hostname} is stopped and the watchdog cannot start it. Check the fleeto-agent service on the endpoint.",
        ComponentServiceState.Disabled =>
            $"The Fleeto agent service on {hostname} is disabled on the endpoint. Set its startup type to Automatic to resume monitoring.",
        ComponentServiceState.NotInstalled =>
            $"The Fleeto agent service on {hostname} is not installed, but its watchdog is. Install the agent again with the install command of the site.",
        _ =>
            $"The Fleeto agent on {hostname} has not connected since {since.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC while its watchdog is online. " +
            "Check the agent log on the endpoint."
    };

    /// <summary>Branding §8: cause and next step, from the state of the watchdog service the agent reports.</summary>
    public static string WatchdogStoppedTitle(string hostname, ComponentServiceState state, DateTime since) => state switch
    {
        ComponentServiceState.Stopped or ComponentServiceState.Stopping =>
            $"The Fleeto watchdog service on {hostname} is stopped and the agent cannot start it. Monitoring works, but agent updates and restarts do not; check the fleeto-watchdog service.",
        ComponentServiceState.Disabled =>
            $"The Fleeto watchdog service on {hostname} is disabled on the endpoint. Set its startup type to Automatic so the agent is kept running and updated.",
        ComponentServiceState.NotInstalled =>
            $"The Fleeto watchdog on {hostname} is no longer installed. The agent installs it again with the next update offer from this instance.",
        _ =>
            $"The Fleeto watchdog on {hostname} has not connected since {since.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC. Check the watchdog log on the endpoint."
    };

    /// <summary>Row of the service alert candidate queries.</summary>
    public sealed class ServiceCandidate
    {
        public Guid EndpointId { get; set; }
        public Guid ClientId { get; set; }
        public string Hostname { get; set; } = string.Empty;
        public string ServiceState { get; set; } = string.Empty;
        public string ServiceDetail { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public DateTime Since { get; set; }
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
