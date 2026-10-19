# Fleeto — Roadmap

> Planned versions and what each one delivers. Version rules are in `CLAUDE.md`
> (Versioning and changelog). Scope per version is a target, not a promise: a version
> ships when its items are done and tested, and items may move between versions. Dates
> are added only once a version is in progress.

## Current

Released versions, from 0.0.0 to 0.6.0, are described in `CHANGELOG.md` and `CHANGELOG-ARCHIVE.md`; this file keeps only
what is not released yet. The latest release is **0.6.0** (2026-10-18, `v0.6.0`): refactors, fixes and what the developer
wanted before the next round of testing, tested through `v0.6.0-alpha.1` to `alpha.26`.

**0.7.0** — next: the performance monitor.

**Platforms**: Windows and Linux. macOS is not supported for now; it may come later when there is demand (decided
2026-09-15, see Later).

**Deployment**: Steaan runs every instance (SaaS, decided 2026-09-15); releases are GitHub Releases of the public
repository, with the images as private packages on ghcr.io (public since 2026-09-21).

Markers: [done] built and tested, [open] still to do.

## 0.7.0 — Performance monitor

Asked for by the developer on 2026-10-14, after the performance monitor of the Servicedesk: an admin-only page that shows
where Fleeto itself is slow and why, with an export to hand to Claude Code in this repository. It measures Fleeto, never the
endpoints it manages. Placed before hardening (which moved from 0.7.0 to 0.8.0 the same day) so the load test at 10,000
agents is measured by it. The generic spec is `performance-monitoring.md`; how it maps onto Fleeto, the data model and the
build plan are in `PERFORMANCE.md`. Built in three steps, each an alpha on the test VPS, released as a whole.

- [open] **Step 1, the core** (built 2026-10-18, to be tested on the test VPS as `0.7.0-alpha.1`; the overhead measurement at
  1,000 agents is still to do): collectors in web, gateway, workers and signer (in memory, one batch write per minute,
  bounded keys without values), database time through the shared Npgsql data source, a run scope in `WorkerLoop` so every
  loop is measured, the agent channel of the gateway, the VPS figures of Instance health reused, deltas of
  `pg_stat_statements`, the findings engine with a score per category, Settings, Performance with Overview, and the
  Markdown export with instructions for Claude Code. Level Basic on by default, below 1 % overhead.
- [open] **Step 2, Diagnose** (built 2026-10-18, to be tested on the test VPS as `0.7.0-alpha.2`, with an hour of Diagnose
  under `Fleeto.LoadTest` and its export handed to Claude Code): a mode that ends by itself on server time (60 minutes by
  default) and adds statement fingerprints, N+1 detection, the calling method per query and the heaviest queries per slow
  interaction; the tabs Pages and API, Database, Server, Background and Agent channel; a generic plan and ANALYZE on click;
  manual markers; the JSON and ZIP export.
- [open] **Step 3, the rest**: the Frontend tab (Web Vitals of staff users, sampled), the edge check of the instance URL, the
  benchmark, budgets, comparison by version and with the previous period, critical findings as an instance health problem.
- [open] **Turn off automatic Windows Update from a patch policy** (asked for by the developer and built 2026-10-19, to be
  tested on the test VPS): Action1 offers it only in its console, so the Fleeto agent sets `NoAutoUpdate` itself on the managed
  Windows endpoints Action1 reports, where no group policy decides, and undoes it when it no longer applies. The Patches tab shows
  where it stands.

## 0.8.0 — Hardening

- Load test at 10,000 simulated agents on the full ingest path, measured with the performance monitor of 0.7.0 in
  Diagnose and handed to Claude Code as its export; fix what breaks. Document
  the per-instance footprint and how many instances fit on one VPS size.
- External security review of enrollment, signing, secrets handling, licensing, remote
  control and multi-tenancy.
- Gateway benchmark at 10,000 connections (open since 0.1.0, tested with 200 simulated agents).
- Key ceremony written out and rehearsed (release and license keys, root key, signer key, backup key pair), and the
  production release and license keys on hardware tokens (open since 0.1.0; test keys are in use since 2026-09-15).
- Two instances side by side on one VPS, each on its own FQDN; restore of a backup onto a fresh VPS tested, also for
  several instances on one VPS; unattended reboot recovery verified with several instances on one VPS (open since 0.1.0).
- GDPR deliverables: data inventory, data processing agreement template, breach procedure,
  client and endpoint deletion with full purge, recording retention.

## 1.0.0 — General availability

- Everything above complete, tested and documented, including the subjects under "Not yet scheduled": they are part of the
  v1 scope in `CLAUDE.md` and each one needs a version of its own before 1.0.0 (decided 2026-09-23).
- Product name, trademark and domain checks done.
- Pricing per managed endpoint decided.
- API field names frozen.

## Not yet scheduled

Wanted, but not in a version yet: the version is chosen once the open questions are answered. These subjects stay part of
  the v1 scope in `CLAUDE.md` (decided 2026-09-23), so each one gets a version before 1.0.0.

- Patch management on Linux (planned as 0.4.1, moved here on 2026-09-23): the Fleeto agent reads the id of the Action1
  agent on a Linux endpoint as it already does on Windows, and Linux endpoints get patch state, missing updates,
  deployments and a place in the compliance counts. Until then a Linux endpoint shows no patch data at all and stays out
  of the counts, because calling it uncovered would be untrue.

- The integrations other than Action1 (planned as 0.5.0, moved here on 2026-09-23): Sophos Central (endpoint health,
  detections), Veeam (backup job status), Proxmox VE and VMware vCenter (host and VM inventory and health), and
  integration health on the dashboard. The connector architecture and the rules they follow are in `CLAUDE.md`
  (Integrations) and `ARCHITECTURE.md` §11; Action1 is the worked example.

- Logs, search and retention (planned as 0.6.0, moved here on 2026-09-23): log and event collection by the agent with
  full-text search under a second at 10,000 endpoints, retention policies per data type with continuous aggregates for
  the dashboards, and a global search across clients, sites, endpoints and notes.

- Point-in-time recovery (removed WAL archiving on 2026-09-16 until this exists): physical base backups (`pg_basebackup`)
  plus WAL archiving to the off-VPS destination, both encrypted like the nightly dump, a bounded spool on the VPS, and a
  restore procedure that is rehearsed. Open questions: how often a base backup runs, the storage it needs per instance, and
  the recovery point the customers need.

- Recording of remote control sessions and remote terminal transcripts (moved out of 0.3.0 on 2026-09-16; 0.3.0 keeps the
  audit per session, participant and action). Open questions: video or events only, storage per hour in PostgreSQL,
  retention, who may watch a recording.

- Servicedesk ticket reference on notes (moved out of 0.2.1 on 2026-09-15). **Not yet scheduled**: the developer works out
  with the Servicedesk team how both products should work together before anything is built. Starting point from
  2026-09-15, to be confirmed in that alignment:
  - an integration with the **Steaan Servicedesk**, configured in Settings, Integrations (URL and API credential, stored
    encrypted);
  - Fleeto fetches the ticket number through the Servicedesk API and shows it on the note as a clickable ticket number
    that opens the ticket in the Servicedesk;
  - open questions for the alignment: how a note finds its ticket (and whether the Servicedesk creates or links notes),
    whether the link goes one way or both ways (ticket to endpoint, alerts to tickets), which API the Servicedesk offers
    and how it authenticates, and how this relates to the Ticksy hook for alert-to-ticket under Later.
  - When built: the ticket reference on notes goes into the public API or onto `API-WAITLIST.md` in the same commit.

## Later (not planned for 1.0)

- macOS: not supported for now; it may come when there is demand (decided 2026-09-15). It would need an Apple Developer
  account for signing and notarisation, a launchd agent and watchdog, and the Screen Recording and Accessibility
  permission flow for remote control.
- Write access in the public API for the resources a technician can change in the UI: only when there is demand
  (decided 2026-09-15).
- Authenticode code signing of the Windows agent and watchdog binaries (decided 2026-09-15, 0.2.1); it comes next to
  the release manifest check, not instead of it.
- Wayland support on Linux. (File transfer moved into 0.3.0 on 2026-09-16.)
- Whitelabel beyond the FQDN: customer logo and product name in UI and email.
- Steaan management server: central issue, renewal and revocation of licenses, fetched by
  instances over an API; later also an overview of all instances and their versions.
- SNMP for network devices.
- Ticksy hook for alert-to-ticket.
- Mobile device management, network topology mapping: noted, not planned.
