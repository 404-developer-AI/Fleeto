# Fleeto — Roadmap

> Planned versions and what each one delivers. Version rules are in `CLAUDE.md`
> (Versioning and changelog). Scope per version is a target, not a promise: a version
> ships when its items are done and tested, and items may move between versions. Dates
> are added only once a version is in progress.

## Current

**0.1.0** — implemented and tested on a development PC, waiting for the developer's local test.
Not tagged yet. What is still open before the tag is listed under "Open before tagging 0.1.0".

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
- [done] Clients, sites, endpoints: CRUD, clients workspace (clients panel, endpoint list with tabs Servers / Workstations / Mixed, endpoint detail below the list), collapsible navigation.
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

- Behind Caddy's SNI passthrough the gateway sees Caddy's address, so the per-IP enrollment rate
  limit is shared by all agents (PROXY protocol towards the gateway is future work).
- Linux and macOS agents are stubs (0.2.0).
- An agent offline past its certificate expiry (90 days, renewal from day 60) cannot reconnect and
  must be enrolled again as a new endpoint; recovery is planned for 0.2.0.
- The web data protection key ring is stored unencrypted on its volume.
- No email throttling or digest during a mass outage; duplicate identity alerts do not resolve on
  their own.
- S3 uploads are single-part (5 GB per backup file).

## 0.2.0 — Full monitoring, jobs and API

- Linux and macOS agents.
- Complete check catalog, maintenance windows, escalation rules, webhook notifications.
- Script library with versions and optional four-eyes approval per policy; signed remote
  execution with `ValidUntil`, output capture, job history including expired and refused jobs.
- Agent self-update with update rings, installed only with a valid Steaan release signature.
- Recovery for agents that were offline past their certificate expiry (for example a laptop
  that stayed in a drawer for months):
  - Renewal with an expired certificate: the gateway accepts an expired but not revoked agent
    certificate for a limited period after expiry (for example 12 months), only for the renewal
    request and never for a normal session. The key must stay the same; the TLS handshake proves
    the agent holds it. The signer re-checks revocation and the grace window. Revoking the agent
    still stops a lost or stolen endpoint.
  - Re-enrollment onto the same endpoint: an agent that enrolls again with a new token can be
    linked to its existing endpoint (chosen by the technician, or matched by the agent's key or
    state), so checks, alerts, notes and audit history are kept instead of creating a new endpoint.
- Notes on endpoints and sites.
- Public REST API `/api/v1`: API keys with scopes, read access to all core resources,
  OpenAPI document with drift test, rate limiting, audit.

## 0.3.0 — Remote control

- Transport decided after a prototype (WebRTC via gateway TURN vs. WebSocket relay).
- End-to-end encryption with the key exchange bound to the signed session token and the
  agent certificate, tested against a hostile relay.
- Windows: console session as SYSTEM (login screen, UAC), active user session with banner,
  keyboard and mouse, two-way text clipboard, consent and recording per policy, session
  audit, reconnect and stuck-key protection.
- macOS: Screen Recording and Accessibility permission flow, same feature set.
- Linux: X11.

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
