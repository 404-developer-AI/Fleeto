# Fleeto — Architecture

> Technical reference for Fleeto (internal: Fleetify). Rules and priorities live in
> `CLAUDE.md` in the repository root; this file describes how the system is put together.
> Status: design, nothing built yet. Sections marked *decision pending* point to the open
> decisions in `CLAUDE.md`.

## 1. Deployment topology

Every Fleeto **customer** (an IT team or MSP) gets its own **instance**: a separate Docker
Compose project with its own database, secrets, root key, licenses, users and FQDN. Several
instances can share one Ubuntu VPS. One host-level Caddy routes each FQDN to its instance.
Instances share nothing else: separate Docker networks, volumes and secret files.

```
                     ┌──────────────── VPS ────────────────────────────────────────────────┐
                     │  caddy (host level, ports 80/443, automatic TLS, routes by FQDN)     │
                     │     │ rmm.customer-a.example          │ rmm.customer-b.example        │
                     │     ▼                                 ▼                              │
[browser, API] ───►  │  ┌─ instance A ───────────────┐   ┌─ instance B ───────────────┐    │
                     │  │ fleetify-web               │   │ fleetify-web               │    │
[agents] ──mTLS──►   │  │ fleetify-gateway           │   │ fleetify-gateway           │    │
                     │  │ fleetify-workers           │   │ fleetify-workers           │    │
                     │  │ postgres + TimescaleDB     │   │ postgres + TimescaleDB     │    │
                     │  │ valkey                     │   │ valkey                     │    │
                     │  └────────────────────────────┘   └────────────────────────────┘    │
                     └────────────────────────────────────────────────────────────────────┘
```

Inside one instance:

```
[agents] --mTLS WebSocket--> [fleetify-gateway] --> [valkey queue] --> [fleetify-workers] --> [postgres + TimescaleDB]
                                    │                                                                ^
                                    └── remote control relay (browser <-> agent)                     │
[integrations: Action1, Sophos, Veeam, Proxmox, vCenter] --> [poller workers] -----------------------+
                                                                                                     │
[browser, API clients] --HTTPS--> [caddy] --> [fleetify-web: Blazor Server UI + public REST API] <---+
                                                     ^
                                                     +-- pub/sub (valkey) for live status
```

| Container | Scope | Role | Notes |
|---|---|---|---|
| **caddy** | VPS | Reverse proxy, automatic TLS | One per VPS. Routes UI and API by FQDN. Agent traffic per instance: *decision pending* (SNI passthrough to `agents.<fqdn>` vs. one port per instance). |
| **fleetify-web** | instance | Blazor Server UI and public REST API (.NET, MudBlazor) | Follows the Migrify project layout and conventions. |
| **fleetify-gateway** | instance | Agent connection endpoint and remote control relay | Persistent WebSocket over mTLS for online state, command push and check results. Target: 10,000 concurrent connections on modest hardware. Language: *decision pending* (.NET vs Go). |
| **fleetify-workers** | instance | Background jobs | Check evaluation, alerting, integration pollers, Action1 patch orchestration, retention cleanup, backups, license checks. |
| **postgres** | instance | PostgreSQL 16 + TimescaleDB | The only durable store. Relational data plus hypertables for check results and metrics plus log storage with full-text search. |
| **valkey** | instance | Queues, pub/sub, short-lived caches | Never the only copy of important data. |

Rationale for one database engine: operational simplicity on a single VPS beats a
polyglot stack. TimescaleDB compression and continuous aggregates cover metrics;
PostgreSQL full-text search (tsvector, GIN) covers logs. If log search outgrows this, the
escape hatch is ClickHouse or Loki as a dedicated log store; the log repository sits
behind an interface so that swap stays cheap.

Rationale for one instance per customer: isolation by construction (no shared database to
filter), per-customer FQDN and TLS certificate, per-customer root key and backups, and a
customer can be moved to another VPS by copying one directory and one backup.

## 2. Domain model

```
Client 1──* Site 1──* Endpoint 1──* CheckResult (hypertable)
                │         │
                │         └──* Job, Alert, Note, InventorySnapshot, RemoteSession
                │
                ├──* SiteMonitoringTemplate ──> MonitoringTemplate 1──* CheckDefinition
                └──* SitePolicy ──────────────> Policy

ClientTemplate 1──* ClientTemplateSite ──* (MonitoringTemplate | Policy) links

License (one per instance)      ApiKey *──> User
User *──* Role                  Integration 1──* IntegrationMapping ──> Client
AuditEntry (append-only)
```

| Entity | Key fields | Notes |
|---|---|---|
| **Client** | `Id`, `Code` (unique, uppercase), `Name`, `CreatedAt` | Tenant boundary inside the instance. Every other table with client data carries `ClientId` and every query filters on it. |
| **Site** | `Id`, `ClientId`, `Name`, `Description` | Groups endpoints. Holds the links to policies and monitoring templates. Enrollment tokens belong to a site. |
| **Endpoint** | `Id`, `SiteId`, `Hostname`, `Class` (`workstation`/`server`), `ClassOverride`, `Tier` (`agent_only`/`managed`), `Os`, `AgentVersion`, `LastSeenAt`, `Source` (`agent`/`integration`) | Endpoints without an agent exist only for hypervisor inventory (ESXi hosts and VMs from vCenter or Proxmox). `Tier` gates every feature server-side. |
| **Policy** | `Id`, `ClientId?`, `Name`, settings | Agent behaviour: intervals, patch behaviour, update ring, script permissions, remote control rules (consent, recording), maintenance windows. `ClientId` null = global. |
| **MonitoringTemplate** | `Id`, `ClientId?`, `Name` | Named set of `CheckDefinition`s with thresholds and alert rules. `ClientId` null = global. |
| **CheckDefinition** | `Id`, `MonitoringTemplateId`, `Type`, `Interval`, `Thresholds`, `AppliesToClass` | Interval from seconds to monthly. |
| **ClientTemplate** | `Id`, `Name`, sites with linked policies and templates | Blueprint used at client creation. Linked, not copied: later changes apply to every client using it; a technician can make an independent copy. |
| **License** | `Id`, `CustomerName`, `Fqdn`, `ManagedEndpointCount`, `ExpiresAt`, `SignedDocument` (encrypted) | One per instance. Verified offline with the Steaan public key baked into the build. |
| **ApiKey** | `Id`, `Name`, `KeyHash`, `Scope` (`read`/`read_write`), `ClientIds?`, `CreatedBy`, `LastUsedAt`, `RevokedAt` | Plaintext shown once at creation. |
| **RemoteSession** | `Id`, `EndpointId`, `TechnicianId`, `Reason`, `StartedAt`, `EndedAt`, `ConsentGiven`, `RecordingRef?` | Every session, whether it connected or not. |
| **CheckResult** | `Time` (ingest), `EndpointId`, `CheckDefinitionId`, `Status`, `Value`, `Payload` | TimescaleDB hypertable, compressed, retention policy. |
| **Alert** | `Id`, `EndpointId`, `CheckDefinitionId`, `Severity`, `State`, `AcknowledgedBy`, timestamps | Deduplicated per endpoint and check. |
| **Job** | `Id`, `EndpointId`, `Type`, `Payload`, `Signature`, `InitiatedBy`, `State`, output | Idempotent by `Id`. |
| **Note** | `Id`, `EndpointId?`, `SiteId?`, `AuthorId`, `Body` (markdown), timestamps | Searchable. |
| **Integration** | `Id`, `Type`, `EncryptedCredentials`, `Status` | Credentials are ciphertext, see §5. |
| **IntegrationMapping** | `IntegrationId`, `ExternalTenantId`, `ClientId` | One external tenant (Action1 organization, Sophos tenant) maps to one client. |
| **User**, **Role** | `Id`, `Email`, `PasswordHash` (Argon2id), `TotpSecret` (encrypted), roles | Roles: admin, technician, read-only. |
| **AuditEntry** | `Time`, `ActorId` (user or API key), `Action`, `TargetType`, `TargetId`, `Details` | Append-only; no update or delete path in code or DB grants. |

## 3. Agents

- **One codebase, Go**: single static binary per platform, no runtime dependencies on the
  endpoint. Targets: Windows (service running as SYSTEM), Linux (systemd), macOS (launchd).
  Proxmox hosts are Debian, so the Linux agent applies, plus optional Proxmox API
  integration for VM inventory.
- **VMware ESXi gets no agent**: monitored agentless through the vCenter/ESXi API from the
  workers, like every other integration.
- Responsibilities: run local checks, stream results, execute signed jobs (scripts,
  installers such as the Action1 agent), send heartbeats, report inventory, self-update,
  serve remote control sessions (screen capture, input injection, clipboard sync).
- **Agent-only tier**: the agent still connects, heartbeats and reports inventory, but the
  gateway refuses to deliver jobs, policies, check definitions or remote control sessions to
  it. The agent itself also refuses them, so a bug on the server side cannot promote an
  endpoint by accident.
- **Scheduler lives in the agent** so checks run offline; results are queued on disk
  (bounded, oldest-first eviction) and flushed on reconnect. Every interval gets per-agent
  random jitter so 10,000 agents never fire in the same second.
- Reconnect with exponential backoff plus jitter.
- Wire format: length-prefixed protobuf over the WebSocket, batched. Never one HTTP request
  per check result.
- Endpoint class detection: Windows Server / Linux without a desktop session / ESXi guests
  flagged as servers → `server`; everything else → `workstation`. The technician can override.

## 4. Key flows

**Enrollment.** Technician creates an enrollment token for a site (expiring, revocable,
optionally single-use). The UI produces a one-line install command that embeds the instance
FQDN and the token. The agent installs, generates a key pair, sends a certificate signing
request with the token to the gateway, receives a per-agent certificate, and connects with
mTLS from then on. The token is marked used; the endpoint appears in the site as
**agent-only**.

**Switching an endpoint to managed.** Technician flips the tier on the endpoint page or in
bulk → web checks the license pool (`ManagedEndpointCount` minus endpoints already managed)
→ refuses with a clear message when the pool is empty → otherwise sets `Tier = managed`,
pushes the site policy and check definitions to the agent, writes an audit entry. Switching
back to agent-only removes policy and checks from the agent, closes its open alerts as
"endpoint no longer managed" and frees the license.

**Check result.** Agent runs a check on its schedule → batches results → gateway validates
the client certificate and stamps ingest time → pushes to the valkey queue → worker writes
the hypertable row, evaluates thresholds, opens/updates/closes alerts → publishes the
status change over pub/sub → the UI updates without polling.

**Job.** Technician starts a script or patch action → web writes a `Job` with initiator,
targets and payload, signs it with the agent signing key (see §5) → gateway pushes it to
online managed agents, queues it for offline ones → agent verifies the signature, dedupes on
`Job.Id`, executes, streams output → worker stores the result → audit entry written.

**Remote control session.** Technician clicks Remote control on a managed endpoint and
enters a reason → web writes a `RemoteSession`, issues a short-lived session token bound to
technician, endpoint and session id → gateway tells the agent to start a session; if the
policy requires consent, the agent shows a prompt to the endpoint user and waits → browser
and agent set up an end-to-end encrypted channel through the gateway relay (transport:
*decision pending*, WebRTC with the gateway as TURN vs. WebSocket relay) → the agent streams
screen frames, the browser sends keyboard, mouse and clipboard events, the agent sends
clipboard changes back → a banner on the endpoint names the technician for the whole
session → on end, drop or timeout the agent releases all pressed keys, the session row is
closed and the audit entry completed. The gateway only relays; it never holds session keys.

**Client creation from template.** Technician picks a client template, enters code and
name → the sites in the template are created under the new client with their policy and
monitoring template links (references to the shared templates, not copies) → one audit
entry describes the whole operation.

**Integration poll.** Worker runs each integration on its own schedule with timeout, retry
and circuit breaker → results are translated into the same check/alert model as agent
data → hypervisor hosts and VMs that exist only in Proxmox or vCenter are created with
`Source = integration` under the mapped client and site.

**API call.** Client sends `Authorization: Bearer <api key>` → web hashes the key, looks it
up, checks revocation, scope and client restriction → applies the same role and license
checks as the UI → rate limiter per key → response with keyset pagination → audit entry
for every write and for reads of personal data.

## 5. Security architecture

### Key hierarchy

```
root key (KEK)                  outside the DB: Docker secret file, 0600, generated by install.sh, one per instance
  └─ wraps → data keys (DEKs)   in the DB, one per purpose, wrapped by the KEK
                └─ encrypt →    integration credentials, SMTP, Action1, TOTP seeds,
                                enrollment tokens, agent signing key, license document,
                                internal CA key, backups key
```

- Algorithms: AES-256-GCM for data, key wrapping with AES-256-GCM as well, ed25519 for
  signing agent binaries, jobs and licenses, Argon2id for user passwords, SHA-256 for API
  key hashes, mTLS with per-agent certificates issued by an internal CA whose private key
  is itself a DEK-encrypted secret.
- Rotation: DEK rotation re-encrypts the data of that purpose in the background; root key
  rotation rewraps all DEKs, which takes seconds. Both are audited.
- Decided: the root key lives in a Docker secret file on the VPS, generated by `install.sh`,
  root-owned, mode 0600, with an offline backup made during the key ceremony.

### Licensing

- Steaan holds a license signing key pair (ed25519). The public key is compiled into the
  server build; the private key never leaves Steaan and has its own key ceremony.
- A license document contains customer name, instance FQDN, managed endpoint count, issue
  and expiry dates, and a serial. The instance verifies the signature and the FQDN match
  offline on every start and once a day. No call home is required to keep working.
- Agent-only endpoints are unlimited; the license only counts managed endpoints.
- Delivery: in v1 the document is loaded in Settings by hand. Later a **Steaan management
  server** issues, renews and revokes licenses and instances fetch them over an API (the
  instance polls; the management server never needs inbound access to an instance). The
  document format and the verification code are the same in both cases, so the management
  server replaces only the manual upload step.
- Expiry: 14 days before expiry the dashboard warns. After expiry every endpoint behaves as
  agent-only until a new license is loaded. Nothing is deleted.
- Tier enforcement lives in three places: the domain layer (every operation checks the
  endpoint tier), the gateway (never delivers managed-only messages to an agent-only
  endpoint) and the agent (refuses them anyway). Tests cover all three.

### Remote control

- Session token: short-lived, single session, bound to technician, endpoint and session id,
  signed by the web container, verified by gateway and agent.
- End-to-end encryption between browser and agent; the gateway relays ciphertext only.
- Visible on the endpoint: banner with technician name for the entire session, consent
  prompt when the policy demands it, session recording when the policy demands it
  (recordings are encrypted with a DEK and follow the retention policy).
- Agent runs as SYSTEM on Windows to reach the console session, the login screen and UAC
  secure desktop; the same privilege is why the session token and signature checks are
  never optional.
- Clipboard sync is text-only in v1 and can be disabled per policy.

### Key ceremony (to be written before 0.1.0)

Generation of the root key and the internal CA on first install of an instance, where the
offline backup of the root key goes (it must exist, or a lost VPS means lost data), who
holds it, the Steaan license signing key handling, and the rehearsed restore of an instance
backup onto a fresh VPS using that key.

### Other controls

- Containers non-root, read-only filesystems where possible, no Docker socket in app
  containers, egress from agents limited to the gateway host.
- Instances on one VPS: separate Compose projects, networks, volumes and secret directories;
  the host Caddy is the only shared component and holds no instance secrets.
- Every privileged action writes an `AuditEntry`; the table has no update or delete path.
- Per-endpoint rate limiting, strict input validation, parameterized queries only, CSP
  without unsafe-inline, TOTP 2FA for every user, API keys hashed and scoped.
- Cross-client isolation is enforced in the data layer (global query filters on `ClientId`)
  and proven by tests that attempt cross-client reads.

## 6. Public API

- Base path `/api/v1` on the instance FQDN. JSON only. OpenAPI 3 document at
  `/api/v1/openapi.json`, generated from the code and verified by a test that fails when the
  document and the controllers diverge.
- Authentication: `Authorization: Bearer <api key>`. Keys are created in Settings, scoped
  `read` or `read_write`, optionally restricted to a list of clients, revocable, shown once.
- Resources in v1: clients, sites, endpoints (with inventory, status, tier), alerts, checks
  and results, jobs, patch compliance, notes, audit entries (read-only), license usage.
- Conventions: keyset pagination (`cursor`, `limit`), ISO 8601 UTC timestamps, `ETag` on
  single resources, problem+json errors with cause and next step, rate limit headers.
- Field names are stable from 1.0.0; breaking changes mean `/api/v2`.
- The Blazor UI calls the same application services as the API, so a capability missing in
  the API is a bug, not a design choice.

## 7. Install and update flow

`install.sh` is the only supported way to install or update the server. It operates per
instance; a VPS can hold several.

```
curl -fsSL https://get.fleeto.app | sudo bash                       # install prerequisites, then create or update an instance
sudo ./install.sh --fqdn rmm.customer.example                        # new instance for this FQDN (asks when omitted)
sudo ./install.sh --fqdn rmm.customer.example --version 0.1.0        # pin a version for that instance
sudo ./install.sh --fqdn rmm.customer.example --check                # show installed vs. latest, change nothing
sudo ./install.sh --list                                             # instances on this VPS
```

First run on a VPS: install Docker → install the host-level Caddy with an empty routing
table → create `/opt/fleetify/`.

New instance: ask for or take the FQDN → check that it resolves to this VPS (fail early
with the DNS record to create) → derive the instance name from the FQDN → create
`/opt/fleetify/<instance>/` with Compose files → generate root key and DB password into
`/opt/fleetify/<instance>/secrets/` (root, 0600, excluded from any backup that leaves the
box unencrypted) → pull images → run migrations → start stack → add the FQDN route to Caddy
(TLS certificate is issued on first request) → print URL and the one-time first-admin
setup link.

Update run for an instance: detect installed version → back up the database → pull the
requested images → run migrations → restart the stack → health check → on failure roll back
to the previous images (the backup remains for a manual restore if a migration has to be
reverted). `install.sh --all` updates every instance on the VPS in turn.

Agents self-update from their instance, staged by update ring from the site policy, and
verify the ed25519 signature on every binary before swapping it in.

## 8. Repository layout (planned)

```
/CLAUDE.md                 rules, priorities, conventions, product model
/MD-Files/                 the rest of the documentation set (this folder)
/src/Fleetify.Web/         Blazor Server UI + public REST API
/src/Fleetify.Core/        domain model, services, interfaces, license and tier checks
/src/Fleetify.Infrastructure/  EF Core, TimescaleDB, valkey, integrations
/src/Fleetify.Workers/     background jobs
/src/Fleetify.Gateway/     agent endpoint and remote control relay (language decision pending)
/agent/                    Go agent (one module, per-platform builds, remote control per platform)
/tests/                    unit, integration, cross-client, tier enforcement, load test (Fleetify.LoadTest)
/deploy/                   Compose templates, host Caddyfile, install.sh
/.github/workflows/        CI: build, test, vulnerability scan, secret scan, branding grep
```

## 9. Integrations

One interface (`IIntegration`) per connector type: `TestConnection`, `Poll`, and for
Action1 the additional `ListMissingUpdates` and `Deploy` operations. Each connector runs in
the workers with its own schedule, timeout, retry with backoff and circuit breaker. An
integration being down never degrades agent-based monitoring.

| Integration | Data in | Actions out |
|---|---|---|
| Action1 | Patch compliance, missing updates, deployment status per endpoint | Start deployment, install Action1 agent via signed job |
| Sophos Central | Endpoint health, detections | None in v1 |
| Veeam | Backup job status | None in v1 |
| Proxmox VE | Host and VM inventory and health | None in v1 |
| VMware vCenter | Host and VM inventory and health | None in v1 |
