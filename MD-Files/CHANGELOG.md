# Changelog

All notable changes to Fleeto are documented here, newest first, following the
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) layout and semantic versioning.

This file holds the `Unreleased` section and the **two most recent released versions**.
When a third released version is added, the oldest entry moves to the top of
`CHANGELOG-ARCHIVE.md` in the same commit.

## [Unreleased]

## [0.2.2] — 2026-09-16

Found while testing 0.2.1 on the first test VPS: why an agent or watchdog update waits, choosing the user a script runs as, and
a safe restore when an update fails. Pre-releases `0.2.2-alpha.1` and `0.2.2-alpha.2` (2026-09-16) were test builds on the
first test VPS.

### Added

- The agent and the watchdog log once per release why they do not install an offered newer release yet: waiting for the
  update ring, for the next attempt after a failure (with its time), until the agent runs the release itself, or a version
  that was rolled back before. Before, such a wait was silent in the log.
- The endpoint detail states what the agent and watchdog updates wait for: the update ring with the ring of the site and the
  day it reaches the release (or that the release is paused), the next attempt after a failure, the random delay, the agent's
  own update before the watchdog's, or a version rolled back before. The installer reports the wait to the gateway (update state
  `waiting` with a reason and the seconds until it ends); stored in `EndpointComponentStates` (migration
  `ComponentUpdateWait`, additive).

- Choose the user a script runs as. The agent reports the signed-in users with their sessions (heartbeat, when the list
  changes), and the run window for one endpoint lists them with the time of the report next to "whoever is signed in". The
  choice (SID on Windows, uid on Linux) is signed with the job; the signer signs it only for an agent from 0.2.2, and the agent
  runs the script only in a session of that user and fails the job when that user is not signed in. The job history names the
  chosen user; `runAsChosenAccount` on Job in the API. Stored on `Endpoints` and `Jobs` (migration `ChosenSignedInUser`,
  additive).
- Run a script for all signed-in users, on one endpoint or a selection: one job per user the agent last reported, each signed
  for that user, with its own result and output. Endpoints where nobody was signed in or whose agent is older than 0.2.2 get no
  job, with the reason; a run creates at most 500 jobs. The run window names the user of each job, and the admin notice
  threshold still counts endpoints.

### Changed

- An agent or watchdog update that fails for a transient reason (the gateway or the signer could not answer right now, or the
  connection dropped during the download) is retried after 1 minute, doubling up to an hour, instead of after an hour. A refusal,
  a download that does not match the signed manifest or a failing service change still waits an hour. The gateway marks a
  watchdog certificate error as `temporary` when the signer did not answer (additive protocol field).

### Fixed

- A failed update no longer loses the instance database when its rollback cannot restore the backup. install.sh checks the
  free disk space before an update (the backup, a second copy of the database and the new images) and changes nothing when
  it is short. A restore waits for a healthy PostgreSQL, checks that the backup can be read, restores into a separate
  database and replaces the instance database only when the restore is complete. When a restore still fails, the backup is
  moved out of the rotation to `backups/kept/`, and the next install.sh run starts the update again from the previous
  configuration.

### Removed

- WAL archiving. Without a physical base backup the archived WAL could not be restored, and without a backup destination
  the spooled WAL grew by about 4.6 GB a day until it filled the disk of the first test VPS during an update. The nightly
  `pg_dump` is the backup: a restore can lose up to 24 hours of changes. An update removes the `wal-spool/` directory and
  `archive_command`; WAL archiving returns together with base backups for point-in-time recovery.

## [0.2.1] — 2026-09-16

The first release since 0.1.0: it holds the 0.2.0 milestone, which was not released on its own
(entries marked "0.2.0:"), and 0.2.1. Pre-releases `0.2.0-alpha.1` to `0.2.0-alpha.5` and `0.2.1-alpha.1` to `0.2.1-alpha.6`
(2026-09-15 and 2026-09-16) were test builds on the first test VPS; testing them on real Windows and Linux endpoints found the
fixes listed below.

### Added

- Read-only public REST API at `/api/v1`: clients, sites, endpoints with status, inventory (hardware, disks, network,
  software, services), checks and notes, alerts and jobs with their output. Keyset pagination, camelCase JSON with snake_case
  values and UTC timestamps, problem details with a stable error code, an OpenAPI 3.1 document at `/api/v1/openapi.json`.
  Agent-only endpoints answer checks and notes with `endpoint_not_managed`; a key limited to clients sees nothing of other
  clients.
- API keys under Settings, API keys (admins): a name, all clients or chosen clients, an expiry of 30 days, 90 days, 1 year
  or none, shown once, revocable, with the last use.
- `MD-Files/API.md`, the complete API contract for integrators, and `MD-Files/API-WAITLIST.md`, the features that are not
  in the API yet. A test fails when the OpenAPI document and `API.md` list different endpoints.
- Agent self-update with update rings. Each policy chooses Preview (at once), Standard (7 days) or Delayed (14 days) after
  the instance installed the release. Settings, Agent updates (admins) shows the release, when each ring gets it, how many agents
  run it and which updates failed, and lets an admin pause the release or release it to all rings. The endpoint detail shows the
  installed version, service state and latest update of the agent and the watchdog.
- Watchdog service `fleeto-watchdog` on Windows: a second service with its own certificate that keeps the agent running
  and installs agent updates, rolling back a version that does not connect within 5 minutes. The agent installs a missing
  watchdog, keeps it running and updates it. New alerts "Agent service stopped" (the watchdog is online, the agent is not) and
  "Watchdog stopped" on managed endpoints; the offline alert now opens only when both are gone.
- The gateway image carries the agent and watchdog binaries and serves them to enrolled endpoints; `install.sh` hands the
  verified release manifest to the gateway. `tools/dev/build-agent.ps1` builds both binaries and, with `-Sign`, a signed
  development manifest.
- Linux agent and watchdog for Ubuntu LTS 22.04 and 24.04, Debian 12 and newer (including Proxmox VE hosts) and the RHEL
  family 8 and 9 (RHEL, Rocky Linux, AlmaLinux). `fleeto-agent install` enrolls the endpoint and writes the systemd units
  `fleeto-agent.service` and `fleeto-watchdog.service`, which run as root, start at boot and keep each other running; a unit an
  administrator disabled or masked is reported and left alone. The identity key is created inside the TPM 2.0 where the endpoint
  has one and is a root-only key file otherwise. Inventory reads the distribution, the kernel, the hardware (DMI), the packages
  (dpkg or rpm) and the systemd services, which the check dialog offers like Windows services. Checks, scripts (sh and bash) and
  jobs run as root; the disk check skips images, container layers and network shares.
- Run a script on a selection of endpoints: check the endpoints in the list and choose "Run script on N endpoints"
  in the right-click menu. The run window shows the state per endpoint while it happens, the endpoints that got no job with
  the reason, and the output of each job. Above a threshold in Settings, Scripts (default: more than 10 endpoints) every
  admin gets an email naming the technician, the script and where it ran, and the run writes one audit entry for the batch
  next to the entry per job.
- A script can run as the signed-in user instead of as SYSTEM or root. The account is chosen per run in the run window
  and signed with the job, so the agent runs what the signer decided. The agent picks the active session, a remote desktop
  session included, and fails the job at once when nobody is signed in. The script is staged where that user may read it but
  not change it, runs from their own profile or home directory with their environment, and on Windows in the interactive
  desktop, so it can show a window. Script checks keep running as the agent's own account. The agent reports the account it
  ran the script under with the start of the job; the job output window and `runAsAccount` on Job in the API show it.
- The cap on job output is a policy setting (1 MiB to 200 MiB, default 50 MiB) instead of a fixed 50 MiB. The signer
  reads it from the policy of the endpoint's site when it signs the job; agent and gateway keep 200 MiB as an absolute ceiling.
- Fleeto icon for the installed web app: a web app manifest with PNG icons (192, 512 and a maskable 512), an
  apple-touch-icon and the theme colour, so installing Fleeto from the browser no longer shows a generic icon.
- Agents for amd64 and arm64 on both platforms. The release manifest lists the agent and the watchdog for `windows-amd64`,
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

### Changed

- **Fleeto is the only name.** The internal name Fleetify is gone from code, images (`ghcr.io/404-developer-ai/fleeto-*`),
  Compose projects and volumes, `/opt/fleeto`, the database `fleeto` with roles `fleeto_*`, notification channels, signature
  contexts (`fleeto-job-v1`, `fleeto-agent-config-v1`, `fleeto-license-v1`), key file prefixes, certificates, the agent and
  watchdog services (`fleeto-agent`, `fleeto-watchdog`) and their folders, and the repository variables (`FLEETO_*`).
  Sign-in cookies have new names, so everyone signs in again once.
- Migration from the Fleetify names. install.sh moves a VPS with its next update: every instance and the host proxy
  are copied to the new names (database and roles renamed, certificates kept) and updated, with the old layout left
  untouched until each instance runs the release and used again for an instance whose update fails. The migration
  `RenameToFleeto` renames database functions and triggers, `fleeto-tool migrate` rewraps the data keys and the signer's key material, and every endpoint
  configuration is signed again. The install command takes a Fleetify agent over with its enrollment. setup-dev.ps1 moves a
  development setup. Certificates, licenses, backups and key files from before the rename stay valid.
- The branding check fails on the old name outside the migration files; `deploy/release-rollback` is `restore`.
- The release manifest lists every agent and watchdog binary with its SHA-256 and size (`agentBinaries`). The release
  workflow builds them reproducibly, checks that the web and gateway images contain the same binaries, and
  `deploy/sign-release.ps1` refuses a manifest without them.
- The watchdog is a separate program (`fleeto-watchdog.exe`) next to the agent; uninstalling the agent removes the
  watchdog service, its key and its state as well.
- The Servicedesk ticket reference on notes is no longer planned for 0.2.1 but listed as not yet scheduled on the roadmap
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

### Fixed

- A watchdog could not be installed on an instance moved from the Fleetify layout. The rename migration replaced the rule for
  who may request which signature with its version from before the watchdog, so the gateway's request for a watchdog certificate
  failed with "Unknown signing request kind WatchdogCertificate". Migration `RestoreSigningRequestOrigin` writes the rule again, and
  the rename now keeps a function that a later migration already wrote under the new name.
- The Linux inventory no longer lists an rpm header without a name as a package called "(none)".
- The Windows install command failed with "A positional parameter cannot be found that accepts argument 'amd64'": the
  download address and the architecture reached `Invoke-WebRequest` as two arguments. They are now joined into one `-Uri`.
- The endpoint Summary no longer calls a private address "Public IP". An agent that reaches the instance inside a private
  network (the same LAN with local DNS, a VPN) connects from a private address; the Summary then shows it as "Connection address"
  marked "private network", because the public IP is not known.
- 0.2.0: Running a script failed on every installed instance with "permission denied for table SigningRequests": the database role
  of web could read signing requests but not create the job signature request. Web now has INSERT on the table (the trigger still
  allows it only jobs), and a test checks with the production grants that every container can request its own signatures.

### Security

- Agents and watchdogs install a binary only when the release manifest that lists it verifies against the Steaan release
  keys compiled into them and the download matches the listed SHA-256, size and version. Older versions are never installed and a
  rolled back version is never retried, so a compromised instance can hold an update back but never install a binary of its own.
  Downloads need a valid agent or watchdog certificate and are limited to 20 at a time and 12 per endpoint per hour.
- Watchdog certificates carry the role *watchdog*, taken from the database: they open only a watchdog session, which receives
  no configurations or jobs, and cannot recover an expired certificate. The signer issues one only at the request of the gateway, for
  an endpoint with a valid agent certificate, for a key different from the agent's, at most 3 a day, and revokes the previous one.
- API keys are `flt_<id>_<secret>` with a 256-bit secret; only its SHA-256 is stored and compared in constant time, and the
  key is read on every call, so a revoked or expired key stops working at once. Only the `Authorization` header authenticates an
  API call, never a session cookie. Rate limits per address (before the key is checked) and per key (after), an audit entry for
  every call and for a wrong secret of an existing key, and no data is sent when the audit entry cannot be written. API
  responses are never cached.
- 0.2.0: Jobs are signed per endpoint with the context `fleeto-job-v1` and carry the script body; a job is never run twice
  on an agent, and a job interrupted by an agent stop is reported as lost instead of run again. Only the web role can request
  job signatures (database trigger). A script approval code is accepted once and a wrong code counts towards the lockout.
