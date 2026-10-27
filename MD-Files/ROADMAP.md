# Fleeto — Roadmap

> Planned versions and what each one delivers. Version rules are in `CLAUDE.md`
> (Versioning and changelog). Scope per version is a target, not a promise: a version
> ships when its items are done and tested, and items may move between versions. Dates
> are added only once a version is in progress.

## Current

Released versions, from 0.0.0 to 0.7.0, are described in `CHANGELOG.md` and `CHANGELOG-ARCHIVE.md`; this file keeps only
what is not released yet. The latest release is **0.7.0** (2026-10-26, `v0.7.0`): the performance monitor and the security
monitor, tested through `v0.7.0-alpha.1` to `alpha.13`.

**0.8.0** — next: vulnerability reporting, then patch management on Linux.

**Platforms**: Windows and Linux. macOS is not supported for now; it may come later when there is demand (decided
2026-09-15, see Later).

**Deployment**: Steaan runs every instance (SaaS, decided 2026-09-15); releases are GitHub Releases of the public
repository, with the images as private packages on ghcr.io (public since 2026-09-21).

Markers: [done] built and tested, [open] still to do.

## 0.8.0 — Vulnerability reporting and patch management on Linux

Replaces hardening as 0.8.0 (decided 2026-10-26; hardening was taken off the roadmap). Two parts, in this order (decided
2026-10-26): vulnerability reporting, designed in `VULNERABILITIES.md`, then patch management on Linux, of which how and what is
discussed with the developer before anything is built.

- [open] Vulnerability reporting (`VULNERABILITIES.md`): security advisories of the CCB on the dashboard, and the CVEs that
  affect the distribution packages of managed Ubuntu, Debian, AlmaLinux and Proxmox VE endpoints, matched inside the instance.
  Step 0, the security advisories on the dashboard, built 2026-10-26 (RSS feed of the CCB only: title, date and link). Step 1,
  matching on Linux endpoints, and step 2, the Vulnerabilities tab, the overview, the dashboard tile and Settings, built 2026-10-26.
  Step 3, actively exploited CVEs (CISA's KEV catalog, ENISA's EUVD as fallback) and EPSS scores, ordering and filtering on them, and
  the vulnerabilities in the History tab, built 2026-10-27. The Windows findings of Action1 moved to "Not yet scheduled".
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

- Windows endpoints in the vulnerability overview (left out of 0.8.0 by the developer on 2026-10-27): Action1 shows vulnerabilities
  with their CVEs per organization in its console. Open question: whether its API offers them, and with which permission of the
  credentials. If it does, the workers read them within the Action1 budget and the overview counts Windows and Linux endpoints per CVE
  together (`VULNERABILITIES.md` §9); until then the overview is Linux only and says so.

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
