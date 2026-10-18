using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            ("Statements measured in Diagnose", AppStatements(data, redactor)),
            ("N+1 suspects", NPlusOne(data, redactor)),
            ("Slowest events", Events(data, redactor)),
            ("Statement statistics", Statements(data, redactor)),
            ("Tables", Tables(data, redactor)),
            ("Indexes", Indexes(data, redactor)),
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

    /// <summary>The version of the JSON export; raised when a field changes meaning or goes away, not when one is added.</summary>
    public const int JsonSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>
    /// The full data of the period as JSON for tooling (spec §10), with a <c>schemaVersion</c>. Through the same redactor as the
    /// Markdown: keys, statements, callers and labels are masked; nothing is cut except the fixed caps of the dataset.
    /// </summary>
    public static string Json(PerformanceDataset data, IReadOnlyList<PerfFinding> findings, IReadOnlyList<PerfCategoryScore> scores, DateTime generatedAt,
        string version, PerfRedactor redactor)
    {
        var report = new
        {
            SchemaVersion = JsonSchemaVersion,
            Product = "Fleeto",
            Version = version,
            GeneratedAt = generatedAt,
            Period = new { data.From, data.To, Minutes = (int)Math.Round(data.Length.TotalMinutes), data.DiagnoseMinutes, data.DiagnoseShare },
            Settings = new
            {
                Level = data.Settings.Level,
                DisabledCollectors = data.Settings.DisabledCollectors,
                data.Settings.SlowEventMs,
                Thresholds = PerfThresholds.All.ToDictionary(t => t.Key, t => data.Settings.Threshold(t.Key))
            },
            Scores = scores.Select(s => new { Category = s.Info.Key, s.Info.Label, s.Info.Measured, s.Score, Conclusion = redactor.Text(s.Conclusion), s.Findings }),
            Findings = findings.Select(f => new
            {
                Id = redactor.Text(f.Id),
                Category = PerfCategories.Of(f.Category).Key,
                f.Severity,
                Title = redactor.Text(f.Title),
                Explanation = redactor.Text(f.Explanation),
                Evidence = f.Evidence.Select(e => new { Label = redactor.Text(e.Label), Value = redactor.Text(e.Value) }),
                Cause = redactor.Text(f.Cause),
                Action = redactor.Text(f.Action),
                CodeRefs = f.CodeRefs.Select(r => new { r.Kind, Value = redactor.Text(r.Value), Detail = r.Detail is null ? null : redactor.Sql(r.Detail) }),
                f.ImpactMs
            }),
            Flows = data.Series.Select(s => new
            {
                s.Component, s.Kind, Key = redactor.Text(s.Key), SubKey = redactor.Text(s.SubKey), s.Count, s.Errors, s.Sum, s.Max, s.P50, s.P95, s.P99,
                s.Bytes, s.DbCount, s.DbMs, s.ExternalMs, s.PeakCount
            }),
            Fingerprints = data.Fingerprints.Values.Select(f => new { f.Hash, Sql = redactor.Sql(f.Sql), Callers = f.Callers.Select(redactor.Text) }),
            Events = data.Events.Select(e => new
            {
                e.Time, e.Component, e.Kind, Key = redactor.Text(e.Key), SubKey = redactor.Text(e.SubKey), e.DurationMs, e.DbCount, e.DbMs, e.ExternalMs, e.Failed,
                e.Statements
            }),
            Runtime = data.Runtime,
            Host = data.Host,
            Statements = data.Statements.Select(s => new
            {
                s.QueryId, Query = redactor.Sql(s.Query), s.Calls, s.TotalMs, s.MeanMs, s.Rows, s.SharedBlocksRead, s.TempBlocksWritten
            }),
            StatementsProblem = data.StatementsProblem is null ? null : redactor.Text(data.StatementsProblem),
            Database = data.Database,
            Tables = data.Tables.Select(t => t with { Name = redactor.Text(t.Name) }),
            Indexes = data.Indexes.Select(i => i with
            {
                Table = redactor.Text(i.Table), Name = redactor.Text(i.Name), Columns = redactor.Text(i.Columns),
                DuplicateOf = i.DuplicateOf is null ? null : redactor.Text(i.DuplicateOf)
            }),
            Markers = data.Markers.Select(m => new { m.Time, m.Kind, Label = redactor.AdminText(m.Label) }),
            Limitations = data.Missing
        };
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    /// <summary>A plan of a top statement for the ZIP export: its file name and the plan as JSON.</summary>
    public sealed record PlanFile(string Name, string Json);

    /// <summary>The ZIP export: the Markdown, the JSON and the generic plans of the top statements under <c>query-plans/</c>.</summary>
    public static byte[] Zip(string markdown, string json, IReadOnlyList<PlanFile> plans)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }

            Add("fleeto-performance.md", markdown);
            Add("fleeto-performance.json", json);
            foreach (var plan in plans)
            {
                Add($"query-plans/{plan.Name}.json", plan.Json);
            }
        }

        return buffer.ToArray();
    }

    private static string Section(string title, string body) => $"## {title}\n\n{body.TrimEnd()}\n\n";

    private static string Header(PerformanceDataset data, DateTime generatedAt, string version) =>
        $"# Fleeto performance report\n\n" +
        $"Period {Utc(data.From)} to {Utc(data.To)} ({Ms(data.Length.TotalMilliseconds)}) · Fleeto {version} · generated {Utc(generatedAt)} · " +
        $"share of the period in Diagnose: {Pct(data.DiagnoseShare * 100)} ({data.DiagnoseMinutes} {(data.DiagnoseMinutes == 1 ? "minute" : "minutes")}).\n\n";

    private static string Instructions(string version) => $"""
        - This report comes from Settings, Performance of a Fleeto instance on version {version}. It measures Fleeto itself, never the endpoints it manages.
        - Start at "Top bottlenecks". Each one names where to look: a page route template, an API route, a worker loop, an agent message type, a statement or a table.
        - Page interactions are Blazor pages: find the page by its route template (the `@page` directive) under `src/Fleeto.Web/Components/Pages`, and the work it does in its service under `src/Fleeto.Web/Services`. A sub key `load` is the page loading its data (`FleetoPageBase.LoadAsync`), `event` a click or other event on it.
        - API routes are mapped in `src/Fleeto.Web/Api/PublicApi.cs`; sign-in routes in `src/Fleeto.Web/Account`.
        - Worker loops are classes deriving from `WorkerLoop` under `src/Fleeto.Workers`, named by the `Name` they pass to the base constructor. Agent message types are in `src/Fleeto.Protocol`, handled in `AgentSessionManager.HandleAsync` under `src/Fleeto.Gateway/Sessions` with the store under `src/Fleeto.Gateway/Data`. Signing request kinds are handled under `src/Fleeto.Signer/Handlers`.
        - Queries are EF Core LINQ or SQL constants in the services and stores. Statements measured in Diagnose carry their fingerprint (16 hexadecimal characters), their normalized text (every literal a `?`) and the method that sent them as `Namespace.Type.Method`: open that method first. Statement statistics are PostgreSQL's own normalized text (parameters as `$1`); match those on table and column names.
        - An N+1 suspect is one statement sent many times within one page interaction, request, loop run or agent message: replace the query per item by one query for all of them.
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

    private static string AppStatements(PerformanceDataset data, PerfRedactor redactor)
    {
        var statements = data.Of(PerfKind.Statement).Where(s => s.SubKey is not (PerfKeys.MonitorSource or PerfKeys.DashboardSource)).GroupBy(s => s.Key)
            .Select(g => (Hash: g.Key, Series: g.ToList(), Total: g.Sum(s => s.Sum), Calls: g.Sum(s => s.Count), Errors: g.Sum(s => s.Errors),
                Histogram: g.Aggregate((int[]?)null, (h, s) => PerfHistogram.Add(h, s.Histogram)) ?? [], Max: g.Max(s => s.Max)))
            .OrderByDescending(s => s.Total).Take(15).ToList();
        if (statements.Count == 0)
        {
            return data.DiagnoseMinutes == 0 ? "Diagnose did not run in this period." : "No statements were measured in the minutes Diagnose ran.";
        }

        var total = data.Of(PerfKind.Statement).Sum(s => s.Sum);
        var md = new StringBuilder("| # | Fingerprint | Calls | Total | Mean | p95 | Share | Failed | Sent by | Caller |\n|---|---|---|---|---|---|---|---|---|---|\n");
        for (var i = 0; i < statements.Count; i++)
        {
            var s = statements[i];
            var sources = s.Series.GroupBy(x => x.SubKey).OrderByDescending(g => g.Sum(x => x.Sum)).Take(3).Select(g => $"`{Cell(redactor.Text(g.Key))}`");
            var callers = data.Fingerprint(s.Hash)?.Callers ?? [];
            md.Append($"| {i + 1} | `{s.Hash}` | {Count(s.Calls)} | {Ms(s.Total)} | {Ms(s.Calls == 0 ? 0 : s.Total / s.Calls)} | {Ms(PerfHistogram.Percentile(s.Histogram, 0.95, s.Max))} | " +
                      $"{Pct(total > 0 ? s.Total / total * 100 : 0)} | {Count(s.Errors)} | {string.Join(", ", sources)} | " +
                      $"{(callers.Count == 0 ? "unknown" : string.Join(", ", callers.Select(c => $"`{Cell(redactor.Text(c))}`")))} |\n");
        }

        md.Append('\n');
        for (var i = 0; i < statements.Count; i++)
        {
            if (data.Fingerprint(statements[i].Hash) is { } fingerprint)
            {
                md.Append($"{i + 1}. fingerprint `{statements[i].Hash}`\n\n```sql\n{redactor.Sql(fingerprint.Sql)}\n```\n\n");
            }
        }

        return md.ToString();
    }

    private static string NPlusOne(PerformanceDataset data, PerfRedactor redactor)
    {
        var suspects = data.Of(PerfKind.NPlusOne).GroupBy(s => (s.Key, s.SubKey))
            .Select(g => (Source: g.Key.Key, Hash: g.Key.SubKey, Units: g.Sum(s => s.Count), Repeats: g.Sum(s => s.Sum), Most: g.Max(s => s.Max), Ms: g.Sum(s => s.DbMs)))
            .OrderByDescending(s => s.Ms).Take(DetailRows).ToList();
        if (suspects.Count == 0)
        {
            return data.DiagnoseMinutes == 0 ? "Diagnose did not run in this period, so N+1 was not looked for." : "None: no statement ran more often than the threshold within one unit of work.";
        }

        var md = new StringBuilder("| Source | Fingerprint | Occurrences | Repeats per occurrence | Most | Time of the repeats | Caller |\n|---|---|---|---|---|---|---|\n");
        foreach (var s in suspects)
        {
            var callers = data.Fingerprint(s.Hash)?.Callers ?? [];
            md.Append($"| `{Cell(redactor.Text(s.Source))}` | `{s.Hash}` | {Count(s.Units)} | {Num(s.Units == 0 ? 0 : s.Repeats / s.Units, "0.#")} | {Num(s.Most, "0")} | {Ms(s.Ms)} | " +
                      $"{(callers.Count == 0 ? "unknown" : string.Join(", ", callers.Select(c => $"`{Cell(redactor.Text(c))}`")))} |\n");
        }

        return md.ToString();
    }

    private static string Events(PerformanceDataset data, PerfRedactor redactor)
    {
        if (data.Events.Count == 0)
        {
            return $"None: nothing took longer than {Ms(data.Settings.SlowEventMs)} (the setting Slow event).";
        }

        var md = new StringBuilder("| Time | Component | Kind | Key | Sub key | Duration | DB | DB commands | External | Failed | Heaviest statements |\n|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var e in data.Events.Take(DetailRows))
        {
            var heaviest = e.Statements.Count == 0 ? "–" : string.Join(", ", e.Statements.Select(x => $"`{x.Hash}` ×{x.Count} {Ms(x.Ms)}"));
            md.Append($"| {Utc(e.Time)} | {e.Component} | {e.Kind} | `{Cell(redactor.Text(e.Key))}` | {Cell(redactor.Text(e.SubKey))} | {Ms(e.DurationMs)} | {Ms(e.DbMs)} | " +
                      $"{e.DbCount} | {Ms(e.ExternalMs)} | {(e.Failed ? "yes" : "no")} | {heaviest} |\n");
        }

        return md.ToString();
    }

    private static string Indexes(PerformanceDataset data, PerfRedactor redactor)
    {
        if (data.Indexes.Count == 0)
        {
            return "The indexes could not be read.";
        }

        var hypertables = data.Tables.Where(t => t.IsHypertable).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var unused = data.Indexes.Where(i => i is { Scans: 0, Unique: false, Primary: false } && i.Bytes >= 1024 * 1024 && !hypertables.Contains(i.Table))
            .Take(DetailRows).ToList();
        var duplicates = data.Indexes.Where(i => i.DuplicateOf is not null).Take(DetailRows).ToList();
        if (unused.Count == 0 && duplicates.Count == 0)
        {
            return "No unused index of 1 MB or more and no duplicate index.";
        }

        var md = new StringBuilder("Unused means no scan since the statistics were last reset; on a fresh instance that says little.\n\n");
        md.Append("| Table | Index | Columns | Size | Scans | Note |\n|---|---|---|---|---|---|\n");
        foreach (var i in duplicates)
        {
            md.Append($"| `{Cell(redactor.Text(i.Table))}` | `{Cell(redactor.Text(i.Name))}` | {Cell(redactor.Text(i.Columns))} | {Bytes(i.Bytes)} | {Count(i.Scans)} | duplicates `{Cell(redactor.Text(i.DuplicateOf))}` |\n");
        }

        foreach (var i in unused.Where(u => duplicates.All(d => d.Name != u.Name)))
        {
            md.Append($"| `{Cell(redactor.Text(i.Table))}` | `{Cell(redactor.Text(i.Name))}` | {Cell(redactor.Text(i.Columns))} | {Bytes(i.Bytes)} | 0 | unused |\n");
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

        var sessions = d.Sessions is { } x
            ? $"- Sessions at the last snapshot: {x.Active} active, {x.Idle} idle, {x.IdleInTransaction} idle in a transaction (longest {Ms(x.LongestIdleInTransactionSeconds * 1000)}), " +
              $"{x.LockWaiting} waiting for a lock (longest {Ms(x.LongestLockWaitSeconds * 1000)}); longest open transaction {Ms(x.LongestTransactionSeconds * 1000)}\n"
            : "- Sessions: not in the snapshots of this period\n";
        return $"- Cache hit ratio: {(d.CacheHitRatio is { } hit ? Pct(hit * 100) : "– (no baseline)")}\n" +
               $"- Temporary files: {(d.TempFiles is { } files ? Count(files) : "–")}, {(d.TempBytes is { } bytes ? Bytes(bytes) : "–")}\n" +
               $"- Deadlocks: {(d.Deadlocks is { } deadlocks ? Count(deadlocks) : "–")}\n" +
               $"- Oldest transaction id age: {Count(d.WraparoundAge)}\n" + sessions;
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

        // In Diagnose the sub key names the class that threw and the kind of work it happened in.
        return "| Component | Type | Thrown by (Diagnose) | Count |\n|---|---|---|---|\n" +
               string.Concat(top.Select(s => $"| {s.Component} | `{Cell(redactor.Text(s.Key))}` | {(s.SubKey.Length == 0 ? "–" : $"`{Cell(redactor.Text(s.SubKey))}`")} | {Count(s.Count)} |\n"));
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
