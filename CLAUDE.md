# CLAUDE.md — Fleeto (internal: Fleetify)

Fleeto is an RMM (remote monitoring and management) platform by Steaan, for IT teams
managing 1 to 10,000 endpoints across many clients. This file is the starting point for
all development work. Read `MD-Files/branding-fleeto.md` before touching any user-visible text.

## Working agreements

- **Language**: conversation with the developer is always in Dutch. Documentation, code,
  comments, commit messages, log messages and UI text are always in English.
- **Current phase**: documentation only. Do not write code until the developer says so.
- **Source control**: git, default branch `main`, private GitHub repository
  `404-developer-AI/Fleeto` as `origin`. Conventional commits. Secrets never enter the repository, not even in example
  files with real values; `.gitignore` blocks the usual suspects.
- **Documentation set**: this file in the repository root, everything else in `MD-Files/`.
  Keep each file to its purpose and update the relevant file in the same commit as the
  change it describes.

| File | Purpose |
|---|---|
| `CLAUDE.md` | Rules, priorities, conventions, product model. Start here. |
| `MD-Files/ARCHITECTURE.md` | Components, data model, data flows, security architecture, install and update flow. |
| `MD-Files/ROADMAP.md` | Planned versions and what each one delivers. |
| `MD-Files/CHANGELOG.md` | Unreleased changes plus the two most recent released versions. |
| `MD-Files/CHANGELOG-ARCHIVE.md` | Every released version older than the two in `MD-Files/CHANGELOG.md`. |
| `MD-Files/branding-fleeto.md` | Names, colors, typography, vocabulary, tone of voice. |

## Priorities (in this order)

1. **Security** — this product runs with high privileges on customer endpoints. A vulnerability here is a supply-chain attack on every client. Security beats features, always.
2. **Robustness** — the platform must degrade gracefully, never lie about endpoint state, and recover from crashes, network partitions and bad data without human intervention.
3. **Performance** — dashboards and log search must feel instant at 10,000 endpoints. Slow queries are bugs.

When these conflict, the higher one wins. When in doubt, ask before implementing.

## Versioning and changelog

Semantic versioning `MAJOR.MINOR.PATCH`. The project starts at `0.0.0`.

| Bump | Example | When |
|---|---|---|
| Patch | `0.0.1` | Small release: fixes, small additions, no migration risk. |
| Minor | `0.1.0` | Large release: a milestone from `MD-Files/ROADMAP.md`. |
| Major | `1.0.0` | First production release. Afterwards only for breaking changes. |

- Git tags are `vX.Y.Z`. Server and agent are released together under one version number.
- `MD-Files/CHANGELOG.md` follows the Keep a Changelog layout: an `Unreleased` section on top, then
  the two most recent released versions, newest first. Sections per version:
  Added, Changed, Fixed, Security, Removed (omit empty ones).
- When a third released version would appear in `MD-Files/CHANGELOG.md`, move the oldest one to the
  top of `MD-Files/CHANGELOG-ARCHIVE.md` in the same commit. Never edit an archived entry.
- Every release commit updates the version number, `MD-Files/CHANGELOG.md` and, if needed, `MD-Files/ROADMAP.md`.

## Deployment model: one instance per customer

A **customer** is an IT team or MSP that buys Fleeto from Steaan. Every customer gets its
own **instance**: a separate Docker Compose stack with its own database, root key, secrets,
licenses, users and FQDN. Instances never share data. Several instances can run on one
VPS; a host-level reverse proxy routes each FQDN to its instance.

- **FQDN whitelabel**: the customer chooses the URL (`rmm.customer.example`). The UI, API,
  agent gateway, enrollment commands and email links all use that FQDN. TLS certificates
  are issued automatically for it. Logo and product name in the UI stay Fleeto for now
  (see open decisions).
- Inside an instance the hierarchy is Client → Site → Endpoint (below). The customer's
  own clients are isolated from each other by the client scope; customers are isolated
  from each other by the instance boundary.

## Product model

The hierarchy inside an instance is **Client → Site → Endpoint**. Everything in the UI,
the API and the database follows it.

- **Client**: a customer of the IT team using Fleeto. Fields: **client code** (short, unique,
  uppercase, e.g. `ACME`) and **client name**. The client is the tenant boundary inside an
  instance: every query is scoped by client, and deleting a client removes all of its data.
- **Site**: a group of endpoints within a client. A site is where **policies** and
  **monitoring templates** are linked (example names: "Monitoring", "Agent Only", "XDR Only"
  for endpoints that also run an XDR agent). Enrollment tokens are issued per site. A client
  has at least one site.
- **Endpoint**: a machine with a Fleeto agent. Each endpoint has a **class**: `workstation` or
  `server`, derived from the OS edition and overridable by hand, and a **license tier**
  (see Licensing). Hypervisor hosts and VMs discovered through Proxmox or vCenter appear as
  endpoints without an agent.
- **Policy**: agent behaviour pushed to every endpoint of a site: check intervals, patch
  behaviour, update ring, script permissions, remote control rules, maintenance windows.
- **Monitoring template**: a named set of checks with thresholds and alert rules. Linked to a
  site, applied to all of its endpoints (class-specific checks apply to matching endpoints only).
- **Client template**: a blueprint used when creating a client. It lists the sites to
  create and the policies and monitoring templates to link to each of them. Selecting a
  template at client creation creates the sites and links in one step.
- **Templates are linked, not copied.** A change to a monitoring template, policy or client
  template (adding or removing a check, for example) applies immediately to every client, site
  and endpoint that uses it. A technician can make a copy of a template to give one client its
  own variant; the copy is independent from then on.

UI structure:

- **Dashboard**: summary tiles (endpoints online/offline, open alerts by severity, patch
  compliance, integration health, agents out of date, license usage), the open alert list,
  recent jobs.
- **Clients**: list of clients → sites of a client → endpoints of a site. The endpoint list has
  tabs **Workstations**, **Servers** and **Mixed** (all endpoints of the site together).
  Endpoints open in a detail page (overview, checks, alerts, jobs, patches, remote control,
  notes, audit).
- **Templates**: client templates, monitoring templates, policies.
- **Settings**: users and roles, licensing, API keys, integrations, notification channels,
  retention, audit log.

## Licensing: per endpoint, two tiers

Licenses are counted **per endpoint** and belong to the instance.

| Tier | Cost | What the endpoint gets |
|---|---|---|
| **Agent only** | Free | The agent is installed and visible: online/offline state, inventory (OS, CPU, memory, disks, software, agent version). Nothing else: no policies, no monitoring templates, no checks or alerts, no jobs or scripts, no patching, no remote control. Look, do not touch. |
| **Managed** | Licensed | Everything Fleeto can do. |

- **Agent-only endpoints are unlimited.** No cap per instance, ever; the free tier is the
  way in.
- An endpoint is agent-only until a technician switches it to managed; switching consumes
  one license from the instance pool. Switching back frees it. The dashboard shows used and
  available licenses; the switch is refused when the pool is empty, with a clear message.
- The license is a signed document (ed25519, Steaan private key, public key baked into the
  build) stating customer, endpoint count, expiry and instance FQDN. Stored encrypted in the
  instance database, verified offline. An expired license turns every endpoint read-only
  (agent-only behaviour) but deletes nothing.
- **License management is central, later.** Target: a Steaan management server that issues,
  renews and revokes licenses, and that instances talk to over an API. Until that exists
  (v1 ships without it) the license document is loaded in Settings by hand. Design the
  license format and the Settings page so the management server can replace the manual
  step without changing the instance side.
- Every feature checks the endpoint tier server-side, not only in the UI. Tests prove that an
  agent-only endpoint rejects jobs, policies and remote control at the API and gateway level.

## Remote control

Screen takeover is built into Fleeto: no external tool, no third-party account.

- Technician opens a session from the endpoint page in the browser. The agent captures the
  screen, streams it through the gateway, and injects keyboard and mouse input.
- **Clipboard works in both directions** for text from day one; file transfer follows later.
- Servers: connect to the console session, including the login screen and UAC prompts,
  which requires the agent to run as SYSTEM on Windows. Workstations: the active user
  session, with a visible banner on the endpoint naming the technician while a session runs.
- Policy decides whether the endpoint user must consent first, and whether sessions are
  recorded. Every session is an audit entry with technician, endpoint, start, end and reason.
- Sessions are end-to-end encrypted between browser and agent, authorised per session with
  a short-lived token, and only possible on managed endpoints.
- Platforms in order: Windows, macOS (needs the Screen Recording and Accessibility
  permissions), Linux (X11 first, Wayland later).

## Public API

Every instance exposes a versioned REST API (`/api/v1`) so the customer can pull data out of
Fleeto into other tools.

- Authentication with **API keys** created in Settings: named, scoped (read-only or
  read-write, optionally limited to clients), revocable, shown once at creation and stored
  hashed. Every call is rate limited and audited.
- Read access to clients, sites, endpoints (with inventory and status), alerts, jobs, checks,
  patch compliance and notes. Write access to the same objects where a technician could do it
  in the UI, with the same license and role checks.
- OpenAPI document served by the instance, kept in sync with the code by a test. Keyset
  pagination, ISO 8601 timestamps in UTC, stable field names once 1.0.0 ships.
- The UI uses the same API where practical so it cannot drift from what customers get.

## Patch management: Action1

Fleeto does not maintain its own package catalog or patch engine. Patch management is
delegated to **Action1** through its REST API, integrated like every other connector
(see Integrations). Fleeto shows patch compliance per endpoint, site and client, lists
missing updates, triggers and tracks deployments, and raises alerts on stale patch state.
The Action1 agent runs next to the Fleeto agent; Fleeto can push the Action1 installer as a
signed job. Platforms that Action1 does not cover show "not covered by patch management"
rather than a home-grown fallback. See the open decisions for what still has to be verified.

## Install and update

One script does both, on a fresh or an existing Ubuntu VPS: `install.sh`. It works per
instance; a VPS can hold several.

- First run on a VPS: installs Docker and the host-level reverse proxy.
- New instance: asks for the **FQDN** (or takes `--fqdn rmm.customer.example`), checks that
  the name resolves to this VPS, generates the root key and database password, writes the
  Compose files under `/opt/fleetify/<instance>/`, pulls the images, runs migrations, starts
  the stack, registers the FQDN with the reverse proxy and prints the URL plus the one-time
  first-admin setup link.
- Every later run for an instance: backs up its database, pulls the requested images
  (default: latest release), runs migrations, restarts the stack. Idempotent and safe to
  re-run; a failed update rolls back to the previous images.
- `install.sh --version 0.1.0` pins a version. `install.sh --check` reports the installed and
  the latest available version without changing anything. `install.sh --list` shows the
  instances on this VPS.
- Agents are installed with a one-line command generated in the UI (it carries the instance
  FQDN and the site enrollment token) and self-update from the server, staged by update ring
  per policy.

## Architecture in one paragraph

Ubuntu VPS, Docker Compose, one stack per instance behind one host-level **caddy** (reverse
proxy, automatic TLS, routes by FQDN). Per instance: **fleetify-web** (Blazor Server UI +
public REST API, .NET, MudBlazor, Migrify conventions), **fleetify-gateway** (mTLS WebSocket
endpoint for agents, remote control relay), **fleetify-workers** (checks, alerting,
integration pollers, patch orchestration, retention), **postgres** (PostgreSQL 16 +
TimescaleDB, the only durable store) and **valkey** (queues, pub/sub, caches, never the only
copy of anything). Agents are one Go codebase, one static binary per platform.
Details, diagrams and data model: `MD-Files/ARCHITECTURE.md`.

## Security requirements (non-negotiable)

- **Enrollment**: one-time enrollment token (per site, expiring, revocable) exchanged for a unique per-agent certificate. All agent traffic is mTLS. No shared API keys across agents.
- **Agent updates and scripts are signed** (ed25519). Agents verify signatures before executing anything. The signing key is stored encrypted like every other secret (see Secrets) and the key ceremony is documented in `MD-Files/ARCHITECTURE.md`.
- **Command authorization**: every job records who initiated it, when, on which endpoints, with what payload. Immutable audit log for all privileged actions (script run, patch, remote control session, credential change, API key change, license change, login, permission change, client or site deletion).
- **Remote control** is the highest-risk feature: per-session tokens, end-to-end encryption, visible on the endpoint, policy-controlled consent, audited, managed endpoints only.
- **Web/API**: 2FA (TOTP) for all users, session hardening, per-endpoint rate limiting, strict input validation, parameterized queries only, CSP without unsafe-inline. API keys hashed at rest, scoped, revocable.
- **Least privilege**: containers run as non-root, read-only filesystems where possible, no Docker socket exposure to app containers. Instances on the same VPS share nothing but the host proxy: separate networks, volumes and secrets.
- **Multi-tenancy discipline**: every query is scoped by client; write tests that prove cross-client reads fail. Instances are separate stacks, so cross-instance access is impossible by construction, not by a filter.
- Dependency policy: minimal, well-maintained packages; `dotnet list package --vulnerable` and `govulncheck` in CI; fail the build on known CVEs.

## Secrets, keys and personal data

- **All secrets live in the database, encrypted at rest**: integration credentials (Sophos,
  Veeam, vCenter, Proxmox, Action1), SMTP, webhook secrets, the agent signing key, TOTP
  seeds, enrollment tokens, API keys (hashed), agent certificates, the license document.
  Nothing in `.env`, `appsettings` or any other file on the server, and nothing in the
  repository.
- **Envelope encryption**: one root key (KEK) wraps per-purpose data keys (DEKs) that are
  stored, wrapped, in the database. DEKs encrypt the data with AES-256-GCM. Rotating a DEK
  re-encrypts its data without touching the root key; rotating the root key only rewraps
  the DEKs.
- **The root key is the one thing that cannot live in the database** (it would encrypt
  itself). `install.sh` generates one per instance and stores it as a Docker secret: a
  root-owned file with mode 0600 outside the repository, never in `.env`, never in a Compose
  file. The database password is handled the same way. An offline backup of the root key is
  part of the key ceremony.
- User passwords: Argon2id. Secrets are never logged, never returned by the API after
  creation (write-only fields), never included in backups in plaintext (backups are
  encrypted with a separate key before leaving the VPS).
- **GDPR**: Fleeto processes personal data (user accounts, endpoint user names, IP addresses,
  log content, remote control recordings) on behalf of customers and their clients. Rules:
  data stays in the EU; retention limits apply to every data type and are enforced
  automatically; deleting a client or endpoint purges its data, with backups expiring on
  their own schedule; every access to personal data is covered by the audit log; a data
  processing agreement template and a breach procedure ship with 1.0.0. Collect only what a
  check or feature needs; log content is never used for anything but the customer's own
  search.
- `.gitignore` excludes `.env*`, `*.pem`, `*.key`, `secrets/` and local Compose overrides.
  CI runs a secret scanner and fails on a hit.

## Robustness requirements

- Idempotent job execution: re-delivered commands must not run twice (job IDs + agent-side dedupe).
- Every external call (integrations, Action1, SMTP) has timeouts, retries with backoff, and a circuit breaker. An integration being down must never degrade core monitoring.
- Migrations are forward-only, tested against a copy of production data.
- Backups: nightly `pg_dump` + WAL archiving to off-VPS storage, per instance; restore procedure documented and tested (a backup that has never been restored does not exist).
- Health endpoints on every container; Compose restart policies; every instance self-recovers from a VPS reboot with no manual steps.
- Clock skew tolerance: never trust agent timestamps for ordering; stamp on ingest.
- A remote control session that drops reconnects on its own; a dropped session never leaves input stuck (keys released) on the endpoint.

## Performance requirements

- Log and event search must return first results in under 1 second at 10,000 endpoints. Use GIN/tsvector indexes, time-bucketed partitions, keyset pagination (no OFFSET on large sets).
- Retention is configurable per data type (e.g. raw check results 30 days, aggregates 13 months, logs 90 days) and enforced by TimescaleDB retention policies — the disk on a VPS is finite.
- Dashboard queries read continuous aggregates, never raw hypertables.
- Live endpoint status via pub/sub push to the UI, not polling loops.
- Remote control: under 100 ms input latency on a LAN-quality link, adaptive quality on poor links.
- Load-test the gateway and ingest path at 10,000 simulated agents before calling anything done; keep the simulator (`Fleetify.LoadTest`) in the repo. Size one VPS for several instances; document the per-instance footprint.

## Integrations

Connector architecture: one interface (`IIntegration`) with typed implementations per
product. Targets: **Action1** (patch management), **Sophos Central** (endpoint health,
detections), **Veeam** (backup job status), **Proxmox VE** and
**VMware vCenter** (host/VM inventory and health). Later: SNMP for network devices,
SMTP/webhooks outbound for alerting (mail, Slack, Teams, and a Ticksy hook when that ships).

Rules: pollers run in workers on their own schedule, results land in the same
check/alert model as agent data (one alert pipeline, not two), credentials encrypted
(see Secrets), every integration is configured globally or per client with a mapping
(an Action1 organization or Sophos tenant maps to exactly one Fleeto client).

## Product scope (v1)

- One instance per customer with its own FQDN, installed and updated by `install.sh`
- Licensing per endpoint: agent-only (free) and managed
- Clients, sites, endpoints; client templates, monitoring templates, policies
- Dashboard with tiles, alerts and recent activity
- Endpoint inventory (hardware, OS, software, patch level) per site
- Checks with per-check intervals (seconds → monthly), thresholds, maintenance windows
- Alerting with acknowledgement, deduplication and escalation via email/webhook
- Script library + remote execution with output capture
- Remote control with two-way clipboard, built in
- Patch compliance and deployment through Action1
- Log and event search
- **Notes**: markdown notes on endpoints and sites, with author + timestamp, searchable, included in the audit trail
- Users, roles (admin / technician / read-only), 2FA
- Public REST API with scoped API keys and OpenAPI document
- Integrations: Action1, Sophos, Veeam, Proxmox, vCenter

Explicitly out of scope for v1: mobile device management, network topology mapping, a
home-grown patch engine, file transfer inside remote control. Note them, do not build them.

## Conventions

- Namespaces, images, env vars, service names: **Fleetify**. User-visible text: **Fleeto**. Run the grep check from `MD-Files/branding-fleeto.md` §7 before every release.
- UI text in English, tone per `MD-Files/branding-fleeto.md` §8 (calm, no exclamation marks, errors state cause + next step). Use the fixed vocabulary from §6 (instance, client, site, endpoint, agent-only, managed, agent, check, alert, job, policy, monitoring template, client template, integration, note, remote control session, API key).
- Follow the Migrify codebase conventions where they exist (project layout, EF Core patterns, MudBlazor usage, email templates).
- Tests: unit tests for domain logic, integration tests against real PostgreSQL in CI, the cross-client tests from Security, license-tier enforcement tests, and the load-test scenario. New features without tests are not done.
- CI on GitHub Actions: build, tests, vulnerability scan, secret scan, the Fleetify grep check. A release is a tagged commit that passes CI.

## Open decisions (revisit before building)

- **Whitelabel depth**: FQDN only (v1) vs. customer logo and product name in the UI and emails.
- **Agent gateway routing per instance**: TLS passthrough by SNI on the host proxy (agents
  connect to `agents.<fqdn>`) vs. one port per instance. Decide during 0.0.x.
- **Remote control transport**: WebRTC with the gateway as TURN relay vs. a plain WebSocket
  relay through the gateway. Prototype both on Windows before the remote control milestone.
- **Action1**: to be worked out when the Action1 milestone starts: platform coverage (Windows
  confirmed; macOS and Linux to check), API rate limits, licensing model, and how Action1
  organizations map to Fleeto clients.
- Final product name — "Fleeto" is a working title; a Google Play app "Fleeto" exists in vehicle fleet management. Do the BOIP/EUIPO and domain checks before public use.
- Gateway implementation language: .NET (stack consistency) vs Go (shared code with agent). Benchmark both at 10k connections.
- Pricing per managed endpoint is undecided.
- Apple platform depth: monitoring only, or also patch/MDM-adjacent features (scope risk).
