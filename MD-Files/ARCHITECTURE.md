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
                     │  │ valkey                     │   │ valkey                     │     │
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
                                                  │        └─ notify ─> [valkey] ─> [fleetify-workers] ─┘      │   │
                                                  └── remote control relay (ciphertext only)                   │   │
[integrations: Action1, Sophos, Veeam, Proxmox, vCenter] --> [poller workers] ─────────────────────────────────┘   │
                                                                                                                   │
[browser, API clients] --HTTPS--> [caddy] --> [fleetify-web: Blazor Server UI + public REST API] <─────────────────┤
                                                     ^                                                             │
                                                     +-- pub/sub (valkey) for live status                          │
                                                                                                                   │
                                              [fleetify-signer] ── LISTEN/NOTIFY, no listening port ───────────────┘
```

| Container | Scope | Role | Notes |
|---|---|---|---|
| **caddy** | VPS | Reverse proxy, automatic TLS | One per VPS, built with the layer4 module. Terminates TLS for the UI and API of every instance, so it holds the TLS private keys and ACME account for every FQDN on the VPS (see §5). Agent traffic to `agents.<fqdn>` is passed through by SNI to the instance gateway and never decrypted, so mTLS stays end to end between agent and gateway. |
| **fleetify-web** | instance | Blazor Server UI and public REST API (.NET, MudBlazor) | Follows the Migrify project layout and conventions. Cannot sign anything an agent executes. |
| **fleetify-gateway** | instance | Agent connection endpoint and remote control relay | Persistent WebSocket over mTLS for online state, command push and check results. Checks certificate revocation on every connection. Acks agent data only after it is written to Postgres. Target: 10,000 concurrent connections on modest hardware. Language: *decision pending* (.NET vs Go). |
| **fleetify-signer** | instance | Signs everything that establishes trust with agents | Holds the instance signing key and the internal CA key, decrypted with its own signer key that no other container mounts. No listening port: it picks up signing requests from the database. Re-checks role, tier, script approval and validity window before signing. See §5. |
| **fleetify-workers** | instance | Background jobs | Check evaluation, alerting, integration pollers, Action1 patch orchestration, retention cleanup, backups, license checks. |
| **postgres** | instance | PostgreSQL 16 + TimescaleDB | The only durable store. Relational data plus hypertables for check results and metrics plus log storage with full-text search. One database role per container with only the grants that container needs. |
| **valkey** | instance | Queues, pub/sub, short-lived caches | Never the only copy of anything. Losing valkey delays alerts and live updates; it never loses data. |

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
                │         ├──* Job, Alert, Note, InventorySnapshot, RemoteSession
                │         └──* AgentCertificate
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
| **Client** | `Id`, `Code` (unique, uppercase), `Name`, `CreatedAt` | Tenant boundary inside the instance. Every client-owned table carries its own `ClientId`, denormalized on purpose (see the rule below the table), and every query filters on it. |
| **Site** | `Id`, `ClientId`, `Name`, `Description` | Groups endpoints. Holds the links to policies and monitoring templates. Enrollment tokens belong to a site. |
| **Endpoint** | `Id`, `ClientId`, `SiteId`, `Hostname`, `Class` (`workstation`/`server`), `ClassOverride`, `Tier` (`agent_only`/`managed`), `Os`, `AgentVersion`, `LastSeenAt`, `Source` (`agent`/`integration`) | Endpoints without an agent exist only for hypervisor inventory (ESXi hosts and VMs from vCenter or Proxmox). `Tier` gates every feature server-side. |
| **AgentCertificate** | `Id`, `ClientId`, `EndpointId`, `Fingerprint`, `IssuedAt`, `ExpiresAt`, `RevokedAt?`, `RevokedBy?`, `RevokedReason?` | One row per issued certificate, renewals included. The gateway refuses every certificate with `RevokedAt` set. |
| **Policy** | `Id`, `ClientId?`, `Name`, settings | Agent behaviour: intervals, patch behaviour, update ring, script permissions and **script approval required**, remote control rules (consent, recording), maintenance windows. `ClientId` null = global. |
| **MonitoringTemplate** | `Id`, `ClientId?`, `Name` | Named set of `CheckDefinition`s with thresholds and alert rules. `ClientId` null = global. |
| **CheckDefinition** | `Id`, `MonitoringTemplateId`, `Type`, `Interval`, `Thresholds`, `AppliesToClass` | Interval from seconds to monthly. |
| **ClientTemplate** | `Id`, `Name`, sites with linked policies and templates | Blueprint used at client creation. Linked, not copied: later changes apply to every client using it; a technician can make an independent copy. |
| **Script**, **ScriptVersion** | `Id`, `ClientId?`, `Name`; version: `Number`, `Body`, `Sha256`, `AuthorId`, `ApprovedBy?`, `ApprovedAt?` | Every edit creates a new version. A version needs approval by a second admin before it runs on a site whose policy requires approval. |
| **Job** | `Id`, `ClientId`, `EndpointId`, `Type`, `Payload`, `ValidUntil`, `Signature`, `InitiatedBy`, `State` (`pending_signature`, `queued`, `running`, `succeeded`, `failed`, `expired`, `refused`), `ExitCode`, `OutputComplete`, `OutputTruncated` | One row per endpoint. Idempotent by `Id`. Never delivered or executed after `ValidUntil`. |
| **JobOutputChunk** | `ClientId`, `JobId`, `Stream` (`stdout`/`stderr`), `Sequence`, `Data`, `ReceivedAt` | Unique on `JobId`, `Stream`, `Sequence`. Protocol in §4, Job output. |
| **SigningRequest** | `Id`, `Kind` (`job`, `session_token`, `agent_csr`, `gateway_csr`, `policy`), `SubjectId`, `RequestedBy`, `State`, `RefusalReason?` | Written by web or gateway, processed by the signer. |
| **License** | `Id`, `CustomerName`, `Fqdn`, `ManagedEndpointCount`, `ExpiresAt`, `SignedDocument` (encrypted) | One per instance. Verified offline with the Steaan license public key baked into the build. Grace period of 14 days after `ExpiresAt`. |
| **ApiKey** | `Id`, `Name`, `Prefix`, `KeyHash`, `Scope` (`read`/`read_write`), `ClientIds?`, `CreatedBy`, `LastUsedAt`, `RevokedAt` | Plaintext shown once at creation. Format in §6. |
| **RemoteSession** | `Id`, `ClientId`, `EndpointId`, `TechnicianId`, `Reason`, `StartedAt`, `EndedAt`, `ConsentGiven`, `BrowserKeyFingerprint`, `RecordingRef?` | Every session, whether it connected or not. |
| **CheckResult** | `Time` (ingest), `ClientId`, `EndpointId`, `CheckDefinitionId`, `Status`, `Value`, `Payload` | TimescaleDB hypertable, compressed, retention policy. Deduplicated per endpoint and agent batch sequence number. |
| **Alert** | `Id`, `ClientId`, `EndpointId`, `CheckDefinitionId`, `Severity`, `State`, `AcknowledgedBy`, timestamps | Deduplicated per endpoint and check. |
| **Note** | `Id`, `ClientId`, `EndpointId?`, `SiteId?`, `AuthorId`, `Body` (markdown), timestamps | Searchable. |
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
move between clients; moving one means enrolling it again. Instance-wide tables (users,
roles, global templates with `ClientId` null, license, integrations) are not client-owned.

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
- Reconnect with exponential backoff plus jitter.
- Wire format: length-prefixed protobuf over the WebSocket, batched. Never one HTTP request
  per check result.
- Endpoint class detection: Windows Server / Linux without a desktop session / ESXi guests
  flagged as servers → `server`; everything else → `workstation`. The technician can override.

## 4. Key flows

**Enrollment.** Technician creates an enrollment token for a site (expiring, revocable,
optionally single-use, stored hashed). The UI produces a one-line install command that
embeds the instance FQDN, the token and the SHA-256 fingerprint of the instance CA
certificate. The agent installs, generates its key pair, connects to `agents.<fqdn>` and
refuses to continue unless the gateway's certificate chains to a CA with that fingerprint
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

**Endpoint removal and agent revocation.** Technician deletes an endpoint or clicks Revoke
agent (for a stolen laptop, a decommissioned machine, a suspected clone) → `RevokedAt` is
set on all of its certificates and an audit entry is written → the revocation is published
over pub/sub and the gateway closes the live connection at once → every new TLS handshake
checks the revocation set. The gateway keeps that set in memory, loaded from the database
at start, updated over pub/sub and fully reloaded every minute; a gateway that has not
loaded it accepts no agent connections. A revoked agent cannot renew and has to enroll
again with a new token.

**Duplicate agent identity.** A second connection with a certificate that already has a
live connection is refused → the endpoint gets an alert "Duplicate agent identity" → the
technician revokes the certificate and enrolls the copies again, each with its own
identity.

**Switching an endpoint to managed.** Technician flips the tier on the endpoint page or in
bulk → web checks the license pool (`ManagedEndpointCount` minus endpoints already managed)
→ refuses with a clear message when the pool is empty → otherwise sets `Tier = managed`,
requests signatures for the site policy and check definitions, pushes them to the agent,
writes an audit entry. License allocation is serialized: the pool check and the tier change
run in one transaction that first locks the instance's `License` row (`SELECT ... FOR
UPDATE`), so two concurrent requests can never both take the last free license. A bulk
switch is all or nothing: when the pool is too small, nothing changes and the message says
how many licenses are missing. A concurrency test proves it. Switching back to agent-only removes policy and checks from the
agent, closes its open alerts as "endpoint no longer managed" and frees the license.

**Check result.** Agent runs a check on its schedule → batches results with a per-agent
sequence number → gateway validates the client certificate (revocation included) and stamps
ingest time → writes the batch to the hypertable in one transaction, deduplicated on
endpoint and sequence number → acknowledges the sequence number → agent removes the batch
from disk → gateway publishes a notification on valkey → worker evaluates thresholds,
opens/updates/closes alerts → publishes the status change over pub/sub → the UI updates
without polling. When valkey is down, workers catch up from the database from their last
watermark: alerts are late, nothing is lost. When Postgres is down, the gateway does not
acknowledge and agents keep buffering on disk.

**Job.** Technician starts a script or patch action, picks the targets and a validity window
(default 24 hours, maximum 7 days) → web writes one `Job` per endpoint in state
`pending_signature` and a `SigningRequest` → signer reads the job from the database and
checks: the initiator has the role for this job type, every target is managed, the script
version is approved when the target site's policy requires approval, the validity window
is within the maximum, the rate limit is not exceeded → signs the canonical payload
(`JobId`, `InstanceId`, `EndpointId`, `Type`, `Payload`, `ValidUntil`, `InitiatedBy`) or
refuses with a reason → gateway pushes the job to online agents and queues it for offline
ones, and never delivers it after `ValidUntil` → agent verifies the instance signature,
checks that `InstanceId` and `EndpointId` are its own and that `ValidUntil` has not passed
(5 minutes clock tolerance), dedupes on `Job.Id` (ids are kept until their `ValidUntil`) →
executes and streams output as chunks (below) → sends a completion message → audit entry
written. A job that runs out of time is marked `expired` and shows in the job history.

**Job output.** Output is never one message. The agent writes stdout and stderr to disk and
sends them as `JobOutputChunk` messages of at most 64 KiB, each carrying `JobId`, `Stream`
(`stdout`/`stderr`) and a `Sequence` number per stream starting at 0 → the gateway stores
each chunk idempotently (unique on `JobId`, `Stream`, `Sequence`; a duplicate is
acknowledged and ignored) and acknowledges it after the write → the agent deletes a chunk
from disk only after its ack and, after a reconnect, resends every unacknowledged chunk.
When the process exits the agent sends `JobCompletion` with the exit code and, per stream,
the final chunk count, total byte count and SHA-256 → the job becomes `succeeded` or `failed`
only when every chunk up to that count is stored and the hashes match; until then it shows
"output incomplete" and the gateway asks the agent for the missing sequence numbers. Output
per job is capped (default 50 MiB, configurable per policy); beyond the cap the agent stops
sending, records the truncation in `JobCompletion` and the UI says so. Execution is never
repeated to recover output.

**Script approval.** When a site's policy has script approval required, jobs for its
endpoints may only run library scripts whose current version is approved. An author saves
a version → a different admin reviews the body and approves it after entering a fresh TOTP
code → the approval is stored with the version's SHA-256 → any change creates a new
version that needs approval again. Ad-hoc scripts are refused for those sites. The setting
is off by default and recommended for server sites.

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
                  └─ encrypt →       integration credentials, SMTP, Action1, backup destination credentials,
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
  generated by `install.sh`, root-owned, mode 0600, each with an offline backup made during
  the key ceremony.

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
  limits. Bulk jobs above a configurable endpoint count notify every admin.
- Optional four-eyes approval for scripts per policy (see §4, Script approval).
- **Accepted residual risk**: the signer decides on database content, and web can write to
  the database. An attacker with full control of web can create jobs that pass the checks
  and have them signed, within the rate limits and, where approval is required, only for
  already approved scripts. What the signer does guarantee: the key cannot be stolen from
  web, a bug in web cannot skip the rules, every signature goes through one audited choke
  point, and the attack ends when web is cleaned up rather than when every agent is re-keyed.

### Agent identity and revocation

- Per-agent certificates, 90 days, automatic renewal, TPM-backed keys where available (§3).
- Revocation is a deny list in the database (`AgentCertificate.RevokedAt`), checked by the
  gateway on every handshake and pushed to live connections at once. No CRL or OCSP
  infrastructure. Deleting an endpoint revokes its certificates.
- A certificate connecting twice at the same time is refused and raises an alert (§4).
- The gateway has no persistent private key: at start it generates one in memory and gets a
  24-hour server certificate for `agents.<fqdn>` from the signer, renewed well before expiry.

### Jobs

- Every signed payload carries `InstanceId`, `EndpointId` and `ValidUntil`, so a signature is
  valid for one endpoint of one instance for a limited time and cannot be replayed elsewhere
  or weeks later.
- Default validity 24 hours, maximum 7 days, enforced by the signer, the gateway and the agent.

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

### Backups

- Nightly `pg_dump` plus continuous WAL archiving, per instance.
- Encrypted on the VPS before upload, using the instance's **backup public key** for key
  agreement only. Per file: generate an ephemeral X25519 key pair → X25519 with the backup
  public key gives a shared secret → HKDF-SHA256 derives a file key → the file is encrypted
  in chunks of 1 MiB with AES-256-GCM (nonce from chunk counter, last chunk flagged so
  truncation is detected) → the ephemeral public key is stored in the file header. Never
  "encrypt with X25519" directly. The private key never exists on the VPS, so neither a
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
- One database role per container with the minimum grants; the gateway has no access to
  encrypted secrets and does not mount the root key.
- Every privileged action writes an `AuditEntry`; the table has no update or delete path.
- Per-endpoint rate limiting, strict input validation, parameterized queries only, CSP
  without unsafe-inline, TOTP 2FA for every user, API keys hashed and scoped.
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
key, signer key and DB passwords into `/opt/fleetify/<instance>/secrets/` (root, 0600,
never part of a backup) → verify the release manifest and pull images by digest → run
migrations → start the stack → the signer creates the instance signing key and internal CA
→ add the HTTPS route for the FQDN and the SNI passthrough route for `agents.<fqdn>` to
Caddy → print the URL, the one-time first-admin setup link and a reminder to run the key
ceremony (offline copies of root key and signer key, backup key pair). First-admin setup
asks for the backup destination and the backup public key.

Update run for an instance: verify the new release manifest → detect installed version →
back up the database (kept locally, 0600, until the next successful update; the nightly
off-VPS backup is separate) → pull the requested images by digest → run migrations →
restart the stack → health check → on failure roll back to the previous images.
`install.sh --all` updates every instance on the VPS in turn.

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

## 8. Repository layout (planned)

```
/CLAUDE.md                 rules, priorities, conventions, product model
/MD-Files/                 the rest of the documentation set (this folder)
/src/Fleetify.Web/         Blazor Server UI + public REST API
/src/Fleetify.Core/        domain model, services, interfaces, license and tier checks
/src/Fleetify.Infrastructure/  EF Core, TimescaleDB, valkey, integrations
/src/Fleetify.Workers/     background jobs
/src/Fleetify.Signer/      signing service: instance signing key, internal CA, signing rules
/src/Fleetify.Gateway/     agent endpoint and remote control relay (language decision pending)
/agent/                    Go agent (one module, per-platform builds, remote control per platform)
/tests/                    unit, integration, cross-client, tier enforcement, signer rules, load test (Fleetify.LoadTest)
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
