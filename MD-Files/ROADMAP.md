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
endpoints it manages. Placed before hardening (which moved from 0.7.0 to 0.8.0 the same day, and was taken off the roadmap on 2026-10-26) so the load test at 10,000
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
- [done] **Copy output of the remote terminal** (asked for by the developer and built 2026-10-19, tested on the test VPS 2026-10-20).
- [done] **Storage report for the customer** (asked for by the developer and built 2026-10-19, tested on the test VPS 2026-10-20):
  "Copy summary" as an image with the top 10 folders and files of a drive, and "Export PDF" with every scanned folder and file,
  in the branding of Fleeto; the PDF written by Fleeto itself.
- [open] **Copy the process list of a CPU or memory result** (asked for by the developer and built 2026-10-20, to be tested on
  the test VPS): "Copy" in the check history copies the process list with the chart of the period shown as an image, like the storage summary
  (the chart added after testing `0.7.0-alpha.7`).
- [open] **Turn off automatic Windows Update from a patch policy** (asked for by the developer and built 2026-10-19, to be
  tested on the test VPS): Action1 offers it only in its console, so the Fleeto agent sets `NoAutoUpdate` itself on the managed
  Windows endpoints Action1 reports, where no group policy decides, and undoes it when it no longer applies. The Patches tab shows
  where it stands.
- [open] **Security monitor** (asked for by the developer on 2026-10-22: detect attacks and suspicious activity, tell the admins
  at once and block malicious addresses, instead of finding out afterwards). Inside Fleeto, per instance; no host firewall and
  no blocking by country. Built in three steps, each an alpha on the test VPS:
  - [open] **Step 1, signals and blocking** (built 2026-10-22): web and the gateway count refused rate limits, failed sign-ins,
    invalid API keys, revoked and unknown agent certificates, failed TLS handshakes, scanner probes and exploit attempts per
    address and minute; scanner probes get a bare 404; Settings, Security blocks and allows addresses by hand, enforced by web
    and the gateway within seconds; `user.locked_out`, the User-Agent of a sign-in and the networks users sign in from.
  - [open] **Step 2, detection** (built 2026-10-23): a workers loop with pure rules over the signals and the audit log (brute force, password
    spraying, an attack on one account, a locked admin, a sign-in from a new network, API key misuse, attacks on the agent port,
    scanners, privilege changes, mass jobs and remote sessions, posture), security incidents with acknowledge and a comment,
    automatic blocks of 1 hour, 24 hours and 7 days that never hit an allowed address, a reserved range or a network with a
    recent sign-in or a valid agent, a dashboard tile and notifications to the channels that choose security incidents (a scan
    Fleeto blocked by itself is not sent), and `install.sh --unblock` for an admin who blocked themselves out.
  - [open] **Step 3, blocklists** (built 2026-10-23): Spamhaus DROP, abuse.ch Feodo Tracker and FireHOL level1 (blocklist.de
    optional), refreshed every 6 hours by the workers and cleaned of reserved ranges, and CrowdSec through a firewall
    integration of its Console. Sharing with CrowdSec was dropped after checking its documentation on 2026-10-23: its
    community blocklist is only for its own Security Engine with unmodified Hub scenarios. A blocklist never refuses an agent
    with a valid certificate.

## 0.8.0 — Vulnerability reporting and patch management on Linux

Replaces hardening as 0.8.0 (decided 2026-10-26; hardening was taken off the roadmap). Two parts, in this order (decided
2026-10-26): vulnerability reporting, designed in `VULNERABILITIES.md`, then patch management on Linux, of which how and what is
discussed with the developer before anything is built.

- [open] Vulnerability reporting (`VULNERABILITIES.md`): security advisories of the CCB on the dashboard, and the CVEs that
  affect the distribution packages of managed Ubuntu, Debian, AlmaLinux and Proxmox VE endpoints, matched inside the instance.
- [open] Patch management on Linux (planned as 0.4.1, moved to "Not yet scheduled" on 2026-09-23, scheduled as 0.8.0 on
  2026-10-26). Until it is built a Linux endpoint shows no patch data at all and stays out of the compliance counts, because
  calling it uncovered would be untrue.

## 1.0.0 — General availability

- Everything above complete, tested and documented, including the subjects under "Not yet scheduled": they are part of the
  v1 scope in `CLAUDE.md` and each one needs a version of its own before 1.0.0 (decided 2026-09-23).
- Product name, trademark and domain checks done.
- Pricing per managed endpoint decided.
- API field names frozen.

## Not yet scheduled

Wanted, but not in a version yet: the version is chosen once the open questions are answered. These subjects stay part of
  the v1 scope in `CLAUDE.md` (decided 2026-09-23), so each one gets a version before 1.0.0.

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
