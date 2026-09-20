# Fleeto — Architecture

> Technical reference for Fleeto. Rules and priorities live in
> `CLAUDE.md` in the repository root; this file describes how the system is put together.
> Status: 0.0.x to 0.2.2 released (agent enrollment, gateway, signer, workers, web UI, licensing, backups, maintenance,
> the check catalog and history, notification routing, scripts and jobs, the read-only public API, agent updates, the
> watchdog, the Linux agent); 0.3.0 in progress (remote sessions: the relay, end-to-end encryption, the full remote
> background — terminal, files, services, processes — and remote control on Windows (H.264 and tiles) and on Linux with X11, with the
> clipboard, several technicians, consent and banner implemented). Integrations are design.
> Sections marked *decision pending* point to the open decisions in `CLAUDE.md`.

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
                     │  │ fleeto-web               │   │ fleeto-web               │     │
[agents] ──mTLS──►   │  │ fleeto-gateway           │   │ fleeto-gateway           │     │
                     │  │ fleeto-signer            │   │ fleeto-signer            │     │
                     │  │ fleeto-workers           │   │ fleeto-workers           │     │
                     │  │ postgres + TimescaleDB     │   │ postgres + TimescaleDB     │     │
                     │  └────────────────────────────┘   └────────────────────────────┘     │
                     └──────────────────────────────────────────────────────────────────────┘
                                         │ encrypted backups (write-only)
                                         ▼
                              off-VPS object storage (EU)
```

Inside one instance:

```
[agents] --mTLS WebSocket (SNI passthrough)--> [fleeto-gateway] --batch write, then ack--> [postgres + TimescaleDB]
                                                  │        │                                        ^      ^   ^
                                                  │        └─ NOTIFY (postgres) ─> [fleeto-workers] ─┘       │   │
                                                  └── remote session relay (ciphertext only, 0.3.0)            │   │
[integrations: Action1, Sophos, Veeam, Proxmox, vCenter] --> [poller workers] ─────────────────────────────────┘   │
                                                                                                                   │
[browser, API clients] --HTTPS--> [caddy] --> [fleeto-web: Blazor Server UI + public REST API] <─────────────────┤
[browser] --wss://<fqdn>/relay/--> [caddy] --> [fleeto-gateway relay port] (remote sessions, end-to-end encrypted)  │
                                                     ^                                                             │
                                                     +-- LISTEN/NOTIFY (postgres) for live status                  │
                                                                                                                   │
                                              [fleeto-signer] ── LISTEN/NOTIFY, no listening port ───────────────┘
```

| Container | Scope | Role | Notes |
|---|---|---|---|
| **caddy** | VPS | Reverse proxy, automatic TLS | One per VPS, built with the layer4 module. Terminates TLS for the UI and API of every instance, so it holds the TLS private keys and ACME account for every FQDN on the VPS (see §5). Agent traffic to `agents.<fqdn>` is passed through by SNI to the instance gateway and never decrypted, so mTLS stays end to end between agent and gateway. In front of the passed-through connection Caddy sends a PROXY protocol v2 header with the agent's address (see §5, Other controls). From 0.3.0 it proxies `https://<fqdn>/relay/` to the gateway's relay port: the browser side of remote sessions, whose content is end-to-end encrypted. |
| **fleeto-web** | instance | Blazor Server UI and public REST API (.NET, MudBlazor) | Follows the Migrify project layout and conventions. Cannot sign anything an agent executes. |
| **fleeto-gateway** | instance | Agent connection endpoint and remote control relay | Persistent WebSocket over mTLS for online state, command push and check results. Checks certificate revocation on every connection. Acks agent data only after it is written to Postgres. Serves the agent and watchdog binaries of the current release for agent updates (0.2.1). Relays remote sessions between browser and endpoint without holding a session key (0.3.0, §4 Remote session). Target: 10,000 concurrent connections on modest hardware. Language: .NET (decided in 0.1.0). |
| **fleeto-signer** | instance | Signs everything that establishes trust with agents | Holds the instance signing key and the internal CA key, decrypted with its own signer key that no other container mounts. No listening port: it picks up signing requests from the database. Re-checks role, tier, script approval and validity window before signing. See §5. |
| **fleeto-workers** | instance | Background jobs | Check evaluation, alerting, integration pollers, Action1 patch orchestration, retention cleanup, backups, license checks. |
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
                │         ├──* AgentCertificate (role agent or watchdog), EndpointComponentState
                │         ├──* CheckDefinition (endpoint-only checks)
                │         ├──* EndpointCheckOverride ──> CheckDefinition (of a template)
                │         └──* EndpointMonitoringTemplate ──> MonitoringTemplate
                │
                ├──* SiteMonitoringTemplate ──> MonitoringTemplate 1──* CheckDefinition
                └──* SitePolicy ──────────────> Policy

ClientTemplate 1──* ClientTemplateSite ──* (MonitoringTemplate | Policy) links

Job 1──* JobOutputChunk
Script 1──* ScriptVersion       SigningRequest (signer work queue)
License (one per instance)      ApiKey 1──* ApiKeyClient ──> Client
AgentRelease (current release offered to agents)
User *──* Role                  Integration 1──* IntegrationMapping ──> Client
AuditEntry (append-only)
```

| Entity | Key fields | Notes |
|---|---|---|
| **Client** | `Id`, `Code` (unique, uppercase), `Name`, `CreatedAt`, `Maintenance` | Tenant boundary inside the instance. Every client-owned table carries its own `ClientId`, denormalized on purpose (see the rule below the table), and every query filters on it. |
| **Site** | `Id`, `ClientId`, `Name`, `Description`, `Maintenance` | Groups endpoints. Holds the link to at most one policy (without one the instance default policy applies) and to any number of monitoring templates. Enrollment tokens belong to a site. |
| **Endpoint** | `Id`, `ClientId`, `SiteId`, `Hostname`, `Class` (`workstation`/`server`), `ClassOverride`, `Tier` (`agent_only`/`managed`), `Os`, `AgentVersion`, `LastSeenAt`, `Source` (`agent`/`integration`), `Maintenance`, `PublicIpAddress?`, `PublicIpSeenAt?`, `WatchdogOnline`, `WatchdogVersion`, `WatchdogLastSeenAt?`, `SignedInUsersJson?`, `SignedInUsersAt?` | Endpoints without an agent exist only for hypervisor inventory (ESXi hosts and VMs from vCenter or Proxmox). `Tier` gates every feature server-side. `IsOnline` and the watchdog columns (0.2.1) are maintained separately for the agent and the watchdog session. `PublicIpAddress` is the address the gateway saw for the latest agent connection (personal data: only the latest value is kept, deleted with the endpoint). `SignedInUsersJson` (0.2.2) is the latest list of signed-in users the agent reported: per user the SID or uid, the account name and the sessions (personal data: only the latest list is kept, deleted with the endpoint). |
| `Maintenance` (on Client, Site, Endpoint) | `MaintenanceStartedAt?`, `MaintenanceEndsAt?`, `MaintenanceStartedByUserId?`, `MaintenanceStartedByName?`, `MaintenanceReason?` | Maintenance mode (0.2.0), stored as nullable columns on each of the three tables. Active while `MaintenanceStartedAt` is set and not in the future and `MaintenanceEndsAt` is null or in the future; ending by hand clears the columns, an end time that passes is left in place and every query compares with the current time. The reason is free text and personal data may appear in it: it is never copied into the audit log. See *Maintenance mode* in §4. |
| **AgentCertificate** | `Id`, `ClientId`, `EndpointId`, `Role` (`agent`/`watchdog`), `Fingerprint`, `IssuedAt`, `ExpiresAt`, `RevokedAt?`, `RevokedBy?`, `RevokedReason?` | One row per issued certificate, renewals included. The gateway refuses every certificate with `RevokedAt` set. `Role` (0.2.1) decides which session the certificate may open; a renewal keeps it. |
| **EndpointComponentState** | `EndpointId`, `Component` (`agent`/`watchdog`), `ClientId`, `InstalledVersion`, `ServiceState`, `ServiceDetail`, `ServiceStateAt?`, `UpdateVersion`, `UpdateState?`, `UpdateDetail`, `UpdateAt?`, `WaitVersion?`, `WaitReason?`, `WaitUntil?`, `WaitAt?` | Per endpoint and component (0.2.1): the service state its peer reports and the latest update report (`downloading`, `installing`, `installed`, `failed`, `rolled_back`). From 0.2.2 also the offered release the installer holds back and why (`UpdateRing`, `NextAttempt`, `RandomDelay`, `InstallerUpdate`, `RolledBack`), cleared by the next update report that is not a wait. Written by the gateway, deleted with the endpoint. |
| **Policy** | `Id`, `ClientId?`, `Name`, settings, `UpdateRing` (`preview`/`standard`/`delayed`, default `standard`), `MaxOutputBytes` (1-200 MiB, default 50 MiB, check constraint), `MaintenanceWindowsJson`, remote session settings (0.3.0): `RemoteConsentRequired` (default off), `RemoteConsentTimeoutSeconds` (10-300, default 30), `RemoteBannerVisible` (default on), `RemoteClipboardEnabled` (default on), `RemoteIdleTimeoutMinutes` (5-480, default 30), `RemoteMaxFileBytes` (1 MiB-10 GiB, default 10 GiB) | Agent behaviour: intervals, patch behaviour, update ring (0.2.1, §4 Agent update), script permissions and **script approval required**, the job output cap (0.2.1, §4 Job), remote session rules (0.3.0; consent and banner apply to remote control on workstations only, the clipboard to remote control on every endpoint; the signer puts what applies into the session token), maintenance windows. `ClientId` null = global. |
| **MaintenanceWindowOccurrence** | `PolicyId`, `WindowIndex`, `StartsAt`, `EndsAt`, `AppliesTo`, `Name?` | Occurrences of the policy's maintenance windows (0.2.0), stored 8 days ahead (§4, Maintenance windows). Deleted with the policy. |
| **MonitoringTemplate** | `Id`, `ClientId?`, `Name` | Named set of `CheckDefinition`s with thresholds and alert rules. `ClientId` null = global. |
| **CheckDefinition** | `Id`, `ClientId?`, `MonitoringTemplateId?`, `EndpointId?`, `Type`, `Interval`, `Thresholds`, `FailuresBeforeAlert`, `AppliesToClass`, `Enabled` | Interval from seconds to monthly. Owned by exactly one of a monitoring template or one endpoint (check constraint). An endpoint-only check carries the endpoint's `ClientId` (composite foreign key) and runs whatever the endpoint class. |
| **EndpointMonitoringTemplate** | `EndpointId`, `ClientId`, `MonitoringTemplateId`, `CreatedAt`, `CreatedBy?` | An extra monitoring template for one endpoint, on top of those of its site. Global or same-client templates only (constraint trigger). |
| **EndpointCheckOverride** | `EndpointId`, `CheckDefinitionId`, `ClientId`, `Disabled`, `IntervalSeconds?`, `FailuresBeforeAlert?`, `OverrideThresholds`, `WarningThreshold?`, `CriticalThreshold?` | Adjusts one template check for one endpoint. Unset values inherit the template, so the template stays linked. `OverrideThresholds` replaces both thresholds as a pair, so "no threshold" can be an override too. Template checks only (constraint trigger). |
| **CheckState** | `EndpointId`, `CheckDefinitionId`, `Target`, `ClientId`, `Status`, `Value?`, `ConsecutiveNonOk`, `LastResultAt`, `ResetAt?` | Current evaluated state per check and target, maintained by the workers. "Re-run requested" while `ResetAt` is later than `LastResultAt`. States of checks that no longer apply are removed on the hourly sweep. |
| **CheckRunRequest** | `Id`, `ClientId`, `EndpointId`, `CheckDefinitionId`, `Reset`, `RequestedBy`, `RequestedAt`, `ExpiresAt`, `ResetAppliedAt?`, `DeliveredAt?`, `Outcome?` (`expired`, `not_applicable`, `not_managed`) | A technician's "Run now" or "Reset and run" (§4). Kept 7 days. |
| **ClientTemplate** | `Id`, `Name`, sites with linked policies and templates | Blueprint used at client creation. Linked, not copied: later changes apply to every client using it; a technician can make an independent copy. |
| **Script**, **ScriptVersion** | `Id`, `ClientId?`, `Name`, `Description`, `Language` (`PowerShell`, `Batch`, `Shell`, `Bash`), `CurrentVersionId`; version: `ClientId?`, `Number`, `Body`, `Sha256`, `TimeoutSeconds`, `AuthorUserId`, `ApprovedByUserId?`, `ApprovedAt?`, `ApprovedSha256?` | `ClientId` null = global, immutable (trigger); a version carries the client of its script (constraint trigger). Saving a changed body or timeout creates a new version; the language is fixed. A version is approved only when approver and author differ and `ApprovedSha256` equals `Sha256` (check constraint). |
| **Job** | `Id`, `ClientId`, `EndpointId`, `BatchId`, `Type` (`Script`), snapshot of the script (`ScriptId?`, `ScriptVersionId?`, name, version number, `Language`, `ScriptSha256`, `TimeoutSeconds`, `MaxOutputBytes`), `RunAs` (`service`/`logged_on_user`, 0.2.1), `RunAsAccount?` (the user the agent reported, 0.2.1), `RunAsUserId?` and `RunAsChosenAccount?` (the user the technician chose, 0.2.2), `ValidUntil`, `InitiatedByUserId`, `State` (`pending_signature`, `queued`, `running`, `succeeded`, `failed`, `expired`, `refused`, `lost`, `cancelled`), `RefusalReason?`, `Payload`, `Signature`, `SigningKeyId`, `DeliveredAt?`, `StartedAt?`, `CompletedAt?`, `Result?` (`exited`, `timed_out`, `refused`, `failed_to_start`, `interrupted`), `ExitCode?`, `OutputState` (`none`, `receiving`, `complete`, `incomplete`), `OutputTruncated`, per stream announced chunks, bytes and SHA-256 | One row per endpoint. Idempotent by `Id`. Never delivered or executed after `ValidUntil` (at most 7 days after creation, check constraint). `State` describes execution, `OutputState` describes the output; they move independently. The snapshot keeps the history readable after the script changes or is deleted. |
| **JobOutputChunk** | `ClientId`, `JobId`, `Stream` (`stdout`/`stderr`), `Sequence`, `Data`, `ReceivedAt` | Unique on `JobId`, `Stream`, `Sequence`. Protocol in §4, Job output. |
| **SigningRequest** | `Id`, `ClientId?`, `Kind` (`Job`, `RemoteSessionToken`, `AgentEnrollment`, `AgentRenewal`, `AgentRecovery`, `GatewayCertificate`, `AgentConfig`, `WatchdogCertificate`), `SubjectId`, `RequestedBy`, `State`, `RefusalReason?` | Written by web, gateway or workers (who may create which kind is a database trigger), processed by the signer. |
| **License** | `Id`, `CustomerName`, `Fqdn`, `ManagedEndpointCount`, `ExpiresAt`, `SignedDocument` (encrypted) | One per instance. Verified offline with the Steaan license public key baked into the build. Grace period of 14 days after `ExpiresAt`. |
| **AgentRelease** | `Version`, `ManifestSha256`, `InstalledAt`, `IsCurrent`, `PausedAt?`, `PausedByUserId?`, `PausedByName?`, `ReleasedToAllAt?`, `ReleasedToAllByUserId?`, `ReleasedToAllByName?` | One row per release the gateway loaded (0.2.1). `InstalledAt` starts the ring delays; at most one `IsCurrent` (unique filtered index). Paused and released to all by admins in Settings, Agent updates, each audited. |
| **ApiKey** | `Id`, `Name`, `SecretHash`, `AllClients`, `CreatedByUserId`, `CreatedByName`, `CreatedAt`, `ExpiresAt?`, `LastUsedAt?`, `RevokedAt?`, `RevokedByUserId?`, `RevokedByName?` | Public API key (0.2.1), read-only. The id is part of the token; only the SHA-256 of the secret is stored (check constraint: 64 hex characters) and the token is shown once. Limited to the clients in **ApiKeyClient** (`ApiKeyId`, `ClientId`, deleted with either side) when `AllClients` is false, so a key whose clients are all deleted sees nothing. Never deleted: revoked keys stay for the audit trail. Web only (grants). Format in §6. |
| **RemoteSession** | `Id`, `ClientId`, `EndpointId`, `Kind` (`RemoteControl`/`RemoteBackground`), `Component` (`agent`/`watchdog`, the serving service), `StartedByUserId`, `StartedByName`, `Reason?`, `CreatedAt`, `StartedAt?`, `EndedAt?`, `EndReason?` | Every session (0.3.0), whether it connected or not. Kept 13 months, then removed with its participants and actions; the audit log keeps its own entries. |
| **RemoteSessionParticipant** | `Id`, `SessionId`, `ClientId`, `EndpointId`, `UserId`, `UserName`, `Reason?`, `State` (`Requested`, `Signed`, `Connecting`, `Connected`, `Ended`, `Refused`, `Failed`), `BrowserPublicKey` (32 bytes, check constraint), `TokenPayload?`, `TokenSignature?`, `SigningKeyId?`, `SignedAt?`, `ValidUntil?`, `IpAddress?`, `CreatedAt`, `ConnectingAt?`, `ConnectedAt?`, `EndedAt?`, `EndReason?` | One technician's connection to a session, with its own single-use token (§4 Remote session). `ConnectingAt` needs a signature (check constraint). Web writes it, the signer signs it, the gateway claims, connects and ends it, the workers end what never connected. |
| **RemoteSessionAction** | `Id`, `SessionId`, `ParticipantId?`, `ClientId`, `EndpointId`, `Time`, `Action`, `Target`, `Detail?` | An action inside a remote background session as the endpoint reports it (file, service or process action with its target; written from 0.3.0 step 2). Terminal content is never stored. |
| **CheckResult** | `Time` (ingest), `ClientId`, `EndpointId`, `CheckDefinitionId`, `Status`, `Value`, `Payload` | TimescaleDB hypertable, compressed, retention policy. Deduplicated per endpoint and agent batch sequence number. |
| **Alert** | `Id`, `ClientId`, `EndpointId`, `CheckDefinitionId`, `Severity`, `State`, `AcknowledgedBy`, `HeldUntil?`, `HeldAt?`, `HeldBy?`, timestamps | Deduplicated per endpoint and check. On hold while `HeldUntil` is in the future (§4, Alert hold). |
| **InventorySnapshot** | `EndpointId`, `ClientId`, `ReceivedAt`, `Hash`, hardware facts, `DisksJson`, `NetworkInterfacesJson`, `SoftwareJson`, `ServicesJson` | Latest inventory, one row per endpoint. `ServicesJson` (0.2.0): name, display name, start type and state per service (at most 2,000), used to pick the service of a service check; for a monitoring template the services of the most recent 1,000 inventories of its endpoints are offered. |
| **CheckResultHourly**, **CheckResultDaily** | `EndpointId`, `CheckDefinitionId`, `Target`, `Bucket`, `ClientId`, `MinValue?`, `MaxValue?`, `SumValue`, `ValueCount`, `ErrorCount`, `NoResponseCount` | Check history rollups (0.2.0), maintained by the workers with the evaluation, kept 13 months. Deleted with their endpoint or check. |
| **Note** | `Id`, `ClientId`, `EndpointId`, `AuthorId`, `AuthorName`, `Body` (markdown, at most 20,000 characters), `CreatedAt`, `UpdatedAt`, `EditedAt?` | Endpoints only (site notes were dropped). Managed endpoints only. The author edits, an admin deletes. Searchable from 0.6.0. A Servicedesk ticket reference is not yet scheduled (ROADMAP, Not yet scheduled). |
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
  EndpointComponentState, IntegrationMapping. Consistency by composite foreign key, as above. One documented
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
- **Instance-wide** (no `ClientId`): users, roles, API keys, client templates, license, agent releases,
  integrations, and SigningRequest and AuditEntry rows that concern no client (their
  `ClientId` is null).

## 3. Agents

- **One codebase, Go**: single static binary per platform, no runtime dependencies on the
  endpoint. Targets: Windows (service running as SYSTEM) and Linux (systemd unit running as root, 0.2.1), on amd64 and arm64. macOS is not
  supported for now and may come when there is demand (decided 2026-09-15); the code keeps building for it only so the
  platform abstraction stays honest.
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
  - **Windows**: a CNG machine key (`Fleeto Agent Identity`), in the Microsoft Platform Crypto Provider when the
    endpoint has a TPM.
  - **Linux** (0.2.1): an ECDSA P-256 key created inside the TPM 2.0 (`/dev/tpmrm0`) under the owner storage key; what
    is stored is the key blob the TPM itself encrypted, which loads in no other TPM. Without a TPM the key is a PKCS#8
    file in the state directory, readable by root only.
- **Linux layout** (0.2.1): the binaries live in `/opt/fleeto-agent` (deliberately not under `/opt/fleeto`, which holds
  the instances of a Fleeto server), the state in `/var/lib/fleeto/agent` and `/var/lib/fleeto/watchdog`, root-only.
  Services are the systemd units `fleeto-agent.service` and `fleeto-watchdog.service`: `Restart=always`, started at
  boot, no sandbox (the agent runs checks, scripts and installers as root). They log to the journal and to their state
  directory. `fleeto-agent install` writes the units and enables them; `fleeto-agent uninstall` removes both, their keys
  and their directories. Service control, supervision and updates go through `systemctl`, so the agent needs no D-Bus
  library; a unit an administrator disabled or masked is reported and never started again by the other service.
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
- **Watchdog service (0.2.1).** A second Windows service, `fleeto-watchdog`, a small separate binary
  (`cmd/fleeto-watchdog`) from the same Go codebase, running as SYSTEM with its own state directory. On Linux (0.2.1) it is
  the systemd unit `fleeto-watchdog.service`, running as root; everything below is the same on both platforms.
  - **Identity.** Its own key (platform key store `Fleeto Watchdog Identity`, TPM-backed where available) and its own
    90-day certificate for the same endpoint with the role *watchdog* (`AgentCertificate.Role`). The agent creates the key
    and sends the CSR over its own session (`WatchdogCertificateRequest`); the signer issues it only for an endpoint with a
    valid agent certificate, only for a key that differs from every agent key, at most 3 times a day, and revokes earlier
    watchdog certificates of the endpoint. The watchdog renews its certificate over its own session like the agent does;
    when it is missing or expires within 7 days, the agent requests a new one. Revoking or deleting the endpoint revokes both.
  - **Sessions.** The gateway keeps one agent session and one watchdog session per endpoint (the role comes from the
    allow list in the database, never from the agent), so both are connected without triggering the duplicate identity
    rule. A watchdog session only sends heartbeats, update reports and renewals, and from 0.3.0 receives remote background
    offers; it never receives configurations, jobs or requests, and a watchdog certificate cannot recover an expired certificate.
  - **Supervision.** Every 30 seconds each service checks the other: a stopped service is started again with a backoff
    (30 seconds, doubling up to 10 minutes), a disabled service is left alone and reported, and supervision pauses while the
    binary is being replaced. The marker file `uninstalling` in the data directory stops both from restarting or installing
    each other during an uninstall. Each reports the other's service state in its heartbeat (`PeerStatus`).
  - **Health.** Each service writes `health.json` in its state directory (version, process id, connected, connected
    at). An installer uses it to decide whether a new version came up.
  - **Alerts.** "Agent service stopped" (`AgentStopped`) when the watchdog is online and the agent has not connected for
    the offline alert delay of the policy, with the severity of the offline alert; "Watchdog stopped" (`WatchdogStopped`,
    warning) when the agent is online and a watchdog that connected before has not. The offline alert means both are
    gone. All three are for managed endpoints only and respect maintenance.
  - **Updates.** The watchdog installs agent updates; the agent installs a missing watchdog and updates the watchdog once
    it runs the release itself. Both install a binary only when the release manifest that lists it carries a valid Steaan
    release signature (§4, Agent update).
- **Remote background (0.3.0).** Terminal, files, services and processes without touching the screen, served by the
  watchdog (`internal/remote`) so it also works when the agent is broken. The terminal runs as SYSTEM or root: PowerShell
  or cmd in a Windows pseudo console (ConPTY, Windows 10 1809 and Server 2019 or newer; Server 2016 gets the shell with
  redirected streams and line input, output converted from the console code page), the login shell (bash, else sh) in a
  pseudo terminal on Linux, each in a job object or its own session so closing it ends everything it started. At most 4
  terminals per session and 8 sessions per service. The watchdog has no configuration of its own: it serves a session only
  while the signed configuration the agent applied verifies against the pinned instance key and is managed. Same trust
  model as remote control (§4 Remote session); admins and technicians, managed endpoints only, no policy switch. Every
  session and participant is recorded and audited (technician, endpoint, start, end, reason); terminal content never
  leaves the session. See §5 for the accepted risk towards script approval.
  - **Files** (0.3.0 step 2): browse the endpoint, download a file (streamed with flow control, at most the policy's file size
    cap, resumable from a byte offset after a reconnect; the endpoint starts sending when it answers, so the browser keeps frames
    of a transfer it does not know yet, and a download without acknowledgements for 2 minutes stops and closes the file), upload a file (written to a `.fleeto-part` file, then renamed), and
    create, rename, delete and copy within the endpoint. All over the same encrypted session; the watchdog acts as SYSTEM or
    root.
  - **Services** (0.3.0 step 2): list them and start, stop, restart or change the start type (the service control manager on
    Windows, systemctl on Linux). **Processes** (0.3.0 step 2): list them with a short CPU sample, memory and user, and end one.
  - Every file, service and process action is reported to the gateway over the watchdog's own control session
    (`RemoteSessionActionReport`), which writes a `RemoteSessionAction` (participant, action, target). The report comes from the
    endpoint, so the audit is authoritative, and it never carries a file's content. The relay carries the session ciphertext
    the gateway cannot read.
- **Remote control (0.3.0 step 3, Windows).** The screen, mouse and keyboard, served by the **agent** (not the watchdog), because
  it needs a process in the Windows session that the agent, as SYSTEM, can start. The agent runs `internal/screen`: a helper per
  session that captures the desktop (DXGI desktop duplication, GDI where that is not available), encodes it as H.264 or as changed
  tiles (step 5) and injects input, and the agent relays its frames over the same encrypted session as remote background. From step 4 the helper also shows the banner, several technicians share one
  session and helper, the agent service asks for consent and stages pasted files, and the clipboard of the session is served by a second
  process that runs as the user signed in on it (`fleeto-agent remote-clipboard`). Step 6 does the same on Linux with X11: the helper,
  the clipboard process and a consent process (`fleeto-agent remote-consent`) on the display of the console.
  Details in §4, Remote control.
- Reconnect with exponential backoff plus jitter.
- Wire format: protobuf over the WebSocket, one message per binary WebSocket frame (the frame is
  the length prefix), results batched. Never one HTTP request per check result. Contract:
  `src/Fleeto.Protocol/Protos/agent.proto`.
- Endpoint class detection: Windows Server / Linux without a desktop session (no display manager unit and no graphical
  default target) / ESXi guests flagged as servers → `server`; everything else → `workstation`. The technician can override.

## 4. Key flows

**Enrollment.** Technician creates an enrollment token for a site (expiring, revocable,
optionally single-use, stored hashed). The UI produces a one-line install command per platform (Windows: PowerShell;
Linux: a POSIX shell command run with sudo or as root, which picks amd64 or arm64 from `uname -m` and runs the
installer from a directory under `/opt`, because `/tmp` is mounted without exec permission on hardened endpoints) that
embeds the instance FQDN, the token and the SHA-256 fingerprint of the instance CA
certificate. The command downloads the agent from the instance itself, `GET /agent/download/<platform>-<architecture>`
(`windows-amd64`, `windows-arm64`, `linux-amd64`, `linux-arm64`), which the web image carries and serves without
sign-in, rate limited. The agent installs and generates its key pair → fetches `GET /v1/ca` from
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

**Agent update (0.2.1).** `install.sh` places the release manifest it verified, with its signature, in the instance
directory; the gateway reads it read-only (`Gateway:ReleaseDirectory`), verifies the signature against the release keys it
was built with, checks every listed binary in its image (`Gateway:AgentBinariesDirectory`) against size and SHA-256, and
records the release as current (`AgentRelease`, audited as `agent_release.installed`) → every agent and watchdog session
gets an `UpdateOffer` with the manifest bytes, the signature and `update_allowed`: whether the ring of the endpoint's
policy (site policy, else the default policy) allows the release now. Preview installs at once, Standard 7 days and
Delayed 14 days after the release was installed on the instance; an admin can pause the release (nobody installs it,
installations already running finish) or release it to all rings at once (Settings, Agent updates). Offers are
re-evaluated when a release loads, when an admin changes a control (notification `fleeto_agent_releases`) and every
5 minutes, which also picks up a passed ring delay and a changed policy ring for live sessions.
→ the endpoint verifies the manifest against the release keys compiled into it and ignores the offer unless the version
is strictly newer than the installed one, allowed, not rolled back before on this endpoint and not waiting for a retry. It
waits a random 0 to 10 minutes per version, so a release does not reach every endpoint in the same second.
→ it downloads `GET /v1/releases/<version>/<file>` over mTLS (the gateway serves only files of the current manifest, at
most 20 downloads at a time and 12 per endpoint per hour, answering 503 or 429 with `Retry-After`), checks size and
SHA-256, and asks the staged binary for its version (`version --short`), which must equal the manifest version; a binary
that does not is skipped for good.
→ it writes `update-journal.json`, stops the target service, keeps the previous binary, puts the new one in place (hash
checked again), starts the service and waits for `health.json` of the target to report the new version with a gateway
connection made after the start: 5 minutes, not counting time the installer itself has no connection, at most 30.
→ healthy: the journal is removed and `installed` is reported. Not healthy: the previous binary is restored and started,
`rolled_back` is reported and that version is never tried again on this endpoint (`update-state.json`). Any other failure
leaves the old version running. A transient one (0.2.2: no connection or no answer from the gateway, a connection dropped during
the download, HTTP 500, 502 or 504, no answer to the watchdog certificate request, or a certificate error the gateway marks
`temporary` because the signer did not answer) is retried after 1 minute, doubling per further transient failure in a row up
to an hour, each wait between half and all of that so endpoints do not retry together. A refusal, a download that does not
match the manifest or a failing service change is retried after an hour. After a crash or power loss mid-replacement the service
restores the previous binary at its next start from the journal.
→ the watchdog installs the agent this way. The agent installs a missing watchdog from the offered release when the ring
allows it or the agent already runs that release (without the random delay: it is a repair), and updates the watchdog
only once it runs the release itself, so a new watchdog never supervises an older agent it was not released with. Every
step is reported (`UpdateStatus`) and shown per component on the endpoint detail and, for failures, in Settings, Agent
updates. An offered newer release that is held back is logged once per release and reason by the installing service (0.2.2):
waiting for the update ring, for the next attempt after a failure (with its time), until the agent runs the release itself,
or a version that was rolled back before. The same moment it reports `UpdateStatus` with state `waiting`, the reason and the
seconds until the wait ends (also for the random delay); the gateway stamps the end on its own clock and stores the wait next
to the last result. The endpoint detail states the wait, for the ring with the ring of the site and the day it reaches the
current release (or that the release is paused), and hides it once the component runs that release.

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
the right-click menu of the endpoint list), or on a selection of endpoints checked in that list (0.2.1, at most 500 per
run; only scripts that run on every chosen endpoint are offered), and picks a validity window (1 hour, 24 hours or 7 days) → web
checks role, tier, platform, client and approval, and writes one `Job` per endpoint in state
`pending_signature` with a snapshot of the current script version, a `SigningRequest` of kind `job` (only
the web role may create one, origin trigger) and an audit entry, all in one transaction. Endpoints that cannot run the
script are skipped with their reason and reported to the technician; a run on more than one endpoint also writes one audit
entry for the batch, and above the threshold of Settings, Scripts (0.2.1, default: more than 10 endpoints, 0 turns it off)
the same transaction queues an email to every admin naming the technician, the script, the endpoint count and the first ten
host names, so a large run cannot happen unseen → the signer locks
the job and checks again from the database: still `pending_signature` and within its validity window; the
initiator exists, has 2FA, is not locked out and is admin or technician; the endpoint is managed with the
license; the body of the stored version still has the snapshot hash and the language; the script is global
or of the endpoint's client; the language runs on the endpoint's platform; and, when the policy of the
endpoint's site (or the default policy) requires approval, the version is the script's current one and
approved by an admin who is not its author → it signs a `JobPayload` (`JobId`, `InstanceId`, `EndpointId`,
`Type`, `ValidUntil`, `InitiatedBy`, timeout, output cap, the account it runs as, and the script: language, name,
version, body, SHA-256) with the context `fleeto-job-v1` and sets `queued`, or sets `refused` with the reason → the
gateway sends queued, signed, valid jobs to managed sessions when they connect, on a notification and in
its 5-minute catch-up, and records `DeliveredAt` → the agent verifies the signature against the pinned
key, that instance and endpoint are its own, that `ValidUntil` has not passed (5 minutes clock tolerance)
and lies at most 7 days ahead, that its applied configuration is managed, the body hash and that the
language runs on its operating system. It dedupes on `Job.Id` (a seen list kept until 7 days after the
job's validity, plus the job directory) → it stores the signed job on disk, runs the script as the account the payload
names (below) and streams output as chunks (below) → it sends `JobCompletion`, and a refusal is a `JobCompletion`
with result `refused` and the reason.

A job that is not signed within 15 minutes becomes `refused` (the signer did not answer); a queued job whose
`ValidUntil` passed more than 10 minutes ago becomes `expired`; both show in the job history. A job can be
cancelled while it waits for its signature or is queued and not yet delivered; delivery and cancel are
atomic, so a delivered job cannot be cancelled. Scripts run at most 4 at a time per agent, with a timeout
from 30 seconds to 24 hours set per version (default 10 minutes); the whole process tree ends at the timeout
(a job object on Windows, a process group on Linux). A job that was running when the agent
stopped is reported as `interrupted` after the restart and becomes `lost`: it is never started again, since
running it twice could be worse than not knowing.

**The account a script runs as (0.2.1).** The technician chooses it per run: the agent's own account (SYSTEM on Windows,
root on Linux) or the signed-in user. The choice is part of the signed payload, so the agent never decides it. For the
signed-in user the agent takes the active session — the console session first on Windows, a graphical session first on
Linux, never root — and fails the job with "No user is signed in on this endpoint" when there is none; it never falls
back to its own account. With several active sessions (a remote desktop server) that is the console session or else the first one Windows lists, so the agent reports the account it used with the start of the job and the job history shows it.

**Choosing the user (0.2.2).** The agent reads the users with an active session every 30 seconds, off the session loop,
and sends the list with the next heartbeat when it changed on the connection (`SignedInUsers`: per user the SID on Windows
or the uid on Linux, the account name and the sessions, at most 200 users). The gateway stores only the latest list on the
endpoint; a watchdog cannot report one. When a script runs on one endpoint as the signed-in user, the run window lists
those users with the time of the report, and the technician picks one or keeps "whoever is signed in". Web accepts only a
user from the stored list; the signer signs the SID or uid into the payload (`run_as_user_id`) only with run as the
signed-in user, only in the form of a SID or uid, and only for an agent from 0.2.2, because an older agent would ignore
the field and run the script as whoever is signed in. The agent runs the script only in a session of that user (the
console session first) and fails the job with "The chosen user is not signed in on this endpoint" otherwise. The user is
matched by SID or uid, not by session number, so a session that another user took over in the meantime is never used.

**All signed-in users (0.2.2).** A run for all signed-in users, on one endpoint or a selection, is split in web: every
endpoint gets one job per user in its stored list, each with that user's SID or uid, all in the same batch. Every job is
signed, delivered, deduplicated and reported like any other, so result, exit code and output stay per user without a
change to the protocol or the agent. The list is the one of the moment the run starts: a user who signs in later gets no
job, and a user who has left fails their job with the reason above. Endpoints with an empty list or an agent older than
0.2.2 are skipped with the reason; a run holds at most 500 jobs and is refused whole above that. The admin notice threshold
counts endpoints, not jobs.

The script is staged in a directory that user may read and not change (Windows: under
`C:\ProgramData\Fleeto`, with a protected DACL for SYSTEM, the administrators and that user; Linux: a root-owned
directory under `/tmp` with the script owned by the user, mode 0400), and runs from the user's own profile or home
directory with that user's environment. On Windows the process is created with `CreateProcessAsUser` in
`winsta0\default`, so it can show a window; Go's process API cannot name a desktop, so the agent calls Win32 itself. The
output cap, the timeout and the process tree work the same in both cases. Script checks from the signed configuration
always run as the agent's own account.

**The output cap (0.2.1).** The policy of the endpoint's site sets how much output one job may send back, in whole
mebibytes between 1 and 200 (default 50). The signer reads it from the effective policy when it signs, so the value web
wrote on the job row never decides it; the gateway refuses chunks beyond the cap on the job row, and the agent stops
sending and marks the output truncated. Agent and gateway hold 200 MiB as an absolute ceiling whatever a policy or a
payload says.

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

**Remote session (0.3.0).** Two kinds, each in its own popup window opened from the endpoint list or detail: **remote
control** (the screen, served by the agent; from step 3) and **remote background** (terminal, files, services and
processes, served by the watchdog; the terminal from step 1). Transport, decided 2026-09-16 without a prototype: a
WebSocket relay through the gateway on port 443, no WebRTC. Traffic passes the VPS either way (TURN would too), UDP is
often blocked at customers, and every session gets its own sockets, so screen traffic never blocks an agent's control
connection. The flow for one technician (a participant):

1. The technician opens the window of a managed endpoint and starts the session, with an optional reason → the browser
   generates an ephemeral X25519 key pair; the private key is not extractable → web checks role, client scope, tier and
   that the serving service is online and new enough, then writes the `RemoteSession`, the `RemoteSessionParticipant` with
   the browser's public key, a `SigningRequest` (`RemoteSessionToken`, web only) and the audit entry
   `remote_session.requested` in one transaction, and waits up to 20 seconds.
2. fleeto-signer decides again from the database (participant still requested and at most 60 seconds old; user exists, has
   two-factor authentication, is not locked out and is an admin or technician; endpoint managed, license included; valid
   browser key) and signs a `RemoteSessionToken` (context `fleeto-remote-session-v1`): participant, session, instance,
   endpoint, kind, serving service, technician, browser key, issued at, valid until (60 seconds), and the policy's idle
   timeout and file size cap. Audited as `remote_session.signed`. A request web gave up on is never signed.
3. Web returns the token, its signature and the public key fingerprints (SHA-256 of the SubjectPublicKeyInfo) of the
   serving service's valid certificates. The browser opens `wss://<fqdn>/relay/v1/sessions/<participant>` (Caddy passes
   it to the gateway's relay port) and sends the token as its first message.
4. The gateway checks the Origin (the instance's web URL only), a per-address rate limit, the token signature against the
   instance signing keys, the participant in the path, the instance and the validity on its own clock, then **claims the
   participant once**: `Signed` becomes `Connecting` only while the stored token is byte for byte the one presented and
   still valid. It refuses an endpoint whose stored tier is not managed, and sends `RemoteSessionOffer` with the token over
   the control session of the serving service (at most 8 sessions per endpoint).
5. The service verifies the token against its pinned instance signing key (instance, endpoint, service, kind, validity with
   5 minutes clock tolerance), checks that its own verified configuration is managed, remembers the participant id so the
   token opens one session, generates its own ephemeral X25519 key and signs, with its certificate key (TPM or CNG where
   available), `fleeto-remote-endpoint-key-v1` || 0x00 || SHA-256(token payload) || its public key. It opens
   `wss://agents.<fqdn>/v1/relay/<participant>` with its client certificate and sends `RelayEndpointHello` (its key, the
   signature as r || s, and its certificate's SubjectPublicKeyInfo). A refusal goes back as `RemoteSessionRefused` over the
   control session and reaches the browser.
6. The gateway pairs the socket only with the certificate of exactly that endpoint and service (allow list), records the
   participant `Connected` and the session started, audits `remote_session.joined`, and gives the browser the key,
   signature and certificate key. The browser accepts the certificate key only when its fingerprint is one web gave,
   verifies the signature with it, and derives the keys.
7. Keys: X25519 shared secret → HKDF-SHA256 with salt SHA-256(token payload) and info `fleeto-remote-v1` || browser key ||
   endpoint key, 64 bytes: browser to endpoint, then endpoint to browser. Every frame is AES-256-GCM with a 96-bit nonce of
   four zero bytes and a 64-bit big-endian counter per direction, starting at 0 and never sent, so a changed, replayed,
   dropped, reordered or reflected frame fails and ends the session. From here the gateway passes binary messages of at
   most 1 MiB unread in both directions.
8. Inside the session every decrypted frame starts with a type byte: Hello (what the endpoint offers), Open and Opened (a
   terminal on a channel), Data (a 16-bit channel and raw bytes), Resize, CloseChannel, Closed (exit code), IdleWarning,
   End and Activity; bodies are JSON except Data. Without input for the policy's idle timeout (default 30 minutes) the
   endpoint warns 2 minutes before and ends the session.
9. When either side closes, the relay closes the other; the gateway ends the participant (`Ended`, with the reason) and the
   session once nobody is connected, and audits `remote_session.left`. Revoking the certificate or the endpoint, switching
   it to agent-only (checked on every endpoint status notification and every minute) and a gateway restart end the relay;
   at its start the gateway ends every connection left from before. The workers end participants that never connected
   within 5 minutes.

**Remote control (0.3.0 step 3, Windows).** Served by the agent, not the watchdog, because showing and using the screen needs a
process in the Windows session. The token carries the Windows session to show (`windows_session_id`, 0 for the console). The
session is the same in every way as remote background — token, relay, key exchange, AES-256-GCM frames, idle timeout — but it
carries the screen only: the agent ignores terminal, file, service and process frames on a remote control session, and the
watchdog refuses a remote control token (it serves remote background only). The agent runs as SYSTEM, so it can start a helper in
any session, including the console with the sign-in screen and UAC.

- The agent starts one helper process for the session, `fleeto-agent remote-helper`, as SYSTEM in the chosen Windows session (its
  own token, duplicated and moved to that session; needs `SeTcbPrivilege`, which SYSTEM has). The helper talks to the agent over
  anonymous pipes only it inherits, and lives in a job object that ends it when the agent stops. It attaches its thread to the
  input desktop and follows it as it switches (Default, Winlogon for the sign-in screen and UAC), so those are shown and usable,
  and it follows the console to another Windows session (fast user switching). A helper that stops is started again a few times.
- **Capture** (step 5): a whole monitor comes from DXGI desktop duplication (a few milliseconds where GDI needs tens of them for a
  large screen); "All monitors", a rotated monitor, an RDP session and anything duplication refuses use GDI (`BitBlt` into a DIB
  section), which works on every desktop, RDP sessions and VMs without a GPU. The first image of a new duplication comes from GDI
  (duplication's own first frame is black), a lost duplication (desktop switch, display change) is made again after a second, and
  the cursor is drawn in with GDI either way. With **tiles** the image is cut into 64-pixel tiles; only changed tiles
  travel, a run of changed tiles in a row as one rectangle, PNG when it has few colors (text, windows) and JPEG otherwise, at a
  quality that drops on a slow link. The endpoint sends the next frame only after the browser acknowledges the last, so a slow
  link never floods the relay. Frame types are a separate range (`0x10`–`0x1F`) next to the remote background frames.
- **H.264** (0.3.0 step 5, decided while building 2026-09-18). The browser names the codecs it decodes in `FrameStart`
  (`codecs: ["h264"]` where WebCodecs decodes H.264 Main profile); the hub passes the helper only the codecs every technician in
  the session named, so one browser without H.264 keeps everyone on tiles, and gives the helper a new Start when such a browser
  leaves. The helper encodes with Media Foundation: the GPU's hardware encoder first (an asynchronous transform driven by its
  events), the Microsoft H.264 encoder that ships with Windows otherwise; both run on a thread of their own with COM, so the capture
  thread can still follow the input desktop. The image is converted to NV12 (BT.709, limited range) in the helper. The encoder runs
  Main profile, no B pictures, low-latency mode, constant bit rate, key frames only when asked: on a Start (a technician joined or
  changed monitor), a desktop switch, a new size, an acknowledgement timeout, or a browser that asks again after a decoder error. A
  frame is one access unit (Annex B, SPS and PPS in front of every key frame) cut into `FrameVideo` parts of at most 768 KiB; the
  header carries the frame number and flags where `FrameUpdate` has them, so the hub's flow control treats both alike, plus the
  size and two timings for the browser's latency estimate (capture and encoding on the endpoint, time waited after the previous
  acknowledgement). An unchanged screen is encoded 8 more times so the picture sharpens, then nothing is sent until it changes.
  The **bit rate** starts at half of about 0.1 bit per pixel at 25 frames a second (2 to 16 Mbit/s) and follows the link: the
  round trip is learned from small frames, the bandwidth from frames of the planned size, and the bit rate aims at 70 percent of
  it, dropping at once and climbing 15 percent after 10 frames with room. **Fallback to tiles** is automatic: an endpoint without
  Media Foundation (Windows Server without the feature, N editions) or an encoder that fails while it runs (a failing hardware
  encoder first hands over to the software one) puts that helper on tiles and says why in `FrameInfo.fallback`; a browser whose
  decoder fails twice asks for tiles with an empty codec list. `FrameInfo` names the codec, the encoder and the capture, and the
  window shows them with frames a second, bit rate and the latency estimate. Measured on a developer laptop (Intel Core Ultra 5,
  3840 x 1080): capture 5.5 ms (DXGI) against 42–51 ms (GDI); 1920 x 1080 NV12 conversion 2.2 ms, encoding 6 ms (software) or
  16 ms (Intel hardware); the output decodes in Edge with WebCodecs. Older browsers and agents keep working: a Start
  without codecs gets tiles, and a `FrameInfo` without a codec shows as tiles.
- **Keyboard**: a key that produces a character is sent as that character and typed with the key of the endpoint's active layout
  and the modifiers it needs there (so AZERTY against QWERTY and the sign-in screen keep the character), a Unicode character when
  the layout lacks a key for it; named keys and Ctrl/Alt shortcuts go as the physical scan code. The helper remembers what it holds
  and releases everything when the window loses focus or the session ends, so no key stays stuck. **Mouse**: absolute position in
  the shown image, buttons and wheel. **Type clipboard** enters the browser clipboard as keystrokes. **Ctrl+Alt+Del** is sent by
  the agent service (`SendSAS`), which needs `SoftwareSASGeneration` to allow services; the agent sets it to 1 when it is missing
  (a group policy that sets it otherwise wins), and the button says why when it is off.
- **Reconnect** (decided 2026-09-17): a dropped session opens a **new** session in the same window with the same reason and Windows
  session, up to three tries; the audit log then shows two sessions. Resuming the same session is not done, because the token is
  single use and a session ends the moment nobody is connected.

**Remote control on Linux (0.3.0 step 6, X11, decided 2026-09-19).** The same session, hub, frames, tiles, keyboard plan, clipboard and
consent as on Windows; only the platform layer differs. The X11 protocol is spoken in pure Go with `github.com/jezek/xgb` (the agent
is built without cgo). Remote control on Linux needs agent 0.3.0-alpha.16 or later; web and the signer refuse an older one.

- **Only the console** is shown: the active session of seat0 (`loginctl show-seat seat0`), including the sign-in screen when it runs
  on X11 (LightDM, SDDM). Web offers no session choice for Linux, and the signer refuses any other session number. A Wayland session
  (and the GDM sign-in screen, which is Wayland) cannot be shown: the technician is told so in the window, and remote background
  works. A text console gets the same kind of message.
- **The display and its cookie**: the agent (root) finds the X server of that session in `/proc` (its display, `-auth` file and VT),
  and reads the display's MIT-MAGIC-COOKIE-1 from the server's own authority file, else from the session's `XAUTHORITY`, the user's
  `~/.Xauthority` or GDM's file. Some of those paths come from the user, so a file is read only when it is a regular file, opened
  without blocking, and at most 1 MiB; only a 16-byte cookie is taken from it.
- **Children drop root before they talk to X.** The agent starts `fleeto-agent remote-helper`, `remote-clipboard` and
  `remote-consent` as root with pipes, and gives each its display, cookie and account in the first pipe frame (`FrameX11`), never in
  its environment or command line. Each switches the whole process to its account before it connects: the helper to **nobody**, the
  clipboard and the consent prompt to the **user of the session**. A child that would stay root refuses to run. Children end when
  their input closes (the agent stopped), without a parent-death signal, which Go ties to a thread.
- **Screen**: `GetImage` of the root window (24-bit TrueColor, 32 bits a pixel, the format every PC X server gives; anything else is
  refused with the reason), the cursor from XFIXES blended in, monitors from RandR 1.5 (the whole screen without it), tiles as on
  Windows. H.264 is Windows only for now.
- **Input** through XTEST. The keyboard plan of keys.go is shared: a character is typed with the keycode and level (plain, Shift,
  AltGr as ISO_Level3_Shift, both) the X keyboard mapping has for it in the active XKB group; a physical key is its evdev code plus
  8. A character no key has is typed through a spare keycode mapped to its keysym for the moment and given back when the helper
  stops. Wheel notches are buttons 4 to 7. Ctrl+Alt+Del does not exist on Linux: the button is not shown.
- **Banner**: an override-redirect window at the top of the primary monitor in the brand's teal with white text (a core font, so
  nothing has to be installed), with an empty input shape so clicks pass through, raised every 2 seconds.
- **Consent**: a window in the middle of the primary monitor, as the user of the session, with Allow and Deny and the seconds left;
  Deny is the default (Enter and Escape refuse), and the keyboard is grabbed while it shows. No answer grants access (as on Windows).
  Nobody signed in (the sign-in screen) grants access at once.
- **Clipboard**: the CLIPBOARD selection, watched with XFIXES. Copied files are read as `x-special/gnome-copied-files` or
  `text/uri-list` (local `file://` paths only), text as `UTF8_STRING` or `STRING`; only what is copied during the session is offered.
  Text from the technician and pasted files are offered by owning the selection (`UTF8_STRING`, `STRING`, `TEXT`,
  `text/plain;charset=utf-8`, and for files `text/uri-list` and `x-special/gnome-copied-files` as a copy). Data over 64 KiB travels in
  INCR pieces both ways. Pasted files wait in `/run/fleeto-remote-clipboard` (memory; root-owned folders that others may pass through
  but not list or write), and are handed to the user of the session (owner, mode 0600) when they go on the clipboard.

**Several technicians (0.3.0 step 4, decided 2026-09-17).** Each technician gets their own participant, token and key exchange with
the endpoint; there is at most one remote control session per Windows session. When web opens remote control on a Windows session where
a session still has a technician in it or on the way, it adds the participant to that session (audited `remote_session.requested` with
`JoinsRunningSession`) instead of starting another.

- The agent joins every connection that names the same session id into one **hub** (`screen.Sessions`) with one helper: one screen,
  one monitor choice (a Start of any technician changes it for all and gives everyone a whole frame), one banner, and input from each.
  Every frame of the helper goes to each technician over their own queue. The hub acknowledges a frame to the helper when every
  technician drew it, except a technician who is more than 3 seconds behind someone who did; a technician whose queue reaches
  512 frames is disconnected ("too slow to follow the screen") so the others keep their screen. A technician who leaves releases
  every key and button; the last one ends the helper.
- Each technician gets the list of technicians (`FrameParticipants`) and the pointer positions of the others (`FramePeerPointer`, at
  most 25 per second), drawn over the screen with their names.

**Consent and banner (0.3.0 step 4).** The signer decides from the effective policy and the endpoint class and puts the result in the
token: `consent_required` and `banner_visible` only on a workstation, `consent_timeout_seconds`, and `clipboard_enabled` on every
endpoint (a missing field means off). Web and the signer refuse remote control for an agent older than 0.3.0-alpha.7, which would
ignore consent and banner.

- Only the **first technician** of a session is asked (decided 2026-09-17). Until the answer, nothing of that session reaches the
  screen: no helper starts, and the technicians who wait (the first and whoever joined meanwhile) see a countdown (`FrameConsent`).
  The agent service shows a message box on the Windows session (`WTSSendMessage`, Yes/No, default No, topmost, the consent timeout):
  Yes grants, No ends every waiting connection, no answer grants. With nobody signed in on the Windows session (the sign-in screen)
  access is granted at once; when the prompt cannot be shown the session ends, because the policy asked for consent. The outcome is
  reported for the audit log (`consent.granted`, `consent.refused`, `consent.timeout`, `consent.not_asked`, `consent.failed`, target the
  signed-in account). Everyone who joins a granted session sees the screen at once.
- The **banner** (when the first technician's token says so) is a click-through, topmost window at the top of the primary monitor
  naming every technician in the session, in the brand's teal. The helper shows it on a desktop thread of its own (a thread that owns
  windows cannot follow the input desktop), so it is part of the captured screen too.

**Clipboard (0.3.0 step 4).** Only for technicians whose token enables it; the endpoint refuses clipboard frames and requests otherwise.
The clipboard of a Windows session belongs to the user signed in on it: Windows Explorer hands its copied files out through OLE, and a
process running as SYSTEM gets nothing from it and cannot replace what is there (measured on the test endpoint, 2026-09-18). The agent
therefore starts a second process for the session, `fleeto-agent remote-clipboard`, with that user's own token (`WTSQueryUserToken`, their
environment, the same pipes and job object as the helper). It does nothing but the clipboard, so it can do no more than the user it runs as;
the screen, mouse and keyboard stay with the helper that runs as SYSTEM, because those need the sign-in screen and UAC. Without a signed-in
user (the sign-in screen) there is no clipboard, and the technician is told so when they use it. The clipboard process starts when the
screen does, so a copy on the endpoint is offered without the technician asking, and it moves along when the console switches sessions.

- **Text** both ways as UTF-8 (`FrameClipboard`, at most 512 KB). The clipboard process watches the clipboard of its window station with a
  clipboard format listener, and checks its sequence number every second so a notification that does not arrive costs a second instead
  of the whole session; a change that holds nothing is read again on the next tick, because a program copying files empties the clipboard
  before it fills it. What it cannot find on the plain clipboard it asks the clipboard's data object for (`OleGetClipboard`), because a
  program that copies files, Windows Explorer among them, leaves a marker on the clipboard and renders the files only when asked. It sends
  what another program copied; what a technician placed is never echoed back. The browser writes
  the text to the technician's clipboard when the window has the focus, otherwise at the next focus or click. The browser reads the
  technician's clipboard from the paste event (no clipboard permission): the paste shortcut (Ctrl+V, Shift+Insert) is held until the
  text went out, and the helper sets the clipboard before it injects the next input.
- **Files to the endpoint**, pasted or dragged into the window: the browser asks for a batch (`clipboard.begin`), uploads each file into
  it with the transfers of remote background (`clipboard.upload`, at most the policy's file size cap, at most 100 files) and places the
  batch (`clipboard.place`); the clipboard process puts them on the clipboard as `CF_HDROP` with the preferred drop effect copy, so pasting
  copies them. The **agent service** writes the files, into `%ProgramData%\Fleeto\RemoteClipboard\<session>\<batch>` with a protected DACL
  (SYSTEM and administrators full, the user signed in on the Windows session read only), and deletes the folder when the last
  technician leaves; the agent clears the whole folder when it starts. The helper takes its files off the clipboard when it ends.
- **Files copied on the endpoint** are offered to the browser by index (`FrameClipboardFiles`: names and sizes, folders counted but not
  offered); the technician downloads one with `clipboard.download`, only a file that is on the clipboard now. Uploads and downloads are
  audited as `clipboard.upload` and `clipboard.download` with the path.

**Client creation from template.** Technician picks a client template, enters code and
name → the sites in the template are created under the new client with their policy and
monitoring template links (references to the shared templates, not copies) → one audit
entry describes the whole operation.

**Integration poll.** Worker runs each integration on its own schedule with timeout, retry
and circuit breaker → results are translated into the same check/alert model as agent
data → hypervisor hosts and VMs that exist only in Proxmox or vCenter are created with
`Source = integration` under the mapped client and site.

**API call** (0.2.1). Rate limiter per client address (before anything else, so key guessing is capped) → client
sends `Authorization: Bearer <api key>` (only that header counts; a session cookie never authenticates an API call) →
web parses `flt_<id>_<secret>`, reads the key by its id on every call (so revocation is immediate), compares the SHA-256
of the secret in constant time (a wrong secret for an existing key writes an `api_key.authentication_failed` audit entry)
and checks revocation and expiry → rate limiter per key (token bucket, applied after authentication so nobody can use up
another key's budget) → the endpoint runs as a `Caller` with the read-only role, the key's client scope and actor type
`ApiKey`, through the same context factory, global query filters, tier rules and, where the shape fits, the same
application services as the UI → an `api.request` audit entry (method, path, query, status) is written before the
response leaves; when it cannot be written the data is withheld with a 500.

**Backup.** Nightly, worker runs `pg_dump` per instance →
the file is encrypted on the VPS (ephemeral X25519 with the backup public key, HKDF,
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
  root key (KEK)                     Docker secret, mounted in fleeto-web and fleeto-workers only
    └─ wraps → data keys (DEKs)      in the DB, one per purpose
                  └─ encrypt →       integration credentials, SMTP, Microsoft Graph secret or certificate key,
                                     webhook URLs and signing secrets, Action1, backup destination credentials,
                                     TOTP seeds, license document
  signer key (KEK)                   Docker secret, mounted in fleeto-signer only
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
- **Agent updates (0.2.1).** The binaries carry no signature of their own: the signed release manifest lists each binary
  with its SHA-256 and size, and the agent and watchdog install one only after verifying that manifest against the release
  keys compiled into them. The instance distributes but adds no trust, so a compromised instance can withhold an update,
  hold back a ring or pause a release, and never install a binary of its own. Downgrades are refused (strictly newer
  versions only), so an attacker cannot push an older signed release with a known flaw; a rolled back version is not tried
  again. The release workflow builds the binaries reproducibly and fails when those in the web and gateway images differ
  from the ones listed in the manifest. Authenticode signing of the Windows binaries is a separate, later step and does not
  replace this check.
- **Accepted risk:** a release that installs and connects but misbehaves later is not rolled back automatically; pausing
  the release stops it from spreading, and a fixed release has to be newer. An endpoint whose agent and watchdog are both
  broken needs the install command again.

### The signer

Whoever holds the instance signing key can run code as SYSTEM or root on every managed
endpoint of that customer. That is inherent to an RMM, so the key sits in its own minimal
container:

- **fleeto-signer** is the only container that mounts the signer key. It has no
  listening port and no outbound network access; it watches the database (LISTEN/NOTIFY)
  for `SigningRequest` rows. Its database role reads what it needs to decide and writes only
  signatures, request states and audit entries.
- Before signing it enforces rules independently of web: initiator role, managed tier of
  every target (a fourth layer of tier enforcement), script approval, validity window, rate
  limits (3,000 jobs per minute). The UI runs a script on one endpoint at a time; notifying every
  admin about bulk jobs arrives with running a script on a selection of endpoints.
- Optional four-eyes approval for scripts per policy (see §4, Script approval).
- **Accepted residual risk**: the signer decides on database content, and web can write to
  the database (what the other containers may change is limited by the triggers above). An attacker with full control of web can create jobs that pass the checks
  and have them signed, within the rate limits and, where approval is required, only for
  already approved scripts. What the signer does guarantee: the key cannot be stolen from
  web, a bug in web cannot skip the rules, every signature goes through one audited choke
  point, and the attack ends when web is cleaned up rather than when every agent is re-keyed.

### What a compromised gateway cannot do (security review of 0.3.0 step 7)

The gateway is the container every agent and every browser reaches, so it is the most exposed one. It keeps no key that signs anything,
but it does write to the database and it carries the relay. The review of 0.3.0 found three ways it could still have reached further, and
each is now closed:

- **What the signer signs cannot change after it was asked for.** The gateway updates jobs, remote sessions and participants (delivery,
  state, the join, the end), and the signer builds what it signs from those rows. Database triggers (migration `ImmutableSigningBindings`)
  now keep the binding columns as they were inserted, for every role: a job's endpoint, script, version, run-as and validity; a session's
  endpoint, kind, component and Windows session; a participant's session, endpoint, user and browser key. A signed payload or token cannot
  be replaced, and nothing goes back to waiting for a signature. Web also writes what it asked for into the signing request, which only web
  may create and nobody may update, and the signer refuses when the rows differ from it.
- **A watchdog certificate needs the agent.** A watchdog certificate is requested by the gateway over the agent's session. The agent now
  signs the request with the key of its own certificate (`fleeto-watchdog-csr-v1`), the gateway passes on the public key of the certificate
  the connection authenticated with, and the signer issues only when that key belongs to a current agent certificate of the endpoint and
  the signature verifies. Without it the gateway could have obtained a watchdog identity of its own and, with it, played the endpoint in a
  remote session (the browser trusts the fingerprints the instance knows). Agents older than 0.3.0-alpha.17 get no new watchdog
  certificate; their existing one keeps working and renews.
- **The gateway cannot change what an endpoint is.** A trigger refuses a change of tier, client, site or class by the gateway role, so the
  tier the signer and web check cannot be raised from the connection side.

Remaining by design: the gateway relays session traffic it cannot read, it can refuse or drop sessions, and it sees which technician works
on which endpoint. A compromised gateway that keeps a valid relay open is still a denial of service, never a way into a session.

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
- Watchdog certificates (0.2.1, §3) are separate certificates with the role *watchdog*: the gateway lets them open only a
  watchdog session, the recovery path refuses them, and `WatchdogCertificate` signing requests may only be created by the
  gateway role (origin trigger).
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

- Session token (0.3.0): signed by the signer with the instance signing key, valid 60 seconds to open the relay, single use
  (claimed once in the database by the gateway, remembered by the endpoint), bound to technician, endpoint, instance,
  session, participant, serving service and the browser's ephemeral public key.
- End-to-end encryption between browser and endpoint with the key exchange anchored outside the relay (§4 Remote session):
  the browser's key is in the signed token, the endpoint's key is signed with its certificate key, and the browser accepts
  that certificate key only by the fingerprint web gives it. A compromised gateway can drop a session but cannot read it,
  inject into it or take part in it; tests prove it against a hostile relay in Go and in the browser implementation
  (tampered, replayed, reordered and reflected frames, swapped keys, a token for another participant, endpoint or instance).
- Limit, stated plainly: the browser learns the fingerprints from web, and web can start sessions anyway. End-to-end
  encryption protects against the gateway and the network, not against a compromised web container.
- Tier enforcement in all four places: web, the signer, the gateway (stored tier at the claim, and a live relay ends when the
  endpoint leaves managed) and the endpoint (its own verified configuration).
- Visible on the endpoint (remote control, decided 2026-09-16): servers never prompt and show no banner; workstations follow
  the policy: consent prompt (default off, access granted after the timeout, an explicit refusal ends the session) and a
  banner naming every technician (default on). Remote background never prompts. Recording of sessions is not scheduled.
- Agent runs as SYSTEM on Windows to reach the console session, the login screen and UAC
  secure desktop; the same privilege is why the session token and signature checks are
  never optional. Remote control (0.3.0 step 3) runs a helper the agent starts as SYSTEM in
  the chosen Windows session, over pipes only it inherits, in a job object that ends it with
  the agent; it captures and injects input, never terminals, files or the network. A remote
  control token is served by the agent only, a remote background token by the watchdog only,
  and each service refuses the other's kind (§4 Remote session).
- Clipboard (0.3.0 step 4): text both ways and files to the endpoint, disabled per policy on every endpoint. It is served by a process with
  the token of the user signed in on the Windows session, which can do no more than that user and never touches the screen, the input or the
  network; the agent starts it only for a session a technician is allowed into. Pasted files are readable
  only by SYSTEM, administrators and the user signed in on the shown Windows session, and deleted when the session ends; a technician
  downloads only files copied on the endpoint, by index, never a path of their choice (remote background serves the file explorer).
- Several technicians (0.3.0 step 4): each has their own token, key exchange and audit entries; joining a session the person at the
  endpoint allowed does not ask again, and the banner names every technician. A relay slot per endpoint is taken atomically, so sessions
  that arrive together never exceed the limit of 8.
- **Against the person at the endpoint (security review of 0.3.0 step 7).** The clipboard and the files of a session are what an untrusted
  user at the endpoint could turn against the technician, so: text copied on the endpoint goes on the technician's own clipboard only right
  after they copied in the window, and is otherwise offered with a button; a file copied on the endpoint is opened with the rights of the
  user who copied it (impersonation on Windows, a thread with their credentials on Linux), so they cannot have the technician fetch a file
  they may not read; the clipboard process, which runs as that user, may send the agent clipboard frames only, never the screen; and an
  upload writes its part file without following a link, in a folder that is not a link, and only replaces the destination when the whole
  file arrived. Files pasted into a session wait in folders the agent creates with their access list in one step (Windows: inside a base
  folder owned by Administrators; Linux: root-owned, in `/run`), so nobody can put a link in their place.
- **A script that runs as the signed-in user, accepted risk (0.2.1).** That user can read the script text while it runs:
  the interpreter has to open the file as them. The script is staged so that they can read it and not change it, and the
  run window says so before the run starts. A script that carries a secret must run as the agent's own account.
- **Remote terminal (0.3.0), accepted risk.** The interactive terminal (§3) is available to
  admins and technicians on every managed endpoint, also where the policy requires script
  approval. On those endpoints script approval therefore only governs library scripts run as
  jobs: a technician who may open a terminal can run any command as SYSTEM. This is a deliberate
  product decision; the controls are the per-session signed token, end-to-end encryption and the
  audit entries per session and participant. Terminal transcripts are not recorded (recording is not scheduled).

### Backups

- Nightly `pg_dump` per instance. No WAL archiving (removed in 0.2.2, decided 2026-09-16): archived WAL cannot be
  restored without physical base backups, so a restore returns to the last nightly dump and up to 24 hours of changes
  can be lost. WAL archiving returns together with base backups for point-in-time recovery (ROADMAP.md, Not yet scheduled).
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
  the connection. The address sets the endpoint's Public IP (shown as a connection address in a private network when it is private), the logs and the per-address
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

Built in 0.2.1, **read-only** (decided 2026-09-15). The contract for integrators is `API.md`; features not in the API yet
are on `API-WAITLIST.md`, which every feature commit keeps up to date (CLAUDE.md, Public API).

- Base path `/api/v1` on the instance FQDN, served by fleeto-web (`src/Fleeto.Web/Api`). JSON only, `GET` only.
- OpenAPI 3.1 document at `/api/v1/openapi.json`, generated at runtime from the minimal API endpoints
  (`Microsoft.AspNetCore.OpenApi`), anonymous and rate limited per address. A test compares its operations with the
  `### GET /api/v1/...` sections of `API.md` in both directions, so an endpoint cannot ship undocumented and the
  documentation cannot describe an endpoint that does not exist.
- Authentication: `Authorization: Bearer <api key>`. Keys are created by admins in Settings, API keys: named, all
  clients or chosen clients, expiring after 30 days, 90 days, 1 year or never, revocable, shown once.
- Key format: `flt_<id>_<secret>`, where the secret is 32 random bytes (256 bits) from a cryptographic random generator.
  Only the SHA-256 of the secret is stored, which is enough because the input is long and random. The fixed `flt_` prefix
  lets secret scanners, ours and GitHub's, recognise a leaked key.
- A key acts as the read-only role within its client scope: other clients are invisible (lists leave them out, single
  objects are 404), and managed-only data (checks, notes) of agent-only endpoints answers 409 `endpoint_not_managed`.
- Resources in v1: clients, sites, endpoints (status, inventory, checks, notes), alerts, jobs (with output). Patch
  compliance follows with Action1 (0.4.0); everything else is on the waiting list.
- Response contract: separate types (`PublicApiModels.cs`) with explicit maps from the entities, so a renamed C# member
  can never change a field name or value. camelCase fields, snake_case enumeration values, ISO 8601 UTC timestamps,
  explicit nulls. A test walks every value of every mapped enumeration.
- Keyset pagination (`limit` up to 200, opaque `cursor`, `nextCursor`); an invalid cursor is a 400, never a silent
  restart at page one. The job list has its own index on `(CreatedAt, Id)`.
- Errors are problem details with a stable `code`, `title` (cause) and `detail` (next step). API paths bypass the HTML
  status code pages; an empty error response (unknown path, wrong method, a parameter of the wrong type) gets a problem
  body. `Cache-Control: no-store` on every response (personal data).
- Rate limits: 300 requests per minute per address (IPv6 per /64) before authentication, 120 per minute per key after;
  both configurable (`PublicApi` section). 429 with `Retry-After`.
- Audit: one `api.request` entry per authenticated call (key id and name as actor, route pattern as target, method, path,
  query, status, address); `api_key.created`, `api_key.revoked` and `api_key.authentication_failed` for key changes and
  wrong secrets. Audit details never contain the key or its hash. Volume is bounded by the per-key rate limit.
- Field names are stable from 1.0.0; breaking changes mean `/api/v2`.
- Not built from the earlier plan: `read_write` keys (write access waits for demand), `ETag` on single resources and
  rate-limit headers other than `Retry-After`; audit entries and license usage as resources (on the waiting list).

## 7. Install and update flow

`install.sh` is the only supported way to install or update the server. It operates per
instance; a VPS can hold several. Steaan runs every instance (SaaS, decided 2026-09-15), so
releases are GitHub Releases of the private repository and the images are private packages on
ghcr.io. `install.sh` is never piped from `curl` into a shell: it is downloaded from the release
with the release token, its signature is checked against the release public key taken from the
key ceremony record (never from GitHub), and only then run (`deploy/README.md`, First install).

```
openssl pkeyutl -verify -rawin -pubin -inkey steaan-release.pub -in install.sh -sigfile install.sh.sig
sudo ./install.sh                                                    # asks for the FQDN and, once, the GitHub tokens
sudo ./install.sh --fqdn rmm.customer.example --version 0.1.0        # pin a version for that instance
sudo ./install.sh --fqdn rmm.customer.example --check                # show installed vs. latest, change nothing
sudo ./install.sh --list                                             # instances on this VPS
sudo ./install.sh --github-tokens                                    # replace the GitHub tokens
```

After the first run `install.sh` carries the release public keys itself.

**Release source and access.** `install.sh` reads the published releases through the GitHub API
with a fine-grained token (Contents read-only on the repository) and pulls images with a classic
token that has only `read:packages`; both are asked once and stored root-only under
`/opt/fleeto/credentials/`, the registry login lives only in the run's temporary directory. The
newest published release is the target; a pre-release only when no release exists yet or an
instance on the VPS already runs a pre-release. The API decides nothing on its own: what it names
is used only after its manifest verifies. Residual risk: the release token can read the source
code, so a root compromise of a VPS exposes the code (not the keys); tokens expire and are one
pair per VPS (`deploy/README.md`, GitHub tokens).

**Release manifest.** Every release carries a manifest listing the version, the image
digests of every container, the hash of `install.sh` and (0.2.1) every agent and watchdog binary with its SHA-256 and
size (`agentBinaries`), signed with the release key outside CI (`deploy/sign-release.ps1`). `install.sh` verifies the
manifest, pulls images by digest only (never by tag) and replaces itself only with a version whose hash is in a verified
manifest. It copies the verified manifest and its signature to `/opt/fleeto/<instance>/release/` (mounted read-only in
the gateway, restored with the previous release on a rollback), so the gateway can offer that release to agents.

First run on a VPS: install Docker → install the host-level Caddy (pinned version with the
layer4 module, admin API on a local socket) with an empty routing table → create
`/opt/fleeto/`.

New instance: ask for or take the FQDN → check that both the FQDN and `agents.<fqdn>`
resolve to this VPS (fail early with the DNS records to create; an address that is not on the VPS
counts only after the operator confirms once that a firewall or NAT forwards TCP 80 and 443 on it) → derive the instance name
from the FQDN → create `/opt/fleeto/<instance>/` with Compose files → generate the root
key, signer key and DB passwords into `/opt/fleeto/<instance>/secrets/` (see Keys for
ownership and modes; never part of a backup) → verify the release manifest and pull images by
digest → run migrations → start the stack → the signer creates the instance signing key and
internal CA → regenerate the host Caddyfile from all instances: the layer4 module runs as a
listener wrapper on Caddy's own :443 server, sends `tls sni agents.<fqdn>` untouched (after a
PROXY protocol v2 header with the agent's address) to the gateway's loopback port and lets
everything else fall through to Caddy's TLS, which serves the FQDN with HSTS and proxies to the
web's loopback port (no second internal hop; real client IPs reach the web through
X-Forwarded-For), except `/relay/*`, which goes to the gateway's relay port (0.3.0; `RELAY_PORT` in instance.conf, allocated
from the top of the loopback range and kept; an instance from before 0.3.0 gets one at its next update) → print the URL, the one-time first-admin setup link
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
- Before an update, install.sh checks free disk space for the pre-update backup, a second copy of the database and the
  new images, and changes nothing when it is short (0.2.2). A restore waits for a healthy PostgreSQL, checks that
  `pg_restore` can read the backup, restores into `fleeto_restore` and only then swaps it in (`fleeto` becomes
  `fleeto_replaced`, which is dropped afterwards), so a restore that fails leaves the database it had. When a restore
  fails, the instance stays stopped in state `rollback-failed`, the backup moves to `backups/kept/`, and the next run
  starts the update again from the previous configuration.

Agents self-update from their instance (0.2.1), staged by the update ring of the site policy: see §4, Agent update. The
instance only distributes the binaries; the agent and watchdog install one only when the release manifest that lists it
verifies against a Steaan release public key compiled into them.

**Rename to Fleeto (0.2.1).** Until 0.2.1 the code, images, database and endpoint services used the internal name Fleetify.
Everything now uses Fleeto (decided 2026-09-15, `MD-Files/branding-fleeto.md` §7). Installations from before the rename move
once, without losing data or enrollments; the release is marked `rollback: restore`, because the previous release cannot
run under the new names.

- **VPS** (`install.sh`, section *Moving a VPS from the Fleetify layout*). The first run of a new install.sh copies the GitHub
  tokens and confirmed public addresses from `/opt/fleetify`. Updating then moves the whole VPS at once, after confirmation
  (`--yes`), because every instance's images only run under the new names:
  1. per instance: a pre-rename `pg_dump`, stop, copy of its directory to `/opt/fleeto/<instance>` (`instance.conf` keys
     `FLEETIFY_*` become `FLEETO_*`) and of its volumes to `fleeto-<instance>_*`, then, in the copy, database `fleetify`
     becomes `fleeto` and every `fleetify_*` role its `fleeto_*` counterpart (SCRAM passwords survive a rename; they are set
     again from the secret files anyway). A failure puts every instance back on the untouched old layout;
  2. the host proxy moves to `fleeto-caddy` with a copy of its volumes, so no certificate is requested again; its routes
     keep instances that are still in the old layout reachable (same loopback ports). The old install.sh is replaced by a stub;
  3. each instance is updated to the release. The migrator renames the database functions and triggers (migration
     `RenameToFleeto`, generic from the catalog, also for the role and channel names in function bodies and trigger
     arguments; a function that a later migration already wrote under the new name is kept, and `RestoreSigningRequestOrigin`
     repairs the signing request rule on instances moved before that was fixed), rewraps the data keys and seals the instance signing key and CA key again with the new associated data
     labels (`LegacyRenameUpgrade`, before the signer starts), renames the recovery codes marker and has every endpoint
     configuration signed again. An instance whose update fails runs again from the old layout and moves with the next run; one that succeeds
     loses its old copy, and `/opt/fleetify` goes once no instance is left in it.
- **Endpoints.** Agents from before the rename cannot update themselves (self-update arrives with this release). Until they are
  replaced they stay connected and keep their last configuration, but refuse new configurations and jobs, which are signed
  with the new contexts. Running the install command of the site again takes such an agent over without enrolling again,
  when it is enrolled with the same gateway and instance CA as the command: the service `fleetify-agent` is stopped, the state
  directory moves from `C:\ProgramData\Fleetify\Agent` to `C:\ProgramData\Fleeto\Agent` (the identity key stays in the key
  store under its old name, which the state records), the `fleeto-agent` service is created and started, and only then are
  the old service, a legacy watchdog with its key and the old program files removed. The install token is not used. A revoked
  agent, or one of another instance, is refused with the next step (`fleetify-agent uninstall`).
- **Data read under the old names**, never written: agent certificates with `urn:fleetify:endpoint:` (until renewed), license
  documents signed with `fleetify-license-v1`, backup files encrypted with the old HKDF salt, and key files with the old
  prefixes. `LegacyNames` holds these names; the branding check allows the old name only in the migration files. They are
  removed once no installation from before 0.2.1 exists.
- **Development** (`tools/dev/setup-dev.ps1`): moves `%LOCALAPPDATA%\Fleetify\dev`, renames the database `fleetify_dev` and the
  roles, and runs the migrations as above.

## 8. Repository layout

```
/CLAUDE.md                     rules, priorities, conventions, product model
/MD-Files/                     the rest of the documentation set (this folder)
/Fleeto.slnx                 solution; Directory.Build.props and Directory.Packages.props hold shared settings
/src/Fleeto.Core/            domain model, enums, pure domain rules (tiers, licensing, check evaluation), interfaces
/src/Fleeto.Protocol/        agent protocol v1 (agent.proto) and generated C#
/src/Fleeto.Infrastructure/  EF Core model and migrations, grants, crypto, CA, licensing, notifications, shared services
/src/Fleeto.Web/             Blazor Server UI and the read-only public REST API (Api/, 0.2.1)
/src/Fleeto.Gateway/         agent endpoint (.NET): enrollment, mTLS WebSocket sessions, ingest, the remote session relay (Remote/, 0.3.0)
/src/Fleeto.Web/wwwroot/js/  browser helpers; remote.js and remote-crypto.mjs run remote sessions (0.3.0)
/src/Fleeto.Web/wwwroot/lib/ vendored browser libraries with their source and hash (xterm.js, 0.3.0)
/src/Fleeto.Signer/          signing service: instance signing key, internal CA, signing rules
/src/Fleeto.Workers/         background jobs: config fan-out, check evaluation, alerts, email, license, backups, retention
/src/Fleeto.Tools/           fleeto-tool: migrate, license, release and backup key utilities
/agent/                        Go agent (one module, per-platform builds)
/tests/Fleeto.Testing/       shared test fixture: a real PostgreSQL database per test project
/tests/Fleeto.*.Tests/       unit and integration tests per component, cross-client and tier enforcement tests
/tests/Fleeto.LoadTest/      simulator for 10,000 agents
/tests/browser/                Node tests of the browser's remote session encryption against the Go vectors (0.3.0)
/tools/dev/                    local development without Docker: setup-dev.ps1, start-dev.ps1, build-agent.ps1
/deploy/                       Compose stack, host Caddy, install.sh (Dockerfiles live next to each project)
/.github/workflows/            CI: build, test, vulnerability scan, secret scan, branding grep
```

The gateway is .NET (decided for 0.1.0): it shares the domain model, EF Core and tier
enforcement with the rest of the server. `Fleeto.LoadTest` provides the 10,000-connection evidence.

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
