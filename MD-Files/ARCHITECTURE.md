# Fleeto — Architecture

> Technical reference for Fleeto (internal: Fleetify). Rules and priorities live in
> `CLAUDE.md` in the repository root; this file describes how the system is put together.
> Status: 0.0.x and 0.1.0 implemented (agent enrollment, gateway, signer, workers, web UI,
> licensing, backups); 0.2.0 in progress (maintenance mode, the check catalog, services in the inventory, the
> check history, notification routing, webhooks, email through Microsoft Graph, expiring credential warnings and
> policy maintenance windows, certificate recovery and enrolling again, the script library and jobs implemented); later sections (remote control, public API, integrations) are design. Sections marked *decision pending* point to the open decisions in `CLAUDE.md`.

## 1. Deployment topology

Every Fleeto **customer** (an IT team or MSP) gets its own **instance**: a separate Docker
Compose project with its own database, secrets, root key, licenses, users and FQDN. Several
instances can share one Ubuntu VPS. One host-level Caddy routes each FQDN to its instance.
Instances share nothing else: separate Docker networks, volumes and secret files.

```
                     ┌──────────────── VPS ─────────────────────────────────────────────────┐
                     │  caddy (host level, port 443)                                         │
                     │    HTTPS for rmm.<fqdn>: TLS terminated, routed by FQDN               │
                     │    agents.<fqdn>: TLS passthrough by SNI, never terminated            │
                     │     │ customer A                        │ customer B                  │
                     │     ▼                                   ▼                             │
[browser, API] ───►  │  ┌─ instance A ───────────────┐   ┌─ instance B ───────────────┐     │
                     │  │ fleetify-web               │   │ fleetify-web               │     │
[agents] ──mTLS──►   │  │ fleetify-gateway           │   │ fleetify-gateway           │     │
                     │  │ fleetify-signer            │   │ fleetify-signer            │     │
                     │  │ fleetify-workers           │   │ fleetify-workers           │     │
                     │  │ postgres + TimescaleDB     │   │ postgres + TimescaleDB     │     │
                     │  └────────────────────────────┘   └────────────────────────────┘     │
                     └──────────────────────────────────────────────────────────────────────┘
                                         │ encrypted backups (write-only)
                                         ▼
                              off-VPS object storage (EU)
```

Inside one instance:

```
[agents] --mTLS WebSocket (SNI passthrough)--> [fleetify-gateway] --batch write, then ack--> [postgres + TimescaleDB]
                                                  │        │                                        ^      ^   ^
                                                  │        └─ NOTIFY (postgres) ─> [fleetify-workers] ─┘       │   │
                                                  └── remote control relay (ciphertext only)                   │   │
[integrations: Action1, Sophos, Veeam, Proxmox, vCenter] --> [poller workers] ─────────────────────────────────┘   │
                                                                                                                   │
[browser, API clients] --HTTPS--> [caddy] --> [fleetify-web: Blazor Server UI + public REST API] <─────────────────┤
                                                     ^                                                             │
                                                     +-- LISTEN/NOTIFY (postgres) for live status                  │
                                                                                                                   │
                                              [fleetify-signer] ── LISTEN/NOTIFY, no listening port ───────────────┘
```

| Container | Scope | Role | Notes |
|---|---|---|---|
| **caddy** | VPS | Reverse proxy, automatic TLS | One per VPS, built with the layer4 module. Terminates TLS for the UI and API of every instance, so it holds the TLS private keys and ACME account for every FQDN on the VPS (see §5). Agent traffic to `agents.<fqdn>` is passed through by SNI to the instance gateway and never decrypted, so mTLS stays end to end between agent and gateway. In front of the passed-through connection Caddy sends a PROXY protocol v2 header with the agent's address (see §5, Other controls). |
| **fleetify-web** | instance | Blazor Server UI and public REST API (.NET, MudBlazor) | Follows the Migrify project layout and conventions. Cannot sign anything an agent executes. |
| **fleetify-gateway** | instance | Agent connection endpoint and remote control relay | Persistent WebSocket over mTLS for online state, command push and check results. Checks certificate revocation on every connection. Acks agent data only after it is written to Postgres. Target: 10,000 concurrent connections on modest hardware. Language: .NET (decided in 0.1.0). |
| **fleetify-signer** | instance | Signs everything that establishes trust with agents | Holds the instance signing key and the internal CA key, decrypted with its own signer key that no other container mounts. No listening port: it picks up signing requests from the database. Re-checks role, tier, script approval and validity window before signing. See §5. |
| **fleetify-workers** | instance | Background jobs | Check evaluation, alerting, integration pollers, Action1 patch orchestration, retention cleanup, backups, license checks. |
| **postgres** | instance | PostgreSQL 17 + TimescaleDB | The only durable store. Relational data plus hypertables for check results and metrics plus log storage with full-text search. One database role per container with only the grants that container needs. LISTEN/NOTIFY carries cross-container notifications (ids only); every subscriber also catches up from the tables, so a lost notification delays work and never loses it. No Valkey: the signer may only talk to the database, so database notifications are needed anyway. |

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
                │         ├──* Job, Alert, Note, InventorySnapshot, RemoteSession, CheckState, CheckRunRequest
                │         ├──* AgentCertificate
                │         ├──* CheckDefinition (endpoint-only checks)
                │         ├──* EndpointCheckOverride ──> CheckDefinition (of a template)
                │         └──* EndpointMonitoringTemplate ──> MonitoringTemplate
                │
                ├──* SiteMonitoringTemplate ──> MonitoringTemplate 1──* CheckDefinition
                └──* SitePolicy ──────────────> Policy

ClientTemplate 1──* ClientTemplateSite ──* (MonitoringTemplate | Policy) links

Job 1──* JobOutputChunk
Script 1──* ScriptVersion       SigningRequest (signer work queue)
License (one per instance)      ApiKey *──> User
User *──* Role                  Integration 1──* IntegrationMapping ──> Client
AuditEntry (append-only)
```

| Entity | Key fields | Notes |
|---|---|---|
| **Client** | `Id`, `Code` (unique, uppercase), `Name`, `CreatedAt`, `Maintenance` | Tenant boundary inside the instance. Every client-owned table carries its own `ClientId`, denormalized on purpose (see the rule below the table), and every query filters on it. |
| **Site** | `Id`, `ClientId`, `Name`, `Description`, `Maintenance` | Groups endpoints. Holds the link to at most one policy (without one the instance default policy applies) and to any number of monitoring templates. Enrollment tokens belong to a site. |
| **Endpoint** | `Id`, `ClientId`, `SiteId`, `Hostname`, `Class` (`workstation`/`server`), `ClassOverride`, `Tier` (`agent_only`/`managed`), `Os`, `AgentVersion`, `LastSeenAt`, `Source` (`agent`/`integration`), `Maintenance`, `PublicIpAddress?`, `PublicIpSeenAt?` | Endpoints without an agent exist only for hypervisor inventory (ESXi hosts and VMs from vCenter or Proxmox). `Tier` gates every feature server-side. `PublicIpAddress` is the address the gateway saw for the latest agent connection (personal data: only the latest value is kept, deleted with the endpoint). |
| `Maintenance` (on Client, Site, Endpoint) | `MaintenanceStartedAt?`, `MaintenanceEndsAt?`, `MaintenanceStartedByUserId?`, `MaintenanceStartedByName?`, `MaintenanceReason?` | Maintenance mode (0.2.0), stored as nullable columns on each of the three tables. Active while `MaintenanceStartedAt` is set and not in the future and `MaintenanceEndsAt` is null or in the future; ending by hand clears the columns, an end time that passes is left in place and every query compares with the current time. The reason is free text and personal data may appear in it: it is never copied into the audit log. See *Maintenance mode* in §4. |
| **AgentCertificate** | `Id`, `ClientId`, `EndpointId`, `Fingerprint`, `IssuedAt`, `ExpiresAt`, `RevokedAt?`, `RevokedBy?`, `RevokedReason?` | One row per issued certificate, renewals included. The gateway refuses every certificate with `RevokedAt` set. |
| **Policy** | `Id`, `ClientId?`, `Name`, settings, `MaintenanceWindowsJson` | Agent behaviour: intervals, patch behaviour, update ring, script permissions and **script approval required**, remote control rules (consent, recording), maintenance windows. `ClientId` null = global. |
| **MaintenanceWindowOccurrence** | `PolicyId`, `WindowIndex`, `StartsAt`, `EndsAt`, `AppliesTo`, `Name?` | Occurrences of the policy's maintenance windows (0.2.0), stored 8 days ahead (§4, Maintenance windows). Deleted with the policy. |
| **MonitoringTemplate** | `Id`, `ClientId?`, `Name` | Named set of `CheckDefinition`s with thresholds and alert rules. `ClientId` null = global. |
| **CheckDefinition** | `Id`, `ClientId?`, `MonitoringTemplateId?`, `EndpointId?`, `Type`, `Interval`, `Thresholds`, `FailuresBeforeAlert`, `AppliesToClass`, `Enabled` | Interval from seconds to monthly. Owned by exactly one of a monitoring template or one endpoint (check constraint). An endpoint-only check carries the endpoint's `ClientId` (composite foreign key) and runs whatever the endpoint class. |
| **EndpointMonitoringTemplate** | `EndpointId`, `ClientId`, `MonitoringTemplateId`, `CreatedAt`, `CreatedBy?` | An extra monitoring template for one endpoint, on top of those of its site. Global or same-client templates only (constraint trigger). |
| **EndpointCheckOverride** | `EndpointId`, `CheckDefinitionId`, `ClientId`, `Disabled`, `IntervalSeconds?`, `FailuresBeforeAlert?`, `OverrideThresholds`, `WarningThreshold?`, `CriticalThreshold?` | Adjusts one template check for one endpoint. Unset values inherit the template, so the template stays linked. `OverrideThresholds` replaces both thresholds as a pair, so "no threshold" can be an override too. Template checks only (constraint trigger). |
| **CheckState** | `EndpointId`, `CheckDefinitionId`, `Target`, `ClientId`, `Status`, `Value?`, `ConsecutiveNonOk`, `LastResultAt`, `ResetAt?` | Current evaluated state per check and target, maintained by the workers. "Re-run requested" while `ResetAt` is later than `LastResultAt`. States of checks that no longer apply are removed on the hourly sweep. |
| **CheckRunRequest** | `Id`, `ClientId`, `EndpointId`, `CheckDefinitionId`, `Reset`, `RequestedBy`, `RequestedAt`, `ExpiresAt`, `ResetAppliedAt?`, `DeliveredAt?`, `Outcome?` (`expired`, `not_applicable`, `not_managed`) | A technician's "Run now" or "Reset and run" (§4). Kept 7 days. |
| **ClientTemplate** | `Id`, `Name`, sites with linked policies and templates | Blueprint used at client creation. Linked, not copied: later changes apply to every client using it; a technician can make an independent copy. |
| **Script**, **ScriptVersion** | `Id`, `ClientId?`, `Name`, `Description`, `Language` (`PowerShell`, `Batch`, `Shell`, `Bash`), `CurrentVersionId`; version: `ClientId?`, `Number`, `Body`, `Sha256`, `TimeoutSeconds`, `AuthorUserId`, `ApprovedByUserId?`, `ApprovedAt?`, `ApprovedSha256?` | `ClientId` null = global, immutable (trigger); a version carries the client of its script (constraint trigger). Saving a changed body or timeout creates a new version; the language is fixed. A version is approved only when approver and author differ and `ApprovedSha256` equals `Sha256` (check constraint). |
| **Job** | `Id`, `ClientId`, `EndpointId`, `BatchId`, `Type` (`Script`), snapshot of the script (`ScriptId?`, `ScriptVersionId?`, name, version number, `Language`, `ScriptSha256`, `TimeoutSeconds`, `MaxOutputBytes`), `ValidUntil`, `InitiatedByUserId`, `State` (`pending_signature`, `queued`, `running`, `succeeded`, `failed`, `expired`, `refused`, `lost`, `cancelled`), `RefusalReason?`, `Payload`, `Signature`, `SigningKeyId`, `DeliveredAt?`, `StartedAt?`, `CompletedAt?`, `Result?` (`exited`, `timed_out`, `refused`, `failed_to_start`, `interrupted`), `ExitCode?`, `OutputState` (`none`, `receiving`, `complete`, `incomplete`), `OutputTruncated`, per stream announced chunks, bytes and SHA-256 | One row per endpoint. Idempotent by `Id`. Never delivered or executed after `ValidUntil` (at most 7 days after creation, check constraint). `State` describes execution, `OutputState` describes the output; they move independently. The snapshot keeps the history readable after the script changes or is deleted. |
| **JobOutputChunk** | `ClientId`, `JobId`, `Stream` (`stdout`/`stderr`), `Sequence`, `Data`, `ReceivedAt` | Unique on `JobId`, `Stream`, `Sequence`. Protocol in §4, Job output. |
| **SigningRequest** | `Id`, `ClientId?`, `Kind` (`job`, `session_token`, `agent_csr`, `gateway_csr`, `policy`), `SubjectId`, `RequestedBy`, `State`, `RefusalReason?` | Written by web or gateway, processed by the signer. |
| **License** | `Id`, `CustomerName`, `Fqdn`, `ManagedEndpointCount`, `ExpiresAt`, `SignedDocument` (encrypted) | One per instance. Verified offline with the Steaan license public key baked into the build. Grace period of 14 days after `ExpiresAt`. |
| **ApiKey** | `Id`, `Name`, `Prefix`, `KeyHash`, `Scope` (`read`/`read_write`), `ClientIds?`, `CreatedBy`, `LastUsedAt`, `RevokedAt` | Plaintext shown once at creation. Format in §6. |
| **RemoteSession** | `Id`, `ClientId`, `EndpointId`, `TechnicianId`, `Reason`, `StartedAt`, `EndedAt`, `ConsentGiven`, `BrowserKeyFingerprint`, `RecordingRef?` | Every session, whether it connected or not. |
| **CheckResult** | `Time` (ingest), `ClientId`, `EndpointId`, `CheckDefinitionId`, `Status`, `Value`, `Payload` | TimescaleDB hypertable, compressed, retention policy. Deduplicated per endpoint and agent batch sequence number. |
| **Alert** | `Id`, `ClientId`, `EndpointId`, `CheckDefinitionId`, `Severity`, `State`, `AcknowledgedBy`, `HeldUntil?`, `HeldAt?`, `HeldBy?`, timestamps | Deduplicated per endpoint and check. On hold while `HeldUntil` is in the future (§4, Alert hold). |
| **InventorySnapshot** | `EndpointId`, `ClientId`, `ReceivedAt`, `Hash`, hardware facts, `DisksJson`, `NetworkInterfacesJson`, `SoftwareJson`, `ServicesJson` | Latest inventory, one row per endpoint. `ServicesJson` (0.2.0): name, display name, start type and state per service (at most 2,000), used to pick the service of a service check; for a monitoring template the services of the most recent 1,000 inventories of its endpoints are offered. |
| **CheckResultHourly**, **CheckResultDaily** | `EndpointId`, `CheckDefinitionId`, `Target`, `Bucket`, `ClientId`, `MinValue?`, `MaxValue?`, `SumValue`, `ValueCount`, `ErrorCount`, `NoResponseCount` | Check history rollups (0.2.0), maintained by the workers with the evaluation, kept 13 months. Deleted with their endpoint or check. |
| **Note** | `Id`, `ClientId`, `EndpointId`, `AuthorId`, `AuthorName`, `Body` (markdown, at most 20,000 characters), `CreatedAt`, `UpdatedAt`, `EditedAt?` | Endpoints only (site notes were dropped). Managed endpoints only. The author edits, an admin deletes. Searchable from 0.6.0. An external ticket reference follows with the public API. |
| **NotificationChannel** | `Id`, `Name`, `Type` (`Email`, `Webhook`), `Recipients`, `WebhookFormat?` (`Generic`, `Slack`, `Teams`), `WebhookHost?`, `EncryptedWebhook?`, `MinimumSeverity`, `NotifyOnResolve`, `Enabled`, `AllClients` | Instance-wide, admins only. Routing (0.2.0): all clients or the clients in **NotificationChannelClient** (`NotificationChannelId`, `ClientId`, deleted with either side), plus minimum severity and resolves. `EncryptedWebhook` holds URL and signing secret, bound to the channel id (§5). The type cannot change. |
| **OutboxEmail**, **OutboxWebhook** | `Id`, recipient or `NotificationChannelId`, `Category`, content or `Payload`, `Attempts`, `NextAttemptAt`, `SentAt?`, `LastError?` | Written in the transaction that changes the alert; delivered by the workers (§4, Alert notifications). Sent rows kept 30 days, given up rows 90 days. |
| **Integration** | `Id`, `Type`, `EncryptedCredentials`, `Status` | Credentials are ciphertext, see §5. |
| **IntegrationMapping** | `IntegrationId`, `ExternalTenantId`, `ClientId` | One external tenant (Action1 organization, Sophos tenant) maps to one client. |
| **User**, **Role** | `Id`, `Email`, `PasswordHash` (Argon2id), `TotpSecret` (encrypted), roles | Roles: admin, technician, read-only. |
| **AuditEntry** | `Time`, `ClientId?`, `ActorId` (user or API key), `Action`, `TargetType`, `TargetId`, `Details` | Append-only; no update or delete path in code or DB grants. `ClientId` null for instance-wide actions. |

**Client scoping rule.** Every table that holds client-owned data carries its own
`ClientId` column, even when the client could be derived through a parent (Endpoint through
Site, Job through Endpoint). This is deliberate defence in depth for an RMM: a query that
forgets a join still cannot leak across clients, and the global query filter works on every
table without joins. The redundant column cannot drift: it is part of a composite foreign
key to the parent (for example `Endpoint (SiteId, ClientId)` references
`Site (Id, ClientId)`, `Job (EndpointId, ClientId)` references `Endpoint (Id, ClientId)`),
so the database rejects a row whose `ClientId` differs from its parent's. Endpoints never
move between clients; moving one means enrolling it again.

Three kinds of tables:

- **Client-owned** (`ClientId` required): Site, Endpoint, AgentCertificate, Job,
  JobOutputChunk, Alert, Note, InventorySnapshot, RemoteSession, CheckResult, CheckState,
  CheckRunRequest, EndpointMonitoringTemplate, EndpointCheckOverride, CheckResultHourly, CheckResultDaily,
  IntegrationMapping. Consistency by composite foreign key, as above. One documented
  exception: CheckResult (the hypertable) has no foreign keys, for ingest speed and because
  compressed chunks and cascading deletes do not mix well. The gateway takes its ClientId from
  the endpoint row, and the workers purge results of deleted endpoints.
- **Global or client-specific** (`ClientId` nullable, null = global): Policy,
  MonitoringTemplate, CheckDefinition, Script, ScriptVersion. A child always carries the
  same `ClientId` as its parent (a CheckDefinition that of its MonitoringTemplate, a
  ScriptVersion that of its Script), so a client-specific check or script is filtered like
  any other client data. PostgreSQL does not check a composite foreign key when one of its
  columns is null, so for these tables a constraint trigger enforces that child and parent
  `ClientId` are equal, null included. Tests cover both directions. An endpoint-only
  CheckDefinition has no template parent; its `ClientId` is required and kept equal to the
  endpoint's by a composite foreign key.
- **Instance-wide** (no `ClientId`): users, roles, API keys, client templates, license,
  integrations, and SigningRequest and AuditEntry rows that concern no client (their
  `ClientId` is null).

## 3. Agents

- **One codebase, Go**: single static binary per platform, no runtime dependencies on the
  endpoint. Targets: Windows (service running as SYSTEM), Linux (systemd), macOS (launchd).
  Proxmox hosts are Debian, so the Linux agent applies, plus optional Proxmox API
  integration for VM inventory.
- **VMware ESXi gets no agent**: monitored agentless through the vCenter/ESXi API from the
  workers, like every other integration.
- **Check catalog (0.2.0).** Every check type is described once in `CheckCatalog` (Core): label, platforms,
  parameters with their validation, how the value is judged (flag, higher or lower is worse, reachability)
  and its unit. Validation, evaluation, alert titles, the signed configuration and the check dialog all follow
  it. Types: CPU, memory, free disk space, service, uptime, ping, TCP port, HTTP(S) URL (with a second result
  for the certificate expiry), process, pending restart (Windows, Linux), file or folder (exists, missing,
  size, age), certificate expiry (Windows store or file), event log (Windows) and antivirus and firewall
  (Windows). Parameters end up in a signed configuration that runs as SYSTEM or root, so the server validates
  them strictly (no URL credentials, no option-like host names, no XPath or path tricks) and the agent checks
  them again. Network checks report -1 for no answer, which the server treats as critical; the agent never
  sends a status. Event log checks count events and never send messages (personal data). A check of a type
  an older agent does not know reports an error, shown as "could not run".
- **Script check (0.2.0).** A check that runs a library script; the exit code is the value (0 OK, 1 warning,
  any other code critical, judged by the server) and the first line of output, at most 200 characters, the
  detail. The check stores the script id and the script's language, copied when the check is saved (a
  script's language never changes), so the platform rule stays a rule on the check in C# and SQL. A global
  monitoring template only uses global scripts, a client's template or endpoint also scripts of that client,
  and a script that checks use cannot be deleted. The signer puts the script into the signed configuration
  (`CheckSpec.script`): the current version, or where the endpoint's policy requires approval the newest
  version approved by a current admin with two-factor authentication who did not write it, so a check keeps
  running the approved body while a change waits for approval. A script of another client, a deleted script,
  a policy without an approved version or more than 1 MiB of scripts in one configuration sends the check
  without a script and with the reason, which the agent reports as "could not run". A new version, an
  approval, a rename or a change to an approver's roles or two-factor authentication is a configuration
  change with scope `Script`: every managed endpoint with a check using the script gets a new configuration.
  The agent verifies the body hash and language, runs at most two script checks at a time with a timeout of
  the version's timeout, at most 5 minutes and the interval (at least 60 seconds), in a protected directory it
  removes afterwards.
- Responsibilities: run local checks, stream results, execute signed jobs (scripts,
  installers such as the Action1 agent), send heartbeats, report inventory, self-update,
  serve remote control sessions (screen capture, input injection, clipboard sync).
- **Trust anchors.** The agent trusts exactly three things, and nothing it receives over
  the network can add a fourth:
  - the **Steaan release public keys** (current and one standby), compiled into the binary:
    a self-update is installed only with a valid release signature;
  - the **instance CA certificate**, pinned at enrollment: the gateway must present a
    certificate from it;
  - the **instance signing public key**, pinned at enrollment: jobs, policies, check
    definitions and remote control session tokens are accepted only with a valid instance
    signature. A rotation to a new instance signing key must be signed by the old one.
- **Agent identity**: the private key is generated on the endpoint and never leaves it.
  Where a TPM is available the key is non-exportable in the TPM, which also stops a cloned
  VM image from carrying a working identity; elsewhere it is a file readable only by
  SYSTEM or root. Certificates live 90 days and renew automatically over the existing mTLS
  connection.
- **Agent-only tier**: the agent still connects, heartbeats and reports inventory, but the
  gateway refuses to deliver jobs, policies, check definitions or remote control sessions to
  it. The agent itself also refuses them, so a bug on the server side cannot promote an
  endpoint by accident.
- **Scheduler lives in the agent** so checks run offline; results are queued on disk
  (bounded, oldest-first eviction) and flushed on reconnect. A batch is removed from disk
  only after the gateway acknowledges its sequence number. Every interval gets per-agent
  random jitter so 10,000 agents never fire in the same second.
- **Run now.** The server can ask the agent to run checks outside their schedule
  (`RunChecksNow`, §4). The message is not signed: it can only start checks of the applied,
  verified signed configuration and never add or change one, so a hostile gateway could at
  most cause extra runs. The agent bounds that: one manual run per check per 30 seconds, 20
  per minute in total, 100 ids per message; unknown ids and agent-only configurations start
  nothing. `InventoryRequest` follows the same reasoning.
- **Jobs (0.2.0).** Signed jobs are verified and run by `internal/jobs`. Each job has a directory
  in the protected state directory (`jobs/<id>`) holding the signed job, a started marker, the
  output chunks and the completion; files are removed as their acks arrive, the directory when the
  completion is acknowledged. Leftovers older than 7 days are removed, and at most 1,000 job
  directories exist, so a flood of refused jobs cannot fill the disk. Scripts are written next to
  the job (PowerShell with a UTF-8 byte order mark, Batch as `.cmd`) and run with
  `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`, `cmd.exe /d /s /c`,
  `/bin/sh` or `/bin/bash`. Enrolling again removes the jobs of the previous enrollment.
- **Watchdog service (0.2.0, design).** A second Windows service, `fleetify-watchdog`, a small
  separate binary from the same Go codebase, running as SYSTEM:
  - It has its own key (TPM-backed where available) and its own 90-day certificate for the same
    endpoint, marked with the role *watchdog*, issued at install and renewed like the agent's.
    The gateway keeps one session per certificate, so agent and watchdog are both connected
    at all times without triggering the duplicate identity rule; revoking or deleting the
    endpoint revokes both certificates.
  - The watchdog watches the agent service and restarts it after a crash or stop; the agent
    watches the watchdog the same way. Each reports the other's state in its heartbeat.
  - When the watchdog is online but the agent service is not running and cannot be started,
    the endpoint gets the alert "Agent service stopped" (separate from the offline alert, which
    means both are gone). When the agent reports the watchdog stopped and cannot restart it,
    the alert is "Watchdog stopped". Both alerts are for managed endpoints only.
  - The watchdog installs agent updates and rolls back to the previous binary when the new one
    does not come up; both binaries are installed only with a valid Steaan release signature.
- **Remote terminal (0.3.0, design).** An interactive terminal as SYSTEM (cmd and PowerShell on
  Windows, sh on Linux and macOS), served by the watchdog so it also works when the agent is
  broken. Same trust model as remote control: a single-use session token from the signer bound
  to the technician, endpoint and the browser's ephemeral key, end-to-end encryption between
  browser and watchdog, the gateway relays ciphertext only. Admins and technicians, managed
  endpoints only, always available (no policy switch). Every session is an audit entry
  (technician, endpoint, start, end); a transcript is kept when the policy records remote
  control sessions. See §5 for the accepted risk towards script approval.
- Reconnect with exponential backoff plus jitter.
- Wire format: protobuf over the WebSocket, one message per binary WebSocket frame (the frame is
  the length prefix), results batched. Never one HTTP request per check result. Contract:
  `src/Fleetify.Protocol/Protos/agent.proto`.
- Endpoint class detection: Windows Server / Linux without a desktop session / ESXi guests
  flagged as servers → `server`; everything else → `workstation`. The technician can override.

## 4. Key flows

**Enrollment.** Technician creates an enrollment token for a site (expiring, revocable,
optionally single-use, stored hashed). The UI produces a one-line install command that
embeds the instance FQDN, the token and the SHA-256 fingerprint of the instance CA
certificate. The agent installs and generates its key pair → fetches `GET /v1/ca` from
`agents.<fqdn>` without sending anything secret and keeps only the CA certificate whose SHA-256
equals the install fingerprint (TLS stacks leave a self-signed root out of the handshake, so the
CA cannot come from the chain) → opens a new connection verified normally against that CA as
the only root and for the agent host name, and refuses to continue otherwise
→ sends a certificate signing request with the token → gateway checks the token hash and
writes a `SigningRequest` → signer checks the token again, issues a 90-day certificate from
the internal CA and records an `AgentCertificate` → the gateway returns the certificate, the
instance CA certificate and the instance signing public key over the same TLS connection;
the response needs no signature of its own, because that connection is already
authenticated by a gateway certificate validated against the pinned CA fingerprint → the
agent pins the CA certificate and the signing public key and uses mTLS from then on. The token
is marked used; the endpoint appears in the site as **agent-only**. The only first-use
trust is the install command itself, which the technician copies from the web UI over
HTTPS; tokens expire and are single-use by default to limit a leaked command.

**Certificate renewal.** When two thirds of its lifetime has passed, the agent sends a new
CSR over its existing mTLS connection → signer issues a new certificate only if the current
one is not revoked → the old one expires on its own.

**Certificate recovery (0.2.0).** An agent that was offline past its certificate's end date (a laptop
in a drawer) notices the expiry on its own clock, or gets a 401 with `Fleeto-Certificate: expired` from
`/v1/connect` → it posts a CSR for the same key to `POST /v1/recover` over mTLS with the expired
certificate → the TLS handshake accepts a client certificate whose only chain problem is the leaf's own
validity period (never another CA, never an expired CA); every path decides again afterwards: a session
still needs a valid certificate → the gateway accepts the request only for a certificate on the allow
list's expired set (never revoked, expired at most 365 days ago), chained to an instance CA at a moment it
was valid, with the CSR key equal to the key the handshake proved → the signer checks on its own that the
key belongs to the endpoint's most recently issued certificate, that it was never revoked, has expired and
not longer than 365 days ago, then issues a new 90-day certificate and writes `certificate.recovered` →
the agent stores it and connects. A refused recovery keeps the old certificate and retries hourly.
Revoking the agent still stops a lost or stolen device, and an older copy of the identity can never come
back once a newer certificate exists.

**Enrolling again (0.2.0).** For an agent beyond recovery (more than a year offline, revoked by
mistake, a reinstalled machine) a technician chooses Enroll again on the endpoint → web creates a
single-use enrollment token bound to that endpoint, valid one day, and shows the install command once →
the agent enrolls with it (an agent whose certificate expired or was revoked may enroll again without
uninstalling) → the signer takes over the existing endpoint instead of creating one: every earlier
certificate is revoked (live sessions drop), the batch sequences are cleared because the new agent state
counts from 1, host name and OS facts are refreshed, and tier, site, checks, alerts, notes and history
stay → `endpoint.enrolled_again` in the audit log. Decided while building: the technician chooses the
endpoint; matching an unknown agent to an endpoint by key or state was left out, because an explicit,
audited choice cannot attach a machine to the wrong endpoint.

**Endpoint removal and agent revocation.** Technician deletes an endpoint or clicks Revoke
agent (for a stolen laptop, a decommissioned machine, a suspected clone) → `RevokedAt` is
set on all of its certificates and an audit entry is written → the revocation is published
as a database notification and the gateway closes the live connection at once → every new TLS
handshake checks the allow list: a certificate is accepted only when it was issued, is not
revoked and has not expired, so a deleted endpoint (whose certificate rows cascade away) can
never reconnect. The gateway keeps the allow list in memory, loaded from the database at start,
updated on notification and fully reloaded every minute; a gateway that has not loaded it
accepts no agent connections. A revoked agent cannot renew and has to enroll
again with a new token.

**Duplicate agent identity.** A second connection with a certificate that already has a
live connection is refused → the endpoint gets an alert "Duplicate agent identity" → the
technician revokes the certificate and enrolls the copies again, each with its own
identity.

**Switching an endpoint to managed.** Technician switches the tier from the right-click menu of
the endpoint list, for one endpoint or in bulk → web checks the license pool (`ManagedEndpointCount` minus endpoints already managed)
→ refuses with a clear message when the pool is empty → otherwise sets `Tier = managed`,
requests signatures for the site policy and check definitions, pushes them to the agent,
writes an audit entry. License allocation is serialized: the pool check and the tier change
run in one transaction that first takes a transaction-scoped advisory lock
(`pg_advisory_xact_lock`), so two concurrent requests can never both take the last free license. A bulk
switch is all or nothing: when the pool is too small, nothing changes and the message says
how many licenses are missing. A concurrency test proves it. Switching back to agent-only removes policy and checks from the
agent, closes its open alerts as "endpoint no longer managed" and frees the license.

**Check result.** Agent runs a check on its schedule → batches results with a per-agent
sequence number → gateway validates the client certificate (revocation included) and stamps
ingest time → writes the batch to the hypertable in one transaction, deduplicated on
endpoint and sequence number → acknowledges the sequence number → agent removes the batch
from disk → gateway raises a database notification with the endpoint id → worker evaluates
thresholds, opens/updates/closes alerts → notifies the status change → the UI updates without
polling. Workers keep a cursor per endpoint (result ids are monotonic per endpoint because an
agent sends one batch at a time, but not globally in commit order) and sweep for endpoints with
newer results, so a lost notification makes alerts late and loses nothing. When Postgres is down, the gateway does not
acknowledge and agents keep buffering on disk.

**Checks of an endpoint.** One rule decides which checks run on an endpoint, implemented once
in C# (`EffectiveChecks`, used by the configuration builder in the signer, the check evaluation
in the workers and the Checks tab in web) with a SQL twin for set-based statements
(`EffectiveCheckResolver.AppliesSql`); a test proves both agree:
- a template check applies when it is enabled, its template is linked to the endpoint's site or
  to the endpoint itself, it matches the endpoint class, and no override disables it on the
  endpoint;
- an endpoint-only check applies when it is enabled;
- either kind applies only when its type runs on the endpoint's platform (`CheckCatalog.IsSupported`, SQL
  twin `CheckCatalog.PlatformSql`), so a Windows event log check in a template linked to a mixed site never
  reaches a Linux endpoint;
- overrides replace the interval (signed into the configuration), thresholds and failures before
  alert (applied by the workers) for that endpoint only.
Tier is applied on top by each caller. Every change (link, override, endpoint check) writes a
configuration change event for the endpoint; the signer re-signs only when the content changed.
Alerts of checks that stop applying resolve on the next sweep. The Checks tab lists every
applying check straight away, as "Not run yet" until its first result.

**Run now and reset.** A technician clicks Run now or Reset and run on a check of a managed
endpoint → web checks role, tier and that the check applies, refuses a second request for the
same check within 30 seconds and more than 6 requests per user per endpoint per minute, writes a
`CheckRunRequest` (expires after 10 minutes) and an audit entry → a database trigger notifies
workers and gateway →
- for a reset, the workers take the same per-endpoint advisory lock as check evaluation, resolve
  the open alerts of the check ("Reset by …"), set its states to Unknown without a value, zero
  the failure count and stamp `ResetAt`, then mark the reset applied (notifying the gateway
  again). The state shows "Re-run requested", never a made-up OK. Results ingested before
  `ResetAt` are ignored by the evaluation, so a result already on its way cannot overwrite the
  reset; the first newer result sets the real state, and an alert needs the configured number of
  failures again;
- the gateway delivers the request to the live agent once (marked delivered in the database; at
  session start and on the 5-minute catch-up as well), only to managed sessions and, for a
  reset, only after the reset was applied; an offline agent receives it if it connects before
  the request expires, otherwise the check runs at its next interval;
- the agent runs the check within its limits (§3) and the result follows the normal path.

**Alert hold.** A technician puts an unresolved alert on hold until a time (1 hour, 4 hours,
24 hours or a chosen time, at most 7 days) → `HeldUntil` is set and an audit entry written →
while the hold lasts, the alert is left out of open alert lists and counts (dashboard, clients
panel, endpoint list) and shown under the "On hold" filter; the notification service sends no
escalation or resolve email for it; the alert itself stays real and still escalates and resolves
with its check → a technician can end the hold early (audit entry) → the workers clear holds
whose time passed every 30 seconds and, for an alert that is still unresolved, send one "still
open after hold" email. Pages already treat a hold in the past as ended. The UI never calls this
snooze or mute (branding §6). Maintenance mode (below) is the tool for planned work on a whole
endpoint, site or client.

**Alert notifications (0.2.0).** An alert opens, escalates, resolves or leaves its hold → in the same
transaction the notification service picks every channel that receives it: enabled, the alert's client
among the channel's clients (or all clients), the severity at least the channel's minimum, and for a resolve
only channels that want resolves (`NotificationRouting`, decided 2026-09-15: routing rules, no time-based
escalation) → it adds one `OutboxEmail` per recipient and one `OutboxWebhook` per webhook channel, with the
request body built there and stored → a database trigger wakes the delivery loops, which also poll every
30 seconds. Both outboxes claim a batch by moving its next attempt ten minutes ahead, retry after 1, 5 and
15 minutes, 1 hour and then every 6 hours, give up after 10 attempts (at once for a permanent refusal such
as an invalid recipient or an HTTP 4xx other than 408, 425 and 429) and keep the last error. Email has one
circuit breaker; webhooks have one per channel, so a receiver that is down does not hold up the others. A
webhook is a POST with `X-Fleeto-Event`, `X-Fleeto-Delivery` (the outbox id, the same on every attempt, so a
receiver can drop duplicates) and, for the generic format, `X-Fleeto-Signature: t=<unix seconds>,v1=<hex>`,
HMAC-SHA256 over `<t>.<body>` with the channel's signing secret. Slack gets an incoming webhook message and
Teams an adaptive card for a workflow; their URL is the secret. Notifications carry hostnames, client and
site names, the alert title and detail and a link, never check output. A delivery whose channel was
disabled or deleted before it went out is dropped. The notification channels page shows the last delivery
per webhook channel and can send a test message.

**Email delivery (0.2.0).** Settings, Email chooses SMTP or Microsoft Graph. Graph uses an app registration
with the `Mail.Send` application permission, limited to the sending mailbox by an Exchange application
access policy, and the client credentials flow: a client secret (its end date entered with it, since Entra
ID does not reveal it) or, recommended, a certificate Fleeto creates (RSA 3072, two years). The certificate
waits as pending until the admin has downloaded its public part, uploaded it to the app registration and
switched to it, so sending never stops during a renewal; the private key never leaves the instance. The
workers request one token per delivery pass and send HTML through `sendMail` without saving to Sent Items.
While the Graph credential has expired and SMTP is configured as well, email goes through SMTP, so the
warning about the expired credential still arrives. Graph can only be chosen when complete and must stay
complete while chosen.

**Expiring credentials (0.2.0).** Every stored credential in use that has an end date is listed by
`ExpiringCredentials` (now the Graph client secret or certificate; Entra ID sign-in and integrations add
theirs later). From 30 days before the end date the dashboard shows a warning to every user (the next step
for admins), red after expiry with what stopped and whether a fallback is in use. The workers email the
admins once per stage (30, 14, 7 and 1 days left, expired; a skipped stage sends only the most urgent) and
remember the stage per credential together with its end date, in the same transaction as the emails, so a
renewed credential starts over and nothing is sent twice.

**Check history (0.2.0).** Per check of an endpoint, an overview of the last hour, day, week, month
and year (History in the check menu, or a click on the time of the last result): a line chart for
numeric checks (average as a line, lowest to highest as a band, thresholds as dashed lines, a red mark
where the check could not run or got no response) and a status timeline for yes/no checks (service,
process, pending restart, file, antivirus and firewall). The hour (1-minute buckets) and day (10-minute
buckets) are read from raw `CheckResult` rows (kept 30 days); the week (hourly), month (6-hourly) and
year (daily) from the rollup tables `CheckResultsHourly` and `CheckResultsDaily` (minimum, maximum, sum
and count of values, errors and missing responses per endpoint, check, target and bucket), kept 13
months. Decided while building: the workers maintain the rollups in both setups, inside the evaluation
transaction that advances the per-endpoint cursor, so every result is counted exactly once and local
development without TimescaleDB behaves the same as the VPS; continuous aggregates could not judge
"no response" per check type. A result is placed at the agent's collection time when that lies at most 7
days before and 5 minutes after ingest (buffered results of an agent that was offline land where they
belong), otherwise at ingest time; ordering and evaluation keep using ingest time only. Every bucket of a
range is returned, so a gap in the chart is a real gap. Charts are inline SVG drawn by a small component;
no chart library.

**Maintenance mode (0.2.0).** A technician puts a client, a site or one managed endpoint in
maintenance, with an optional end time. An endpoint is in *effective maintenance* when its own,
its site's or its client's maintenance is active, or a maintenance window of its policy runs (below). One domain rule (`MaintenanceRules`, with the EF Core predicate
`EndpointInMaintenance`) defines it, and its SQL twin `MaintenanceSql.EndpointInMaintenance` serves
the set-based statements in the workers; a test proves the three agree. When sources overlap, the one
that lasts longest is shown (until turned off beats any end time). Endpoint maintenance is for managed
endpoints only (an agent-only endpoint raises no alerts); clients and sites can always be put in
maintenance. Effects:
- Check evaluation keeps updating check states and failure counters, and still resolves alerts
  whose check recovers, but opens and escalates nothing. When maintenance ends, the next failing
  result opens the alert right away.
- Offline detection opens no offline alert; an endpoint still offline after maintenance gets one
  on the next sweep. Fleeto does not hide a real problem once maintenance is over.
- Open alerts stay open. Duplicate identity alerts are never suppressed: they are a security signal.
- No alert transition means no email; the notification path needs no maintenance logic.
- Start, end and expiry are audit entries. A worker notices expired end times (watermark) and
  publishes an endpoint status notification so open pages update; client- and site-level changes
  publish a resync. Maintenance on an endpoint cannot be ended at endpoint level while its site
  or client keeps it in maintenance.

**Maintenance windows (0.2.0).** A policy holds up to 20 recurring windows: days of the week, a local
start time, a duration (15 minutes to 7 days), an IANA time zone and whether they apply to all
endpoints, servers or workstations. A running window is the fourth source of effective maintenance
for the managed endpoints of the sites that use the policy (the site's linked policy, else the default
policy), with the same effects as maintenance mode. Decided while building: occurrences are computed in
C# only (`MaintenanceWindows.Occurrences`, with .NET time zone rules: a start time the clock skips starts
after the jump, a start time that occurs twice starts at its first occurrence, the duration is elapsed
time) and stored ahead in `MaintenanceWindowOccurrence` (policy, window, start and end in UTC, class) for
8 days: web replaces them when a policy is saved, the workers recompute every policy every hour. So the
rule stays a plain time comparison in C#, EF Core and SQL, and the agreement test covers it. The workers
publish a resync when an occurrence starts or ends so open pages update. Windows are not audited per
occurrence (they are scheduled); the policy change that creates them is.

**Job.** An admin or technician runs a library script on a managed endpoint (Jobs tab of the endpoint or
the right-click menu of the endpoint list) and picks a validity window (1 hour, 24 hours or 7 days) → web
checks role, tier, platform, client and approval, and writes one `Job` per endpoint in state
`pending_signature` with a snapshot of the current script version, a `SigningRequest` of kind `job` (only
the web role may create one, origin trigger) and an audit entry, all in one transaction → the signer locks
the job and checks again from the database: still `pending_signature` and within its validity window; the
initiator exists, has 2FA, is not locked out and is admin or technician; the endpoint is managed with the
license; the body of the stored version still has the snapshot hash and the language; the script is global
or of the endpoint's client; the language runs on the endpoint's platform; and, when the policy of the
endpoint's site (or the default policy) requires approval, the version is the script's current one and
approved by an admin who is not its author → it signs a `JobPayload` (`JobId`, `InstanceId`, `EndpointId`,
`Type`, `ValidUntil`, `InitiatedBy`, timeout, output cap, and the script: language, name, version, body,
SHA-256) with the context `fleetify-job-v1` and sets `queued`, or sets `refused` with the reason → the
gateway sends queued, signed, valid jobs to managed sessions when they connect, on a notification and in
its 5-minute catch-up, and records `DeliveredAt` → the agent verifies the signature against the pinned
key, that instance and endpoint are its own, that `ValidUntil` has not passed (5 minutes clock tolerance)
and lies at most 7 days ahead, that its applied configuration is managed, the body hash and that the
language runs on its operating system. It dedupes on `Job.Id` (a seen list kept until 7 days after the
job's validity, plus the job directory) → it stores the signed job on disk, runs the script as SYSTEM or
root and streams output as chunks (below) → it sends `JobCompletion`, and a refusal is a `JobCompletion`
with result `refused` and the reason.

A job that is not signed within 15 minutes becomes `refused` (the signer did not answer); a queued job whose
`ValidUntil` passed more than 10 minutes ago becomes `expired`; both show in the job history. A job can be
cancelled while it waits for its signature or is queued and not yet delivered; delivery and cancel are
atomic, so a delivered job cannot be cancelled. Scripts run at most 4 at a time per agent, with a timeout
from 30 seconds to 24 hours set per version (default 10 minutes); the whole process tree ends at the timeout
(a job object on Windows, a process group on Linux and macOS). A job that was running when the agent
stopped is reported as `interrupted` after the restart and becomes `lost`: it is never started again, since
running it twice could be worse than not knowing. Running a script as the logged-on user comes later
(decided 2026-09-15).

**Job output.** Output is never one message. The agent writes stdout and stderr to disk and
sends them as `JobOutputChunk` messages of at most 64 KiB, each carrying `JobId`, `Stream`
(`stdout`/`stderr`) and a `Sequence` number per stream starting at 0 → the gateway stores
each chunk idempotently (unique on `JobId`, `Stream`, `Sequence`; a duplicate is
acknowledged and ignored) and acknowledges it after the write → the agent deletes a chunk
from disk only after its ack and, after a reconnect, resends every unacknowledged message
(started, chunks, completion). When the database write fails the gateway sends no ack, so
the agent sends the message again on the next connection.
When the process exits the agent sends `JobCompletion` with the exit code and, per stream,
the final chunk count, total byte count and SHA-256. Execution state and output state are
separate:

- **Execution**: `JobCompletion` sets `State` at once, whatever the output looks like:
  `succeeded` for exit code 0, `failed` for another exit code, a timeout or a script that
  could not start, `refused` for an agent refusal and `lost` for `interrupted`. Only the
  first completion counts. A job that stays `running` without a `JobCompletion` for 15
  minutes beyond its timeout becomes `lost`: the result is unknown, and the UI says so rather
  than guessing.
- **Output**: `OutputState` is `receiving` while chunks arrive and becomes `complete` when
  every chunk up to the announced counts is stored and the byte counts and hashes match,
  or `incomplete` when they do not. The agent keeps unacknowledged output on disk for 7
  days and sends it again on every reconnect; when output is still not complete 7 days after
  the completion, `OutputState` becomes `incomplete` for good. So a job can be `succeeded`
  with output `incomplete`.

Output per job is capped at 50 MiB (a constant for now, not per policy); beyond the cap the
agent stops sending, records the truncation in `JobCompletion` (`OutputTruncated`) and the
UI says so. The gateway also stops storing a job's chunks past the cap plus one chunk, so a
hostile agent cannot fill the database. The UI shows the first 1 MiB per stream, updated live
while a job runs. Execution is never repeated to recover output. Output is kept 90 days and
the job history 13 months (retention).

**Script approval.** When a site's policy has script approval required, jobs for its
endpoints may only run the current version of a library script, and only when it is approved. An
author saves a version → a different admin reviews the body and approves it after entering a
fresh TOTP code (a code is accepted once: the same code within 3 minutes is refused, a wrong code
counts towards the account lockout and is audited) → the approval is stored with the version's
SHA-256 → any change creates a new version that needs approval again. Jobs only run library
scripts, so there are no ad-hoc scripts to refuse. The setting is off by default and recommended
for server sites. Scripts live under Settings, Templates, Scripts: every user can read them,
admins and technicians write them, admins approve.

**Remote control session.** Technician clicks Remote control on a managed endpoint and
enters a reason → the browser generates an ephemeral X25519 key pair → web writes a
`RemoteSession` and a `SigningRequest` with the browser's public key → signer issues a
session token (valid 60 seconds to start the session, single use) bound to technician,
endpoint, session id and the browser's public key → gateway hands the token to the agent →
agent verifies the instance signature; if the policy requires consent, it shows a prompt to
the endpoint user and waits → agent generates its own ephemeral key pair and signs its
public key and the session id with its agent certificate key → the browser checks that
signature against the endpoint's certificate, the agent checks that the browser's key
matches the one in the token → both derive the session keys (X25519, HKDF) and every frame
is encrypted with AES-256-GCM → the gateway relays ciphertext it cannot read or alter
unnoticed (transport: *decision pending*, WebRTC with the gateway as TURN vs. WebSocket
relay; with WebRTC the DTLS certificate fingerprints are bound in the same way) → the agent
streams screen frames, the browser sends keyboard, mouse and clipboard events, the agent
sends clipboard changes back → a banner on the endpoint names the technician for the whole
session → on end, drop or timeout the agent releases all pressed keys, the session row is
closed and the audit entry completed.

**Client creation from template.** Technician picks a client template, enters code and
name → the sites in the template are created under the new client with their policy and
monitoring template links (references to the shared templates, not copies) → one audit
entry describes the whole operation.

**Integration poll.** Worker runs each integration on its own schedule with timeout, retry
and circuit breaker → results are translated into the same check/alert model as agent
data → hypervisor hosts and VMs that exist only in Proxmox or vCenter are created with
`Source = integration` under the mapped client and site.

**API call.** Client sends `Authorization: Bearer <api key>` → web splits prefix and secret,
looks the key up by its id, compares the SHA-256 of the secret in constant time, checks
revocation, scope and client restriction → applies the same role and license checks as the
UI → rate limiter per key → response with keyset pagination → audit entry for every write
and for reads of personal data.

**Backup.** Nightly, worker runs `pg_dump` per instance and WAL is archived continuously →
each file is encrypted on the VPS (ephemeral X25519 with the backup public key, HKDF,
chunked AES-256-GCM; see §5 Backups) before it touches the network
→ uploaded to the off-VPS destination with write-only credentials → the destination's own
lifecycle rule deletes backups after the retention period. The VPS can create backups but
cannot read or delete them.

## 5. Security architecture

### Keys and who holds them

```
Steaan, offline (hardware token, never on a VPS, never in CI secrets)
  ├─ release signing key (ed25519)   signs agent binaries, install.sh and the release manifest (image digests)
  └─ license signing key (ed25519)   signs license documents

Per instance, on the VPS
  root key (KEK)                     Docker secret, mounted in fleetify-web and fleetify-workers only
    └─ wraps → data keys (DEKs)      in the DB, one per purpose
                  └─ encrypt →       integration credentials, SMTP, Microsoft Graph secret or certificate key,
                                     webhook URLs and signing secrets, Action1, backup destination credentials,
                                     TOTP seeds, license document, remote control recordings
  signer key (KEK)                   Docker secret, mounted in fleetify-signer only
    └─ encrypts →                    instance signing key (ed25519): jobs, policies, check definitions, session tokens
                                     internal CA key: agent certificates, gateway server certificate
  backup public key (X25519)         in the DB; used for key agreement only (see Backups); private half offline
```

- Hashed, not encrypted (verification only, high-entropy input): API key secrets and
  enrollment tokens, both SHA-256. User passwords: Argon2id.
- Algorithms: AES-256-GCM for data and key wrapping, ed25519 for every signature, X25519
  for key agreement (remote control, backups), mTLS with per-agent certificates.
- Rotation: DEK rotation re-encrypts the data of that purpose in the background; root key
  and signer key rotation rewrap only what they protect, which takes seconds. The instance
  signing key rotates by announcing the new public key to agents in a message signed by
  the old key. All rotations are audited.
- Decided: the root key and the signer key live in Docker secret files on the VPS,
  generated by `install.sh`, each with an offline backup made during the key ceremony. Compose
  mounts file secrets as bind mounts that keep the host owner and mode, so the files are
  `0440 root:10001` (10001 is the container user) inside a `secrets/` directory with mode
  `0700 root`: no other account on the VPS can reach them, and each file is mounted only into the
  containers that need it.

### Signing: release key versus instance key

Two keys with two jobs, strictly separated:

- The **release signing key** belongs to Steaan. Release builds run in CI; CI produces the
  artifacts and their hashes, and a Steaan release manager signs them from the hardware
  token. The public keys (current and standby) are compiled into the agent and into
  `install.sh`. A compromised instance, VPS or CI runner cannot produce an agent binary that
  any endpoint will install.
- The **instance signing key** belongs to one instance. It can sign jobs for that instance's
  managed endpoints and nothing else: it cannot sign binaries, and no other instance's agents
  trust it.

### The signer

Whoever holds the instance signing key can run code as SYSTEM or root on every managed
endpoint of that customer. That is inherent to an RMM, so the key sits in its own minimal
container:

- **fleetify-signer** is the only container that mounts the signer key. It has no
  listening port and no outbound network access; it watches the database (LISTEN/NOTIFY)
  for `SigningRequest` rows. Its database role reads what it needs to decide and writes only
  signatures, request states and audit entries.
- Before signing it enforces rules independently of web: initiator role, managed tier of
  every target (a fourth layer of tier enforcement), script approval, validity window, rate
  limits (3,000 jobs per minute). The UI runs a script on one endpoint at a time; notifying every
  admin about bulk jobs arrives with running a script on a selection of endpoints.
- Optional four-eyes approval for scripts per policy (see §4, Script approval).
- **Accepted residual risk**: the signer decides on database content, and web can write to
  the database. An attacker with full control of web can create jobs that pass the checks
  and have them signed, within the rate limits and, where approval is required, only for
  already approved scripts. What the signer does guarantee: the key cannot be stolen from
  web, a bug in web cannot skip the rules, every signature goes through one audited choke
  point, and the attack ends when web is cleaned up rather than when every agent is re-keyed.

### Agent identity and revocation

- Per-agent certificates, 90 days, automatic renewal, TPM-backed keys where available (§3).
- Recovery of an expired certificate (0.2.0, §4): the agent TLS port accepts an expired agent
  certificate in the handshake, because only a certificate can prove the key; the allow list,
  the recovery path and the signer each refuse it for anything but one renewal of a never
  revoked, most recent certificate within 365 days after expiry. `AgentRecovery` signing requests
  may only be created by the gateway role, and the origin trigger now refuses unknown kinds for
  every container role.
- Revocation uses an allow list in the database (`AgentCertificates`: issued, `RevokedAt` null,
  not expired), checked by the gateway on every handshake and pushed to live connections at
  once. No CRL or OCSP infrastructure. Deleting an endpoint removes its certificates, so it can
  never reconnect.
- Certificates (internal CA, agents, gateway) use ECDSA P-256: Windows SChannel and the Windows
  platform key store do not support ed25519 certificates in TLS. Every application signature
  (configurations, licenses, releases) is ed25519.
- A certificate connecting twice at the same time is refused and raises an alert (§4).
- The gateway has no persistent private key: at start it generates one in memory and gets a
  24-hour server certificate for `agents.<fqdn>` from the signer, renewed well before expiry.

### Jobs

- Every signed payload carries `InstanceId`, `EndpointId` and `ValidUntil`, so a signature is
  valid for one endpoint of one instance for a limited time and cannot be replayed elsewhere
  or weeks later.
- Default validity 24 hours, maximum 7 days, enforced by the signer, the gateway and the agent.
- The job carries the script body. The signer reads it from the stored version and checks its hash
  against the snapshot web took, so the body that is signed is the body that was approved.
- Agent-only endpoints get no jobs at any layer: web refuses to create them, the signer refuses to
  sign them, the gateway sends jobs only to managed sessions and the agent refuses them unless its
  applied signed configuration is managed.
- Grants: web creates and cancels jobs and reads output; the signer only updates jobs; the gateway
  updates jobs and inserts output chunks; the workers expire, mark lost and remove old rows. No
  container but web writes scripts.

### Licensing

- Steaan holds the license signing key (see Keys). The public key is compiled into the
  server build.
- A license document contains customer name, instance FQDN, managed endpoint count, issue
  and expiry dates, and a serial. The instance verifies the signature and the FQDN match
  offline on every start and once a day. No call home is required to keep working. The
  instance remembers the latest time it has seen, so setting the system clock back does not
  extend a license. Threat model, stated precisely: this stops accidental or casual clock
  rollback, not someone with control of the host who restores an older database. Licensing
  is a commercial control, not a security boundary; nothing in the security model depends
  on it.
- Agent-only endpoints are unlimited; the license only counts managed endpoints.
- Delivery: in v1 the document is loaded in Settings by hand. Later a **Steaan management
  server** issues, renews and revokes licenses and instances fetch them over an API (the
  instance polls; the management server never needs inbound access to an instance). The
  document format and the verification code are the same in both cases, so the management
  server replaces only the manual upload step.
- Expiry: 14 days before expiry the dashboard warns. After expiry a **grace period of 14
  days** starts: everything keeps working, every page shows a banner with the end date and
  admins get a daily email. When the grace period ends every endpoint behaves as agent-only
  until a new license is loaded. Nothing is deleted.
- Tier enforcement lives in four places: the domain layer (every operation checks the
  endpoint tier), the signer (never signs for an agent-only endpoint), the gateway (never
  delivers managed-only messages to an agent-only endpoint) and the agent (refuses them
  anyway). Tests cover all four.

### Remote control

- Session token: signed by the signer with the instance signing key, valid 60 seconds to
  start, single use, bound to technician, endpoint, session id and the browser's ephemeral
  public key.
- End-to-end encryption between browser and agent with the key exchange anchored outside
  the relay (§4): the browser's key is in the signed token, the agent's key is signed with
  its certificate key. A compromised gateway can drop a session but cannot read or inject
  into it.
- Limit, stated plainly: the browser learns the endpoint's certificate from web, and web can
  start sessions anyway. End-to-end encryption protects against the gateway and the network,
  not against a compromised web container.
- Visible on the endpoint: banner with technician name for the entire session, consent
  prompt when the policy demands it, session recording when the policy demands it
  (recordings are encrypted with a DEK and follow the retention policy).
- Agent runs as SYSTEM on Windows to reach the console session, the login screen and UAC
  secure desktop; the same privilege is why the session token and signature checks are
  never optional.
- Clipboard sync is text-only in v1 and can be disabled per policy.
- **Remote terminal (0.3.0), accepted risk.** The interactive terminal (§3) is available to
  admins and technicians on every managed endpoint, also where the policy requires script
  approval. On those endpoints script approval therefore only governs library scripts run as
  jobs: a technician who may open a terminal can run any command as SYSTEM. This is a deliberate
  product decision; the controls are the per-session signed token, end-to-end encryption, the
  audit entry per session and the transcript when recording is on.

### Backups

- Nightly `pg_dump` plus continuous WAL archiving, per instance.
- Encrypted on the VPS before upload, using the instance's **backup public key** for key
  agreement only. Per file: generate an ephemeral X25519 key pair → X25519 with the backup
  public key gives a shared secret → HKDF-SHA256 derives a file key (the HKDF input includes
  both public keys) → the file is encrypted in chunks of 1 MiB with AES-256-GCM → the
  ephemeral public key is stored in the file header. Never "encrypt with X25519" directly.
- Nonces: the 96-bit nonce is an 88-bit big-endian chunk counter plus one final byte that
  is 1 for the last chunk and 0 otherwise, so truncation or reordering is detected. The
  counter starts at 0 for every file. This is safe only because every file gets a fresh
  ephemeral key pair and therefore its own file key: a file key is never used for a second
  file, a retry of an upload re-encrypts with a new ephemeral key, and a (key, nonce) pair is
  never used twice. Tests assert that two encryptions of the same file produce different
  keys. The private key never exists on the VPS, so neither a
  compromised VPS nor the storage provider can read a backup.
- Destination: S3-compatible object storage in the EU, configured per instance in Settings
  (first-admin setup asks for it). Credentials are write-only (no read, no delete) and stored
  encrypted; retention is a lifecycle rule on the destination, which also covers the GDPR
  rule that backups expire on their own schedule.
- Until a destination is configured the dashboard shows a warning tile and backups exist on
  the VPS only.
- Restoring onto a fresh VPS needs three things kept offline: the backup private key, the
  root key and the signer key, plus read credentials for the destination.

### Key ceremony (to be written before 0.1.0)

- Steaan: generation and storage of the release signing key and the license signing key on
  hardware tokens, standby keys, who may sign, and the procedure when a key is compromised.
- Per instance: generation of the root key and signer key on first install, their offline
  backups, generation of the backup key pair with the private half offline, who holds what.
- A rehearsed restore of an instance backup onto a fresh VPS using those keys.

### Other controls

- Containers non-root, read-only filesystems where possible, no Docker socket in app
  containers, egress from agents limited to the gateway host.
- Instances on one VPS: separate Compose projects, networks, volumes and secret directories.
  The host Caddy is the only shared component and it is **not secret-free**: it holds the
  TLS private keys and ACME account for every instance FQDN on the VPS. A compromised Caddy
  can impersonate the web UI of every instance on that VPS. It cannot impersonate a gateway
  to agents (agent traffic passes through untouched and agents pin the instance CA) and it
  has no access to instance databases or keys. Mitigations: pinned Caddy version, admin API
  on a local socket only, minimal configuration written by `install.sh`, HSTS on every FQDN.
- **Agent addresses behind the host proxy.** Caddy sends a PROXY protocol v2 header before the
  passed-through TLS connection. The gateway reads it in a connection middleware before TLS, and
  only from the trusted proxy networks (`Gateway:ProxyProtocol:TrustedNetworks`, the private
  ranges the published loopback port is reached from); from anywhere else the bytes go to TLS
  unread. A trusted connection without a header keeps its own address, a malformed header closes
  the connection. The address sets the endpoint's Public IP, the logs and the per-address
  enrollment rate limit; it never grants an identity (mTLS does). Residual risk: a process on the
  VPS or a compromised container of the instance could forge the header and so the address it
  appears to come from. Off for local development.
- **Outgoing requests to admin-entered URLs** (webhooks, 0.2.0) go only to public addresses: https
  only, no credentials in the URL, no redirects, no proxy, and the workers check every address a host
  name resolves to at connect time (`NetworkAddressPolicy`: no loopback, private, carrier-grade NAT,
  link-local or cloud metadata, multicast or reserved ranges, also when embedded in IPv6), so a
  webhook cannot reach the database, other containers or the host, also not through DNS rebinding.
  Webhook URLs and signing secrets are write-only and never written to the audit log.
- One database role per container with the minimum grants; the gateway has no access to
  encrypted secrets and does not mount the root key.
- Every privileged action writes an `AuditEntry`; the table has no update or delete path.
  Audit details never copy personal free text such as note bodies (only ids and lengths), so
  deleting an endpoint really removes that data.
- Notes are rendered from markdown with raw HTML disabled; images are shown as text and links
  are kept only for absolute http, https and mailto addresses, with
  `rel="noopener noreferrer nofollow"`.
- Per-endpoint rate limiting, strict input validation, parameterized queries only, CSP
  with a per-request nonce and without unsafe-inline for scripts (styles allow inline because
  MudBlazor renders style attributes), TOTP 2FA for every user, API keys hashed and scoped.
- Cross-client isolation is enforced in the data layer (global query filters on the
  `ClientId` that every client-owned table carries, composite foreign keys that keep it
  consistent, see §2) and proven by tests that attempt cross-client reads.

## 6. Public API

- Base path `/api/v1` on the instance FQDN. JSON only. OpenAPI 3 document at
  `/api/v1/openapi.json`, generated from the code and verified by a test that fails when the
  document and the controllers diverge.
- Authentication: `Authorization: Bearer <api key>`. Keys are created in Settings, scoped
  `read` or `read_write`, optionally restricted to a list of clients, revocable, shown once.
- Key format: `flt_<id>_<secret>`, where the secret is 32 random bytes (256 bits) from a
  cryptographic random generator. Only the SHA-256 of the secret is stored, which is enough
  because the input is long and random. The fixed `flt_` prefix lets secret scanners, ours
  and GitHub's, recognise a leaked key.
- Resources in v1: clients, sites, endpoints (with inventory, status, tier), alerts, checks
  and results, jobs, patch compliance, notes, audit entries (read-only), license usage.
- Conventions: keyset pagination (`cursor`, `limit`), ISO 8601 UTC timestamps, `ETag` on
  single resources, problem+json errors with cause and next step, rate limit headers.
- Field names are stable from 1.0.0; breaking changes mean `/api/v2`.
- The Blazor UI calls the same application services as the API, so a capability missing in
  the API is a bug, not a design choice.

## 7. Install and update flow

`install.sh` is the only supported way to install or update the server. It operates per
instance; a VPS can hold several. It is never piped from `curl` into a shell: it is
downloaded, its signature is checked with the Steaan release public key, and only then run.

```
curl -fsSLO https://get.fleeto.app/install.sh
curl -fsSLO https://get.fleeto.app/install.sh.sig
openssl pkeyutl -verify -rawin -pubin -inkey steaan-release.pub -in install.sh -sigfile install.sh.sig
sudo ./install.sh --fqdn rmm.customer.example                        # new instance for this FQDN (asks when omitted)
sudo ./install.sh --fqdn rmm.customer.example --version 0.1.0        # pin a version for that instance
sudo ./install.sh --fqdn rmm.customer.example --check                # show installed vs. latest, change nothing
sudo ./install.sh --list                                             # instances on this VPS
```

`steaan-release.pub` is published on the Fleeto website and in the customer documentation.
After the first run `install.sh` carries the release public keys itself.

**Release manifest.** Every release publishes a manifest listing the version, the image
digests of every container and the hash of `install.sh`, signed with the release key.
`install.sh` verifies the manifest, pulls images by digest only (never by tag) and replaces
itself only with a version whose hash is in a verified manifest.

First run on a VPS: install Docker → install the host-level Caddy (pinned version with the
layer4 module, admin API on a local socket) with an empty routing table → create
`/opt/fleetify/`.

New instance: ask for or take the FQDN → check that both the FQDN and `agents.<fqdn>`
resolve to this VPS (fail early with the DNS records to create) → derive the instance name
from the FQDN → create `/opt/fleetify/<instance>/` with Compose files → generate the root
key, signer key and DB passwords into `/opt/fleetify/<instance>/secrets/` (see Keys for
ownership and modes; never part of a backup) → verify the release manifest and pull images by
digest → run migrations → start the stack → the signer creates the instance signing key and
internal CA → regenerate the host Caddyfile from all instances: the layer4 module runs as a
listener wrapper on Caddy's own :443 server, sends `tls sni agents.<fqdn>` untouched (after a
PROXY protocol v2 header with the agent's address) to the gateway's loopback port and lets
everything else fall through to Caddy's TLS, which serves the FQDN with HSTS and proxies to the
web's loopback port (no second internal hop; real client IPs reach the web through
X-Forwarded-For) → print the URL, the one-time first-admin setup link
and a reminder to run the key
ceremony (offline copies of root key and signer key, backup key pair). First-admin setup
asks for the backup destination and the backup public key.

Update run for an instance: verify the new release manifest → detect installed version →
back up the database (kept locally, 0600, until the next successful update; the nightly
off-VPS backup is separate) → pull the requested images by digest → run migrations →
restart the stack → health check → on failure roll back to the previous images.
`install.sh --all` updates every instance on the VPS in turn. Operational details (VPS
requirements, DNS, networks, restore outline, per-instance footprint) are in `deploy/README.md`;
release signing steps in `deploy/RELEASING.md`.

**Local development** needs no Docker: `tools/dev/setup-dev.ps1` creates the roles, database,
secrets and development keys against a local PostgreSQL 17, and `tools/dev/start-dev.ps1` runs
signer, gateway, workers and web as plain processes. Without TimescaleDB the check results
table stays a plain table and the workers enforce retention with deletes.

**Migration compatibility policy.** Image rollback only works if the previous release still
runs on the migrated schema, so migrations follow **expand/contract**:

- A release may only *expand* the schema in ways the previous release tolerates: add tables,
  add nullable columns or columns with defaults, add indexes, backfill data.
- Renames and type changes are split over releases: add the new column, write both, read
  the new one, and only *contract* (drop the old column or table) in a later release once no
  supported version uses it.
- CI runs the previous release's test suite against the new schema to prove it.
- A release that cannot follow the policy is marked `rollback: restore` in the release
  manifest. `install.sh` then shows that before starting, asks for confirmation (or
  `--yes`), and on a failed update restores the pre-update backup together with the previous
  images instead of swapping images only.

Agents self-update from their instance, staged by update ring from the site policy. The
instance only distributes the binaries; the agent installs one only when its signature
verifies against a Steaan release public key compiled into the agent.

## 8. Repository layout

```
/CLAUDE.md                     rules, priorities, conventions, product model
/MD-Files/                     the rest of the documentation set (this folder)
/Fleetify.slnx                 solution; Directory.Build.props and Directory.Packages.props hold shared settings
/src/Fleetify.Core/            domain model, enums, pure domain rules (tiers, licensing, check evaluation), interfaces
/src/Fleetify.Protocol/        agent protocol v1 (agent.proto) and generated C#
/src/Fleetify.Infrastructure/  EF Core model and migrations, grants, crypto, CA, licensing, notifications, shared services
/src/Fleetify.Web/             Blazor Server UI (public REST API from 0.2.0)
/src/Fleetify.Gateway/         agent endpoint (.NET): enrollment, mTLS WebSocket sessions, ingest; remote control relay later
/src/Fleetify.Signer/          signing service: instance signing key, internal CA, signing rules
/src/Fleetify.Workers/         background jobs: config fan-out, check evaluation, alerts, email, license, backups, retention
/src/Fleetify.Tools/           fleetify-tool: migrate, license, release and backup key utilities
/agent/                        Go agent (one module, per-platform builds)
/tests/Fleetify.Testing/       shared test fixture: a real PostgreSQL database per test project
/tests/Fleetify.*.Tests/       unit and integration tests per component, cross-client and tier enforcement tests
/tests/Fleetify.LoadTest/      simulator for 10,000 agents
/tools/dev/                    local development without Docker: setup-dev.ps1, start-dev.ps1, build-agent.ps1
/deploy/                       Compose stack, host Caddy, install.sh (Dockerfiles live next to each project)
/.github/workflows/            CI: build, test, vulnerability scan, secret scan, branding grep
```

The gateway is .NET (decided for 0.1.0): it shares the domain model, EF Core and tier
enforcement with the rest of the server. `Fleetify.LoadTest` provides the 10,000-connection evidence.

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
