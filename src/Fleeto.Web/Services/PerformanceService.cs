using System.Globalization;
using System.Text.Json;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Performance;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Web.Services;

/// <summary>A period of the page, anchored on the server clock.</summary>
public enum PerfPeriod
{
    Minutes15,
    Hour1,
    Hours6,
    Hours24,
    Days7
}

/// <summary>What an export holds: the Markdown for Claude Code, the JSON for tooling, or both with plans in a ZIP (spec §10).</summary>
public enum PerfExportFormat
{
    Markdown,
    Json,
    Zip
}

/// <summary>Settings, Performance as one view: the period, its scores, findings, timelines and the dataset behind them.</summary>
/// <param name="Now">The server clock when the view was built, for the countdown of Diagnose.</param>
public sealed record PerformanceView(
    PerfPeriod Period,
    DateTime From,
    DateTime To,
    TimeSpan Bucket,
    DateTime Now,
    PerformanceSettings Settings,
    IReadOnlyList<PerfCategoryScore> Scores,
    IReadOnlyList<PerfFinding> Findings,
    IReadOnlyList<PerfTimelinePoint> Timeline,
    PerformanceDataset Data,
    bool CanManage)
{
    public IReadOnlyList<PerfRuntimePoint> RuntimeTimeline { get; init; } = [];
    public IReadOnlyList<PerfHostPoint> HostTimeline { get; init; } = [];
    public IReadOnlyList<PerfKeyPoint> LoopTimeline { get; init; } = [];
    public IReadOnlyList<PerfKeyPoint> MessageTimeline { get; init; } = [];
    public IReadOnlyList<PerfKeyPoint> ConnectionTimeline { get; init; } = [];

    /// <summary>The end of Diagnose when it runs now, else null.</summary>
    public DateTime? DiagnoseUntil => Settings.IsDiagnosing(Now) ? Settings.DiagnoseUntil : null;
}

/// <summary>An export ready to copy or download.</summary>
public sealed record PerfExport(string FileName, string ContentType, byte[] Content);

/// <summary>A setting of PostgreSQL that matters for performance, as the server reports it.</summary>
public sealed record PerfDatabaseSetting(string Name, string Value, string? Unit, string Description);

/// <summary>Reads and writes of one kind of backend since the I/O statistics were reset (PostgreSQL 16 and later).</summary>
public sealed record PerfIoRow(string BackendType, long Reads, long Writes, long Hits, long Evictions, long Fsyncs);

/// <summary>What the Database tab reads live when it opens: settings and I/O. Problem says what could not be read.</summary>
public sealed record PerfLiveDatabase(IReadOnlyList<PerfDatabaseSetting> Settings, IReadOnlyList<PerfIoRow> Io, string? ServerVersion, string? Problem);

/// <summary>What the plan of a statement is asked for: a statement of pg_stat_statements by its query id, or a fingerprint.</summary>
public enum PerfPlanTarget
{
    Statement,
    Fingerprint
}

/// <summary>
/// The performance monitor in web (0.7.0, PERFORMANCE.md §8): what the four components measured, judged by
/// <see cref="PerformanceRules"/>, for users with the permission Performance, which counts only for users whose groups give every
/// client. Changing the settings, Diagnose, markers, plans and ANALYZE need Manage performance monitoring and are audited; every
/// export is audited. What leaves the server is redacted, the client codes and endpoint host names of the instance included.
/// </summary>
public sealed class PerformanceService
{
    public const int SettingsRowId = 1;
    public const int MinDiagnoseMinutes = 5;
    public const int MaxMarkerLength = 200;

    /// <summary>An edge check or benchmark is asked for at most this often, and one that waits this long without starting has expired.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);

    /// <summary>The ZIP plans the top statements, but never for longer than this in all.</summary>
    private static readonly TimeSpan PlanBudget = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, string> RelevantSettings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["shared_buffers"] = "Memory for cached table and index pages; the cache hit ratio depends on it.",
        ["effective_cache_size"] = "What the planner assumes the operating system caches; it only steers plans.",
        ["work_mem"] = "Memory per sort or hash before it spills to a temporary file.",
        ["maintenance_work_mem"] = "Memory for vacuum and index builds.",
        ["max_connections"] = "Connections the server accepts, for all instances' roles together.",
        ["random_page_cost"] = "How expensive the planner thinks a random read is; low on SSD.",
        ["autovacuum"] = "Whether dead rows are cleaned up automatically.",
        ["autovacuum_vacuum_scale_factor"] = "Share of a table that must be dead before autovacuum runs on it.",
        ["track_io_timing"] = "Whether I/O time is measured, for timing in the statement statistics.",
        ["jit"] = "Just-in-time compilation of expressions; can cost more than it saves on short queries.",
        ["shared_preload_libraries"] = "Extensions loaded at start: pg_stat_statements and timescaledb belong here.",
        ["pg_stat_statements.track"] = "Which statements the statement statistics count."
    };

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly PerfSettingsCache _cache;
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;

    public PerformanceService(IFleetoDbContextFactory dbFactory, INotificationBus bus, PerfSettingsCache cache, NpgsqlDataSource dataSource, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _cache = cache;
        _dataSource = dataSource;
        _time = time;
    }

    public static TimeSpan Length(PerfPeriod period) => period switch
    {
        PerfPeriod.Minutes15 => TimeSpan.FromMinutes(15),
        PerfPeriod.Hour1 => TimeSpan.FromHours(1),
        PerfPeriod.Hours6 => TimeSpan.FromHours(6),
        PerfPeriod.Hours24 => TimeSpan.FromHours(24),
        _ => TimeSpan.FromDays(7)
    };

    public static TimeSpan BucketOf(PerfPeriod period) => period switch
    {
        PerfPeriod.Minutes15 or PerfPeriod.Hour1 => TimeSpan.FromMinutes(1),
        PerfPeriod.Hours6 => TimeSpan.FromMinutes(5),
        PerfPeriod.Hours24 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1)
    };

    public static string Label(PerfPeriod period) => period switch
    {
        PerfPeriod.Minutes15 => "15 minutes",
        PerfPeriod.Hour1 => "1 hour",
        PerfPeriod.Hours6 => "6 hours",
        PerfPeriod.Hours24 => "24 hours",
        _ => "7 days"
    };

    public async Task<PerformanceView?> GetAsync(Caller caller, PerfPeriod period, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceView))
        {
            return null;
        }

        var settings = await ReadSettingsAsync(cancellationToken);
        var (from, to, bucket) = Range(period);
        await using var db = _dbFactory.CreateSystem();
        var data = await PerformanceReader.ReadAsync(db, from, to, settings, cancellationToken);
        var findings = PerformanceRules.Evaluate(data);
        var timeline = await PerformanceReader.ReadTimelineAsync(db, from, to, bucket, cancellationToken);
        return new PerformanceView(period, from, to, bucket, _time.GetUtcNow().UtcDateTime, settings, PerformanceRules.Score(data, findings), findings, timeline,
            data, caller.Has(Permissions.PerformanceManage))
        {
            RuntimeTimeline = await PerformanceReader.ReadRuntimeTimelineAsync(db, from, to, bucket, cancellationToken),
            HostTimeline = await PerformanceReader.ReadHostTimelineAsync(db, from, to, bucket, cancellationToken),
            LoopTimeline = await PerformanceReader.ReadKeyTimelineAsync(db, from, to, bucket, PerfKind.WorkerRun, cancellationToken),
            MessageTimeline = await PerformanceReader.ReadKeyTimelineAsync(db, from, to, bucket, PerfKind.AgentMessage, cancellationToken),
            ConnectionTimeline = await PerformanceReader.ReadKeyTimelineAsync(db, from, to, bucket, PerfKind.Connection, cancellationToken)
        };
    }

    /// <summary>One page, route or loop over the period, per bucket: opened from its row on the page.</summary>
    public async Task<IReadOnlyList<PerfFlowPoint>?> GetFlowTimelineAsync(Caller caller, PerfPeriod period, PerfKind kind, string key, string subKey,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceView))
        {
            return null;
        }

        var (from, to, bucket) = Range(period);
        await using var db = _dbFactory.CreateSystem();
        return await PerformanceReader.ReadFlowTimelineAsync(db, from, to, bucket, kind, key, subKey, cancellationToken);
    }

    public async Task<PerformanceSettings?> GetSettingsAsync(Caller caller, CancellationToken cancellationToken = default) =>
        caller.Has(Permissions.PerformanceView) ? await ReadSettingsAsync(cancellationToken) : null;

    /// <summary>Validates and stores the settings, audits what changed and tells every component. Diagnose is kept as it runs.</summary>
    public async Task<ServiceResult> SaveSettingsAsync(Caller caller, PerformanceSettings settings, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceManage))
        {
            return ServiceResult.Forbidden();
        }

        // Only thresholds that differ from their default are stored, so a default changed in a later release reaches every instance.
        settings = settings with
        {
            DisabledCollectors = settings.DisabledCollectors.Distinct().Order().ToList(),
            Budgets = (settings.Budgets ?? []).Select(b => b with { Key = b.Key.Trim(), SubKey = (b.SubKey ?? string.Empty).Trim() }).ToList(),
            Thresholds = settings.Thresholds
                .Where(t => PerfThresholds.Find(t.Key) is not { } info || Math.Abs(info.Default - t.Value) > 1e-9)
                .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal)
        };
        if (settings.Problem() is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        return await StoreAsync(caller, before => settings with
        {
            // The form never sets Diagnose; level Off ends it.
            DiagnoseUntil = settings.Level == PerfLevel.Off ? null : before.DiagnoseUntil
        }, AuditActions.PerformanceSettingsChanged, (before, after) => new
        {
            Before = JsonSerializer.Deserialize<JsonElement>(before.Serialize(), AuditJson),
            After = JsonSerializer.Deserialize<JsonElement>(after.Serialize(), AuditJson)
        }, cancellationToken);
    }

    /// <summary>
    /// Starts Diagnose for <paramref name="minutes"/> (at least 5, at most the setting), or extends it when it runs: its end on
    /// server time is stored, so it ends by itself (spec §2.4). Needs the level Basic.
    /// </summary>
    public async Task<ServiceResult> StartDiagnoseAsync(Caller caller, int minutes, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceManage))
        {
            return ServiceResult.Forbidden();
        }

        var current = await ReadSettingsAsync(cancellationToken);
        if (!current.IsOn)
        {
            return ServiceResult.Fail("The level is Off, so nothing is measured. Set it to Basic in the Settings tab first, then start Diagnose.");
        }

        if (minutes < MinDiagnoseMinutes || minutes > current.DiagnoseMaxMinutes)
        {
            return ServiceResult.Fail($"Diagnose runs {MinDiagnoseMinutes} to {current.DiagnoseMaxMinutes} minutes; the longest is a setting.");
        }

        var until = _time.GetUtcNow().UtcDateTime.AddMinutes(minutes);
        return await StoreAsync(caller, before => before with { DiagnoseUntil = until }, AuditActions.PerformanceDiagnoseStarted,
            (_, _) => new { Minutes = minutes, Until = until }, cancellationToken);
    }

    /// <summary>Ends Diagnose now. Nothing to do when it does not run.</summary>
    public async Task<ServiceResult> StopDiagnoseAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceManage))
        {
            return ServiceResult.Forbidden();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var current = await ReadSettingsAsync(cancellationToken);
        if (!current.IsDiagnosing(now))
        {
            return ServiceResult.Ok();
        }

        return await StoreAsync(caller, before => before with { DiagnoseUntil = null }, AuditActions.PerformanceDiagnoseStopped,
            (before, _) => new { PlannedUntil = before.DiagnoseUntil }, cancellationToken);
    }

    /// <summary>
    /// Puts a marker on the timeline at the current time ("added an index on Jobs"), so before and after are visible. The label is
    /// stored through the redactor of the instance; the audit entry holds its length, never its text.
    /// </summary>
    public async Task<ServiceResult> AddMarkerAsync(Caller caller, string? label, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceManage))
        {
            return ServiceResult.Forbidden();
        }

        var text = (label ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > MaxMarkerLength)
        {
            return ServiceResult.Fail($"Describe the change in 1 to {MaxMarkerLength} characters.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var redactor = await RedactorAsync(db, cancellationToken);
        var stored = redactor.Text(text);
        db.PerfMarkers.Add(new PerfMarker { Time = now, Kind = PerfMarkerKind.Manual, Label = stored.Length <= MaxMarkerLength ? stored : stored[..MaxMarkerLength] });
        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PerformanceMarkerAdded, "Instance", instance.InstanceId.ToString(), null,
            new { text.Length }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// The generic plan of a stored statement (spec §2.8): the page names it by its id, the server looks up its own normalized text.
    /// Planned as web, read-only, without running it; audited.
    /// </summary>
    public async Task<ServiceResult<PerfPlan>> PlanAsync(Caller caller, PerfPlanTarget target, string id, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceManage))
        {
            return ServiceResult<PerfPlan>.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var sql = await StoredSqlAsync(db, target, id, cancellationToken);
        if (sql is null)
        {
            return ServiceResult<PerfPlan>.NotFound("statement");
        }

        var plan = await PerformancePlanner.ExplainAsync(_dataSource, sql, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PerformancePlanned, "Instance", instance.InstanceId.ToString(), null,
            new { Target = target.ToString(), Id = id, plan.Planned }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<PerfPlan>.Ok(plan);
    }

    /// <summary>Runs ANALYZE on one table of Fleeto (spec §2.8), through the one database function that may; audited.</summary>
    public async Task<ServiceResult> AnalyzeAsync(Caller caller, string table, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceManage))
        {
            return ServiceResult.Forbidden();
        }

        if (string.IsNullOrWhiteSpace(table) || table.Length > 200)
        {
            return ServiceResult.Fail("Choose a table of Fleeto to analyze.");
        }

        await using var db = _dbFactory.CreateSystem();
        using (PerfContext.Begin(PerfKeys.DashboardSource))
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync("SELECT fleeto_perf_analyze({0})", [table], cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                return ServiceResult.Fail($"Fleeto has no table called {table}. Refresh the page and try again.");
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.UndefinedFunction)
            {
                return ServiceResult.Fail("The database does not let web analyze tables yet. Run install.sh for this instance again, which applies the grants.");
            }
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PerformanceTableAnalyzed, "Instance", instance.InstanceId.ToString(), null,
            new { Table = table }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Asks the workers for an edge check of the instance URL or a benchmark of the VPS (spec §5 and §9, on click only). Web may not
    /// reach the internet, so it writes the request and notifies; the workers run it and write the result, which the page follows.
    /// One of each kind a minute; audited.
    /// </summary>
    public async Task<ServiceResult<Guid>> RequestProbeAsync(Caller caller, PerfProbeKind kind, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceManage))
        {
            return ServiceResult<Guid>.Forbidden();
        }

        if (!Enum.IsDefined(kind))
        {
            return ServiceResult<Guid>.Fail("Choose an edge check or a benchmark.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var since = now - ProbeInterval;
        if (await db.PerfProbes.AnyAsync(p => p.Kind == kind && (p.RequestedAt > since || (p.CompletedAt == null && p.StartedAt != null)), cancellationToken))
        {
            return ServiceResult<Guid>.Fail(kind == PerfProbeKind.EdgeCheck
                ? "An edge check ran less than a minute ago or still runs. Wait for its result, then try again."
                : "A benchmark ran less than a minute ago or still runs. Wait for its result, then try again.");
        }

        var probe = new PerfProbe { Id = Guid.NewGuid(), Kind = kind, RequestedAt = now, RequestedBy = caller.UserId };
        db.PerfProbes.Add(probe);
        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PerformanceProbeRequested, "Instance", instance.InstanceId.ToString(), null,
            new { Kind = kind.ToString() }), now));
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await _bus.PublishAsync(NotificationChannels.PerformanceProbes, probe.Id.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The workers look for waiting requests every minute anyway.
        }

        return ServiceResult<Guid>.Ok(probe.Id);
    }

    /// <summary>One edge check or benchmark, for the page to follow until it completed.</summary>
    public async Task<PerfProbeView?> GetProbeAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceView))
        {
            return null;
        }

        await using var db = _dbFactory.CreateSystem();
        using var scope = PerfContext.Begin(PerfKeys.DashboardSource);
        var probe = await db.PerfProbes.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return probe is null ? null : PerformanceReader.ToView(probe);
    }

    /// <summary>Settings and I/O of PostgreSQL, read live as web when the Database tab opens. What cannot be read is said.</summary>
    public async Task<PerfLiveDatabase?> GetLiveDatabaseAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceView))
        {
            return null;
        }

        await using var db = _dbFactory.CreateSystem();
        using var scope = PerfContext.Begin(PerfKeys.DashboardSource);
        var problems = new List<string>();
        List<PerfDatabaseSetting> settings = [];
        string? version = null;
        try
        {
            var names = RelevantSettings.Keys.ToArray();
            var rows = await db.Database.SqlQueryRaw<SettingRow>(
                """SELECT name AS "Name", setting AS "Value", unit AS "Unit" FROM pg_settings WHERE name = ANY(@names) ORDER BY name""",
                new NpgsqlParameter("names", names)).ToListAsync(cancellationToken);
            settings = rows.Select(r => new PerfDatabaseSetting(r.Name, r.Value, r.Unit, RelevantSettings[r.Name])).ToList();
            version = await db.Database.SqlQueryRaw<string>("""SELECT current_setting('server_version') AS "Value" """).SingleAsync(cancellationToken);
        }
        catch (PostgresException ex)
        {
            problems.Add($"The settings could not be read: {PerfRedactor.Default.Text(ex.MessageText)}");
        }

        List<PerfIoRow> io = [];
        try
        {
            io = await db.Database.SqlQueryRaw<PerfIoRow>("""
                SELECT backend_type AS "BackendType", COALESCE(sum(reads), 0)::bigint AS "Reads", COALESCE(sum(writes), 0)::bigint AS "Writes",
                       COALESCE(sum(hits), 0)::bigint AS "Hits", COALESCE(sum(evictions), 0)::bigint AS "Evictions", COALESCE(sum(fsyncs), 0)::bigint AS "Fsyncs"
                FROM pg_stat_io GROUP BY backend_type HAVING COALESCE(sum(reads), 0) + COALESCE(sum(writes), 0) + COALESCE(sum(hits), 0) > 0
                ORDER BY 2 DESC
                """).ToListAsync(cancellationToken);
        }
        catch (PostgresException)
        {
            problems.Add("I/O statistics need PostgreSQL 16 or later.");
        }

        return new PerfLiveDatabase(settings, io, version, problems.Count == 0 ? null : string.Join(" ", problems));
    }

    /// <summary>The report for Claude Code as Markdown, and an audit entry for the export (<paramref name="how"/>: "copied" or "downloaded").</summary>
    public async Task<ServiceResult<string>> ExportMarkdownAsync(Caller caller, PerfPeriod period, string how, CancellationToken cancellationToken = default)
    {
        var result = await ExportAsync(caller, period, PerfExportFormat.Markdown, how, cancellationToken);
        return result.Success ? ServiceResult<string>.Ok(System.Text.Encoding.UTF8.GetString(result.Value!.Content)) : ServiceResult<string>.Fail(result.Problem!);
    }

    /// <summary>
    /// An export of the period: Markdown, JSON with a schema version, or a ZIP with both and the generic plans of the top
    /// statements (spec §10). Everything through the redactor of the instance; audited with its format and size.
    /// </summary>
    public async Task<ServiceResult<PerfExport>> ExportAsync(Caller caller, PerfPeriod period, PerfExportFormat format, string how,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceView))
        {
            return ServiceResult<PerfExport>.Forbidden();
        }

        var settings = await ReadSettingsAsync(cancellationToken);
        var (from, to, _) = Range(period);
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var data = await PerformanceReader.ReadAsync(db, from, to, settings, cancellationToken);
        var findings = PerformanceRules.Evaluate(data);
        var scores = PerformanceRules.Score(data, findings);
        var redactor = await RedactorAsync(db, cancellationToken);
        var stamp = now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        PerfExport export;
        switch (format)
        {
            case PerfExportFormat.Json:
                export = new PerfExport($"fleeto-performance-{stamp}.json", "application/json",
                    System.Text.Encoding.UTF8.GetBytes(PerformanceReport.Json(data, findings, scores, now, FleetoVersion.Current, redactor)));
                break;
            case PerfExportFormat.Zip:
                var markdown = PerformanceReport.Markdown(data, findings, scores, now, FleetoVersion.Current, redactor);
                var json = PerformanceReport.Json(data, findings, scores, now, FleetoVersion.Current, redactor);
                var plans = await TopPlansAsync(data, redactor, cancellationToken);
                export = new PerfExport($"fleeto-performance-{stamp}.zip", "application/zip", PerformanceReport.Zip(markdown, json, plans));
                break;
            default:
                export = new PerfExport($"fleeto-performance-{stamp}.md", "text/markdown",
                    System.Text.Encoding.UTF8.GetBytes(PerformanceReport.Markdown(data, findings, scores, now, FleetoVersion.Current, redactor)));
                break;
        }

        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PerformanceExported, "Instance", instance.InstanceId.ToString(), null,
            new { How = how, Format = format.ToString().ToLowerInvariant(), Period = Label(period), Bytes = export.Content.Length }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<PerfExport>.Ok(export);
    }

    /// <summary>The generic plans of the costliest statements of the period, within <see cref="PlanBudget"/>; a plan that fails says why.</summary>
    private async Task<IReadOnlyList<PerformanceReport.PlanFile>> TopPlansAsync(PerformanceDataset data, PerfRedactor redactor, CancellationToken cancellationToken)
    {
        var targets = data.Statements.Take(10).Select(s => ($"statement-{s.QueryId.ToString(CultureInfo.InvariantCulture)}", s.Query))
            .Concat(data.Of(PerfKind.Statement).GroupBy(s => s.Key).OrderByDescending(g => g.Sum(s => s.Sum)).Take(5)
                .Select(g => (Name: $"fingerprint-{g.Key}", Sql: data.Fingerprint(g.Key)?.Sql)).Where(t => t.Sql is not null).Select(t => (t.Name, t.Sql!)))
            .ToList();
        var plans = new List<PerformanceReport.PlanFile>();
        var started = _time.GetUtcNow();
        foreach (var (name, sql) in targets)
        {
            if (_time.GetUtcNow() - started > PlanBudget)
            {
                break;
            }

            var plan = await PerformancePlanner.ExplainAsync(_dataSource, sql, cancellationToken);
            plans.Add(new PerformanceReport.PlanFile(name, plan.Json is { } json
                ? redactor.Text(json)
                : JsonSerializer.Serialize(new { problem = plan.Problem })));
        }

        return plans;
    }

    private static async Task<string?> StoredSqlAsync(FleetoDbContext db, PerfPlanTarget target, string id, CancellationToken cancellationToken)
    {
        if (target == PerfPlanTarget.Statement)
        {
            return long.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var queryId)
                ? await db.PerfStatementSnapshots.AsNoTracking().Where(s => s.QueryId == queryId).OrderByDescending(s => s.Time).Select(s => s.Query)
                    .FirstOrDefaultAsync(cancellationToken)
                : null;
        }

        return id.Length == 16
            ? await db.PerfFingerprints.AsNoTracking().Where(f => f.Hash == id).OrderByDescending(f => f.LastSeen).Select(f => f.Sql).FirstOrDefaultAsync(cancellationToken)
            : null;
    }

    /// <summary>Changes the stored settings in one transaction with its audit entry, then tells every component.</summary>
    private async Task<ServiceResult> StoreAsync(Caller caller, Func<PerformanceSettings, PerformanceSettings> change, string action,
        Func<PerformanceSettings, PerformanceSettings, object> details, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        PerformanceSettings after;
        await using (var db = _dbFactory.CreateSystem())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var row = await db.PerformanceSettings.SingleOrDefaultAsync(s => s.Id == SettingsRowId, cancellationToken);
            var before = PerformanceSettings.Parse(row?.Json);
            if (row is null)
            {
                row = new PerformanceSettingsRow { Id = SettingsRowId };
                db.PerformanceSettings.Add(row);
            }

            after = change(before);
            row.Json = after.Serialize();
            row.UpdatedAt = now;
            row.UpdatedBy = caller.UserId;
            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(action, "PerformanceSettings", SettingsRowId.ToString(CultureInfo.InvariantCulture), null,
                details(before, after)), now));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        _cache.Set(after);
        try
        {
            await _bus.PublishAsync(NotificationChannels.Performance, string.Empty, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The other components read the settings again within 30 seconds anyway.
        }

        return ServiceResult.Ok();
    }

    /// <summary>The redactor of this instance: its client codes and endpoint host names are masked wherever they slip in.</summary>
    private static async Task<PerfRedactor> RedactorAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        var codes = await db.Clients.AsNoTracking().Select(c => c.Code).ToListAsync(cancellationToken);
        var hosts = await db.Endpoints.AsNoTracking().Select(e => e.Hostname).Where(h => h != "").ToListAsync(cancellationToken);
        return PerfRedactor.ForInstance(codes, hosts);
    }

    private async Task<PerformanceSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var json = await db.PerformanceSettings.AsNoTracking().Where(s => s.Id == SettingsRowId).Select(s => s.Json).FirstOrDefaultAsync(cancellationToken);
        return PerformanceSettings.Parse(json);
    }

    /// <summary>The period ending at the current minute of the server clock, so buckets line up with the minute rows.</summary>
    private (DateTime From, DateTime To, TimeSpan Bucket) Range(PerfPeriod period)
    {
        var to = PerfRecorder.MinuteOf(_time.GetUtcNow().UtcDateTime).AddMinutes(1);
        return (to - Length(period), to, BucketOf(period));
    }

    private sealed class SettingRow
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string? Unit { get; set; }
    }
}
