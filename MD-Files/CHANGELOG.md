# Changelog

All notable changes to Fleeto are documented here, newest first, following the
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) layout and semantic versioning.

This file holds the `Unreleased` section and the **two most recent released versions**.
When a third released version is added, the oldest entry moves to the top of
`CHANGELOG-ARCHIVE.md` in the same commit.

## [Unreleased]

Implementation of 0.0.x (foundation) and 0.1.0 (first usable release), to be released as 0.1.0.

### Added

- .NET 10 solution: `Fleetify.Core`, `Fleetify.Protocol` (agent protocol v1), `Fleetify.Infrastructure`,
  `Fleetify.Web`, `Fleetify.Gateway`, `Fleetify.Signer`, `Fleetify.Workers`, `Fleetify.Tools`
  (`fleetify-tool`), per-component test projects against a real PostgreSQL, and `Fleetify.LoadTest`.
- PostgreSQL 17 schema with migrations: client-scoped tables with composite foreign keys and
  consistency triggers, append-only audit log, notification triggers, TimescaleDB hypertable when
  installed, least-privilege grants per container role.
- Envelope encryption with a root key and per-purpose data keys; signer key for the instance
  signing key and internal CA; Argon2id passwords; encrypted TOTP keys and hashed recovery codes.
- Go agent for Windows (`fleetify-agent`): Windows service install, enrollment with CA fingerprint
  pinning, TPM-backed or software CNG key, mTLS WebSocket session, signed configuration, CPU,
  memory, disk, service and uptime checks with jitter, on-disk result buffer, inventory, renewal,
  revocation handling. Linux and macOS compile as stubs.
- Gateway: enrollment endpoint, public CA endpoint (`/v1/ca`), mTLS sessions with an allow list,
  duplicate identity detection, idempotent batch ingest acknowledged after commit, config
  delivery with tier enforcement, short-lived gateway certificate from the signer.
- Signer: key bootstrap, enrollment, renewal, gateway certificate and agent configuration
  signing, with every rule re-checked against the database and rate limits.
- Workers: configuration fan-out, check evaluation with per-endpoint cursors, alerts with
  deduplication and escalation, offline and duplicate identity alerts, email outbox (SMTP or
  pickup directory), license monitoring, encrypted backups (pg_dump and WAL shipping to
  S3-compatible storage or a directory), retention.
- Web UI (Blazor Server, MudBlazor): first-admin setup, mandatory 2FA, dashboard with live
  updates, clients, sites with Workstations/Servers/Mixed tabs and enrollment tokens, endpoint
  pages, alerts, and a settings workspace (settings panel opened from the sidebar footer) with
  client and monitoring templates, policies, users, licensing, email, notification channels,
  backups, audit log and instance.
- Licensing: signed license documents, license page, serialized license allocation, 14-day
  grace period, protected clock.
- Deployment: Dockerfiles, per-instance Compose stack, host Caddy with layer4 SNI passthrough,
  `install.sh` with signed manifests, update and rollback, GitHub Actions CI and release
  workflows, `deploy/README.md` and `deploy/RELEASING.md`.
- Local development without Docker: `tools/dev/setup-dev.ps1`, `start-dev.ps1`,
  `build-agent.ps1`.
- Git repository with `.gitignore` (secrets, IDE, .NET, Go), `.gitattributes` (LF, shell
  scripts always LF), `.editorconfig` and `README.md`.
- `fleetify-signer` container in the design: sole holder of the instance signing key and
  internal CA, no listening port, enforces signing rules independently of web.
- Optional four-eyes script approval per policy.
- License grace period of 14 days after expiry.
- Job output protocol: numbered chunks per stream, idempotent storage, per-chunk ack,
  completion message with counts and hashes, output cap per job.
- Migration compatibility policy (expand/contract) with a restore-based rollback for
  releases that cannot comply.

### Changed

- Agent gateway routing decided: SNI passthrough on port 443 for `agents.<fqdn>`.
- Gateway acknowledges agent data only after it is written to Postgres.
- No Valkey: cross-container notifications use PostgreSQL LISTEN/NOTIFY, because the signer may
  only talk to the database; every subscriber also catches up from the tables.
- PostgreSQL 17 instead of 16, locally and on the VPS.
- Certificates (internal CA, agents, gateway) use ECDSA P-256 because Windows SChannel does not
  support ed25519 in TLS; application signatures stay ed25519.
- Agent certificate checks use an allow list (issued, not revoked, not expired) instead of a deny
  list, so a deleted endpoint can never reconnect.
- Gateway implementation language decided: .NET.
- A site links at most one policy; the instance default policy applies otherwise.
- CheckResults has no foreign keys (hypertable ingest); the workers purge results of deleted
  endpoints.
- Off-VPS encrypted backups moved from 0.7.0 to 0.1.0 in the roadmap.
- Every client-owned table carries its own `ClientId`, kept consistent by composite foreign
  keys; tables that can be global or client-specific (policies, monitoring templates, check
  definitions, scripts, script versions) carry a nullable `ClientId` equal to their parent's,
  enforced by a constraint trigger.
- Job execution state and output state are separate (`State` and `OutputState`), with a
  `lost` state for jobs that never report completion.
- License allocation and tier change run in one serialized transaction.
- Enrollment response relies on the TLS connection validated against the pinned CA
  fingerprint instead of a separate signature.
- License clock-rollback protection documented with its precise threat model.

### Security

- Separate Steaan release signing key (offline, hardware token) for agent binaries,
  `install.sh` and the release manifest; instance signing key limited to jobs, policies,
  check definitions and session tokens.
- Only the gateway role can request certificate signatures and only workers or the signer can
  request configurations (database trigger), so a compromised web or workers container cannot
  obtain a gateway certificate.
- Enrollment fetches the instance CA from `/v1/ca`, matches it against the install fingerprint
  and sends the token only over a connection verified against that CA.
- Agent certificate revocation (allow list checked on every connection), 90-day certificates
  with renewal, TPM-backed keys where available, duplicate identity detection, CA
  fingerprint in the install command.
- Signed jobs carry `InstanceId`, `EndpointId` and `ValidUntil`.
- Remote control key exchange bound to the signed session token and the agent certificate.
- Backups encrypted per file with ephemeral X25519 key agreement against a backup public key
  whose private half stays offline, HKDF and chunked AES-256-GCM with a per-file counter
  nonce under a key that is never reused; write-only storage credentials.
- `install.sh` verified by signature instead of `curl | sudo bash`; images pulled by digest.
- API key format with 256-bit secret and `flt_` prefix; enrollment tokens stored hashed.
- Documented that the host Caddy holds the TLS keys of every instance FQDN on the VPS, and
  the accepted residual risk of a compromised web container.

## [0.0.0] — 2026-09-14

### Added

- Documentation set: `CLAUDE.md` in the repository root; `ARCHITECTURE.md`, `ROADMAP.md`,
  `CHANGELOG.md`, `CHANGELOG-ARCHIVE.md` and `branding-fleeto.md` in `MD-Files/`.
- Deployment model: one instance per customer with its own FQDN, several instances per VPS.
- Product model: client → site → endpoint, policies, monitoring templates, client templates
  (linked, not copied).
- Licensing per endpoint with two tiers: agent-only (free) and managed.
- Remote control built in, with two-way clipboard.
- Public REST API with scoped API keys.
- Decision to delegate patch management to Action1 instead of a home-grown patch engine.
- Secrets policy: everything encrypted in the database, one root key per instance outside it.
- Install and update through a single `install.sh`, with the FQDN chosen at install time.
