# Fleeto — Roadmap

> Planned versions and what each one delivers. Version rules are in `CLAUDE.md`
> (Versioning and changelog). Scope per version is a target, not a promise: a version
> ships when its items are done and tested, and items may move between versions. Dates
> are added only once a version is in progress.

## Current

**0.1.0** — implemented; tagged `v0.1.0` on 2026-09-15 as a historical marker on its last commit, without a release
build (decided 2026-09-15). What was listed as open before the tag stays a checklist under "Open items from 0.1.0".

**0.2.0** — every item built (2026-09-15); everything that was still open moved to 0.2.1 (decided 2026-09-15). Pre-releases `v0.2.0-alpha.1` (2026-09-15, first CI
run; its release build failed), `v0.2.0-alpha.2` (first published test build), `v0.2.0-alpha.3` (VPS behind NAT, first VPS install), `v0.2.0-alpha.4` (fixes from the first install) and `v0.2.0-alpha.5` (network MTU). `v0.2.0-alpha.5` runs on the first test VPS.

**0.2.1** — in progress (started 2026-09-15): the read-only public API, agent self-update with update rings, the
Windows watchdog, the Linux agent with its watchdog and the rename to Fleeto everywhere are built (pre-releases `v0.2.1-alpha.1`, whose release build
failed, `v0.2.1-alpha.2`, whose move of the test VPS fell back to the old layout, and `v0.2.1-alpha.3`, 2026-09-15), and so are the
script run on a selection of endpoints, the output cap per policy, running a script as the signed-in user and the icon for the installed web app,
and the images of the failed `v0.2.0-alpha.1` are removed from ghcr.io; what is left is testing on real endpoints before the tag. The Servicedesk ticket reference on notes moved to "Not yet scheduled" (decided 2026-09-15).

**Platforms**: Windows and Linux. macOS is not supported for now; it may come later when there is demand (decided
2026-09-15, see Later).

**Deployment**: Steaan runs every instance (SaaS, decided 2026-09-15); releases are GitHub Releases of the private
repository.

Markers: [done] built and tested, [open] still to do.

## 0.0.x — Foundation

Built together with 0.1.0 in one piece of work, without separate patch releases.

- [done] Private GitHub repository, `.gitignore`, CI (build, tests against PostgreSQL + TimescaleDB,
  vulnerability scans, gitleaks secret scan, branding check, shellcheck, image builds).
- [done] Solution following the Migrify conventions: Core, Protocol, Infrastructure, Web, Gateway,
  Signer, Workers, Tools, shared test fixture.
- [done] PostgreSQL 17 schema and migrations; TimescaleDB hypertable when the extension is installed.
  Local development without Docker (`tools/dev`); the Compose stack is for the VPS.
- [done] Root key, envelope encryption, encrypted settings, append-only audit log, one database role
  per container with least-privilege grants.
- [done] `Fleeto.Signer`: signer key, instance signing key, internal CA, signing requests over
  LISTEN/NOTIFY, signing rules with tests, role check on who may request which signature.
- [done] Release signing tooling: release key, signed release manifest with image digests, signed
  `install.sh` (verifiable with openssl). [done] Test keys for the first VPS (2026-09-15). [open] Production key on a
  hardware token.
- [done] Users, roles, login, mandatory TOTP 2FA, first-admin setup flow with backup step.
- [done] Clients, sites, endpoints: CRUD, clients workspace (clients panel, endpoint list with tabs Servers / Workstations / Mixed, endpoint detail below the list), collapsible navigation, settings workspace (settings panel with templates and administration pages, opened from the sidebar footer).
- [done] Client templates, monitoring templates, policies: CRUD, linking to sites, copy.
- [done] Endpoint tier with server-side enforcement in four layers; license format, signing tool,
  license page, pool counting with serialized allocation, 14-day grace period.
- [done] `install.sh` first version (signature and manifest verification, Docker, DNS check, host
  Caddy with SNI passthrough, update with rollback, `--version`, `--check`, `--list`, `--all`).
  [done] First install and updates on a real VPS (2026-09-15, see 0.2.0, Deployment).
- [done] Load-test simulator (`Fleeto.LoadTest`).

## 0.1.0 — First usable release

- [done] Go agent for Windows: enrollment with per-site token and CA fingerprint pinning, mTLS
  certificate with TPM-backed key where available, renewal, heartbeat, online/offline state,
  inventory (hardware, OS, software), Windows service install. Agent-only tier complete.
- [done] Certificate revocation: allow list checked on every connection, live disconnect, revoke on
  endpoint deletion, duplicate identity detection.
- [done] Gateway in .NET. [open] Benchmark at 10,000 connections (tested with 200 simulated agents).
- [done] Managed tier: CPU, memory, disk, service and uptime checks with per-check intervals and
  jitter, offline buffering, acknowledgement only after the batch is written to Postgres.
- [done] Alerting: open, escalate, acknowledge, resolve, deduplicate; email notifications with retry.
- [done] Dashboard with tiles (including license usage and backups) and open alerts, live updates.
- [done] Endpoint detail: Summary in two columns (Status and Hardware, Disks and Network with the
  public IP of the agent connection); tier switch only in the right-click menu of the endpoint list.
- [done] PROXY protocol v2 from the host Caddy to the gateway, so the gateway sees agent addresses
  (public IP, logs, per-address enrollment rate limit).
- [done] Checks tab lists every check that applies straight away ("Not run yet" until the first result).
- [done] Checks per endpoint on top of the linked monitoring templates: disable a check, override
  interval, thresholds and failures before alert, add a check for this endpoint only, link an extra
  monitoring template.
- [done] Run a check now, and reset a check (open alerts resolved, "Re-run requested" until the new
  result arrives).
- [done] Alert actions on the Alerts page, the dashboard and the endpoint: acknowledge, put on hold
  until a time (no emails, out of the open alert counts, one email when the hold ends), resolve.
- [done] Notes on endpoints (managed endpoints only): table newest first with author, markdown, the
  author edits, an admin deletes.
- [open] Two instances on one VPS running side by side, each on its own FQDN.
- [open] Key ceremony written out and rehearsed (release and license keys, root key, signer key,
  backup key pair).
- [done] Encrypted off-VPS backups per instance (S3-compatible or directory, WAL shipping).
  [open] Restore onto a fresh VPS tested.

### Open items from 0.1.0

Listed as open before the 0.1.0 tag; the tag was set as a historical marker, so they stay a checklist for the first
production release.

1. [open] Local test by the developer (web UI click-through, agent install as a Windows service). [done] Partly on the
   first test VPS (2026-09-15): sign-in, UI and a Windows agent.
2. [done] First CI run on GitHub (Docker image builds, tests with TimescaleDB, gitleaks over the history), green since
   2026-09-15.
3. [done] First install on a VPS (2026-09-15, one instance behind NAT). [open] Two instances on one VPS; restore of a
   backup onto a fresh VPS.
4. [open] Load test at 10,000 simulated agents.
5. [done] Repository variables `FLEETO_LICENSE_PUBLIC_KEYS` and `FLEETO_RELEASE_PUBLIC_KEYS` set, with test keys
   (2026-09-15). [open] Key ceremony document and production keys (release, license) on hardware tokens.

### Known limitations of 0.1.0

- The Linux agent follows in 0.2.1; macOS is not supported for now.
- An agent offline past its certificate expiry (90 days, renewal from day 60) cannot reconnect and
  must be enrolled again as a new endpoint. [done] Recovery built in 0.2.0.
- The web data protection key ring is stored unencrypted on its volume.
- No email throttling or digest during a mass outage; duplicate identity alerts do not resolve on
  their own.
- S3 uploads are single-part (5 GB per backup file).

## 0.2.0 — Full monitoring, jobs and API

- [done] Complete check catalog (decided 2026-09-15), with a check only sent to endpoints whose platform runs it:
  - network: ping to a host, TCP port reachable, HTTP(S) URL (status code, response time, certificate
    expiry);
  - system: process running, pending reboot, file or folder (exists, size, age), local certificate
    expiring;
  - Windows: event log (number of events per source and id within a window), Windows Security Center
    (antivirus and firewall on);
  - [done] script check: an approved library script whose exit code sets the status (with the script library).
    Decided while building: approval follows the policy like jobs; where approval is required the check runs the
    newest approved version, so a change waits for approval without stopping the check.
- [done] Maintenance windows in the policy (recurring, feeding the maintenance mode rule): days, local start
  time, duration, time zone and all endpoints, servers or workstations; occurrences stored ahead so the rule
  stays a time comparison.
- [done] Escalation rules as routing rules (decided 2026-09-15): per notification channel, which clients and
  which severities it receives. No time-based escalation.
- [done] Webhook notifications next to email: generic JSON signed with HMAC-SHA256, Slack and Microsoft Teams
  (workflow adaptive card); per-channel retry and circuit breaker, test message, last delivery shown. Sent
  only to public https addresses, checked at connect time.
- [done] Service checks by picking a service (Windows services; systemd services with the Linux agent, 0.2.1): besides typing the service name, choose from a list of the
  services on the endpoint. The agent reports its services (name, display name, start type, state) as
  part of the inventory; the check dialog on an endpoint offers them, and on a monitoring template it
  offers the services seen on the endpoints of the linked sites. Typing a name stays possible for a
  service that is not installed yet.
- [done] Email through Microsoft Graph (`sendMail` with application permission, limited to the sending
  mailbox by an Exchange application access policy) as an alternative to SMTP, chosen in Settings,
  Email. Same outbox, retry and circuit breaker as SMTP. Client secret or certificate stored encrypted
  in the database (decided while building: the certificate is created by Fleeto and waits as pending
  until the admin switches to it, so a renewal never interrupts sending).
- [done] Expiring credentials: every stored secret with an end date (starting with the Graph client secret or
  certificate) carries that date. From 30 days before expiry the dashboard warns and admins get an
  email, repeated at 14, 7 and 1 days; after expiry the warning turns red and states what stopped
  working (email delivery) and the next step. An expired Graph secret cannot send its own warning, so
  the warning also shows in the UI and the email goes through SMTP when that is configured as well.
- [done] Check history per check of an endpoint: the last hour, day, week, month and year; a line chart for
  numeric checks (CPU, memory, free disk space including storage growth, uptime, response times) and a
  status timeline for yes/no checks. Hourly and daily rollups kept 13 months, maintained by the workers
  in every setup (decided while building: exactly-once with the evaluation, same behaviour without
  TimescaleDB).
- [done] Maintenance mode per client, site and endpoint: started by hand, with an optional end time
  (1 hour, 4 hours, 24 hours, a chosen time, or until turned off). While an endpoint is in
  maintenance no alert opens or escalates; open alerts stay open and still resolve when their
  check recovers. Duplicate identity alerts are never suppressed. The clients panel shows per
  client and site whether all or some endpoints are in maintenance ("all" or "2/14"); the
  endpoint list and detail show it per endpoint, with a filter "In maintenance". [done] Maintenance
  windows from the policy feed the same rule.
- [done] Script library with versions and optional four-eyes approval per policy; signed remote
  execution with `ValidUntil`, output capture, job history including expired and refused jobs.
  PowerShell and Batch on Windows, sh and bash on Linux, always as SYSTEM or root (decided 2026-09-15). Decided
  while building: a script runs on one endpoint at a time from the UI. Running a script on a selection of endpoints,
  an output cap per policy and running as the signed-in user followed in 0.2.1.
- [done] Deployment for SaaS (decided 2026-09-15: Steaan runs every instance), tested with the first install on a VPS:
  - releases are GitHub Releases of the private repository; install.sh reads them with a fine-grained read-only token
    and pulls the private images with a classic `read:packages` token, both asked once and stored root-only;
  - the release workflow drafts the release, `deploy/sign-release.ps1` checks, signs and publishes it;
  - pre-release versions (`vX.Y.Z-alpha.N`), ordered by semantic versioning; a VPS follows pre-releases only when it
    runs one;
  - a VPS behind a firewall or NAT: install.sh asks once to confirm the forwarded public address;
  - the instance networks use the MTU of the VPS uplink (a 1400 link works without MSS clamping);
  - fixes found by the first install: the Blazor framework script in the web image, the proxy configuration check
    with the capabilities of the running proxy.
- [done] Recovery for agents that were offline past their certificate expiry (for example a laptop
  that stayed in a drawer for months):
  - Renewal with an expired certificate: the gateway accepts an expired but not revoked agent
    certificate for a limited period after expiry (for example 12 months), only for the renewal
    request and never for a normal session. The key must stay the same; the TLS handshake proves
    the agent holds it. The signer re-checks revocation and the grace window. Revoking the agent
    still stops a lost or stolen endpoint.
  - Re-enrollment onto the same endpoint: an agent that enrolls again with a new token can be
    linked to its existing endpoint (chosen by the technician, or matched by the agent's key or
    state), so checks, alerts, notes and audit history are kept instead of creating a new endpoint.
    Decided while building: chosen by the technician only (Enroll again on the endpoint, a single-use
    token bound to it); no automatic matching.
## 0.2.1 — API, agent updates, watchdog and Linux agent

Everything that was still open for 0.2.0, moved here on 2026-09-15, with the developer's answers of that day.

- [done] Public REST API `/api/v1`, **read-only** (decided 2026-09-15: no write access and none planned until there is
  demand):
  - API keys created in Settings: named, optionally limited to clients, revocable, shown once, format
    `flt_<id>_<secret>` with a 256-bit secret stored as SHA-256; decided while building: optionally expiring (30 days,
    90 days, 1 year or never) and created by admins only;
  - read access to clients, sites, endpoints (inventory and status), alerts, jobs, checks and notes (patch compliance follows with Action1 in 0.4.0);
    keyset pagination, ISO 8601 timestamps in UTC;
  - OpenAPI document served by the instance, kept in sync with the code by a test;
  - rate limiting per key and an audit entry per call; decided while building: also a rate limit per address before the
    key is checked, and no response when the audit entry cannot be written;
  - `MD-Files/API.md` documents the whole API for integrators and `MD-Files/API-WAITLIST.md` lists every feature not in
    the API yet, both kept up to date in the same commit as a change (decided 2026-09-15); a test compares `API.md`
    with the OpenAPI document.
- [done] **Fleeto everywhere** (decided 2026-09-15): the internal name Fleetify is no longer used, in code, images, database,
  services on endpoints and documents alike, with a migration path for everything installed before
  (`MD-Files/ARCHITECTURE.md` §7, Rename to Fleeto). Decided while building:
  - a VPS moves as a whole with its next update: copies of every instance directory and volume under the new names, database
    and roles renamed in the copy, the host proxy with its certificates; the old layout stays until an instance runs the new
    release, and an instance whose update fails runs again from it;
  - endpoints keep their enrollment: the install command of the site takes a Fleetify agent over (same gateway and instance
    CA only), because 0.2.0 agents cannot update themselves;
  - data signed or encrypted under the old names stays readable (certificates until renewed, licenses, backups, key files);
    wrapped data keys are rewrapped and every endpoint configuration is signed again by the migration;
  - the CSS prefix `--fl-` stays; the old `fleetify-*` packages on ghcr.io and the `FLEETIFY_*` repository variables are
    replaced by `fleeto-*` and `FLEETO_*`;
  - the release is marked `rollback: restore`.
- [done] Agent self-update, installed only with a valid Steaan release signature:
  - decided while building: the signed release manifest lists every agent and watchdog binary with its SHA-256 and size
    instead of a signature file next to each binary; the agent verifies the manifest against the release public keys
    compiled into it and the download against the listed hash. Same trust, one signature per release, and the manifest
    was already signed outside CI;
  - three update rings chosen in the policy (decided 2026-09-15): Preview (at once), Standard (after 7 days), Delayed
    (after 14 days), **counted from the moment the instance installs the release** (decided 2026-09-15: the binaries
    ship in the instance image; decided while building: in the gateway image too, which serves the downloads, and
    `install.sh` hands the verified manifest to the gateway);
  - an admin can pause a release or release it to every ring at once (Settings, Agent updates, with the rollout per
    ring and the failed updates);
  - the watchdog installs the update and rolls back a failed one; decided while building: an update counts as
    succeeded only when the new version connects to the gateway within 5 minutes, a rolled back version is never
    retried on that endpoint, older versions are never installed, a random delay of up to 10 minutes spreads the
    downloads, and the gateway limits downloads (20 at a time, 12 per endpoint per hour);
  - the agent installs a missing watchdog and updates the watchdog once it runs the new release itself;
  - Authenticode code signing of the Windows binaries comes later (decided 2026-09-15).
- [done] Watchdog service on **every supported platform**, Windows and Linux (decided 2026-09-15): a second service
  with its own certificate for the same endpoint, always connected. Agent and watchdog restart each other; alerts
  "Agent service stopped" and "Watchdog stopped" on managed endpoints, separate from the offline alert. Built for
  Windows as `fleeto-watchdog`; decided while building: the Linux watchdog is built with the Linux agent (below),
  since the Linux agent has no service yet. Also decided while building: the agent requests the watchdog certificate
  over its own session (the signer checks that the endpoint has a valid agent certificate and that the watchdog key
  differs), "Watchdog stopped" only for an endpoint whose watchdog connected before, and a watchdog session never
  receives configurations or jobs.
- [done] Linux agent for **Ubuntu LTS (22.04, 24.04), Debian 12 and newer (including Proxmox VE hosts) and the RHEL
  family (RHEL, Rocky Linux, AlmaLinux 8 and 9)** (decided 2026-09-15): systemd service and the watchdog as a second
  systemd service (service control, supervision and self-update on Linux), install command generated in the UI, inventory, the check catalog on Linux, systemd services offered in the check dialog, key storage (TPM where
  available, otherwise a root-only file), scripts in sh and bash as root. Decided while building:
  - service control goes through `systemctl` instead of the D-Bus API, so the agent needs no extra library; a unit an
    administrator disabled or masked is reported and never started again by the other service;
  - the TPM key is an ECDSA P-256 key created inside the TPM under the owner storage key, stored as the key blob the TPM
    itself encrypted (no persistent handle); CI tests it against a software TPM;
  - binaries in `/opt/fleeto-agent`, state in `/var/lib/fleeto/{agent,watchdog}`, units `fleeto-agent.service` and
    `fleeto-watchdog.service`, logging to the journal and to the state directory;
  - the install command picks the architecture itself and downloads with curl or wget; it runs the installer from a
    directory under `/opt`, because `/tmp` is mounted without exec permission on hardened endpoints;
  - inventory: distribution and kernel from `/etc/os-release` (a Proxmox VE host names Proxmox), hardware from DMI,
    packages from dpkg or rpm without the maintainer email address, systemd services with the start type vocabulary of
    every platform (enabled, static, generated and indirect are `automatic`, disabled is `manual`, masked is `disabled`);
  - the disk check skips images, container layers and network shares, and reports a filesystem mounted twice once.
- [done] Agents for **amd64 and arm64** (decided 2026-09-15), on Windows and Linux, in the release pipeline and the install
  command: the release manifest lists eight binaries (agent and watchdog for `windows-amd64`, `windows-arm64`,
  `linux-amd64` and `linux-arm64`), the instance serves them at `/agent/download/<platform>`, and each install command
  picks the architecture of the endpoint itself.
- [done] Run a script on a selection of endpoints, with a notification to every admin above a configurable number of
  endpoints (deferred from 0.2.0). Decided while building:
  - the endpoint list gets a checkbox per row and one in its header; the selection holds only endpoints the list shows, so
    filtering or another site never leaves an endpoint selected out of sight, and the run starts from the right-click menu
    ("Run script on 12 endpoints");
  - the threshold is instance-wide, in Settings, Scripts (a run spans clients and sites, so a policy value would be
    ambiguous). Default: more than 10 endpoints; 0 turns the email off. The run dialog says beforehand that admins will be
    told;
  - the email is queued in the same transaction as the jobs, so a run that exists is always reported; it names the
    technician, the script and version, the endpoint count, the first ten host names and how many endpoints were skipped;
  - a run on more than one endpoint also writes one audit entry for the batch, next to the entry per job;
  - after starting, a run window shows the state per endpoint, updated live, with the skipped endpoints and their reason;
    selecting an endpoint there opens its output.
- [done] Output cap for job output per policy instead of the fixed 50 MiB (deferred from 0.2.0). Decided while building: the
  cap is chosen in whole mebibytes in the policy (1 MiB to 200 MiB, default 50 MiB); the signer reads it from the effective
  policy of the endpoint's site and puts it in the signed job, so what web wrote on the job row never decides it; agent and
  gateway hold 200 MiB as an absolute ceiling whatever a policy or a payload says; a policy that is not the default shows
  its cap in the policy list.
- [done] Run a script as the logged-on user instead of SYSTEM or root (deferred from 0.2.0). Decided while building:
  - the account is chosen **per run** in the run window ("System (SYSTEM or root)" or "The signed-in user"), not on the
    script and not in the policy: the same script is useful in both places, and running as a user is never more than the
    service account may do anyway. It is signed with the job, so the agent runs what the signer decided and nothing else;
  - the session is the **active** one, a remote desktop session included; on Windows the console session wins when someone
    is signed in there, on Linux a graphical session wins over a text one and root is never chosen;
  - an endpoint where nobody is signed in **fails the job at once** with that reason instead of waiting: the technician
    sees it in the job list and starts it again later;
  - the script is not written in the agent's own directory, which the user may not read, but in a directory the user may
    read and not change (Windows: under `C:\ProgramData\Fleeto` with SYSTEM, the administrators and that user only;
    Linux: a root-owned directory under `/tmp` with the script owned by the user, mode 0400). The user can read the script
    while it runs; the run window says so;
  - the script runs from the user's own profile or home directory, with that user's environment, and on Windows in
    `winsta0\default`, so it can show a window; `CreateProcessAsUser` is called directly because Go's process API cannot
    name a desktop;
  - script checks from the signed configuration always run as the agent's own account, never as a user.
- [done] Fleeto icon for the installed web app: `manifest.webmanifest` (name Fleeto, standalone display, theme colour
  teal `#0F766E`) with PNG icons made from the favicon mark (192 and 512 px, plus a maskable 512 px), an
  `apple-touch-icon` (180 px) and the `theme-color` meta tag in `App.razor`. Decided while building: the icons are
  generated from the same geometry as `favicon.svg` by `tools/dev/build-icons.cs` (run with `dotnet run`, no image
  library and no design tool), so the mark cannot drift from the favicon; `manifest-src 'self'` was added to the Content
  Security Policy, and a test reads the sizes from the PNG headers and checks that every icon the manifest names
  exists.
- [done] Remove the container images of the failed `v0.2.0-alpha.1` release from ghcr.io (2026-09-16: web, gateway, signer, workers and tool; the release build stopped before the Caddy image).
- Not supported for now: **macOS** (decided 2026-09-15): no macOS agent, watchdog, remote control or remote terminal.
  It may come later when there is demand (see Later).

## 0.3.0 — Remote control

- Transport decided after a prototype (WebRTC via gateway TURN vs. WebSocket relay).
- End-to-end encryption with the key exchange bound to the signed session token and the
  agent certificate, tested against a hostile relay.
- Windows: console session as SYSTEM (login screen, UAC), active user session with banner,
  keyboard and mouse, two-way text clipboard, consent and recording per policy, session
  audit, reconnect and stuck-key protection.
- Linux: X11.
- Remote terminal: an interactive terminal as SYSTEM or root (cmd and PowerShell on Windows, sh on Linux) in the browser, served by the watchdog so it also works when the agent is broken. Same
  session token and end-to-end encryption as remote control; admins and technicians on every
  managed endpoint, also where the policy requires script approval (accepted risk, see
  ARCHITECTURE.md §5); audit per session and a transcript when the policy records sessions.

## 0.4.0 — Patch management via Action1

- Action1 integration: organization-to-client mapping, patch compliance per endpoint, site
  and client, missing updates, deployment start and tracking, alerts on stale patch state.
- Push the Action1 agent as a signed job.
- Dashboard tile for patch compliance.

## 0.5.0 — Integrations

- Sophos Central (endpoint health, detections).
- Veeam (backup job status).
- Proxmox VE and VMware vCenter (host and VM inventory and health).
- Integration health on the dashboard.
- Sign-in with Microsoft Entra ID (OpenID Connect) next to local accounts: an admin configures the
  tenant, client id and client secret (or certificate) in Settings; users are linked to a local user
  with its role and client restriction (no automatic account creation without an admin decision).
  The secret is stored encrypted in the database like every other secret.
- The Entra ID client secret or certificate uses the expiring-credentials warnings from 0.2.0; after
  expiry the warning states that sign-in with Entra ID stopped working.

## 0.6.0 — Logs, search and retention

- Log and event collection by the agent; full-text search under 1 second at 10,000 endpoints.
- Retention policies per data type, continuous aggregates for dashboards.
- Global search across clients, sites, endpoints, notes.

## 0.7.0 — Hardening

- Load test at 10,000 simulated agents on the full ingest path; fix what breaks. Document
  the per-instance footprint and how many instances fit on one VPS size.
- External security review of enrollment, signing, secrets handling, licensing, remote
  control and multi-tenancy.
- Restore rehearsed again for several instances on one VPS; unattended reboot recovery
  verified with several instances on one VPS.
- GDPR deliverables: data inventory, data processing agreement template, breach procedure,
  client and endpoint deletion with full purge, recording retention.

## 1.0.0 — General availability

- Everything above complete, tested and documented.
- Product name, trademark and domain checks done.
- Pricing per managed endpoint decided.
- API field names frozen.

## Not yet scheduled

Wanted, but not in a version yet: the version is chosen once the open questions are answered.

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
- File transfer inside remote control; Wayland support on Linux.
- Whitelabel beyond the FQDN: customer logo and product name in UI and email.
- Steaan management server: central issue, renewal and revocation of licenses, fetched by
  instances over an API; later also an overview of all instances and their versions.
- SNMP for network devices.
- Ticksy hook for alert-to-ticket.
- Mobile device management, network topology mapping: noted, not planned.
