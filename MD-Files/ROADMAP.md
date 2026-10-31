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

## 0.8.0 — Vulnerability reporting, the Proxmox web interface and patch management on Linux

Replaces hardening as 0.8.0 (decided 2026-10-26; hardening was taken off the roadmap). Four parts, in this order (decided
2026-10-26, the Proxmox web interface and the dashboard designer added 2026-10-27): vulnerability reporting, designed in
`VULNERABILITIES.md`, the Proxmox web interface, designed in `PROXMOX-WEB.md`, the dashboard designer, designed in `DASHBOARDS.md`,
then patch management on Linux, of which how and what is discussed with the developer
before anything is built.

- [open] Vulnerability reporting (`VULNERABILITIES.md`): security advisories of the CCB on the dashboard, and the CVEs that
  affect the distribution packages of managed Ubuntu, Debian, AlmaLinux and Proxmox VE endpoints, matched inside the instance.
  Step 0, the security advisories on the dashboard, built 2026-10-26 (RSS feed of the CCB only: title, date and link). Step 1,
  matching on Linux endpoints, and step 2, the Vulnerabilities tab, the overview, the dashboard tile and Settings, built 2026-10-26.
  Step 3, actively exploited CVEs (CISA's KEV catalog, ENISA's EUVD as fallback) and EPSS scores, ordering and filtering on them, and
  the vulnerabilities in the History tab, built 2026-10-27. The Windows findings of Action1 moved to "Not yet scheduled".
- [open] Fix: vulnerability feeds are never fetched (found on the test VPS with `v0.8.0-alpha.4`, 2026-10-27). The workers role
  may not create the temporary tables the Ubuntu and KEV imports use, and the `PostgresException` stops every feed without a
  reason on the page. Cause, workaround and fix (grant `TEMPORARY` to `fleeto_workers` in `10-fleeto-roles.sh` and
  `DatabaseGrants`, contain a failure to its own feed, tests, changelog) in `FIX-VULNERABILITY-FEEDS-TEMP-TABLES.md`. Built
  2026-10-27, to be tested on the VPS with the next alpha.
- [open] Local development on Linux (the developer moved from Windows to Ubuntu 26.04 on 2026-10-27). `setup-dev.ps1`,
  `start-dev.ps1`, `build-agent.ps1` and `README.md` work on Linux (secrets in `~/.local/share/Fleeto/dev`, the four
  components in one terminal); built, set up and run on 2026-10-27, in `v0.8.0-alpha.5`. Still to decide: `global.json` on
  `10.0.400`, because the `dotnet-sdk-10.0` package of Ubuntu (SDK 10.0.1xx) fails on the Razor components with 112 errors
  where the SDK of Microsoft (10.0.403, as in CI) builds them.
- [open] Proxmox web interface (`PROXMOX-WEB.md`, decided 2026-10-27): the real web interface of a managed Proxmox VE host, opened
  from the endpoint list through a tunnel in a remote session to `127.0.0.1:8006` on the host, with Fleeto Connect on the technician's
  PC (Windows and Ubuntu desktop), proposed for download when it is missing. Built 2026-10-27 for `v0.8.0-alpha.6`; tested the same
  day on Ubuntu with Chrome (install proposal, `.deb`, sign-in, an upgrade of the host with its shell). Still to test: Windows, Edge and
  Firefox, a VM console and an ISO upload (`PROXMOX-WEB.md` §7).
- [open] A code signing certificate for Fleeto Connect on Windows. Until Steaan has one, SmartScreen warns on its first run; its
  integrity comes from the signed release manifest (decided 2026-10-27).
- [open] Dashboard designer (`DASHBOARDS.md`, decided 2026-10-27, built before the rest of 0.8.0): dashboards an admin designs
  in Settings, with tiles of a catalog in code, their size, place and settings, linked to roles; a user sees the first dashboard in
  priority order linked to one of its roles, otherwise the built-in default dashboard. Built 2026-10-27 for `v0.8.0-alpha.7`; still to
  test in a browser on the test VPS (`DASHBOARDS.md` §9 step 7).
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
- Synology DSM as an endpoint platform (asked 2026-10-31): the Fleeto agent on a Synology NAS, chosen over an integration
  through the DSM Web API or SNMP because those need a port on the NAS reachable from the VPS and a DSM admin credential in
  Fleeto, while the agent only connects outbound. Findings on a NAS with DSM 7.1.1-42962 Update 8 (x86_64): systemd 219 runs
  the unit Fleeto writes, but `/opt` does not exist (the install command needs `mkdir /opt` first), `/etc/os-release` is
  missing (the OS name comes from `/etc.defaults/VERSION`), the system partition is 2.3 GB, and `dpkg` exists without the
  packages of DSM. The 0.8.0 agent installed and enrolled there on 2026-11-01 after `mkdir /opt`, but the service then
  exited at every start, because the agent recognised a start by systemd only by `INVOCATION_ID`, which systemd sets from 232
  (fixed for the next build, see `CHANGELOG.md`). With a drop-in that sets `INVOCATION_ID` the agent and the watchdog run there,
  in `/system.slice/fleeto-agent.service` and `/system.slice/fleeto-watchdog.service`; the endpoint is online as a Linux server
  with the right host name, CPU, memory and storage. Synology itself offers only a Package Center package (`.spk`), which since DSM 7.0 runs as its own
  unprivileged user unless Synology signs it; the agent needs root, so it stays an install outside Package Center, which
  Synology does not support. What it would take:
  - install without SSH, as a root script in the DSM Task Scheduler, with the install command creating `/opt` itself;
  - DSM recognised in the inventory from `/etc.defaults/VERSION` (name, version, update), shown as a server: built for 0.8.0
    (the operating system "Synology DSM 7.1.1-42962 Update 8"), which the DSM web interface can recognise like `Proxmox VE `;
  - units in `/usr/local/lib/systemd/system`, where Package Center puts the units of packages, and program and state
    files where a DSM update leaves them; open question, to be tested first on a NAS with an agent through a DSM update:
    which of `/etc/systemd/system`, `/usr/local`, `/opt`, `/var/lib` and `/volume1` survive it;
  - checks for what a NAS is watched for, read locally by the agent: RAID state (`/proc/mdstat`), SMART of the disks,
    volumes and temperature;
  - only x86_64 and arm64 models; 32-bit ARM models get no agent;
  - the **DSM web interface** in the technician's own browser, exactly like the Proxmox web interface (`PROXMOX-WEB.md`, asked
    2026-11-01): opened from the right-click menu on a managed endpoint whose agent reports DSM, carried by Fleeto Connect as tunnel
    channels through a remote session, the agent connecting to one fixed target on the NAS itself and the TLS of DSM running end to
    end, so Fleeto never sees the pages or a DSM password and the technician signs in with their own DSM account (with its own 2-step
    verification). Decided with the developer on 2026-11-01: the one fixed target is `127.0.0.1:5001` (then `[::1]:5001`), the default
    HTTPS port of DSM, never port 5000 and never a port read from the NAS or named by the server; when nothing answers there, the
    window states that the DSM web interface only works on its default HTTPS port 5001. Its own permission "Synology web interface"
    in the Remote group. Found out while building it on a test NAS: whether DSM accepts the `Host`, `Origin` and `Referer` of
    `127.0.0.1` as `pveproxy` does, and what its auto block, firewall and IP check of a session do with sign-ins that all come from
    `127.0.0.1` (a block of `127.0.0.1` would lock every technician out, a trusted `127.0.0.1` would bypass the auto block).
- Whitelabel beyond the FQDN: customer logo and product name in UI and email.
- Steaan management server: central issue, renewal and revocation of licenses, fetched by
  instances over an API; later also an overview of all instances and their versions.
- SNMP for network devices.
- Ticksy hook for alert-to-ticket.
- Mobile device management, network topology mapping: noted, not planned.
