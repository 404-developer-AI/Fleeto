# Built-in performance monitoring — reusable spec

> Purpose of this document: tell Claude (and any developer) what I expect when an application of mine gets a **built-in performance monitor**. It is stack-agnostic; the reference implementation is the Servicedesk app (ASP.NET Core 8 + Dapper + PostgreSQL + React/Vite + SignalR, Docker behind nginx). Where a detail is stack-specific it is marked *(ref impl)* — translate it to the stack at hand, don't copy it blindly.
>
> Put this file (or a pointer to it) in the project's `CLAUDE.md` / architecture docs. Project-specific deviations go in the project's own docs, not here.

---

## 1. What the feature is

An **admin-only page inside the application** that answers two questions about *the application itself*:

1. **Where is it slow?** — sorted into a fixed set of categories (hosting, network, backend code, database, DB maintenance, frontend, background & realtime).
2. **Why, and what should change?** — explainable findings with evidence, likely cause, recommended action and *exact code references* (route template, query fingerprint + calling method, table, worker, frontend route).

Its main deliverable is an **export for an AI coding agent** (Claude Code): after a "deep" monitoring window (typically 1 h of *Diagnose* mode) the admin exports a compact, redacted report; I hand it to Claude in the repository and Claude proposes and implements concrete fixes, then the dashboard is used to verify the improvement.

It is **not**:
- an APM/SaaS replacement (no external telemetry vendor, no data leaves the install);
- a log viewer, uptime monitor or error tracker;
- monitoring of things the app *manages* (e.g. in an RMM: the managed endpoints' CPU). That is product functionality. This spec is about the app's **own** performance — the two must never share tables, settings or UI.

## 2. Hard rules (non-negotiable)

1. **Never a database write per request / per event.** Collectors record into an in-memory window; one batch write per minute.
2. **Metric keys are bounded and value-free.** Keys are route *templates*, normalised SQL fingerprints, worker names, host names, frontend route templates — **never** ids, parameter values, user names, e-mail addresses, IPs, query strings. Every key map is capped with an `__other__` overflow bucket.
3. **Cheap by default, expensive only on purpose.** Basic level must stay well under 1 % overhead and be safe to leave on forever. Expensive capture (statement text, call stacks, 100 % browser sampling) only runs in Diagnose mode.
4. **Diagnose always ends by itself.** It is stored as an *until timestamp on server time*, evaluated on every check — never a level someone can forget to switch off. Default 60 min, capped by a max setting.
5. **Server time is the source of truth** for buckets, periods, Diagnose expiry, retention. Client timestamps are never trusted.
6. **Admin-only, MFA-complete.** The page, its API and the export are admin-only. Timing headers to the browser (e.g. `Server-Timing`) only for authenticated, MFA-complete staff sessions — never on auth, public, portal or other anonymous endpoints (timing on sign-in endpoints is an enumeration side channel), never for impersonated sessions.
7. **Everything that leaves the server is redacted** through one redactor (see § 11) — dashboard responses and exports alike.
8. **Read-only towards the database engine.** Allowed: catalog/statistics views, generic `EXPLAIN` without execution, `ANALYZE <table>` on an existing table on click. Forbidden from the app: `EXPLAIN ANALYZE` of arbitrary SQL, VACUUM FULL, REINDEX, config changes, statistics resets, any SQL text supplied by the client (the client sends an id, the server looks up its own stored SQL). The app DB role gets monitoring rights only (`pg_monitor` in PostgreSQL).
9. **Audited:** export, Diagnose start/stop, plan/analyze, benchmark, edge check, markers, data clear, settings changes.
10. **The monitor measures itself** and shows its own overhead (ms per request, share of CPU) so it can prove it isn't the problem.
11. **All thresholds and tunables are settings** (DB-backed, on the settings tab, with descriptions and sane defaults) — nothing hardcoded that an admin might want to change.
12. **Degrade, don't fail.** Missing permissions, non-Linux host, a collector switched off or an unreadable section → the page and report say *what is missing and how to fix it* (e.g. "run update.sh to grant pg_monitor"); everything else keeps working.

## 3. Levels

| Level | What runs | Overhead target |
|---|---|---|
| **Off** | Nothing is measured. | 0 |
| **Basic** *(default)* | Per-minute aggregates of every layer: HTTP, DB time per request/worker, external HTTP, runtime, host, workers, realtime, sampled RUM, periodic DB statistics snapshots. | < 1 % |
| **Diagnose** (= Basic + until-timestamp) | Additionally: per-statement capture → fingerprint, N+1 detection, calling method per query, heaviest queries per slow request, 100 % RUM sampling, faster DB/table snapshot intervals. | a few % |

Each collector can also be switched on/off individually. Hot paths read **one immutable options snapshot**, refreshed every few seconds and immediately after a settings write — never a settings lookup per request.

## 4. The seven categories

Every finding, score and dashboard section maps to exactly one category. Each category has a plain-language question shown in the UI:

| Key | Label | Question |
|---|---|---|
| `hosting` | Hosting / server | Is the server (CPU, memory, disk) or the VPS provider the bottleneck? |
| `network` | Network / internet | Is time lost between the browser and the server? |
| `code` | Backend code | Is our own code, an external API or the request pipeline slow? |
| `database` | Database queries | Which queries cost the most, and why? |
| `maintenance` | Database maintenance | Is the database cleaning up and caching well? |
| `frontend` | Frontend / browser | Is the page slow to load or sluggish to use? |
| `background` | Background & realtime | Do background jobs or realtime traffic slow users down? |

An app may add a category when it has a genuinely distinct layer (e.g. an RMM's **agent ingest / device channel**: heartbeat throughput, command dispatch latency, connected-agent count, queue lag). Don't add categories for cosmetic reasons.

## 5. Collectors — what to measure per layer

### HTTP (server)
- Middleware placed right after routing (route template known), before rate limiting/auth/CSRF, so rejected requests are measured too.
- Per route template + method + status class: count, histogram, errors, response bytes (pass-through counting stream), plus a **time split**: pipeline (middleware until a marker), DB, external HTTP, own code (= remainder).
- Labels: route template only; special labels `unmatched`, `spa-fallback`. Static files, realtime hub paths and the monitor's own endpoints are not measured (the dashboard's own DB work is attributed to a dedicated `perf-dashboard` source).
- **Slow requests** above a threshold are kept individually (with breakdown; in Diagnose with their heaviest query fingerprints) — still without ids or values.
- In-flight count = API requests only.

### Database (client side)
- Hook the DB driver's **metrics** (not code changes in repositories): command duration → per-request DB count/time via an async-local request context; per source kind (`request`, `worker:<name>`, `other`); pool usage, pending, timeouts.
- Diagnose only: statement text at command end → **fingerprint** (literals → `?`, IN-lists collapsed, short hash), per-request **N+1** counting (same fingerprint > threshold in one request), and the **calling method** captured at command *start* (the repository frame is still on the stack there) under a stack-walk budget (e.g. 50/s). Outside Diagnose the tracing source must sample to "none" so no allocation happens.

### Database (server side)
- Periodic statement-statistics snapshot (`pg_stat_statements` *(ref impl)*): top N by total ∪ top M by mean, own DB only, folded per query id. Compute **reset-aware deltas** between snapshots; new statements count from first appearance. Never reset the statistics.
- Database snapshot: cache hit ratio, temp files, deadlocks, checkpoints, connections/activity, wraparound age.
- Table snapshot (hourly, faster in Diagnose, immediately after restart/clear): size, seq vs index scans, dead tuples, last (auto)vacuum/analyze. **Table deltas need a baseline** — no snapshot at/before the period start → report "–" and a gap line, never cumulative counters as if they were the period.
- Live views on demand: sessions, lock waits + blockers, I/O stats, relevant settings, bloat estimate, unused/duplicate indexes. Use catalog estimates, not per-file size functions (seconds on a large schema).

### External HTTP
- Every outbound HTTP client via the runtime's HTTP metrics: per host / method / status class (count, histogram, errors, 429s).

### Runtime / process
- GC (pause %, heap, allocations), thread pool (threads, queue length), lock contention, first-chance exceptions **by type name** (never messages; in Diagnose plus throwing class and source kind), process CPU and memory, server connection/queue metrics. Sample every ~10 s.
- Flows that are expected to fail (e.g. the monitor's own EXPLAIN attempts) are flagged so they don't count as exceptions/failed commands.

### Host (Linux)
- Parse `/proc` and cgroup files: CPU incl. **steal** and iowait, load average vs core count, available memory, swap, disk latency/utilisation (whole disks only), free space per relevant mount, cgroup CPU throttling and memory limits. Label host vs container. Non-Linux → "not available", not an error.
- If the database runs outside the app container, its process is only visible through its own statistics — say so.

### Background workers
- Every worker wraps **one iteration** in a run scope (`PerfWorkerRun.Start("name")` *(ref impl)*) → duration, items, errors; queries inside count as `worker:<name>`. Drop idle runs (< 25 ms, no items) to keep noise down. The monitor's own services are attributed to themselves.
- This is a rule for *every new worker*, not optional.

### Realtime (WebSockets / SignalR)
- Hub method duration, connects/disconnects, connected clients, and **broadcast counts per hub/method/target type** (all / group / user) — broadcasts to everyone multiply by open tabs. If a decorator is used, a test pins that it overrides every member.

### Search / fan-out
- If the app has global search or other fan-out (several sources in parallel), record each source's duration — the slowest source sets the latency.

### Frontend RUM (real user monitoring)
- Staff only, never public/customer portals. Sampling decided **once per page load** from a config endpoint (100 % in Diagnose); an unsampled tab re-checks periodically (visible tabs only) and joins late when Diagnose starts (late join skips page-load metrics).
- Collect: Web Vitals (LCP, INP, CLS + attribution target, FCP, TTFB), navigation timing, API calls with server/network split (via `Server-Timing`: network = browser time − server time) and **browser queueing** separately, long tasks + Long Animation Frames (script chunk/function only), route-change "open time" (void samples where the tab was hidden), API calls and KB per screen (first load vs in-app navigation), JS heap over time, realtime reconnects, HTTP protocol.
- Ingest endpoint: authenticated staff only, body cap (e.g. 32 KB), item cap, **metric whitelist**, value ranges, routes scrubbed by the redactor, bucketed on **server time**; own rate-limit policy; exempt from the stale-client version gate (telemetry must not trigger reloads).
- Build step writes a bundle-size manifest (chunk sizes) next to the app so the dashboard can show the largest chunks.

### Edge / proxy
- Reverse proxy log format with timing (`request_time`, upstream times, no query strings) so `request − upstream` = time between proxy and browser. The app does **not** parse proxy logs; it's for manual investigation.
- On-click **edge check** of the public URL: HTTP version, compression, asset caching headers, keep-alive, HSTS.

## 6. Collection architecture

- **In-memory window** keyed by bounded keys, lock-free aggregates (count, sum, min/max, histogram). Every map capped; overflow → `__other__`.
- **Flush service** swaps the window at each minute boundary and writes it in one batch (bulk copy), and flushes on shutdown.
- **Histograms on a fixed log-linear bucket set**, stored as arrays. Fixed buckets make histograms **additive**: minutes roll up into hours and any period still yields exact-to-bucket percentiles (p50/p95/p99). Never store pre-computed percentiles that can't be combined.
- Request context (async-local) carries: source kind, DB count/time, external time, N+1 counters, flags like "expected failures only".

## 7. Storage, rollup, retention

- Minute tables per domain (http, db query, span, RUM, N+1, runtime/minute incl. **app version + level**), plus hour twins; event tables (slow requests, worker runs); fingerprint dictionary; DB statement/db/table snapshots; markers; benchmarks; rollup state; alert state.
- Volume is small → ordinary tables. Backups dump them **structure-only**.
- Periods ≤ ~6 h read minute rows; longer periods read hour rows up to the rollup watermark plus minute rows after it.
- Maintenance job (every few minutes): idempotent hourly rollup (delete + rebuild per hour, bounded hours per pass), batched retention deletes per setting, **deploy marker on version change**, optional alerts.
- Schema follows the project's normal schema mechanism (idempotent bootstrap SQL in the ref impl).

## 8. Findings engine

Plain, explainable rules — **no ML, no black box**. A pure function `Evaluate(dataset) → findings` over one immutable dataset built for the period; unit-testable without a database.

Each finding has:
- `id` (stable, kebab-case), `category`, `severity` (info / warning / critical), `title` (with numbers)
- `explanation` (what it means, for a non-specialist admin)
- `evidence` (label/value pairs)
- `cause` (most likely cause) and `action` (what to change)
- `codeRefs`: kind + value (+ normalised SQL + caller for queries) — route template, fingerprint, table, worker, frontend route, host
- **impact estimate** = extra waiting time per occurrence × occurrences in the period, rendered in a human unit. Ranking is by impact, so a 50 ms query run 100 000× outranks a 3 s query run twice.

Number formatting in findings and exports is **culture-invariant** ("1.5 s", never "1,5 s").

**Health score** per category: 100 minus a penalty per finding by severity; `nodata` when the category had no data (never a fake 100).

**Budgets:** admin-defined lines like `METHOD route=ms`; compare each flow's p95 with its target.

**Comparisons:** by app version (join minute rows with the runtime row's version) and against the previous period of equal length; regression rule above a % threshold.

**Monitor rule:** report the monitor's own overhead, judged against the level that actually ran (Diagnose is allowed to cost more).

### Baseline rule catalogue (adapt per app)
| Category | Rules |
|---|---|
| hosting | CPU high, **CPU steal** (noisy VPS neighbour), load > cores, low available memory (swap only counts as pressure when memory is actually low), OOM kills, disk latency/utilisation/free space, cgroup throttling, server accept queue |
| network | high network share of API time (RUM), slow connections (connect/TLS) |
| code | slow routes vs threshold, large responses (p95 KB, file downloads excluded), DB share / external share / own-code share per route (capped at 100 %), N+1 suspects, pool wait, thread-pool queueing, GC pause, heap growth, lock waits, idle-in-transaction, rate-limited clients, external API errors/429, monitor overhead |
| database | top queries by total time (top N), seq scans on big tables, temp files (work_mem spills) |
| maintenance | cache hit ratio, dead tuples / stale vacuum, wraparound age, missing monitoring permission |
| frontend | LCP / INP / CLS over thresholds, long tasks per view, too many API calls per screen, **browser queueing**, slow route open time, oversized bundle chunks |
| background | worker overlap slows the API (needs enough minutes *without* the worker for a fair baseline **and** an absolute p95 gap, not just a ratio), long/failed worker runs, broadcast storms, slow search source |

Every threshold is a `Performance.Findings.*` setting; minimum-sample settings (e.g. min requests per route) prevent noise findings.

## 9. Dashboard (UI)

- Lives under Settings → Performance, admin-only, **lazy-loaded** (charts library only there).
- **Header:** level indicator + Diagnose start (duration picker) / stop with a live countdown on server time; period presets (15 min, 1 h, 6 h, 24 h, 7 d, 30 d, custom) anchored on the server clock; auto-refresh for short periods; export menu; the monitor's own overhead with an info tip. Selected tab/preset remembered per browser.
- **Tabs:**
  - *Overview* — category cards (score + one-line conclusion + question), Top bottlenecks (findings list, click → relevant tab), budgets, markers, timeline (latency/throughput/errors with deploy and manual markers).
  - *API* — routes table (count, p50/p95/p99, errors, KB, time split); per route: latency over time, distribution histogram, queries issued by the route (Diagnose), slowest requests with breakdown.
  - *Database* — DB time by source, app-measured queries, N+1 suspects, statement statistics, tables + growth chart, unused/duplicate indexes, sessions, lock waits, settings, server info, I/O stats; actions: generic plan, analyze table.
  - *Server* — CPU (host/steal/iowait/app), load, memory, disk, thread pool, connection pool, GC, connections & concurrency, exceptions by type; benchmark (CPU, fsync'd disk write, N × trivial DB round-trip) and edge check, on click only.
  - *Frontend* — per screen vitals with Core-Web-Vitals colour bands, API server-vs-network-vs-queue split, scripts blocking the page, first page load waterfall, JS heap trend, bundle chunks.
  - *Background* — worker timeline (bars per run), "do workers slow down the API?" comparison, workers table, external APIs per host, realtime connections, broadcasts, hub methods, search sources.
  - *Settings* — level, collectors, Diagnose durations, sampling, thresholds, slow request/query thresholds, budgets, retention, alerts, clear data (audited, confirmed).
- Every panel has an **info tip in plain language** explaining what it shows and what "bad" looks like. Empty states say *why* there is no data and what to do.
- Markers: automatic on deploy (version change), manual by the admin ("changed index X") — so before/after is visible on the timeline.
- Follows the app's design system and themes; must look premium, not like a generic admin template.

## 10. Export for AI (the core deliverable)

`GET …/export?format=zip|md|json&compare=…&plans=…`, admin-only, audited.

- **Markdown** (< ~100 KB, cut at a section boundary with a note pointing to the JSON when larger) — the file I hand to Claude.
- **JSON** with a `schemaVersion` — full data for tooling.
- **ZIP** = both + optional `query-plans/*.json` (generic plans of the top queries).
- Also "copy Markdown to clipboard" in the UI.

### Markdown structure (in this order)
1. Title + one line: period (in the install's time zone), app version, generated at, **share of the period in Diagnose**.
2. **Instructions for Claude Code** — project-specific, written for the repository it ships with. Template:
   - This report comes from the built-in performance monitor of this repository (*where in the UI*).
   - Start at "Top bottlenecks". Each points to route templates, query fingerprints, tables or workers; find them in the code: *where routes are mapped*, *where SQL lives* (the caller column names the issuing method), *where frontend routes are defined*.
   - Propose **one concrete change per bottleneck** (index, query rewrite, batching instead of N+1, caching, async fix, smaller payload, frontend change) and say **how to verify** the improvement with this dashboard.
   - When the bottleneck is the hosting (CPU steal, RAM, disk), say so explicitly instead of changing code.
   - Project constraints that matter for fixes (e.g. "schema changes go in the bootstrapper, never migrations").
   - The report contains no personal data: no ids, names, e-mail addresses, content or SQL parameter values.
3. Environment (cores, RAM, swap, free disk, DB version + monitoring access, pool size, runtime version, peak active users, request count, monitor overhead, key DB settings).
4. Summary per category (status, score, conclusion) + budgets.
5. **Top bottlenecks** sorted by impact (max 25): category, severity, impact, what it means, evidence, likely cause, recommended action, where to look (with normalised SQL and caller).
6. Detail tables (top-N, bounded): API routes, slow requests, app-measured queries, statement statistics, N+1 suspects, tables & indexes, DB health, server/runtime, frontend, external APIs & workers.
7. Comparison (by version and/or previous period) when requested.
8. Markers in the period.
9. **Missing data / limitations** — collectors off, platform limits, no baseline snapshot, missing permissions, "Diagnose did not run → no fingerprints/N+1/callers", unreadable sections. Always present ("None." when empty) so the AI never mistakes absence of data for absence of problems.

## 11. Security & privacy

- One **redactor** is the last line of defence for everything that leaves the server:
  - SQL → normalised (literals → `?`) then text-masked;
  - free text → e-mails `{email}`, GUIDs `{guid}`, IPv4/IPv6 `{ip}`, JWT/long mixed tokens `{token}`; admin-typed text (marker labels) also masks digit runs `{n}`;
  - client-reported routes → strip query/fragment, whitelist characters, id-like segments (all digits, GUID, long hex, e-mail, > 40 chars) → `{id}`, length cap; nothing sane left → drop.
- Keys are value-free *by construction*; the redactor exists for what slips through (hand-written SQL constants, exception type names, client routes).
- Plans: generic plan only, read-only transaction, short statement timeout, `SELECT/WITH` without DML only, SQL looked up server-side by id.
- RUM ingest validated against a whitelist; never trusted for anything but its own metrics.
- Exports are audited; data clear is audited and confirmed.
- Backups contain the perf tables' structure only.

## 12. Settings (generic key set)

`Performance.Level`, `Performance.DiagnoseUntilUtc`, `Performance.DiagnoseDefaultMinutes`, `Performance.DiagnoseMaxMinutes`, `Performance.Collectors.<Layer>.Enabled` (one per collector), `Performance.Rum.SamplePercent`, `Performance.ServerTimingEnabled`, `Performance.SlowRequestThresholdMs`, `Performance.SlowQueryThresholdMs`, `Performance.NPlusOneThreshold`, `Performance.PgSnapshotIntervalMinutes` (+ Diagnose interval), `Performance.Retention.{MinuteDays,HourDays,EventDays,PgSnapshotDays,TableSnapshotDays}`, `Performance.Budgets`, `Performance.Alerts.{Enabled,CooldownHours}`, `Performance.Findings.*` (one per threshold in § 8). All with defaults, descriptions and grouping on the settings page.

**Alerts (optional):** critical findings → the app's incident/health log, with a cooldown per finding id. No e-mail storms.

## 13. Tests I expect

- Findings rules: pure unit tests per rule (fires / doesn't fire / respects min-sample and thresholds), scoring incl. `nodata`.
- Histogram additivity and percentile accuracy; minute → hour rollup idempotency.
- Fingerprinting/normalisation and redactor cases (e-mail, GUID, IP, token, id segments, snake_case identifiers stay intact).
- Middleware: route-template labels, excluded paths, `Server-Timing` only for MFA-complete staff and never on auth/public endpoints.
- RUM ingest: auth, size/item caps, whitelist, value ranges.
- Access: non-admin → 403 on every endpoint and the export.
- Decorators (e.g. realtime lifetime manager) override every member (reflection test).

## 14. Adapting to a new application — checklist

1. Map the stack: where routes are defined, where SQL lives, how workers run, what the realtime channel is, which DB engine (statistics views equivalent to `pg_stat_statements` / `pg_stat_user_tables`?).
2. Decide whether an extra category is needed (see § 4) and what its rules are.
3. Wire collectors without touching business code: middleware, driver metrics/tracing hooks, HTTP client metrics, runtime counters, a worker run scope, a realtime filter/decorator.
4. Define bounded keys for every new dimension and the `__other__` cap.
5. Write the project-specific "Instructions for Claude Code" block of the export.
6. Register settings with defaults; add the page to the admin settings navigation and settings search.
7. Grant the DB role monitoring rights in install/update scripts; degrade gracefully without them.
8. Make "every new worker uses the run scope" and "metric keys never contain values" project rules in `CLAUDE.md`.

### Example: RMM / remote management tool
- Extra category **Agent channel**: connected agents (peak), heartbeat/check-in ingest rate and p95 processing time, ingest queue lag, command dispatch → result latency, reconnect storms, per-message-type payload size. Keys = message type / command type, **never** device id, hostname or tenant.
- Background: scheduled checks, patch/inventory jobs, alert evaluation as workers with run scopes; overlap rule matters (fleet-wide jobs at the top of the hour).
- Database: time-series/inventory tables grow fast → table growth chart and retention findings are first-class.
- Remember § 1: device telemetry is product data with its own pipeline; the performance monitor only measures how fast the RMM itself handles it.

## 15. Anti-goals (don't propose these)

- External APM/telemetry vendors, agents or SaaS dashboards.
- Per-request DB writes, unbounded label sets, storing request parameters or bodies.
- `EXPLAIN ANALYZE` of arbitrary or client-supplied SQL, maintenance commands or config changes from the app.
- Timing headers or RUM for anonymous/customer users.
- Parsing reverse-proxy logs from the app.
- ML/anomaly detection instead of explainable rules.
- A Diagnose mode without automatic expiry.

## 16. Definition of done

- Basic on by default with measured overhead < 1 %; Diagnose auto-expires on server time.
- All seven (or adapted) categories have data or an explained "no data".
- Findings carry evidence, cause, action, code refs and impact; ranked by impact.
- Export MD/JSON/ZIP works, is redacted, audited, size-capped, starts with project-specific Claude instructions and ends with "Missing data / limitations".
- A 1-hour Diagnose run → export → Claude can locate every referenced route/query/table/worker in the repo without guessing.
- Tests from § 13 pass; docs updated.
