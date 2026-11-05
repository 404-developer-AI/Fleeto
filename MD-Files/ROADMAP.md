# Fleeto — Roadmap

> Planned versions and what each one delivers. Version rules are in `CLAUDE.md`
> (Versioning and changelog). Scope per version is a target, not a promise: a version
> ships when its items are done and tested, and items may move between versions. Dates
> are added only once a version is in progress.

## Current

Released versions, from 0.0.0 to 0.9.0, are described in `CHANGELOG.md` and `CHANGELOG-ARCHIVE.md`; this file keeps only
what is not released yet. The latest release is **0.9.0** (2026-11-05, `v0.9.0`): patch management by the Fleeto agent itself on
Windows, Windows Server, Ubuntu, Debian, AlmaLinux and Proxmox VE, next to Action1 (designed in `PATCHING.md`), released without a
round on the test VPS (decided by the developer on 2026-11-05).

No next version is decided yet. Open after 0.9.0:

- [open] 0.9.0 on the test VPS: an installation and a restart by hand and by a round on every test endpoint (Ubuntu, Proxmox VE,
  AlmaLinux, Windows 11, Windows Server), the scan as SYSTEM with missing updates, under a WSUS and with Microsoft Update registered
  by a policy, the systemd scope of apt and dnf when the agent stops, and the other checks of `PATCHING.md` section 15. What they
  find is a patch release.
- [open] The load test of patch scans and rounds at 10,000 agents (`Fleeto.LoadTest --patch-scans-per-hour --patch-updates`).
- [open] When a Debian endpoint runs: captures of Debian 12 and 13 (`apt-get -q update`, `apt-get -s dist-upgrade`, `apt-mark
  showhold`) for the parser tests; Debian is supported on the representative output of `agent/internal/patch/testdata` until then.
  Also a Proxmox VE host with the enterprise repository answering 401.

**Platforms**: Windows and Linux. macOS is not supported for now; it may come later when there is demand (decided
2026-09-15, see Later).

**Deployment**: Steaan runs every instance (SaaS, decided 2026-09-15); releases are GitHub Releases of the public
repository, with the images as private packages on ghcr.io (public since 2026-09-21).

Markers: [done] built and tested, [open] still to do.

## 1.0.0 — General availability

- Everything above complete, tested and documented, including the subjects under "Not yet scheduled": they are part of the
  v1 scope in `CLAUDE.md` and each one needs a version of its own before 1.0.0 (decided 2026-09-23).
- Product name, trademark and domain checks done.
- Pricing per managed endpoint decided.
- API field names frozen.

## Not yet scheduled

Wanted, but not in a version yet: the version is chosen once the open questions are answered. These subjects stay part of
  the v1 scope in `CLAUDE.md` (decided 2026-09-23), so each one gets a version before 1.0.0.

- A code signing certificate for Fleeto Connect on Windows. Until Steaan has one, SmartScreen warns on its first run; its
  integrity comes from the signed release manifest (decided 2026-10-27; moved out of
  0.8.0 on 2026-11-04, because it waits on Steaan and blocks no release).

- The integrations other than Action1 (planned as 0.5.0, moved here on 2026-09-23): Sophos Central (endpoint health,
  detections), Veeam (backup job status), Proxmox VE and VMware vCenter (host and VM inventory and health), and
  integration health on the dashboard. The connector architecture and the rules they follow are in `CLAUDE.md`
  (Integrations) and `ARCHITECTURE.md` §11; Action1 is the worked example.

- Third-party applications on Windows (Chrome, Adobe, 7-Zip) by the Fleeto agent itself, left out of 0.9.0 on 2026-11-04
  (`PATCHING.md` §1): Action1 covers them today. Open question: whether winget, which runs per user, can be used reliably as SYSTEM.
  Also later: feature updates of Windows, and a limit per site for a round ("at most N endpoints of a site at once", for clusters).

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
  - install without SSH, as a root script in the DSM Task Scheduler, with the install command creating `/opt` itself: built for
    0.8.0 (the install dialog explains the Task Scheduler);
  - DSM recognised in the inventory from `/etc.defaults/VERSION` (name, version, update), shown as a server: built for 0.8.0
    (the operating system "Synology DSM 7.1.1-42962 Update 8"), which the Synology web interface recognises like `Proxmox VE `;
  - units in `/usr/local/lib/systemd/system`, where Package Center puts the units of packages, and program and state
    files where a DSM update leaves them; open question, to be tested first on a NAS with an agent through a DSM update:
    which of `/etc/systemd/system`, `/usr/local`, `/opt`, `/var/lib` and `/volume1` survive it;
  - checks for what a NAS is watched for, read locally by the agent: built for 0.8.0 and tested on the NAS of the developer, as RAID state (`/proc/mdstat`, without
    `md0` and `md1` on DSM), Disk health and Disk temperature (smartctl 6.5 on DSM, text output, never waking a disk in standby);
    volumes are the existing Free disk space check;
  - only x86_64 and arm64 models; 32-bit ARM models get no agent;
  - the **Synology web interface** in the technician's own browser, exactly like the Proxmox web interface (`PROXMOX-WEB.md` §8, asked
    2026-11-01, built for 0.8.0, works on the NAS of the developer with `0.8.0-alpha.9`): opened from the right-click menu on a managed endpoint whose agent reports DSM, carried by Fleeto Connect as tunnel
    channels through a remote session, the agent connecting to one fixed target on the NAS itself and the TLS of DSM running end to
    end, so Fleeto never sees the pages or a DSM password and the technician signs in with their own DSM account (with its own 2-step
    verification). Decided with the developer on 2026-11-01: the one fixed target is `127.0.0.1:5001` (then `[::1]:5001`), the default
    HTTPS port of DSM, never port 5000 and never a port read from the NAS or named by the server; when nothing answers there, the
    window states that the Synology web interface only works on the default HTTPS port of DSM, 5001. Its own permission "Synology web interface"
    in the Remote group. Tested 2026-11-01: DSM accepts the `Host`, `Origin` and `Referer` of `127.0.0.1:5001` as `pveproxy` does and
    keeps its cookie; still to test is what its auto block does after failed sign-ins, which all come from `127.0.0.1`.
- Whitelabel beyond the FQDN: customer logo and product name in UI and email.
- Steaan management server: central issue, renewal and revocation of licenses, fetched by
  instances over an API; later also an overview of all instances and their versions.
- SNMP for network devices.
- Ticksy hook for alert-to-ticket.
- Mobile device management, network topology mapping: noted, not planned.
