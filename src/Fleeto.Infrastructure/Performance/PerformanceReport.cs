using System.Globalization;
using System.Text;
using Fleeto.Core.Performance;
using static Fleeto.Core.Performance.PerformanceRules;

namespace Fleeto.Infrastructure.Performance;

/// <summary>
/// The export of the performance monitor for Claude Code in this repository (0.7.0, PERFORMANCE.md §9, spec §10): Markdown in the
/// order of the spec, under 100 KB, everything through the redactor. It starts with what Claude needs to find the code and ends
/// with what was not measured, so absence of data is never read as absence of problems. No instance name, no ids, no values.
/// </summary>
public static class PerformanceReport
{
    public const int MaxBytes = 100 * 1024;

    private const int DetailRows = 20;

    public static string Markdown(PerformanceDataset data, IReadOnlyList<PerfFinding> findings, IReadOnlyList<PerfCategoryScore> scores,
        DateTime generatedAt, string version, PerfRedactor redactor)
    {
        var sections = new List<(string Title, string Body)>
        {
            ("", Header(data, generatedAt, version)),
            ("Instructions for Claude Code", Instructions(version)),
            ("Environment", Environment(data)),
            ("Summary per category", Summary(scores)),
            ("Top bottlenecks", Bottlenecks(findings, redactor)),
            ("Page interactions", Flows(data, PerfKind.Interaction, redactor, "Page (route template)")),
            ("API and sign-in requests", Flows(data, PerfKind.Http, redactor, "Route")),
            ("Worker loops", Flows(data, PerfKind.WorkerRun, redactor, "Loop")),
            ("Agent messages", Flows(data, PerfKind.AgentMessage, redactor, "Message type")),
            ("Signing requests", Flows(data, PerfKind.Signing, redactor, "Request kind")),
            ("External calls", Flows(data, PerfKind.External, redactor, "Host")),
            ("Database time by source", Flows(data, PerfKind.Db, redactor, "Source")),
            ("Statement statistics", Statements(data, redactor)),
            ("Tables", Tables(data, redactor)),
            ("Database health", DatabaseHealth(data)),
            ("Runtime per component", Runtime(data)),
            ("Exceptions", Exceptions(data, redactor)),
            ("Markers", Markers(data, redactor)),
        };
        var limitations = ("Missing data / limitations", Limitations(data));

        var md = new StringBuilder();
        var tail = Section(limitations.Item1, limitations.Item2);
        foreach (var (title, body) in sections)
        {
            var section = title.Length == 0 ? body : Section(title, body);
            if (Encoding.UTF8.GetByteCount(md.ToString()) + Encoding.UTF8.GetByteCount(section) + Encoding.UTF8.GetByteCount(tail) > MaxBytes - 512)
            {
                md.Append("> The report was cut here to stay under 100 KB. The sections below this line were left out.\n\n");
                break;
            }

            md.Append(section);
        }

        md.Append(tail);
        return md.ToString();
    }

    private static string Section(string title, string body) => $"## {title}\n\n{body.TrimEnd()}\n\n";

    private static string Header(PerformanceDataset data, DateTime generatedAt, string version) =>
        $"# Fleeto performance report\n\n" +
        $"Period {Utc(data.From)} to {Utc(data.To)} ({Ms(data.Length.TotalMilliseconds)}) · Fleeto {version} · generated {Utc(generatedAt)} · " +
        "share of the period in Diagnose: 0% (Diagnose comes with 0.7.0 step 2).\n\n";

    private static string Instructions(string version) => $"""
        - This report comes from Settings, Performance of a Fleeto instance on version {version}. It measures Fleeto itself, never the endpoints it manages.
        - Start at "Top bottlenecks". Each one names where to look: a page route template, an API route, a worker loop, an agent message type, a statement or a table.
        - Page interactions are Blazor pages: find the page by its route template (the `@page` directive) under `src/Fleeto.Web/Components/Pages`, and the work it does in its service under `src/Fleeto.Web/Services`. A sub key `load` is the page loading its data (`FleetoPageBase.LoadAsync`), `event` a click or other event on it.
        - API routes are mapped in `src/Fleeto.Web/Api/PublicApi.cs`; sign-in routes in `src/Fleeto.Web/Account`.
        - Worker loops are classes deriving from `WorkerLoop` under `src/Fleeto.Workers`, named by the `Name` they pass to the base constructor. Agent message types are in `src/Fleeto.Protocol`, handled in `AgentSessionManager.HandleAsync` under `src/Fleeto.Gateway/Sessions` with the store under `src/Fleeto.Gateway/Data`. Signing request kinds are handled under `src/Fleeto.Signer/Handlers`.
        - Queries are EF Core LINQ or SQL constants in the services and stores; statements are PostgreSQL's normalized text (parameters as `$1`). Match them on table and column names.
        - Propose one concrete change per bottleneck (an index, a query rewrite, batching instead of a query per item, caching, an async fix, a smaller payload) and say how to verify it on this page afterwards.
        - Schema changes are EF Core migrations under `src/Fleeto.Infrastructure/Migrations`: forward-only, expand/contract (the previous release must run on the new schema), and every new table gets its grants in `DatabaseGrants`. Indexes on hypertables (`CheckResults`, `PerfMinutes`) follow TimescaleDB.
        - When the bottleneck is the VPS (CPU steal, memory, disk), say so instead of changing code.
        - The report holds no personal data: no ids, client codes, host names, users, paths or SQL parameter values. Placeholders such as `{"{host}"}` or `{"{client}"}` were masked by the redactor.
        """;

    private static string Environment(PerformanceDataset data)
    {
        var lines = new List<string>();
        if (data.Host is { } host)
        {
            string Known(long? bytes) => bytes is { } value ? Bytes(value) : "unknown";
            lines.Add($"- VPS (shared by every instance on it): {host.Cores?.ToString(CultureInfo.InvariantCulture) ?? "–"} cores, memory {Known(host.MemoryTotalBytes)} " +
                      $"(lowest available {Known(host.MemoryAvailableMinBytes)}), swap {Known(host.SwapTotalBytes)}, " +
                      $"disk {Known(host.DiskFreeBytes)} free of {Known(host.DiskTotalBytes)}; measured {host.Minutes} minutes.");
        }
        else
        {
            lines.Add("- VPS: not measured in this period.");
        }

        if (data.Database is { } database)
        {
            lines.Add($"- Database: {Bytes(database.DatabaseBytes)}, {database.Connections} of {database.MaxConnections} connections at the last snapshot.");
        }

        lines.Add($"- Statement statistics: {(data.StatementsProblem is null ? data.Statements.Count > 0 ? "available" : "no difference between snapshots yet" : data.StatementsProblem.TrimEnd('.'))}.");
        foreach (var runtime in data.Runtime)
        {
            lines.Add($"- {runtime.Component}: version {runtime.Version ?? "–"}, database pool {(runtime.DbPoolMax > 0 ? runtime.DbPoolMax.ToString(CultureInfo.InvariantCulture) : "–")}, " +
                      $"peak connections {runtime.ConnectionsMax}, monitor overhead {Ms(runtime.OverheadMs)} ({Pct(runtime.Cores > 0 ? runtime.OverheadCpuPercent / runtime.Cores : runtime.OverheadCpuPercent)} of the CPU).");
        }

        lines.Add($"- Runtime: .NET {System.Environment.Version.ToString(3)}; level {data.Settings.Level}.");
        var interactions = data.Of(PerfKind.Interaction).Sum(s => s.Count);
        var requests = data.Of(PerfKind.Http).Sum(s => s.Count);
        lines.Add($"- Page interactions {Count(interactions)}, HTTP requests {Count(requests)}, worker runs {Count(data.Of(PerfKind.WorkerRun).Sum(s => s.Count))}, " +
                  $"agent messages {Count(data.Of(PerfKind.AgentMessage).Sum(s => s.Count))}.");
        return string.Join('\n', lines);
    }

    private static string Summary(IReadOnlyList<PerfCategoryScore> scores)
    {
        var md = new StringBuilder("| Category | Score | Findings | Conclusion |\n|---|---|---|---|\n");
        foreach (var score in scores.Where(s => s.Info.Measured))
        {
            md.Append($"| {score.Info.Label} | {(score.Score is { } value ? value.ToString(CultureInfo.InvariantCulture) : "no data")} | {score.Findings} | {Cell(score.Conclusion)} |\n");
        }

        return md.ToString();
    }

    private static string Bottlenecks(IReadOnlyList<PerfFinding> findings, PerfRedactor redactor)
    {
        if (findings.Count == 0)
        {
            return "None: no rule found a bottleneck in this period.";
        }

        var md = new StringBuilder();
        var rank = 0;
        foreach (var finding in findings.Take(MaxFindings))
        {
            rank++;
            md.Append($"### {rank}. {redactor.Text(finding.Title)}\n\n");
            md.Append($"- Category: {PerfCategories.Of(finding.Category).Label} · severity: {finding.Severity.ToString().ToLowerInvariant()} · " +
                      $"impact: {(finding.ImpactMs is { } impact ? Ms(impact) + " of extra waiting in the period" : "not estimated")} · id: `{redactor.Text(finding.Id)}`\n");
            md.Append($"- What it means: {redactor.Text(finding.Explanation)}\n");
            md.Append($"- Evidence: {string.Join("; ", finding.Evidence.Select(e => $"{redactor.Text(e.Label)} {redactor.Text(e.Value)}"))}\n");
            md.Append($"- Likely cause: {redactor.Text(finding.Cause)}\n");
            md.Append($"- Recommended action: {redactor.Text(finding.Action)}\n");
            foreach (var reference in finding.CodeRefs)
            {
                md.Append($"- Where to look: {reference.Kind} `{redactor.Text(reference.Value)}`\n");
                if (reference.Detail is { Length: > 0 } detail)
                {
                    md.Append($"\n```sql\n{redactor.Sql(detail)}\n```\n");
                }
            }

            md.Append('\n');
        }

        if (findings.Count > MaxFindings)
        {
            md.Append($"{findings.Count - MaxFindings} more findings with less impact are on the page.\n");
        }

        return md.ToString();
    }

    private static string Flows(PerformanceDataset data, PerfKind kind, PerfRedactor redactor, string keyLabel)
    {
        var flows = data.Of(kind).OrderByDescending(s => s.Sum).Take(DetailRows).ToList();
        if (flows.Count == 0)
        {
            return "None in this period.";
        }

        var valueIsMs = kind != PerfKind.Broadcast;
        var md = new StringBuilder($"| Component | {keyLabel} | Sub key | Count | Median | p95 | p99 | Total | DB share | DB per call | Errors | Bytes |\n");
        md.Append("|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var s in flows)
        {
            md.Append($"| {s.Component} | `{Cell(redactor.Text(s.Key))}` | {Cell(redactor.Text(s.SubKey))} | {Count(s.Count)} | {(valueIsMs ? Ms(s.P50) : "–")} | " +
                      $"{(valueIsMs ? Ms(s.P95) : "–")} | {(valueIsMs ? Ms(s.P99) : "–")} | {(valueIsMs ? Ms(s.Sum) : "–")} | {(kind == PerfKind.Db ? "–" : Pct(s.DbShare * 100))} | " +
                      $"{Num(s.DbPerCall, "0.#")} | {Count(s.Errors)} | {Bytes(s.Bytes)} |\n");
        }

        return md.ToString();
    }

    private static string Statements(PerformanceDataset data, PerfRedactor redactor)
    {
        if (data.Statements.Count == 0)
        {
            return data.StatementsProblem ?? "No difference between two statement snapshots in this period yet.";
        }

        var total = data.Statements.Sum(s => s.TotalMs);
        var md = new StringBuilder("| # | Calls | Total | Mean | Share | Rows | Blocks from disk | Temp blocks |\n|---|---|---|---|---|---|---|---|\n");
        var top = data.Statements.Take(15).ToList();
        for (var i = 0; i < top.Count; i++)
        {
            var s = top[i];
            md.Append($"| {i + 1} | {Count(s.Calls)} | {Ms(s.TotalMs)} | {Ms(s.MeanMs)} | {Pct(total > 0 ? s.TotalMs / total * 100 : 0)} | {Count(s.Rows)} | " +
                      $"{Count(s.SharedBlocksRead)} | {Count(s.TempBlocksWritten)} |\n");
        }

        md.Append('\n');
        for (var i = 0; i < top.Count; i++)
        {
            md.Append($"{i + 1}. query id {top[i].QueryId.ToString(CultureInfo.InvariantCulture)}\n\n```sql\n{redactor.Sql(top[i].Query)}\n```\n\n");
        }

        return md.ToString();
    }

    private static string Tables(PerformanceDataset data, PerfRedactor redactor)
    {
        if (data.Tables.Count == 0)
        {
            return "No table snapshot in this period.";
        }

        var md = new StringBuilder("| Table | Size | Growth | Rows | Seq scans | Rows per seq scan | Index scans | Dead rows | Last vacuum | Hypertable |\n");
        md.Append("|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var t in data.Tables.Take(DetailRows))
        {
            var perScan = t is { SequentialScans: > 0, SequentialRowsRead: { } read } ? Count(read / t.SequentialScans.Value) : "–";
            md.Append($"| `{Cell(redactor.Text(t.Name))}` | {Bytes(t.Bytes)} | {(t.BytesGrowth is { } growth ? Bytes(growth) : "–")} | {Count(t.Rows)} | " +
                      $"{(t.SequentialScans is { } seq ? Count(seq) : "–")} | {perScan} | {(t.IndexScans is { } idx ? Count(idx) : "–")} | {Count(t.DeadRows)} | " +
                      $"{(t.LastVacuum is { } at ? Utc(at) : "never")} | {(t.IsHypertable ? $"yes, {t.Chunks} chunks{(t.Compressed ? ", compressed" : "")}{(t.RetentionPolicy ? ", retention" : "")}" : "no")} |\n");
        }

        return md.ToString();
    }

    private static string DatabaseHealth(PerformanceDataset data)
    {
        if (data.Database is not { } d)
        {
            return "No database snapshot in this period.";
        }

        return $"- Cache hit ratio: {(d.CacheHitRatio is { } hit ? Pct(hit * 100) : "– (no baseline)")}\n" +
               $"- Temporary files: {(d.TempFiles is { } files ? Count(files) : "–")}, {(d.TempBytes is { } bytes ? Bytes(bytes) : "–")}\n" +
               $"- Deadlocks: {(d.Deadlocks is { } deadlocks ? Count(deadlocks) : "–")}\n" +
               $"- Oldest transaction id age: {Count(d.WraparoundAge)}\n";
    }

    private static string Runtime(PerformanceDataset data)
    {
        if (data.Runtime.Count == 0)
        {
            return "No runtime figures in this period.";
        }

        var md = new StringBuilder("| Component | Minutes | CPU avg | CPU max | Working set | Heap | GC pause | Allocated | Threads | Queue | Pool busy/max | Pool timeouts | Exceptions |\n");
        md.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var r in data.Runtime)
        {
            md.Append($"| {r.Component} | {r.Minutes} | {Pct(r.CpuPercentAverage)} | {Pct(r.CpuPercentMax)} | {Bytes(r.WorkingSetMaxBytes)} | {Bytes(r.HeapMaxBytes)} | " +
                      $"{Pct(r.GcPausePercent)} | {Bytes(r.AllocatedBytes)} | {r.ThreadPoolThreadsMax} | {r.ThreadPoolQueueMax} | {r.DbPoolBusyMax}/{(r.DbPoolMax > 0 ? r.DbPoolMax : 0)} | " +
                      $"{Count(r.DbPoolTimeouts)} | {Count(r.Exceptions)} |\n");
        }

        return md.ToString();
    }

    private static string Exceptions(PerformanceDataset data, PerfRedactor redactor)
    {
        var top = data.Of(PerfKind.Exception).OrderByDescending(s => s.Count).Take(DetailRows).ToList();
        if (top.Count == 0)
        {
            return "None in this period.";
        }

        return "| Component | Type | Count |\n|---|---|---|\n" +
               string.Concat(top.Select(s => $"| {s.Component} | `{Cell(redactor.Text(s.Key))}` | {Count(s.Count)} |\n"));
    }

    private static string Markers(PerformanceDataset data, PerfRedactor redactor) =>
        data.Markers.Count == 0
            ? "None in this period."
            : string.Join('\n', data.Markers.Select(m => $"- {Utc(m.Time)} · {m.Kind.ToLowerInvariant()} · {redactor.AdminText(m.Label)}"));

    private static string Limitations(PerformanceDataset data) =>
        data.Missing.Count == 0 ? "None." : string.Join('\n', data.Missing.Select(m => "- " + m));

    private static string Utc(DateTime time) => time.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');
}
