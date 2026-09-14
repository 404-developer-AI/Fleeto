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
- Root key, envelope encryption, encrypted secrets table, audit log.
- Users, roles, login, TOTP 2FA, first-admin setup flow.
- Clients (code + name), sites, endpoints: CRUD, list views, tabs Workstations / Servers / Mixed.
- Client templates, monitoring templates, policies: CRUD, linking to sites, copy of a template.
- Endpoint tier (agent-only / managed) in the model with server-side enforcement; license
  document format, signing tool for Steaan, license page in Settings, pool counting.
- `install.sh` first version: host prerequisites, instance creation with FQDN prompt and
  DNS check, host Caddy routing, update, `--version`, `--check`, `--list`.
- Decide agent gateway routing per instance (SNI passthrough vs. port per instance).
- Load-test simulator scaffold (`Fleetify.LoadTest`).

## 0.1.0 — First usable release

- Go agent for Windows: enrollment with per-site token and mTLS certificate, heartbeat,
  online/offline state, inventory (hardware, OS, software). Agent-only tier complete.
- Gateway at production quality for the chosen language; benchmark at 10,000 connections.
- Managed tier: basic checks from the monitoring template (CPU, memory, disk, service
  state, uptime) with per-check intervals and jitter, offline buffering.
- Alerting: open, acknowledge, close, deduplicate; email notifications.
- Dashboard with tiles (including license usage) and open alerts, live updates via pub/sub.
- Two instances on one VPS running side by side, each on its own FQDN.
- Key ceremony documented and rehearsed; backup and restore tested per instance.

## 0.2.0 — Full monitoring, jobs and API

- Linux and macOS agents.
- Complete check catalog, maintenance windows, escalation rules, webhook notifications.
- Script library, signed remote execution with output capture, job history.
- Agent self-update with update rings.
- Notes on endpoints and sites.
- Public REST API `/api/v1`: API keys with scopes, read access to all core resources,
  OpenAPI document with drift test, rate limiting, audit.

## 0.3.0 — Remote control

- Transport decided after a prototype (WebRTC via gateway TURN vs. WebSocket relay).
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
- Encrypted off-VPS backups per instance with rehearsed restore; unattended reboot recovery
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
