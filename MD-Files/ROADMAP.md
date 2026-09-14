# Fleeto — Roadmap

> Planned versions and what each one delivers. Version rules are in `CLAUDE.md`
> (Versioning and changelog). Scope per version is a target, not a promise: a version
> ships when its items are done and tested, and items may move between versions. Dates
> are added only once a version is in progress. This is a first draft, still to be
> confirmed by the developer.

## Current

**0.0.0** — documentation only. No code.

## 0.0.x — Foundation (patch releases towards 0.1.0)

Each item below is a patch release on its own, or a few of them together.

- Private GitHub repository, `.gitignore`, secret scanning, CI skeleton (build, test).
- Solution skeleton following the Migrify layout: Web, Core, Infrastructure, Workers.
- PostgreSQL + TimescaleDB schema and first migrations; Compose stack that starts locally.
- Root key, envelope encryption, encrypted secrets table, audit log, one database role per
  container.
- `Fleetify.Signer`: signer key, instance signing key, internal CA, signing requests over
  LISTEN/NOTIFY, signing rules with tests.
- Release signing: Steaan release key on a hardware token, signed release manifest with image
  digests, signed `install.sh`.
- Users, roles, login, TOTP 2FA, first-admin setup flow (including backup destination and
  backup public key).
- Clients (code + name), sites, endpoints: CRUD, list views, tabs Workstations / Servers / Mixed.
- Client templates, monitoring templates, policies: CRUD, linking to sites, copy of a template.
- Endpoint tier (agent-only / managed) in the model with server-side enforcement; license
  document format, signing tool for Steaan, license page in Settings, pool counting, 14-day
  grace period after expiry.
- `install.sh` first version: signature and manifest verification, host prerequisites,
  instance creation with FQDN prompt and DNS check (FQDN and `agents.<fqdn>`), host Caddy
  with HTTPS routing and SNI passthrough for agents, update, `--version`, `--check`, `--list`.
- Load-test simulator scaffold (`Fleetify.LoadTest`).

## 0.1.0 — First usable release

- Go agent for Windows: enrollment with per-site token, CA fingerprint pinning and mTLS
  certificate (TPM-backed key where available), certificate renewal, heartbeat,
  online/offline state, inventory (hardware, OS, software). Agent-only tier complete.
- Certificate revocation: deny list checked on every connection, live disconnect, revoke on
  endpoint deletion, duplicate identity detection.
- Gateway at production quality for the chosen language; benchmark at 10,000 connections.
- Managed tier: basic checks from the monitoring template (CPU, memory, disk, service
  state, uptime) with per-check intervals and jitter, offline buffering, acknowledgement only
  after the batch is written to Postgres.
- Alerting: open, acknowledge, close, deduplicate; email notifications.
- Dashboard with tiles (including license usage) and open alerts, live updates via pub/sub.
- Two instances on one VPS running side by side, each on its own FQDN.
- Key ceremony documented and rehearsed (release and license keys, root key, signer key,
  backup key pair).
- Encrypted off-VPS backups per instance (backup public key, write-only destination), restore
  onto a fresh VPS tested.

## 0.2.0 — Full monitoring, jobs and API

- Linux and macOS agents.
- Complete check catalog, maintenance windows, escalation rules, webhook notifications.
- Script library with versions and optional four-eyes approval per policy; signed remote
  execution with `ValidUntil`, output capture, job history including expired and refused jobs.
- Agent self-update with update rings, installed only with a valid Steaan release signature.
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
