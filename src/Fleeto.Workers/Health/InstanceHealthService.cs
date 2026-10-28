using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Email;
using Fleeto.Infrastructure.Notifications;
using Fleeto.Infrastructure.Performance;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Email;
using Fleeto.Core.Performance;
using Fleeto.Workers.Hosting;
using Fleeto.Workers.Performance;
using Fleeto.Workers.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Health;

/// <summary>
/// Instance health (0.6.0, ARCHITECTURE.md §4, Instance health): every 5 minutes a sample of the VPS (disk, memory, CPU from
/// /proc), the database (size, connections, largest tables) and the instance (stuck worker tasks, backups, waiting
/// notifications and events), judged by <see cref="InstanceHealthRules"/>. A finding opens an issue, a worse one escalates it,
/// and one that stays away for 15 minutes resolves it, so a value around a threshold does not flap. Every transition goes to the
/// notification channels an admin chose for instance health, in the same transaction. Once an hour the costliest statements
/// are read from pg_stat_statements for the diagnostics. Sizes and counts only: no personal data.
/// </summary>
public sealed class InstanceHealthService : WorkerLoop
{
    public const string CategoryOpened = "instance_health.opened";
    public const string CategoryEscalated = "instance_health.escalated";
    public const string CategoryResolved = "instance_health.resolved";

    /// <summary>An issue whose finding stays away this long is resolved.</summary>
    public static readonly TimeSpan ResolveAfter = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan QueryInterval = TimeSpan.FromHours(1);

    // Measuring the hypertables takes TimescaleDB a few hundred milliseconds, and table sizes decide no finding (a disk or
    // database finding only names the largest tables), so every sample of the hour reuses the last measurement.
    private static readonly TimeSpan TablesInterval = TimeSpan.FromHours(1);

    private const string DatabaseSql = """
        SELECT pg_database_size(current_database()) AS "Size",
               (SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'client backend')::int AS "Connections",
               current_setting('max_connections')::int AS "MaxConnections"
        """;

    private const int ListedTables = 15;

    private const string TablesSql = """
        SELECT c.relname AS "Name", pg_total_relation_size(c.oid) AS "Bytes", GREATEST(c.reltuples, 0)::bigint AS "Rows"
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
        """;

    // A hypertable keeps its rows in chunks outside the public schema (compressed ones in yet another table), so its own
    // relation is nearly empty: TimescaleDB's functions measure it as a whole. Only asked when the extension exists.
    private const string HypertablesSql = """
        SELECT h.hypertable_name AS "Name",
               COALESCE(hypertable_size(format('%I.%I', h.hypertable_schema, h.hypertable_name)::regclass), 0) AS "Bytes",
               GREATEST(approximate_row_count(format('%I.%I', h.hypertable_schema, h.hypertable_name)::regclass), 0) AS "Rows"
        FROM timescaledb_information.hypertables h
        WHERE h.hypertable_schema = 'public'
        """;

    private const string TimescaleInstalledSql = """SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') AS "Value" """;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;
    private readonly HostMetricsReader _host;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly INotificationBus _bus;
    private IDisposable? _subscription;
    private DateTimeOffset _nextQueries = DateTimeOffset.MinValue;
    private IReadOnlyList<TableSize>? _tables;
    private DateTimeOffset _nextTables = DateTimeOffset.MinValue;
    private IReadOnlyList<HealthFinding> _performance = [];
    private DateTimeOffset _nextPerformance = DateTimeOffset.MinValue;

    public InstanceHealthService(IFleetoDbContextFactory dbFactory, SettingsStore settings, HostMetricsReader host, WorkerHeartbeat heartbeat,
        INotificationBus bus, TimeProvider time, ILogger<InstanceHealthService> logger)
        : base("instance-health", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _host = host;
        _heartbeat = heartbeat;
        _bus = bus;
    }

    protected override TimeSpan Interval => InstanceHealthRules.SampleInterval;

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.InstanceHealth, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await SampleAsync(await _host.ReadAsync(cancellationToken), cancellationToken);
        if (Time.GetUtcNow() >= _nextQueries)
        {
            _nextQueries = Time.GetUtcNow() + QueryInterval;
            await ReadQueryStatisticsAsync(cancellationToken);
        }

        return false;
    }

    /// <summary>Stores one sample with these host values, judges it and applies the issues. Returns the findings.</summary>
    public async Task<IReadOnlyList<HealthFinding>> SampleAsync(HostHealth host, CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();

        var database = await db.Database.SqlQueryRaw<DatabaseRow>(DatabaseSql).SingleAsync(cancellationToken);
        if (_tables is null || Time.GetUtcNow() >= _nextTables)
        {
            _tables = await ReadTablesAsync(db, cancellationToken);
            _nextTables = Time.GetUtcNow() + TablesInterval;
        }

        var tables = _tables;
        var detail = new InstanceHealthDetail(tables, await ReadFleetoAsync(db, cancellationToken));

        var sample = new InstanceHealthSample
        {
            Time = now,
            CpuPercent = host.CpuPercent,
            Load1 = host.Load1,
            Cores = host.Cores,
            MemoryTotalBytes = host.MemoryTotalBytes,
            MemoryAvailableBytes = host.MemoryAvailableBytes,
            SwapTotalBytes = host.SwapTotalBytes,
            SwapFreeBytes = host.SwapFreeBytes,
            DiskTotalBytes = host.DiskTotalBytes,
            DiskFreeBytes = host.DiskFreeBytes,
            DatabaseBytes = database.Size,
            DatabaseConnections = database.Connections,
            DatabaseMaxConnections = database.MaxConnections,
            DetailJson = InstanceHealthRules.SerializeDetail(detail)
        };
        db.InstanceHealthSamples.Add(sample);
        await db.SaveChangesAsync(cancellationToken);

        var since = now - InstanceHealthRules.TrendWindow;
        var disk = await db.InstanceHealthSamples.AsNoTracking()
            .Where(s => s.Time >= since && s.DiskTotalBytes != null && s.DiskFreeBytes != null)
            .OrderBy(s => s.Time)
            .Select(s => new DiskPoint(s.Time, s.DiskTotalBytes!.Value - s.DiskFreeBytes!.Value))
            .ToListAsync(cancellationToken);
        var cpuSince = now - InstanceHealthRules.CpuWindow;
        var cpu = await db.InstanceHealthSamples.AsNoTracking()
            .Where(s => s.Time > cpuSince && s.CpuPercent != null)
            .Select(s => s.CpuPercent!.Value)
            .ToListAsync(cancellationToken);

        var findings = InstanceHealthRules.Evaluate(InstanceHealthRules.ToSnapshot(sample), disk, cpu)
            .Concat(await PerformanceProblemsAsync(db, now, cancellationToken))
            .Concat(AdvisoryRules.HealthFindings(await db.AdvisorySources.AsNoTracking()
                .Select(s => new AdvisorySourceState(s.Key, s.Enabled, s.LastSuccessAt, s.FailingSince, s.Problem)).ToListAsync(cancellationToken), now))
            .Concat(Vulnerabilities.HealthFindings((await db.VulnerabilityFeeds.AsNoTracking()
                .Select(f => new { f.Key, f.LastSuccessAt, f.FailingSince, f.Problem }).ToListAsync(cancellationToken))
                .Select(f => (f.Key, f.LastSuccessAt, f.FailingSince, f.Problem)), now))
            .ToList();
        await ApplyAsync(db, findings, now, cancellationToken);
        return findings;
    }

    /// <summary>
    /// The critical findings of the performance monitor over the last hour as problems (0.7.0 step 3), judged every 15 minutes and
    /// reused by the samples in between, so a problem does not resolve between two judgements. Off with the monitor or its setting;
    /// a failure keeps the last judgement and never stops the sample.
    /// </summary>
    private async Task<IReadOnlyList<HealthFinding>> PerformanceProblemsAsync(FleetoDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        if (Time.GetUtcNow() < _nextPerformance)
        {
            return _performance;
        }

        _nextPerformance = Time.GetUtcNow() + PerformanceHealth.Interval;
        try
        {
            var json = await db.PerformanceSettings.AsNoTracking().Select(s => s.Json).FirstOrDefaultAsync(cancellationToken);
            var settings = Fleeto.Core.Performance.PerformanceSettings.Parse(json);
            if (!settings.IsOn || !settings.HealthProblems)
            {
                return _performance = [];
            }

            var to = PerfRecorder.MinuteOf(now);
            var data = await PerformanceReader.ReadAsync(db, to - PerformanceHealth.Window, to, settings, cancellationToken);
            var since = now - TimeSpan.FromHours(settings.HealthCooldownHours);
            var resolved = await db.InstanceHealthIssues.AsNoTracking()
                .Where(i => i.Component == HealthComponent.Performance && i.ResolvedAt != null && i.ResolvedAt > since)
                .Select(i => i.Key).ToListAsync(cancellationToken);
            var open = await db.InstanceHealthIssues.AsNoTracking()
                .Where(i => i.Component == HealthComponent.Performance && i.ResolvedAt == null)
                .Select(i => i.Key).ToListAsync(cancellationToken);
            return _performance = PerformanceHealth.Problems(PerformanceRules.Evaluate(data), resolved.Except(open).ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "The performance of the instance could not be judged for instance health; the last judgement stays");
            return _performance;
        }
    }

    /// <summary>The largest tables of the instance, hypertables measured with their chunks.</summary>
    private static async Task<IReadOnlyList<TableSize>> ReadTablesAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        var sizes = (await db.Database.SqlQueryRaw<TableRow>(TablesSql).ToListAsync(cancellationToken))
            .ToDictionary(t => t.Name, t => new TableSize(t.Name, t.Bytes, t.Rows), StringComparer.Ordinal);
        if (await db.Database.SqlQueryRaw<bool>(TimescaleInstalledSql).SingleAsync(cancellationToken))
        {
            foreach (var hypertable in await db.Database.SqlQueryRaw<TableRow>(HypertablesSql).ToListAsync(cancellationToken))
            {
                sizes[hypertable.Name] = new TableSize(hypertable.Name, hypertable.Bytes, hypertable.Rows);
            }
        }

        return sizes.Values.OrderByDescending(t => t.Bytes).ThenBy(t => t.Name, StringComparer.Ordinal).Take(ListedTables).ToList();
    }

    private async Task<FleetoHealth> ReadFleetoAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        var configured = false;
        string? external = null;
        try
        {
            var settings = await _settings.GetAsync<BackupSettings>(SettingKeys.Backup, cancellationToken);
            configured = settings is not null && settings.DestinationType != BackupDestinationType.None;
            external = settings is { IsExternal: true } ? settings.ExternalDescription!.Trim() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "The backup settings could not be read for instance health");
        }

        var lastSuccess = await db.BackupRuns.AsNoTracking().Where(r => r.Status == BackupRunStatus.Succeeded)
            .OrderByDescending(r => r.StartedAt).Select(r => (DateTime?)(r.CompletedAt ?? r.StartedAt)).FirstOrDefaultAsync(cancellationToken);
        var lastStatus = await db.BackupRuns.AsNoTracking().Where(r => r.Status != BackupRunStatus.Running)
            .OrderByDescending(r => r.StartedAt).Select(r => (BackupRunStatus?)r.Status).FirstOrDefaultAsync(cancellationToken);

        var emails = db.OutboxEmails.AsNoTracking().Where(e => e.SentAt == null && e.Attempts < OutboxEmailService.MaxAttempts);
        var webhooks = db.OutboxWebhooks.AsNoTracking().Where(w => w.SentAt == null && w.Attempts < OutboxWebhookService.MaxAttempts);
        var events = db.EndpointEvents.AsNoTracking().Where(e => e.ProcessedAt == null);

        return new FleetoHealth(
            _heartbeat.StaleLoops(),
            configured,
            lastSuccess,
            lastStatus == BackupRunStatus.Failed,
            await emails.CountAsync(cancellationToken),
            await emails.MinAsync(e => (DateTime?)e.CreatedAt, cancellationToken),
            await webhooks.CountAsync(cancellationToken),
            await webhooks.MinAsync(w => (DateTime?)w.CreatedAt, cancellationToken),
            await events.CountAsync(cancellationToken),
            await events.MinAsync(e => (DateTime?)e.Time, cancellationToken),
            await db.Endpoints.CountAsync(e => e.IsOnline, cancellationToken),
            await db.Endpoints.CountAsync(cancellationToken),
            external);
    }

    private async Task ApplyAsync(FleetoDbContext db, IReadOnlyList<HealthFinding> findings, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var open = await db.InstanceHealthIssues.Where(i => i.ResolvedAt == null).ToListAsync(cancellationToken);
        var transitions = new List<(InstanceHealthIssue Issue, NotificationEvent Event)>();

        foreach (var finding in findings)
        {
            var issue = open.FirstOrDefault(i => i.Key == finding.Key);
            if (issue is null)
            {
                issue = new InstanceHealthIssue
                {
                    Id = Guid.NewGuid(),
                    Key = finding.Key,
                    Component = finding.Component,
                    Severity = finding.Severity,
                    Title = Truncate(finding.Title, 500),
                    Detail = Truncate(finding.Detail, 2000),
                    OpenedAt = now,
                    UpdatedAt = now
                };
                db.InstanceHealthIssues.Add(issue);
                transitions.Add((issue, NotificationEvent.Opened));
                continue;
            }

            if (finding.Severity > issue.Severity)
            {
                transitions.Add((issue, NotificationEvent.Escalated));
            }

            // A lower severity is written silently: the issue is still open, and its resolve is what people wait for.
            issue.Severity = finding.Severity;
            issue.Title = Truncate(finding.Title, 500);
            issue.Detail = Truncate(finding.Detail, 2000);
            issue.UpdatedAt = now;
        }

        foreach (var issue in open.Where(i => findings.All(f => f.Key != i.Key) && now - i.UpdatedAt >= ResolveAfter))
        {
            issue.ResolvedAt = now;
            transitions.Add((issue, NotificationEvent.Resolved));
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transitions.Count > 0)
        {
            await AddNotificationsAsync(db, transitions, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            foreach (var (issue, notificationEvent) in transitions)
            {
                Logger.LogWarning("Instance health {Event}: {Key} ({Severity})", notificationEvent, issue.Key, issue.Severity);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Queues an email per recipient and a webhook per channel that receives instance health: switched on for it by an admin,
    /// enabled, at least its minimum severity, and for a resolve only when it wants resolves. Never combined into a digest.
    /// </summary>
    internal static async Task<int> AddNotificationsAsync(FleetoDbContext db, IReadOnlyList<(InstanceHealthIssue Issue, NotificationEvent Event)> transitions,
        DateTime now, CancellationToken cancellationToken)
    {
        var channels = await db.NotificationChannels.AsNoTracking().Where(c => c.Enabled && c.InstanceHealth).ToListAsync(cancellationToken);
        if (channels.Count == 0)
        {
            return 0;
        }

        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        var added = 0;
        foreach (var (issue, notificationEvent) in transitions)
        {
            var receiving = channels.Where(c => c.MinimumSeverity <= issue.Severity &&
                                                (notificationEvent != NotificationEvent.Resolved || c.NotifyOnResolve)).ToList();
            var category = notificationEvent switch
            {
                NotificationEvent.Escalated => CategoryEscalated,
                NotificationEvent.Resolved => CategoryResolved,
                _ => CategoryOpened
            };

            foreach (var channel in receiving.Where(c => c is { Type: NotificationChannelType.Webhook, WebhookFormat: not null }))
            {
                var id = Guid.NewGuid();
                db.OutboxWebhooks.Add(new OutboxWebhook
                {
                    Id = id,
                    NotificationChannelId = channel.Id,
                    Category = category,
                    Payload = WebhookPayloads.InstanceHealth(channel.WebhookFormat!.Value, id, instance.Fqdn, notificationEvent, issue.Id,
                        issue.Component.ToString(), issue.Severity, issue.Title, issue.Detail, issue.OpenedAt, issue.ResolvedAt, instance.InstanceHealthUrl, now),
                    NextAttemptAt = now,
                    CreatedAt = now
                });
                added++;
            }

            var recipients = receiving.Where(c => c.Type == NotificationChannelType.Email)
                .SelectMany(c => EmailAddresses.Split(c.Recipients))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipients.Count == 0)
            {
                continue;
            }

            var content = EmailTemplates.InstanceHealth(instance.Fqdn, notificationEvent, issue.Severity.ToString(), issue.Title, issue.Detail,
                instance.InstanceHealthUrl);
            foreach (var recipient in recipients)
            {
                db.OutboxEmails.Add(OutboxEmails.Create(recipient, content, category, now));
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Reads the costliest statements and stores them for the diagnostics; a missing extension is stored as the reason. The same
    /// reader as the performance monitor (0.7.0), so both name the same statements. They are ranked by what they did over
    /// <see cref="InstanceHealthRules.StatementWindow"/>, counted from the statement snapshot of the performance monitor at its start;
    /// without one (the monitor keeps none, or the statistics were reset since) by everything since the reset.
    /// </summary>
    public async Task<QueryStatistics> ReadQueryStatisticsAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        StatementRead read;
        DateTime? from = null;
        Dictionary<long, StatementTotals>? baseline = null;
        await using (var db = _dbFactory.CreateSystem())
        {
            read = await StatementStatisticsReader.ReadAsync(db, cancellationToken);
            if (read.Problem is null)
            {
                var start = now - InstanceHealthRules.StatementWindow;
                var earliest = start - InstanceHealthRules.StatementBaselineSlack;
                if (read.ResetAt is { } reset && reset > earliest)
                {
                    earliest = reset;
                }

                from = await db.PerfStatementSnapshots.Where(s => s.Time <= start && s.Time > earliest)
                    .MaxAsync(s => (DateTime?)s.Time, cancellationToken);
                if (from is { } at)
                {
                    baseline = (await db.PerfStatementSnapshots.AsNoTracking().Where(s => s.Time == at).ToListAsync(cancellationToken))
                        .GroupBy(s => s.QueryId)
                        .ToDictionary(g => g.Key, g => g.Select(s => new StatementTotals(s.QueryId, s.Query, s.Calls, s.TotalMs, s.Rows)).First());
                }
            }
        }

        var queries = InstanceHealthRules.Costliest(
            read.Statements.Select(q => new StatementTotals(q.QueryId, q.Query, q.Calls, q.TotalMs, q.Rows)), baseline);
        var statistics = new QueryStatistics(now, read.Problem, queries, from, read.ResetAt);
        await _settings.SetAsync(SettingKeys.InstanceHealthQueries, statistics, encrypted: false, userId: null, cancellationToken);
        return statistics;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed class DatabaseRow
    {
        public long Size { get; set; }
        public int Connections { get; set; }
        public int MaxConnections { get; set; }
    }

    private sealed class TableRow
    {
        public string Name { get; set; } = string.Empty;
        public long Bytes { get; set; }
        public long Rows { get; set; }
    }
}
