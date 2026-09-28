using System.Globalization;
using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public enum HealthStatus
{
    Ok,
    Warning,
    Critical,
    Unknown
}

/// <summary>One part of the VPS or the instance with its state, its current value in words and the open issue, if any.</summary>
public sealed record HealthComponentView(HealthComponent Component, string Name, HealthStatus Status, string Value, InstanceHealthIssueView? Issue);

public sealed record InstanceHealthIssueView(Guid Id, HealthComponent Component, AlertSeverity Severity, string Title, string Detail, DateTime OpenedAt,
    DateTime? ResolvedAt);

/// <summary>The dashboard tile: the worst state and what it is about.</summary>
public sealed record InstanceHealthTile(HealthStatus Status, int OpenIssues, string Text);

/// <param name="MeasuredAt">The latest sample; null before the workers took one.</param>
/// <param name="Stale">No sample within <see cref="InstanceHealthRules.StaleAfter"/>: the workers are not measuring.</param>
/// <param name="Trends">Hourly charts of the last 7 days: disk, memory and CPU in use, database size.</param>
public sealed record InstanceHealthView(
    DateTime? MeasuredAt,
    bool Stale,
    HealthStatus Overall,
    IReadOnlyList<HealthComponentView> Components,
    IReadOnlyList<InstanceHealthIssueView> RecentlyResolved,
    IReadOnlyList<CheckHistoryView> Trends,
    IReadOnlyList<TableSize> Tables,
    QueryStatistics? Queries);

/// <summary>
/// Instance health in web (0.6.0, ARCHITECTURE.md §4, Instance health): what the workers measured, for admins only. The tile and
/// the page read the samples and issues the workers write; web measures nothing itself. The diagnostics are a Markdown report to
/// give to a person or an AI that troubleshoots: sizes, counts and normalized statements, never secrets or personal data.
/// Every export is audited.
/// </summary>
public sealed class InstanceHealthService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;

    public InstanceHealthService(IFleetoDbContextFactory dbFactory, SettingsStore settings, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _time = time;
    }

    public async Task<InstanceHealthTile?> GetTileAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return null;
        }

        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow().UtcDateTime;
        var latest = await db.InstanceHealthSamples.AsNoTracking().OrderByDescending(s => s.Time).Select(s => (DateTime?)s.Time)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not { } measuredAt || now - measuredAt > InstanceHealthRules.StaleAfter)
        {
            return new InstanceHealthTile(HealthStatus.Unknown, 0,
                latest is null ? "Not measured yet" : "Not measured recently: check the workers container");
        }

        var issues = await db.InstanceHealthIssues.AsNoTracking().Where(i => i.ResolvedAt == null)
            .OrderByDescending(i => i.Severity).ThenBy(i => i.OpenedAt)
            .Select(i => new { i.Severity, i.Title }).ToListAsync(cancellationToken);
        if (issues.Count == 0)
        {
            return new InstanceHealthTile(HealthStatus.Ok, 0, "Disk, memory, CPU, database and workers are fine");
        }

        return new InstanceHealthTile(Status(issues[0].Severity), issues.Count, issues[0].Title);
    }

    public async Task<InstanceHealthView?> GetAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return null;
        }

        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow().UtcDateTime;
        var since = now - InstanceHealthRules.TrendWindow;
        var samples = await db.InstanceHealthSamples.AsNoTracking().Where(s => s.Time >= since).OrderBy(s => s.Time).ToListAsync(cancellationToken);
        var latest = samples.LastOrDefault() ??
                     await db.InstanceHealthSamples.AsNoTracking().OrderByDescending(s => s.Time).FirstOrDefaultAsync(cancellationToken);
        var open = await db.InstanceHealthIssues.AsNoTracking().Where(i => i.ResolvedAt == null).ToListAsync(cancellationToken);
        var resolvedSince = now.AddDays(-7);
        var resolved = await db.InstanceHealthIssues.AsNoTracking().Where(i => i.ResolvedAt >= resolvedSince)
            .OrderByDescending(i => i.ResolvedAt).Take(20).ToListAsync(cancellationToken);

        var stale = latest is null || now - latest.Time > InstanceHealthRules.StaleAfter;
        var snapshot = latest is null ? null : InstanceHealthRules.ToSnapshot(latest);
        var disk = samples.Where(s => s.DiskTotalBytes is not null && s.DiskFreeBytes is not null)
            .Select(s => new DiskPoint(s.Time, s.DiskTotalBytes!.Value - s.DiskFreeBytes!.Value)).ToList();
        var daysUntilFull = snapshot?.Host.DiskTotalBytes is { } total ? InstanceHealthRules.DaysUntilFull(disk, total) : null;

        var components = Components(snapshot, open.Select(View).ToList(), daysUntilFull);
        var overall = stale ? HealthStatus.Unknown : components.Select(c => c.Status).Where(s => s != HealthStatus.Unknown).DefaultIfEmpty(HealthStatus.Ok).Max();
        return new InstanceHealthView(latest?.Time, stale, overall, components, resolved.Select(View).ToList(), Trends(samples, now),
            snapshot?.Detail.Tables ?? [], await ReadQueriesAsync(cancellationToken));
    }

    /// <summary>The diagnostics as Markdown, and an audit entry for the export (<paramref name="how"/>: "copied" or "downloaded").</summary>
    public async Task<ServiceResult<string>> ExportDiagnosticsAsync(Caller caller, string how, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<string>.Forbidden();
        }

        var view = await GetAsync(caller, cancellationToken);
        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow().UtcDateTime;
        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        var since = now - InstanceHealthRules.TrendWindow;
        var samples = await db.InstanceHealthSamples.AsNoTracking().Where(s => s.Time >= since).OrderBy(s => s.Time).ToListAsync(cancellationToken);
        var retentionDays = await _settings.GetStringAsync(SettingKeys.RetentionCheckResultsDays, cancellationToken);
        var report = Diagnostics(instance.Fqdn, now, view!, samples, retentionDays);

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.InstanceDiagnosticsExported, "Instance", instance.InstanceId.ToString(), null,
            new { How = how }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<string>.Ok(report);
    }

    private async Task<QueryStatistics?> ReadQueriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _settings.GetAsync<QueryStatistics>(SettingKeys.InstanceHealthQueries, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static InstanceHealthIssueView View(InstanceHealthIssue i) => new(i.Id, i.Component, i.Severity, i.Title, i.Detail, i.OpenedAt, i.ResolvedAt);

    private static HealthStatus Status(AlertSeverity severity) => severity == AlertSeverity.Critical ? HealthStatus.Critical : HealthStatus.Warning;

    private static IReadOnlyList<HealthComponentView> Components(HealthSnapshot? s, IReadOnlyList<InstanceHealthIssueView> open, double? daysUntilFull)
    {
        var host = s?.Host ?? HostHealth.Unknown;
        var fleeto = s?.Detail.Fleeto ?? FleetoHealth.Empty;

        HealthComponentView Component(HealthComponent component, string name, string? value)
        {
            var issue = open.Where(i => i.Component == component).OrderByDescending(i => i.Severity).FirstOrDefault();
            var status = issue is not null ? Status(issue.Severity) : value is null ? HealthStatus.Unknown : HealthStatus.Ok;
            return new HealthComponentView(component, name, status, value ?? "Not measured on this platform", issue);
        }

        string? Disk() => host is { DiskTotalBytes: > 0 and var total, DiskFreeBytes: { } free }
            ? $"{Percent(1 - (double)free / total)} used · {ByteSize.Format(free)} free of {ByteSize.Format(total)}" +
              (daysUntilFull is { } days ? $" · full in about {Math.Round(days).ToString(CultureInfo.InvariantCulture)} days" : string.Empty)
            : null;
        string? Memory() => host is { MemoryTotalBytes: > 0 and var total, MemoryAvailableBytes: { } available }
            ? $"{Percent(1 - (double)available / total)} used · {ByteSize.Format(available)} available of {ByteSize.Format(total)}"
            : null;
        string? Cpu() => host.CpuPercent is { } cpu
            ? $"{cpu.ToString("0", CultureInfo.InvariantCulture)}%" +
              (host is { Load1: { } load, Cores: { } cores } ? $" · load {load.ToString("0.0", CultureInfo.InvariantCulture)} on {cores} cores" : string.Empty)
            : null;

        return
        [
            Component(HealthComponent.Disk, "Disk", Disk()),
            Component(HealthComponent.Memory, "Memory", Memory()),
            Component(HealthComponent.Cpu, "CPU", Cpu()),
            Component(HealthComponent.Database, "Database", s is null ? null
                : $"{ByteSize.Format(s.DatabaseBytes)} · {s.Connections} of {s.MaxConnections} connections"),
            Component(HealthComponent.Workers, "Workers", s is null ? null
                : fleeto.StaleLoops.Count == 0 ? $"Every task reports · {fleeto.UnprocessedEvents} endpoint events waiting"
                : $"{fleeto.StaleLoops.Count} task(s) stuck"),
            Component(HealthComponent.Backups, "Backups", s is null ? null
                : !fleeto.BackupConfigured ? "No destination configured"
                : fleeto.LastBackupAt is { } at ? $"Last successful {At(at)}" : "No successful backup yet"),
            Component(HealthComponent.Notifications, "Notifications", s is null ? null
                : $"{fleeto.OutboxEmails} emails and {fleeto.OutboxWebhooks} webhooks waiting")
        ];
    }

    /// <summary>Hourly buckets of the last 7 days, drawn with the check history chart.</summary>
    private static IReadOnlyList<CheckHistoryView> Trends(IReadOnlyList<InstanceHealthSample> samples, DateTime now)
    {
        var bucket = TimeSpan.FromHours(1);
        var to = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc) + bucket;
        var from = to - InstanceHealthRules.TrendWindow;

        CheckHistoryView Trend(string name, string unit, Func<InstanceHealthSample, double?> value, double? warning, double? critical)
        {
            var points = new List<HistoryPoint>();
            for (var t = from; t < to; t += bucket)
            {
                var end = t + bucket;
                var values = samples.Where(s => s.Time >= t && s.Time < end).Select(value).OfType<double>().ToList();
                points.Add(values.Count == 0
                    ? new HistoryPoint(t, null, null, null, 0, 0, 0)
                    : new HistoryPoint(t, values.Min(), values.Average(), values.Max(), values.Count, 0, 0));
            }

            return new CheckHistoryView(Guid.Empty, name, CheckType.CpuUsage, string.Empty, [], new Dictionary<string, string>(),
                ThresholdKind.HigherIsWorse, unit, warning, critical, HistoryRange.Week, from, to, bucket, points);
        }

        return
        [
            Trend("Disk used", "%", s => s.DiskTotalBytes is > 0 && s.DiskFreeBytes is { } free ? 100 * (1 - (double)free / s.DiskTotalBytes.Value) : null,
                InstanceHealthRules.DiskWarningUsed * 100, InstanceHealthRules.DiskCriticalUsed * 100),
            Trend("Memory used", "%", s => s.MemoryTotalBytes is > 0 && s.MemoryAvailableBytes is { } available
                    ? 100 * (1 - (double)available / s.MemoryTotalBytes.Value) : null,
                100 - InstanceHealthRules.MemoryWarningAvailable * 100, 100 - InstanceHealthRules.MemoryCriticalAvailable * 100),
            Trend("CPU", "%", s => s.CpuPercent, InstanceHealthRules.CpuWarning, InstanceHealthRules.CpuCritical),
            Trend("Database size", "GB", s => s.DatabaseBytes / 1024d / 1024 / 1024, null, null)
        ];
    }

    private static string Diagnostics(string fqdn, DateTime now, InstanceHealthView view, IReadOnlyList<InstanceHealthSample> samples, string? retentionDays)
    {
        var md = new StringBuilder();
        void Line(string text = "") => md.Append(text).Append('\n');
        string F(double value, string format = "0.#") => value.ToString(format, CultureInfo.InvariantCulture);

        Line("# Fleeto instance diagnostics");
        Line();
        Line($"- Instance: {fqdn}");
        Line($"- Fleeto version: {FleetoVersion.Current}");
        Line($"- Generated: {Utc(now)}");
        Line($"- Last measurement: {(view.MeasuredAt is { } at ? Utc(at) : "none")}{(view.Stale ? " (stale: the workers are not measuring)" : "")}");
        Line("- Host values (disk, memory, CPU) are of the whole VPS; several Fleeto instances can share it.");
        Line("- The report holds sizes, counts and normalized database statements only: no secrets, no personal data.");
        Line();
        Line("## Components");
        Line();
        Line("| Component | State | Value |");
        Line("|---|---|---|");
        foreach (var c in view.Components)
        {
            Line($"| {c.Name} | {c.Status} | {Cell(c.Value)} |");
        }

        Line();
        Line("## Open issues");
        Line();
        var open = view.Components.Where(c => c.Issue is not null).Select(c => c.Issue!).ToList();
        if (open.Count == 0)
        {
            Line("None.");
        }

        foreach (var issue in open)
        {
            Line($"- **{issue.Severity}** ({issue.Component}, since {Utc(issue.OpenedAt)}): {issue.Title} {issue.Detail}");
        }

        if (view.RecentlyResolved.Count > 0)
        {
            Line();
            Line("## Resolved in the last 7 days");
            Line();
            foreach (var issue in view.RecentlyResolved)
            {
                Line($"- {issue.Severity} ({issue.Component}), {Utc(issue.OpenedAt)} to {Utc(issue.ResolvedAt!.Value)}: {issue.Title}");
            }
        }

        Line();
        Line("## Last 7 days per day");
        Line();
        Line("| Day (UTC) | CPU avg / max % | Memory used max % | Disk used % | Disk free | Database size | Connections max |");
        Line("|---|---|---|---|---|---|---|");
        foreach (var day in samples.GroupBy(s => s.Time.Date).OrderBy(g => g.Key))
        {
            var cpu = day.Select(s => s.CpuPercent).OfType<double>().ToList();
            var memory = day.Where(s => s.MemoryTotalBytes is > 0 && s.MemoryAvailableBytes is not null)
                .Select(s => 100 * (1 - (double)s.MemoryAvailableBytes!.Value / s.MemoryTotalBytes!.Value)).ToList();
            var last = day.Last();
            var diskUsed = last.DiskTotalBytes is > 0 && last.DiskFreeBytes is { } free ? F(100 * (1 - (double)free / last.DiskTotalBytes.Value), "0") : "-";
            Line($"| {day.Key:yyyy-MM-dd} | {(cpu.Count == 0 ? "-" : $"{F(cpu.Average(), "0")} / {F(cpu.Max(), "0")}")} | " +
                 $"{(memory.Count == 0 ? "-" : F(memory.Max(), "0"))} | {diskUsed} | {(last.DiskFreeBytes is { } f ? ByteSize.Format(f) : "-")} | " +
                 $"{ByteSize.Format(last.DatabaseBytes)} | {day.Max(s => s.DatabaseConnections)} of {last.DatabaseMaxConnections} |");
        }

        Line();
        Line("## Largest database tables");
        Line();
        Line($"Raw check results are kept {retentionDays ?? "30"} days (setting retention.check-results-days when TimescaleDB is not installed).");
        Line();
        Line("| Table | Size (with indexes) | Rows (estimate) |");
        Line("|---|---|---|");
        foreach (var table in view.Tables)
        {
            Line($"| {table.Name} | {ByteSize.Format(table.Bytes)} | {table.Rows.ToString("N0", CultureInfo.InvariantCulture)} |");
        }

        Line();
        Line("## Costliest database statements (pg_stat_statements, by total time)");
        Line();
        if (view.Queries is not { } queries)
        {
            Line("Not read yet: the workers read them once an hour.");
        }
        else if (queries.Problem is { } problem)
        {
            Line($"{problem} (read {Utc(queries.ReadAt)})");
        }
        else
        {
            Line($"Read {Utc(queries.ReadAt)}.");
            Line();
            foreach (var (query, index) in queries.Queries.Select((q, i) => (q, i + 1)))
            {
                Line($"{index}. {query.Calls.ToString("N0", CultureInfo.InvariantCulture)} calls, total {F(query.TotalMs / 1000)} s, " +
                     $"mean {F(query.MeanMs, "0.##")} ms, {query.Rows.ToString("N0", CultureInfo.InvariantCulture)} rows");
                Line();
                Line("   ```sql");
                Line("   " + query.Query.ReplaceLineEndings(" "));
                Line("   ```");
            }
        }

        var fleeto = samples.Count > 0 ? InstanceHealthRules.ParseDetail(samples[^1].DetailJson).Fleeto : FleetoHealth.Empty;
        Line();
        Line("## Instance");
        Line();
        Line($"- Endpoints: {fleeto.EndpointsOnline} online of {fleeto.Endpoints}");
        Line($"- Stuck worker tasks: {(fleeto.StaleLoops.Count == 0 ? "none" : string.Join(", ", fleeto.StaleLoops))}");
        Line($"- Endpoint events waiting: {fleeto.UnprocessedEvents}{(fleeto.OldestUnprocessedEvent is { } e ? $", oldest {Utc(e)}" : "")}");
        Line($"- Emails waiting: {fleeto.OutboxEmails}{(fleeto.OldestOutboxEmail is { } m ? $", oldest {Utc(m)}" : "")}");
        Line($"- Webhooks waiting: {fleeto.OutboxWebhooks}{(fleeto.OldestOutboxWebhook is { } w ? $", oldest {Utc(w)}" : "")}");
        Line($"- Backups: {(fleeto.BackupConfigured ? "destination configured" : "no destination")}, last success {(fleeto.LastBackupAt is { } b ? Utc(b) : "never")}" +
             $"{(fleeto.LastBackupFailed ? ", last run failed" : "")}");
        return md.ToString();
    }

    private static string Cell(string value) => value.Replace("|", "/", StringComparison.Ordinal);

    private static string Percent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string At(DateTime utc) => Utc(utc);

    private static string Utc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
