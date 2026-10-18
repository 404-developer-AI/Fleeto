# Fleeto — Performance monitor (0.7.0)

> How the generic spec `performance-monitoring.md` maps onto Fleeto, and the build plan of 0.7.0 (`ROADMAP.md`).
> The spec states the rules; this file states what is different in Fleeto, where the code goes and in which order it is
> built. Once a step is built, what it delivers moves into `ARCHITECTURE.md` and this file keeps only what is still open.
> Written 2026-10-14.

## 1. What it is, and what it is not

An admin page, Settings, Performance, that answers where Fleeto itself is slow and why: in the VPS, the database, our own
code, the agent channel, the workers or the browser. Its main deliverable is the export: after an hour in Diagnose an admin
exports a redacted Markdown report and hands it to Claude Code in this repository, which proposes and builds fixes; the page
then shows whether they helped.

- It measures **Fleeto**, never the endpoints. Check results, storage analysis and inventory are product data with their own
  pipeline; the monitor only measures how fast Fleeto handles them. The two never share tables, settings or UI.
- It is **not Instance health**. Instance health (0.6.0) answers "is this instance healthy?" every 5 minutes and notifies;
  the performance monitor answers "where is time lost?" per minute and explains. They share the VPS figures (§4.6) and the
  statement statistics (§4.3), measured once by the workers.
- No external APM, no OpenTelemetry exporter, nothing leaves the instance (spec §15).

## 2. What is different in Fleeto

| Spec assumes | Fleeto | Consequence |
|---|---|---|
| One process | Four: web, gateway, workers, signer | Each collects its own window and flushes its own minute rows, tagged with the component. The dashboard reads all four. |
| A REST frontend: every user action is an HTTP request | Blazor Server: user actions are messages on the circuit | "Routes" are the public API (`/api/v1`), sign-in and the OIDC callback. User actions are measured as **interactions** per page route template through a circuit handler (§4.1). `Server-Timing` has nothing to attach to and is not built. |
| Dapper | EF Core in web and workers, raw Npgsql in gateway and signer | Everything goes through the one `NpgsqlDataSource` per process (`Fleeto.Infrastructure/DependencyInjection.cs`), so one hook measures every query in every component. |
| Settings in a settings table | `SettingsStore` exists only where the root key is (web, workers) | Performance settings are not secret: a table of their own, readable by all four components, changed through web, announced on `fleeto_performance`. |
| Seven categories | Plus **Agent channel** | The gateway: connected agents, ingest, acknowledge, job dispatch, relay (§4.4). |
| Ordinary tables | TimescaleDB, retention by policy (`CLAUDE.md`) | The minute table is a hypertable with a retention policy; the hour table is small and cleaned by the maintenance loop. |
| An app DB role | One role per container, grants in `DatabaseGrants` | Every component gets INSERT on its own minute rows only; reading is web; snapshots are workers, which already have `pg_read_all_stats`. |
| Admin-only | Roles with permissions (0.6.0) | A permission `performance.view` and `performance.manage` in `Permissions`, administration of the instance (only for users whose groups give every client), admin by default. |

## 3. Categories

| Key | Label | Question | Fed by |
|---|---|---|---|
| `hosting` | Hosting / server | Is the VPS the bottleneck (CPU, steal, memory, disk)? | workers (`/proc`, as Instance health), runtime of all four |
| `network` | Network / internet | Is time lost between the browser and the server? | step 3: RUM, circuit latency |
| `code` | Backend code | Is our own code, an external API or the pipeline slow? | web interactions and API routes, external HTTP |
| `database` | Database queries | Which queries cost most, and why? | Npgsql hook, `pg_stat_statements` deltas |
| `maintenance` | Database maintenance | Is the database cleaning up and caching well? | database and table snapshots, Timescale chunks |
| `frontend` | Frontend / browser | Is a page slow to load or sluggish to use? | step 3: RUM |
| `background` | Background & realtime | Do workers or live updates slow users down? | worker runs, notification bus, live updates to circuits |
| `agents` | Agent channel | Does the gateway keep up with the agents? | gateway |

## 4. Collectors

All collectors write into the window of their process (§5); none writes to the database itself. A key is a template, a
type name or a fixed name, never a value: no endpoint id, client code, hostname, user, IP, path or job payload.

### 4.1 Web

- **Interactions**: a `CircuitHandler` with `CreateInboundActivityHandler` wraps every inbound circuit message (an event
  handler, a JS interop callback, a navigation). Key: the route template of the page (`/clients/{ClientId}`), resolved from
  the router, never the URL. Measures duration, database count and time (through the request context, §5), errors. Slow
  interactions above the threshold are kept individually, with their split and in Diagnose their heaviest fingerprints.
- **Circuits**: open circuits, connects, disconnects, reconnects, render batches and their size (the built-in Blazor meters of
  .NET 10 where they give this; checked when building, our own counters otherwise).
- **HTTP**: middleware after routing for `/api/v1`, `/account/*`, the OIDC callback and downloads: route template, method,
  status class, histogram, response bytes, split pipeline / database / own code. Not measured: static files, `/_blazor`
  (the circuit is measured above), health endpoints, the monitor itself (attributed to the source `perf-dashboard`).
- **Live updates**: `LiveUpdates` broadcasts per kind and how many circuits each reaches.

### 4.2 Database, client side (all four)

- Npgsql 10 publishes command durations and pool figures as metrics; a `MeterListener` per process adds them per source
  (`interaction:<route>`, `api:<route>`, `worker:<loop>`, `gateway:<message type>`, `signer:<request kind>`, `other`) from the
  request context. Pool: busy, idle, waiting, timeouts.
- **Diagnose only**: tracing on the same data source builder (`ConfigureTracing`) gives the statement text at the end of a
  command → fingerprint (literals to `?`, IN lists collapsed, hash), N+1 per interaction or run, and the calling method
  captured at command start under a stack budget (50 per second). Outside Diagnose the tracing filter answers false before
  anything is allocated.

### 4.3 Database, server side (workers)

- `pg_stat_statements` every 15 minutes (5 in Diagnose): top 50 by total time ∪ top 20 by mean, own database only, reset-
  aware deltas. Instance health reads the same view every 5 minutes; one reader feeds both.
- Database snapshot: cache hit ratio, temp files, deadlocks, checkpoints, connections, wraparound age.
- Table snapshot hourly (15 minutes in Diagnose): size, sequential and index scans, dead tuples, last (auto)vacuum and
  analyze, and per hypertable its chunks and compression. A delta without a baseline before the period shows "–".
- Live on demand (web, read-only): sessions, lock waits, settings, unused and duplicate indexes, I/O.
- `install.sh` grants `pg_monitor` to the role of workers (it has `pg_read_all_stats`, which is part of it); web gets no
  monitoring role and reads the snapshots.

### 4.4 Gateway: agent channel

Extends `GatewayMetrics` (now cumulative counters for the summary log line) with histograms. Keys are message types and job
types from `Fleeto.Protocol`, never an endpoint.

- Connected agents and watchdogs (peak per minute), connects, refusals by reason, reconnect storms.
- Per inbound message type: count, payload bytes, time to durable write, time to acknowledge.
- Job dispatch: signed → delivered → first output → done, per job type.
- Relay: open remote sessions, bytes per second, frames dropped, time to first frame.
- `LastSeenFlusher`: batch size and duration; the allow-list refresh after a revocation.

### 4.5 Workers

- **Run scope in `WorkerLoop`**: every loop already goes through `RunOnceAsync`, so one scope there measures every loop of
  today and every loop added later: duration, items (from the returned `more` and a counter the loop may set), errors, database
  time as `worker:<Name>`. Idle runs (under 25 ms, no queries) are dropped. Overlap with interactions is a finding (§6).
- External HTTP through the `HttpClient` metrics: per host (Action1, Microsoft, webhooks, Slack, Teams, S3), status class,
  429s; the circuit breaker opening counts too.
- The notification bus: notifications sent and received per channel, the lag between them.

### 4.6 Runtime and host

- Every component every 10 seconds from the `System.Runtime` meter: CPU, working set, GC pause share, heap, allocations, thread
  pool threads and queue, lock contention, exceptions by type name.
- Host from workers: Instance health reads `/proc` every 5 minutes; for performance the same reader runs every minute and
  adds steal, iowait, load against cores, disk latency and cgroup throttling. Shared by every instance on the VPS: the page
  says so.
- Each component reports its own monitor overhead (time spent in collectors, flush duration).

### 4.7 Signer

Only what tells whether signing is a bottleneck: requests per kind, wait in the queue, time to sign, refusals per reason, the
rate limiter refusing. No payloads, no users, no endpoints.

## 5. Collection

- `Fleeto.Core/Performance`: the fixed log-linear histogram (additive, percentiles from buckets), `MetricKey` with the cap
  and the `__other__` bucket, the fingerprinter, the redactor, the findings rules. Pure, unit-tested without a database.
- `Fleeto.Infrastructure/Performance`: `PerfWindow` (lock-free per key), `PerfContext` (async-local: source, database
  count and time, external time, N+1 counters), `PerfFlusher` (swaps the window on the minute boundary, one binary COPY,
  flushes on shutdown), the `MeterListener`s, `PerfOptions` (one immutable snapshot, refreshed on `fleeto_performance` and
  every 30 seconds; Diagnose evaluated against server time on every read).
- Registered in `AddFleetoInfrastructure`, so all four components get it; each adds its own collectors.

## 6. Data model

| Table | Content | Written by | Read by |
|---|---|---|---|
| `PerfMinute` (hypertable) | Component, kind, key, sub key, minute, count, errors, sum, min, max, histogram `int[]`, bytes | all four, own rows | web, workers |
| `PerfHour` | The same per hour, rolled up | workers | web |
| `PerfRuntimeMinute` | Component, minute, version, level, CPU, memory, GC, thread pool, overhead | all four | web, workers |
| `PerfHostMinute` | Minute, CPU incl. steal and iowait, load, memory, swap, disk | workers | web |
| `PerfEvent` | Slow interactions, slow requests, worker runs above the threshold, with split | web, gateway, workers | web, workers |
| `PerfFingerprint` | Hash, normalized SQL, calling method, first and last seen | all four | web |
| `PerfStatementSnapshot`, `PerfDatabaseSnapshot`, `PerfTableSnapshot` | §4.3 | workers | web |
| `PerfMarker` | Deploy (version change) and manual markers | workers, web | web |
| `PerformanceSettings` | One row, the settings of §8 as JSON | web | all four |

Kind is an enum: `Interaction`, `Http`, `Db`, `WorkerRun`, `External`, `AgentMessage`, `JobDispatch`, `Relay`, `Signing`,
`Broadcast`, `Notification`. One minute table instead of one per domain keeps the rollup, the retention and the reading in
one place. Every table gets its grants in `DatabaseGrants` (the test enforces it). The nightly backup dumps these tables
without data (`--exclude-table-data`), so a restore does not bring old measurements. Retention: minutes 7 days, hours 90
days, events 14 days, snapshots 30 days, settings in §8.

## 7. Findings

`PerformanceRules.Evaluate(PerformanceDataset) → findings`, pure, in `Fleeto.Core/Performance`, like `InstanceHealthRules`.
Each finding has id, category, severity, title with numbers, explanation, evidence, cause, action, code references and the
impact estimate (extra waiting time per occurrence × occurrences); ranking by impact. The rule catalogue of the spec (§8),
plus for Fleeto:

| Category | Fleeto rules |
|---|---|
| `code` | slow interaction per page; many queries per interaction; a page whose interactions are mostly database time |
| `background` | a worker loop that overlaps with slower interactions (baseline needs enough minutes without it and an absolute p95 gap); a loop whose run exceeds its interval; a live update kind reaching every circuit too often; notification lag |
| `agents` | ingest p95 above threshold; acknowledge slower than the agent's buffer flush; job dispatch latency; reconnect storm; payload size per message type |
| `maintenance` | hypertables without compression or retention where `CLAUDE.md` expects it; dead tuples on the hot tables (`Endpoints`, `Alerts`, check state) |
| `hosting` | a VPS shared by instances: the page states the share of this instance's processes in the host CPU |

Score per category: 100 minus a penalty per finding; `nodata` without data, never a fake 100. Thresholds are settings.

## 8. Settings, access and audit

- Settings, Performance, tab Settings: level (Off, Basic, Diagnose until), collectors on and off, Diagnose default and
  maximum minutes, slow interaction and slow query thresholds, N+1 threshold, snapshot intervals, retention, budgets, the
  finding thresholds. Defaults in code, stored in `PerformanceSettings`.
- Permissions: `performance.view` (page and export) and `performance.manage` (level, Diagnose, settings, plan, analyze,
  markers, clearing data). Admin by default; neither for technician or read-only.
- Audit: Diagnose start and stop, export, plan, analyze, marker, clearing data, every settings change.
- Not in the public API: an entry on `API-WAITLIST.md` in the commit that builds it (the export could become an API call).

## 9. Export for Claude Code

Markdown (under 100 KB), JSON with `schemaVersion`, ZIP with both and the generic plans of the top queries. The order of the
spec (§10). The instructions block for this repository says:

- The report comes from Settings, Performance of a Fleeto instance, version X, with this share of the period in Diagnose.
- Interactions are Blazor pages: find the page by its route template under `src/Fleeto.Web/Components/Pages`, the work it
  does in `src/Fleeto.Web/Services`. API routes are mapped in `src/Fleeto.Web/Api/PublicApi.cs`. Worker loops are classes
  deriving from `WorkerLoop` under `src/Fleeto.Workers`, named by `Name`. Agent message types are in `src/Fleeto.Protocol`, handled
  under `src/Fleeto.Gateway/Sessions`. Queries are EF Core LINQ or named SQL fields; the caller column names the method.
- One change per bottleneck, with how to verify it on this page.
- Schema changes are EF Core migrations under `src/Fleeto.Infrastructure/Migrations`, forward-only, expand/contract, and
  every new table gets its grants in `DatabaseGrants`. Indexes on hypertables follow TimescaleDB.
- When the bottleneck is the VPS (steal, memory, disk), say so instead of changing code.
- The report holds no personal data: no ids, client codes, hostnames, users, paths or SQL parameter values.

The redactor of the spec (§11) runs over everything that leaves: SQL, exception type names, route templates. In Fleeto it
also masks client codes and hostnames should one slip into a hand-written SQL constant.

## 10. Build plan

### Step 1 — the core (alpha 1) — built 2026-10-18

Built as planned below and described in `ARCHITECTURE.md` §4, *Performance monitor*. Still open: item 7, the overhead at 1,000
agents with `Fleeto.LoadTest`, on the test VPS. Where the build differs from the plan, decided while building:

- A page load is measured by `FleetoPageBase.LoadAsync` (sub key `load`), an event by the circuit handler (sub key `event`): a
  navigation is an inbound activity that does not wait for `OnInitializedAsync`, so the circuit handler alone would miss the
  page's own data loading.
- `PerfDialogService` replaces MudBlazor's dialog service: a click handler that awaits a confirmation dialog would otherwise
  count the time the user reads it as Fleeto being slow (100 call sites await a dialog).
- The recorder is reached through `Perf.Recorder` where a collector is not created by dependency injection (the base class of
  loops, the gateway's handlers, the signer's processor, the notification bus), so no constructor of the 35 loops changed.
- Not built in step 1: items handled per worker run (no loop reports them yet), job dispatch beyond delivery (first output,
  done), the time to first frame of the relay, budgets and the comparison by version (step 3). `PerfFingerprint` comes with
  Diagnose in step 2, so the migration of step 1 does not create it.
- Retention of the minutes is a setting (1 to 30 days) the workers enforce with batched deletes; the TimescaleDB retention
  policy on `PerfMinutes` drops chunks after 31 days as a ceiling, because a policy cannot follow a setting without the owner
  role.
- Every component writes only its own rows: triggers on `PerfMinutes`, `PerfRuntimeMinutes` and `PerfEvents` compare the
  component with the role, because table grants alone cannot.
- The redactor also takes the client codes and endpoint host names of the instance (words of at least three characters) and
  masks them wherever they slip into free text; SQL loses every literal before that.

1. `Fleeto.Core/Performance`: histogram, keys, redactor, fingerprinter, the categories, the findings model. Tests: additivity,
   percentiles, cap and `__other__`, redactor cases.
2. Migration `PerformanceMonitor`: the tables of §6, the hypertable and its retention policy, the grants.
3. `Fleeto.Infrastructure/Performance`: window, context, flusher, options, the Npgsql meter listener, runtime sampling.
4. Web: circuit handler for interactions, HTTP middleware, live updates. Gateway: agent channel. Workers: run scope in
   `WorkerLoop`, external HTTP, host every minute, statement and database snapshots, maintenance loop (rollup, deploy marker).
   Signer: signing figures.
5. `PerformanceRules` with the baseline catalogue and the Fleeto rules above, unit tests per rule.
6. Settings, Performance: header (level, period, overhead), Overview (category cards, top bottlenecks, timeline), the
   Settings tab; Markdown export with copy to clipboard.
7. Measure the overhead with `Fleeto.LoadTest` at 1,000 agents with the monitor off and on Basic; below 1 % or it is not done.

### Step 2 — Diagnose (alpha 2)

Diagnose with its countdown; fingerprints, N+1 and callers; tabs API and interactions, Database (with plan and analyze),
Server, Background, Agent channel; manual markers; JSON and ZIP. Test: one hour of Diagnose under `Fleeto.LoadTest`, export,
and Claude finds every referenced page, loop, query and table in the repository without guessing (spec §16).

### Step 3 — the rest (alpha 3)

The Frontend tab with sampled Web Vitals of staff users (ingest with whitelist and caps, server time), the edge check of the
instance URL, the benchmark, budgets, comparison by version and with the previous period, critical findings as an instance
health problem with a cooldown.

### Rules for `CLAUDE.md` once step 1 is built

- Every worker loop derives from `WorkerLoop` (it already does), so it is measured; a loop that does not is a review finding.
- Metric keys never contain values: templates, type names and fixed names only.

## 11. Open questions

- Should a technician ever see the page (for example only the Agent channel tab)? Planned: no, admin only.
- Does the minute data need to survive a restore? Planned: no, structure only in backups.
- Should the Steaan management server later collect the exports of every instance? Not before that server exists.
