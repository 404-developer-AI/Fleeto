# Changelog

All notable changes to Fleeto are documented here, newest first, following the
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) layout and semantic versioning.

This file holds the `Unreleased` section and the **two most recent released versions**.
When a third released version is added, the oldest entry moves to the top of
`CHANGELOG-ARCHIVE.md` in the same commit.

## [Unreleased]

Implementation of 0.0.x (foundation) and 0.1.0 (first usable release), to be released as 0.1.0, and work on 0.2.0
that started on 2026-09-15 on top of it (entries starting with "0.2.0:"). Pre-release `0.2.0-alpha.1` (2026-09-15) is a
test build of this state for the first CI run; its release build failed on the Caddy image. `0.2.0-alpha.2` was published for the
first VPS install; `0.2.0-alpha.3` adds support for a VPS behind NAT, `0.2.0-alpha.4` fixes what the first install found, and `0.2.0-alpha.5` sets the network MTU. Pre-releases are not releases, so their entries stay here.
Work on 0.2.1, which holds everything still open for 0.2.0, has entries starting with "0.2.1:". Pre-release `0.2.1-alpha.1`
(2026-09-15) was the first test build of it; its release build failed while exporting the agent binaries, before any
image was pushed. `0.2.1-alpha.2` fixed that; moving the test VPS with it fell back to the old layout, because the signer could
not open its keys under the new name. `0.2.1-alpha.3` seals them again during the move: public API, agent self-update and
watchdog, and the rename to Fleeto with the move of the test VPS from the Fleetify layout.

### Added

- 0.2.1: Read-only public REST API at `/api/v1`: clients, sites, endpoints with status, inventory (hardware, disks, network,
  software, services), checks and notes, alerts and jobs with their output. Keyset pagination, camelCase JSON with snake_case
  values and UTC timestamps, problem details with a stable error code, an OpenAPI 3.1 document at `/api/v1/openapi.json`.
  Agent-only endpoints answer checks and notes with `endpoint_not_managed`; a key limited to clients sees nothing of other
  clients.
- 0.2.1: API keys under Settings, API keys (admins): a name, all clients or chosen clients, an expiry of 30 days, 90 days, 1 year
  or none, shown once, revocable, with the last use.
- 0.2.1: `MD-Files/API.md`, the complete API contract for integrators, and `MD-Files/API-WAITLIST.md`, the features that are not
  in the API yet. A test fails when the OpenAPI document and `API.md` list different endpoints.
- 0.2.1: Agent self-update with update rings. Each policy chooses Preview (at once), Standard (7 days) or Delayed (14 days) after
  the instance installed the release. Settings, Agent updates (admins) shows the release, when each ring gets it, how many agents
  run it and which updates failed, and lets an admin pause the release or release it to all rings. The endpoint detail shows the
  installed version, service state and latest update of the agent and the watchdog.
- 0.2.1: Watchdog service `fleeto-watchdog` on Windows: a second service with its own certificate that keeps the agent running
  and installs agent updates, rolling back a version that does not connect within 5 minutes. The agent installs a missing
  watchdog, keeps it running and updates it. New alerts "Agent service stopped" (the watchdog is online, the agent is not) and
  "Watchdog stopped" on managed endpoints; the offline alert now opens only when both are gone.
- 0.2.1: The gateway image carries the agent and watchdog binaries and serves them to enrolled endpoints; `install.sh` hands the
  verified release manifest to the gateway. `tools/dev/build-agent.ps1` builds both binaries and, with `-Sign`, a signed
  development manifest.
- 0.2.1: Linux agent and watchdog for Ubuntu LTS 22.04 and 24.04, Debian 12 and newer (including Proxmox VE hosts) and the RHEL
  family 8 and 9 (RHEL, Rocky Linux, AlmaLinux). `fleeto-agent install` enrolls the endpoint and writes the systemd units
  `fleeto-agent.service` and `fleeto-watchdog.service`, which run as root, start at boot and keep each other running; a unit an
  administrator disabled or masked is reported and left alone. The identity key is created inside the TPM 2.0 where the endpoint
  has one and is a root-only key file otherwise. Inventory reads the distribution, the kernel, the hardware (DMI), the packages
  (dpkg or rpm) and the systemd services, which the check dialog offers like Windows services. Checks, scripts (sh and bash) and
  jobs run as root; the disk check skips images, container layers and network shares.
- 0.2.1: Agents for amd64 and arm64 on both platforms. The release manifest lists the agent and the watchdog for `windows-amd64`,
  `windows-arm64`, `linux-amd64` and `linux-arm64`, the instance serves them at `/agent/download/<platform>`, and the install
  command of a site is shown per operating system and picks the architecture of the endpoint itself.

- 0.2.0: Maintenance mode for a client, a site or a managed endpoint, started from the client and site settings menus and
  the right-click menu of the endpoint list, for 1, 4 or 24 hours, until a chosen time or until turned off, with an
  optional reason. No check or offline alert opens or escalates while it lasts; open alerts still resolve, duplicate
  identity alerts still open, and the first failing result afterwards opens the alert at once. The clients panel shows
  "all" or "2/14" per client and site, the endpoint list and detail show "In maintenance until 16:00", and the endpoint
  list has an "In maintenance" filter. Start, change, end and expiry are audit entries (without the reason).
- 0.2.0: Check catalog: ping, TCP port, HTTP(S) URL (status, response time, expected text and certificate expiry), process
  running, pending restart (Windows, Linux), file or folder (exists, must not exist, size, hours since the last change),
  certificate expiry (Windows certificate store or certificate files), event log (Windows) and antivirus and firewall
  (Windows), next to CPU, memory, disk, service and uptime. One description per type (`CheckCatalog`) drives validation,
  evaluation, alert titles and the check dialog; a check reaches only endpoints whose platform runs it.
- 0.2.0: Certificate recovery: an agent whose certificate expired while it was offline renews it with that certificate and the
  same key, up to a year after expiry, unless it was revoked or replaced. Enroll again on an endpoint creates a single-use
  install command that lets a reinstalled or long-offline agent take over the endpoint with its history; earlier certificates
  are revoked.
- 0.2.0: Maintenance windows in policies: recurring on chosen days at a local start time, for a duration, in a time zone, for all
  endpoints, servers or workstations. While a window runs, endpoints of the sites that use the policy are in maintenance; the
  endpoint list and detail name the policy.
- 0.2.0: Routing rules per notification channel: alerts of all clients or of chosen clients, with the minimum severity and
  resolves as before.
- 0.2.0: Webhook notification channels next to email: generic JSON signed with an `X-Fleeto-Signature` header (secret shown
  once, replaceable), Slack and Microsoft Teams. Retries with backoff, a circuit breaker per channel, a test message and the
  last delivery on the notification channels page. Webhooks only go to public https addresses.
- 0.2.0: Email through Microsoft Graph as an alternative to SMTP, with a client secret or a certificate created by Fleeto
  (downloaded and uploaded to the app registration, then switched to). SMTP, when configured, takes over while the Graph
  credential has expired.
- 0.2.0: Warnings for expiring credentials (the Microsoft Graph secret or certificate): a dashboard warning from 30 days
  before the end date and admin emails at 30, 14, 7 and 1 days and after expiry.
- 0.2.0: Check history per check of an endpoint for the last hour, day, week, month and year: a line chart with average,
  lowest to highest and thresholds for numeric checks, a status timeline for yes/no checks. Hourly and daily rollups kept 13
  months, maintained by the workers together with the evaluation.
- 0.2.0: Script library under Settings, Templates, Scripts: PowerShell and Batch scripts for Windows, sh and bash for Linux
  and macOS, global or for one client, with a description and a timeout. Saving a change creates a new version; older versions
  stay readable. A policy can require approval of scripts by a second admin, who confirms with an authenticator code; a
  change needs approval again.
- 0.2.0: Jobs: run a library script as SYSTEM or root on a managed endpoint from its Jobs tab or the right-click menu of the
  endpoint list, valid for 1 hour, 24 hours or 7 days. The signer checks role, tier, platform, client and approval again
  before it signs; the agent verifies the signature, the endpoint, the validity and the script hash, runs the script with
  its timeout and sends stdout and stderr in numbered chunks that it keeps on disk until the gateway stored them. The Jobs
  tab shows state, exit code and output (live while the job runs), the dashboard shows recent jobs, and a job waiting for
  delivery can be cancelled. Jobs that were not signed, expired, refused or lost stay in the history with the reason.
- 0.2.0: Script check: runs a library script on its interval; exit code 0 is OK, 1 a warning and any other code critical, and the
  first line of output is the detail. Available in monitoring templates and on one endpoint, only for scripts of the same scope and
  platform. Where the policy requires approval the check runs the newest approved version. A script that checks use cannot be
  deleted.
- 0.2.0: Services in the inventory (Windows): name, display name, start type and state. The check dialog suggests them for a
  service check, from the endpoint or, for a monitoring template, from the endpoints that run it; typing a name still works.
- .NET 10 solution: `Fleeto.Core`, `Fleeto.Protocol` (agent protocol v1), `Fleeto.Infrastructure`,
  `Fleeto.Web`, `Fleeto.Gateway`, `Fleeto.Signer`, `Fleeto.Workers`, `Fleeto.Tools`
  (`fleeto-tool`), per-component test projects against a real PostgreSQL, and `Fleeto.LoadTest`.
- PostgreSQL 17 schema with migrations: client-scoped tables with composite foreign keys and
  consistency triggers, append-only audit log, notification triggers, TimescaleDB hypertable when
  installed, least-privilege grants per container role.
- Envelope encryption with a root key and per-purpose data keys; signer key for the instance
  signing key and internal CA; Argon2id passwords; encrypted TOTP keys and hashed recovery codes.
- Go agent for Windows (`fleeto-agent`): Windows service install, enrollment with CA fingerprint
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
  updates, clients, sites with Workstations/Servers/Mixed tabs and enrollment tokens, a
  right-click menu on the endpoint list (edit class and site, switch tier, revoke agent, delete
  endpoint), endpoint pages (Summary with status and hardware, Checks, Software, History),
  alerts, and a settings workspace (settings panel opened from the sidebar footer) with
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
- `fleeto-signer` container in the design: sole holder of the instance signing key and
  internal CA, no listening port, enforces signing rules independently of web.
- Optional four-eyes script approval per policy.
- License grace period of 14 days after expiry.
- Job output protocol: numbered chunks per stream, idempotent storage, per-chunk ack,
  completion message with counts and hashes, output cap per job.
- Migration compatibility policy (expand/contract) with a restore-based rollback for
  releases that cannot comply.
- Checks per endpoint on top of the linked monitoring templates: disable a template check on one
  endpoint, override its interval, thresholds and failures before alert, add checks that exist
  only on that endpoint, and link extra monitoring templates to one endpoint. One shared rule
  (`EffectiveChecks` with its SQL twin) decides which checks run, for the signer, the workers and
  the web UI.
- Run now and Reset and run per check: a `RunChecksNow` protocol message, rate-limited on the agent;
  a reset resolves the open alerts of the check and shows "Re-run requested" until the new result
  arrives, and results ingested before the reset are ignored.
- Alert hold: put an unresolved alert on hold until a time (at most 7 days); no emails while it
  lasts, left out of the open alert counts, one email when the hold ends on a still unresolved
  alert. Alert actions (acknowledge, put on hold, end hold, resolve) on the Alerts page, the
  dashboard and the endpoint page, with an "On hold" filter.
- Notes tab on managed endpoints: markdown notes newest first with author, edited marker, write and
  preview; the author edits, an admin deletes.
- Public IP of the agent connection on the endpoint Summary, from a PROXY protocol v2 header sent by
  the host Caddy and parsed by the gateway before TLS.
- Roadmap: check history with charts, the watchdog service, service checks picked from the services
  on the endpoint, email through Microsoft Graph and warnings before stored credentials expire (0.2.0),
  note ticket reference through the API (0.2.0), remote terminal as SYSTEM through the watchdog
  (0.3.0), sign-in with Microsoft Entra ID (0.5.0).

### Changed

- 0.2.1: **Fleeto is the only name.** The internal name Fleetify is gone from code, images (`ghcr.io/404-developer-ai/fleeto-*`),
  Compose projects and volumes, `/opt/fleeto`, the database `fleeto` with roles `fleeto_*`, notification channels, signature
  contexts (`fleeto-job-v1`, `fleeto-agent-config-v1`, `fleeto-license-v1`), key file prefixes, certificates, the agent and
  watchdog services (`fleeto-agent`, `fleeto-watchdog`) and their folders, and the repository variables (`FLEETO_*`).
  Sign-in cookies have new names, so everyone signs in again once.
- 0.2.1: Migration from the Fleetify names. install.sh moves a VPS with its next update: every instance and the host proxy
  are copied to the new names (database and roles renamed, certificates kept) and updated, with the old layout left
  untouched until each instance runs the release and used again for an instance whose update fails. The migration
  `RenameToFleeto` renames database functions and triggers, `fleeto-tool migrate` rewraps the data keys and the signer's key material, and every endpoint
  configuration is signed again. The install command takes a Fleetify agent over with its enrollment. setup-dev.ps1 moves a
  development setup. Certificates, licenses, backups and key files from before the rename stay valid.
- 0.2.1: The branding check fails on the old name outside the migration files; `deploy/release-rollback` is `restore`.
- 0.2.1: The release manifest lists every agent and watchdog binary with its SHA-256 and size (`agentBinaries`). The release
  workflow builds them reproducibly, checks that the web and gateway images contain the same binaries, and
  `deploy/sign-release.ps1` refuses a manifest without them.
- 0.2.1: The watchdog is a separate program (`fleeto-watchdog.exe`) next to the agent; uninstalling the agent removes the
  watchdog service, its key and its state as well.
- 0.2.1: The Servicedesk ticket reference on notes is no longer planned for 0.2.1 but listed as not yet scheduled on the roadmap
  (decided 2026-09-15): how Fleeto and the Servicedesk work together is aligned with the Servicedesk team first.
- 0.2.0: macOS is not supported for now (decided 2026-09-15): the UI names Windows and Linux as the platforms of checks
  and scripts.
- 0.2.0: The Docker networks of an instance use the MTU of the VPS uplink (detected by install.sh, 1280 to 1500), so containers
  work on a 1400 link without relying on "packet too big" messages or MSS clamping. An update recreates the networks when
  the MTU changed.
- 0.2.0: The web image contains the Blazor framework script again (`_framework/blazor.web.js`): the image restored the web project
  before its .razor files were copied, so the SDK left the script out and no page became interactive. The image build now
  fails when the script is missing. install.sh validates the proxy configuration with the capabilities of the running
  proxy (the first install stopped before setting the routes) and asks the NAT question on the terminal.
- 0.2.0: install.sh supports a VPS behind a firewall or NAT whose inbound public address differs from its outbound one: it asks
  once whether a firewall forwards TCP 80 and 443 on the address the DNS records use, stores a confirmed address and
  suggests it for missing records. deploy/README.md describes downloading install.sh from the release directly on the VPS,
  and deploy/sign-release.ps1 asks to confirm the image digests before signing.
- 0.2.0: Releases come from GitHub Releases of the private repository instead of a public release host, because Steaan runs
  every instance. install.sh asks once for a fine-grained token (release files) and a classic `read:packages` token
  (images), checks and stores them root-only, warns before they expire, and keeps the registry login only while it runs.
  It installs the newest published release, and pre-releases only on a VPS that runs one or while no release exists.
  The release workflow creates a draft release; `deploy/sign-release.ps1` checks, signs, verifies and publishes it.
  `fleeto-tool release verify` checks a signature. The signed `latest` pointer is gone.
- 0.2.0: Pre-release versions (`0.2.0-alpha.1`): the release workflow, the install.sh bundler and install.sh accept them, and
  install.sh orders them by semantic versioning, so `0.2.0-alpha.1` updates to `0.2.0`.
- 0.2.0: The signing request origin trigger refuses signing request kinds it does not know for every container role.
- 0.2.0: The type of an existing check can no longer be changed: its results and history belong to that type. Add a new check
  instead.
- Endpoint Summary in two columns: Status and Hardware on the left, Disks and Network on the right.
  Status shows connection, operating system, agent version and certificate only. The two columns
  follow the width of the detail itself (one column below 640 pixels), not the screen width.
- The clients panel of the clients workspace, and the settings panel that shares its style, is 250 pixels wide
  instead of 300.
- The tier switch moved from the endpoint header to the right-click menu of the endpoint list only.
- The Checks tab lists every check that applies at once ("Not run yet" before its first result) and
  hides states of checks that no longer apply; the workers remove such states hourly.
- Notes moved from 0.2.0 to 0.1.0, on endpoints only; site notes were dropped.
- Client-specific monitoring templates and policies are deleted with their client (foreign keys
  added; orphans of earlier deletions are removed by the migration).
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

- 0.2.1: Agents and watchdogs install a binary only when the release manifest that lists it verifies against the Steaan release
  keys compiled into them and the download matches the listed SHA-256, size and version. Older versions are never installed and a
  rolled back version is never retried, so a compromised instance can hold an update back but never install a binary of its own.
  Downloads need a valid agent or watchdog certificate and are limited to 20 at a time and 12 per endpoint per hour.
- 0.2.1: Watchdog certificates carry the role *watchdog*, taken from the database: they open only a watchdog session, which receives
  no configurations or jobs, and cannot recover an expired certificate. The signer issues one only at the request of the gateway, for
  an endpoint with a valid agent certificate, for a key different from the agent's, at most 3 a day, and revokes the previous one.
- 0.2.1: API keys are `flt_<id>_<secret>` with a 256-bit secret; only its SHA-256 is stored and compared in constant time, and the
  key is read on every call, so a revoked or expired key stops working at once. Only the `Authorization` header authenticates an
  API call, never a session cookie. Rate limits per address (before the key is checked) and per key (after), an audit entry for
  every call and for a wrong secret of an existing key, and no data is sent when the audit entry cannot be written. API
  responses are never cached.
- 0.2.0: Jobs are signed per endpoint with the context `fleeto-job-v1` and carry the script body; a job is never run twice
  on an agent, and a job interrupted by an agent stop is reported as lost instead of run again. Only the web role can request
  job signatures (database trigger). A script approval code is accepted once and a wrong code counts towards the lockout.
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
- The gateway reads the PROXY protocol header only from trusted proxy networks; a malformed header
  closes the connection. The per-address enrollment rate limit now applies per agent address
  instead of to all agents behind Caddy together.
- Per-endpoint links, overrides and check run requests are checked against the client of the
  endpoint by constraint triggers; tier enforcement for checks per endpoint, run requests and notes
  in web, signer, gateway and agent.
- Notes render markdown with raw HTML disabled, images as text and only http, https and mailto
  links; audit entries of notes carry the note id and length, never the body.
- Accepted risk documented for 0.3.0: the remote terminal is available where script approval is
  required.

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
