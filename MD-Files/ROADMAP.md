# Fleeto — Roadmap

> Planned versions and what each one delivers. Version rules are in `CLAUDE.md`
> (Versioning and changelog). Scope per version is a target, not a promise: a version
> ships when its items are done and tested, and items may move between versions. Dates
> are added only once a version is in progress.

## Current

**0.1.0** — implemented and tested on a development PC, waiting for the developer's local test.
Not tagged yet. What is still open before the tag is listed under "Open before tagging 0.1.0".

**0.2.0** — in progress since 2026-09-15, on top of the untagged 0.1.0. Pre-releases `v0.2.0-alpha.1` (2026-09-15, first CI
run; its release build failed), `v0.2.0-alpha.2` (first published test build) `v0.2.0-alpha.3` (VPS behind NAT, first VPS install) and `v0.2.0-alpha.4` (fixes from the first install).

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
- [done] `Fleetify.Signer`: signer key, instance signing key, internal CA, signing requests over
  LISTEN/NOTIFY, signing rules with tests, role check on who may request which signature.
- [done] Release signing tooling: release key, signed release manifest with image digests, signed
  `install.sh` (verifiable with openssl). [open] Production key on a hardware token.
- [done] Users, roles, login, mandatory TOTP 2FA, first-admin setup flow with backup step.
- [done] Clients, sites, endpoints: CRUD, clients workspace (clients panel, endpoint list with tabs Servers / Workstations / Mixed, endpoint detail below the list), collapsible navigation, settings workspace (settings panel with templates and administration pages, opened from the sidebar footer).
- [done] Client templates, monitoring templates, policies: CRUD, linking to sites, copy.
- [done] Endpoint tier with server-side enforcement in four layers; license format, signing tool,
  license page, pool counting with serialized allocation, 14-day grace period.
- [done] `install.sh` first version (signature and manifest verification, Docker, DNS check, host
  Caddy with SNI passthrough, update with rollback, `--version`, `--check`, `--list`, `--all`).
  [open] Never run on a real VPS yet.
- [done] Load-test simulator (`Fleetify.LoadTest`).

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

### Open before tagging 0.1.0

1. Local test by the developer (web UI click-through, agent install as a Windows service).
2. First CI run on GitHub (Docker image builds, tests with TimescaleDB, gitleaks over the history).
3. First install on a VPS with two instances; restore of a backup onto a fresh VPS.
4. Load test at 10,000 simulated agents.
5. Key ceremony document and production keys (release, license) on hardware tokens; repository
   variables `FLEETIFY_LICENSE_PUBLIC_KEYS` and `FLEETIFY_RELEASE_PUBLIC_KEYS` set.

### Known limitations of 0.1.0

- Linux and macOS agents are stubs (0.2.0).
- An agent offline past its certificate expiry (90 days, renewal from day 60) cannot reconnect and
  must be enrolled again as a new endpoint; recovery is planned for 0.2.0.
- The web data protection key ring is stored unencrypted on its volume.
- No email throttling or digest during a mass outage; duplicate identity alerts do not resolve on
  their own.
- S3 uploads are single-part (5 GB per backup file).

## 0.2.0 — Full monitoring, jobs and API

- [open] Linux and macOS agents.
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
- [done] Service checks by picking a service (Windows services now; systemd and launchd with the Linux and macOS agents): besides typing the service name, choose from a list of the
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
- [open] Watchdog service (`fleetify-watchdog`, Windows first): a second service with its own certificate
  for the same endpoint, always connected. Agent and watchdog restart each other; alerts "Agent
  service stopped" and "Watchdog stopped" on managed endpoints, separate from the offline alert.
  The watchdog installs agent updates and rolls back a failed one.
- [done] Maintenance mode per client, site and endpoint: started by hand, with an optional end time
  (1 hour, 4 hours, 24 hours, a chosen time, or until turned off). While an endpoint is in
  maintenance no alert opens or escalates; open alerts stay open and still resolve when their
  check recovers. Duplicate identity alerts are never suppressed. The clients panel shows per
  client and site whether all or some endpoints are in maintenance ("all" or "2/14"); the
  endpoint list and detail show it per endpoint, with a filter "In maintenance". [done] Maintenance
  windows from the policy feed the same rule.
- [done] Script library with versions and optional four-eyes approval per policy; signed remote
  execution with `ValidUntil`, output capture, job history including expired and refused jobs.
  PowerShell and Batch on Windows, sh and bash on Linux and macOS, always as SYSTEM or root; running
  as the logged-on user comes later (decided 2026-09-15). Decided while building: a script runs on one
  endpoint at a time from the UI; running it on a selection of endpoints, with a notification to every
  admin above a number of endpoints, and an output cap per policy come later.
- [open] Agent self-update, installed only with a valid Steaan release signature. Three update rings chosen
  in the policy (decided 2026-09-15): Preview (at once), Standard (after 7 days), Delayed (after 14
  days); an admin can pause a release or release it to every ring at once.
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
- [open] Public REST API `/api/v1`: API keys with scopes, read access to all core resources,
  OpenAPI document with drift test, rate limiting, audit. Notes get an external ticket reference
  (for a servicedesk), set and read through the API.

## 0.3.0 — Remote control

- Transport decided after a prototype (WebRTC via gateway TURN vs. WebSocket relay).
- End-to-end encryption with the key exchange bound to the signed session token and the
  agent certificate, tested against a hostile relay.
- Windows: console session as SYSTEM (login screen, UAC), active user session with banner,
  keyboard and mouse, two-way text clipboard, consent and recording per policy, session
  audit, reconnect and stuck-key protection.
- macOS: Screen Recording and Accessibility permission flow, same feature set.
- Linux: X11.
- Remote terminal: an interactive terminal as SYSTEM (cmd and PowerShell on Windows, sh on Linux and
  macOS) in the browser, served by the watchdog so it also works when the agent is broken. Same
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
- API write access for the resources a technician can change in the UI.
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

## Later (not planned for 1.0)

- File transfer inside remote control; Wayland support on Linux.
- Whitelabel beyond the FQDN: customer logo and product name in UI and email.
- Steaan management server: central issue, renewal and revocation of licenses, fetched by
  instances over an API; later also an overview of all instances and their versions.
- SNMP for network devices.
- Ticksy hook for alert-to-ticket.
- Mobile device management, network topology mapping: noted, not planned.
