# Fleeto — Roadmap

> Planned versions and what each one delivers. Version rules are in `CLAUDE.md`
> (Versioning and changelog). Scope per version is a target, not a promise: a version
> ships when its items are done and tested, and items may move between versions. Dates
> are added only once a version is in progress.

## Current

**0.1.0** — implemented; tagged `v0.1.0` on 2026-09-15 as a historical marker on its last commit, without a release
build (decided 2026-09-15). What was listed as open before the tag stays a checklist under "Open items from 0.1.0".

**0.2.0** — every item built (2026-09-15); everything that was still open moved to 0.2.1 (decided 2026-09-15). Not released
on its own: it ships with 0.2.1. Pre-releases `v0.2.0-alpha.1` (2026-09-15, first CI run; its release build failed),
`v0.2.0-alpha.2` (first published test build), `v0.2.0-alpha.3` (VPS behind NAT, first VPS install), `v0.2.0-alpha.4` (fixes
from the first install) and `v0.2.0-alpha.5` (network MTU).

**0.2.1** — released 2026-09-16 (`v0.2.1`), the first release since 0.1.0, together with 0.2.0: the read-only public API,
agent self-update with update rings, the watchdog, the Linux agent, the rename to Fleeto everywhere, a script run on a
selection of endpoints, the output cap per policy, running a script as the signed-in user and the icon for the installed web
app. Pre-releases `v0.2.1-alpha.1` to `v0.2.1-alpha.6` (2026-09-15 and 2026-09-16) were tested on the first test VPS with two
Windows endpoints and a Linux endpoint: self-update, the watchdog, taking over a Fleetify agent, scripts as SYSTEM, as the
signed-in user and without a signed-in user, a run on a selection and the output cap. The Servicedesk ticket reference on
notes moved to "Not yet scheduled" (decided 2026-09-15).

**0.2.2** — released 2026-09-16 (`v0.2.2`): why an agent or watchdog update waits, choosing the user a script runs as, and a
safe restore when an update fails, with WAL archiving removed (all found while testing 0.2.1). Pre-releases `v0.2.2-alpha.1` and
`v0.2.2-alpha.2` (2026-09-16) on the first test VPS.

**0.3.0** — in progress (started 2026-09-16): remote control and remote background, planned with the developer on 2026-09-16 in
seven steps. Steps 1 and 2 (the relay, end-to-end encryption and the complete remote background: terminal, files, services and
processes) are built and verified on Windows and Linux endpoints (`v0.3.0-alpha.4`, 2026-09-17). Step 3 (remote control on Windows:
screen, mouse and keyboard) is built and verified on a Windows endpoint (`v0.3.0-alpha.6`). Step 4 (clipboard, several technicians,
consent and banner) is built (`v0.3.0-alpha.7`).

**Platforms**: Windows and Linux. macOS is not supported for now; it may come later when there is demand (decided
2026-09-15, see Later).

**Deployment**: Steaan runs every instance (SaaS, decided 2026-09-15); releases are GitHub Releases of the private
repository.

Markers: [done] built and tested, [open] still to do.

## 0.0.x — Foundation

Built together with 0.1.0 in one piece of work, without separate patch releases.

- [done] Private GitHub repository, `.gitignore`, CI (build, tests against PostgreSQL + TimescaleDB,
  vulnerability scans, gitleaks secret scan, branding check, shellcheck, image builds).
- [done] Solution following the Migrify conventions: Core, Protocol, Infrastructure, Web, Gateway,
  Signer, Workers, Tools, shared test fixture.
- [done] PostgreSQL 17 schema and migrations; TimescaleDB hypertable when the extension is installed.
  Local development without Docker (`tools/dev`); the Compose stack is for the VPS.
- [done] Root key, envelope encryption, encrypted settings, append-only audit log, one database role
  per container with least-privilege grants.
- [done] `Fleeto.Signer`: signer key, instance signing key, internal CA, signing requests over
  LISTEN/NOTIFY, signing rules with tests, role check on who may request which signature.
- [done] Release signing tooling: release key, signed release manifest with image digests, signed
  `install.sh` (verifiable with openssl). [done] Test keys for the first VPS (2026-09-15). [open] Production key on a
  hardware token.
- [done] Users, roles, login, mandatory TOTP 2FA, first-admin setup flow with backup step.
- [done] Clients, sites, endpoints: CRUD, clients workspace (clients panel, endpoint list with tabs Servers / Workstations / Mixed, endpoint detail below the list), collapsible navigation, settings workspace (settings panel with templates and administration pages, opened from the sidebar footer).
- [done] Client templates, monitoring templates, policies: CRUD, linking to sites, copy.
- [done] Endpoint tier with server-side enforcement in four layers; license format, signing tool,
  license page, pool counting with serialized allocation, 14-day grace period.
- [done] `install.sh` first version (signature and manifest verification, Docker, DNS check, host
  Caddy with SNI passthrough, update with rollback, `--version`, `--check`, `--list`, `--all`).
  [done] First install and updates on a real VPS (2026-09-15, see 0.2.0, Deployment).
- [done] Load-test simulator (`Fleeto.LoadTest`).

## 0.1.0 — First usable release

- [done] Go agent for Windows: enrollment with per-site token and CA fingerprint pinning, mTLS
  certificate with TPM-backed key where available, renewal, heartbeat, online/offline state,
  inventory (hardware, OS, software), Windows service install. Agent-only tier complete.
- [done] Certificate revocation: allow list checked on every connection, live disconnect, revoke on
  endpoint deletion, duplicate identity detection.
- [done] Gateway in .NET. [open] Benchmark at 10,000 connections (tested with 200 simulated agents).
- [done] Managed tier: CPU, memory, disk, service and uptime checks with per-check intervals and
  jitter, offline buffering, acknowledgement only after the batch is written to Postgres.
- [done] Alerting: open, escalate, acknowledge, resolve, deduplicate; email notifications with retry.
- [done] Dashboard with tiles (including license usage and backups) and open alerts, live updates.
- [done] Endpoint detail: Summary in two columns (Status and Hardware, Disks and Network with the
  public IP of the agent connection); tier switch only in the right-click menu of the endpoint list.
- [done] PROXY protocol v2 from the host Caddy to the gateway, so the gateway sees agent addresses
  (public IP, logs, per-address enrollment rate limit).
- [done] Checks tab lists every check that applies straight away ("Not run yet" until the first result).
- [done] Checks per endpoint on top of the linked monitoring templates: disable a check, override
  interval, thresholds and failures before alert, add a check for this endpoint only, link an extra
  monitoring template.
- [done] Run a check now, and reset a check (open alerts resolved, "Re-run requested" until the new
  result arrives).
- [done] Alert actions on the Alerts page, the dashboard and the endpoint: acknowledge, put on hold
  until a time (no emails, out of the open alert counts, one email when the hold ends), resolve.
- [done] Notes on endpoints (managed endpoints only): table newest first with author, markdown, the
  author edits, an admin deletes.
- [open] Two instances on one VPS running side by side, each on its own FQDN.
- [open] Key ceremony written out and rehearsed (release and license keys, root key, signer key,
  backup key pair).
- [done] Encrypted off-VPS backups per instance (S3-compatible or directory, WAL shipping).
  [open] Restore onto a fresh VPS tested.

### Open items from 0.1.0

Listed as open before the 0.1.0 tag; the tag was set as a historical marker, so they stay a checklist for the first
production release.

1. [open] Local test by the developer (web UI click-through, agent install as a Windows service). [done] Partly on the
   first test VPS (2026-09-15): sign-in, UI and a Windows agent.
2. [done] First CI run on GitHub (Docker image builds, tests with TimescaleDB, gitleaks over the history), green since
   2026-09-15.
3. [done] First install on a VPS (2026-09-15, one instance behind NAT). [open] Two instances on one VPS; restore of a
   backup onto a fresh VPS.
4. [open] Load test at 10,000 simulated agents.
5. [done] Repository variables `FLEETO_LICENSE_PUBLIC_KEYS` and `FLEETO_RELEASE_PUBLIC_KEYS` set, with test keys
   (2026-09-15). [open] Key ceremony document and production keys (release, license) on hardware tokens.

### Known limitations of 0.1.0

- The Linux agent follows in 0.2.1; macOS is not supported for now.
- An agent offline past its certificate expiry (90 days, renewal from day 60) cannot reconnect and
  must be enrolled again as a new endpoint. [done] Recovery built in 0.2.0.
- The web data protection key ring is stored unencrypted on its volume.
- No email throttling or digest during a mass outage; duplicate identity alerts do not resolve on
  their own.
- S3 uploads are single-part (5 GB per backup file).

## 0.2.0 — Full monitoring, jobs and API

- [done] Complete check catalog (decided 2026-09-15), with a check only sent to endpoints whose platform runs it:
  - network: ping to a host, TCP port reachable, HTTP(S) URL (status code, response time, certificate
    expiry);
  - system: process running, pending reboot, file or folder (exists, size, age), local certificate
    expiring;
  - Windows: event log (number of events per source and id within a window), Windows Security Center
    (antivirus and firewall on);
  - [done] script check: an approved library script whose exit code sets the status (with the script library).
    Decided while building: approval follows the policy like jobs; where approval is required the check runs the
    newest approved version, so a change waits for approval without stopping the check.
- [done] Maintenance windows in the policy (recurring, feeding the maintenance mode rule): days, local start
  time, duration, time zone and all endpoints, servers or workstations; occurrences stored ahead so the rule
  stays a time comparison.
- [done] Escalation rules as routing rules (decided 2026-09-15): per notification channel, which clients and
  which severities it receives. No time-based escalation.
- [done] Webhook notifications next to email: generic JSON signed with HMAC-SHA256, Slack and Microsoft Teams
  (workflow adaptive card); per-channel retry and circuit breaker, test message, last delivery shown. Sent
  only to public https addresses, checked at connect time.
- [done] Service checks by picking a service (Windows services; systemd services with the Linux agent, 0.2.1): besides typing the service name, choose from a list of the
  services on the endpoint. The agent reports its services (name, display name, start type, state) as
  part of the inventory; the check dialog on an endpoint offers them, and on a monitoring template it
  offers the services seen on the endpoints of the linked sites. Typing a name stays possible for a
  service that is not installed yet.
- [done] Email through Microsoft Graph (`sendMail` with application permission, limited to the sending
  mailbox by an Exchange application access policy) as an alternative to SMTP, chosen in Settings,
  Email. Same outbox, retry and circuit breaker as SMTP. Client secret or certificate stored encrypted
  in the database (decided while building: the certificate is created by Fleeto and waits as pending
  until the admin switches to it, so a renewal never interrupts sending).
- [done] Expiring credentials: every stored secret with an end date (starting with the Graph client secret or
  certificate) carries that date. From 30 days before expiry the dashboard warns and admins get an
  email, repeated at 14, 7 and 1 days; after expiry the warning turns red and states what stopped
  working (email delivery) and the next step. An expired Graph secret cannot send its own warning, so
  the warning also shows in the UI and the email goes through SMTP when that is configured as well.
- [done] Check history per check of an endpoint: the last hour, day, week, month and year; a line chart for
  numeric checks (CPU, memory, free disk space including storage growth, uptime, response times) and a
  status timeline for yes/no checks. Hourly and daily rollups kept 13 months, maintained by the workers
  in every setup (decided while building: exactly-once with the evaluation, same behaviour without
  TimescaleDB).
- [done] Maintenance mode per client, site and endpoint: started by hand, with an optional end time
  (1 hour, 4 hours, 24 hours, a chosen time, or until turned off). While an endpoint is in
  maintenance no alert opens or escalates; open alerts stay open and still resolve when their
  check recovers. Duplicate identity alerts are never suppressed. The clients panel shows per
  client and site whether all or some endpoints are in maintenance ("all" or "2/14"); the
  endpoint list and detail show it per endpoint, with a filter "In maintenance". [done] Maintenance
  windows from the policy feed the same rule.
- [done] Script library with versions and optional four-eyes approval per policy; signed remote
  execution with `ValidUntil`, output capture, job history including expired and refused jobs.
  PowerShell and Batch on Windows, sh and bash on Linux, always as SYSTEM or root (decided 2026-09-15). Decided
  while building: a script runs on one endpoint at a time from the UI. Running a script on a selection of endpoints,
  an output cap per policy and running as the signed-in user followed in 0.2.1.
- [done] Deployment for SaaS (decided 2026-09-15: Steaan runs every instance), tested with the first install on a VPS:
  - releases are GitHub Releases of the private repository; install.sh reads them with a fine-grained read-only token
    and pulls the private images with a classic `read:packages` token, both asked once and stored root-only;
  - the release workflow drafts the release, `deploy/sign-release.ps1` checks, signs and publishes it;
  - pre-release versions (`vX.Y.Z-alpha.N`), ordered by semantic versioning; a VPS follows pre-releases only when it
    runs one;
  - a VPS behind a firewall or NAT: install.sh asks once to confirm the forwarded public address;
  - the instance networks use the MTU of the VPS uplink (a 1400 link works without MSS clamping);
  - fixes found by the first install: the Blazor framework script in the web image, the proxy configuration check
    with the capabilities of the running proxy.
- [done] Recovery for agents that were offline past their certificate expiry (for example a laptop
  that stayed in a drawer for months):
  - Renewal with an expired certificate: the gateway accepts an expired but not revoked agent
    certificate for a limited period after expiry (for example 12 months), only for the renewal
    request and never for a normal session. The key must stay the same; the TLS handshake proves
    the agent holds it. The signer re-checks revocation and the grace window. Revoking the agent
    still stops a lost or stolen endpoint.
  - Re-enrollment onto the same endpoint: an agent that enrolls again with a new token can be
    linked to its existing endpoint (chosen by the technician, or matched by the agent's key or
    state), so checks, alerts, notes and audit history are kept instead of creating a new endpoint.
    Decided while building: chosen by the technician only (Enroll again on the endpoint, a single-use
    token bound to it); no automatic matching.
## 0.2.1 — API, agent updates, watchdog and Linux agent (released 2026-09-16)

Everything that was still open for 0.2.0, moved here on 2026-09-15, with the developer's answers of that day.

- [done] Public REST API `/api/v1`, **read-only** (decided 2026-09-15: no write access and none planned until there is
  demand):
  - API keys created in Settings: named, optionally limited to clients, revocable, shown once, format
    `flt_<id>_<secret>` with a 256-bit secret stored as SHA-256; decided while building: optionally expiring (30 days,
    90 days, 1 year or never) and created by admins only;
  - read access to clients, sites, endpoints (inventory and status), alerts, jobs, checks and notes (patch compliance follows with Action1 in 0.4.0);
    keyset pagination, ISO 8601 timestamps in UTC;
  - OpenAPI document served by the instance, kept in sync with the code by a test;
  - rate limiting per key and an audit entry per call; decided while building: also a rate limit per address before the
    key is checked, and no response when the audit entry cannot be written;
  - `MD-Files/API.md` documents the whole API for integrators and `MD-Files/API-WAITLIST.md` lists every feature not in
    the API yet, both kept up to date in the same commit as a change (decided 2026-09-15); a test compares `API.md`
    with the OpenAPI document.
- [done] **Fleeto everywhere** (decided 2026-09-15): the internal name Fleetify is no longer used, in code, images, database,
  services on endpoints and documents alike, with a migration path for everything installed before
  (`MD-Files/ARCHITECTURE.md` §7, Rename to Fleeto). Decided while building:
  - a VPS moves as a whole with its next update: copies of every instance directory and volume under the new names, database
    and roles renamed in the copy, the host proxy with its certificates; the old layout stays until an instance runs the new
    release, and an instance whose update fails runs again from it;
  - endpoints keep their enrollment: the install command of the site takes a Fleetify agent over (same gateway and instance
    CA only), because 0.2.0 agents cannot update themselves;
  - data signed or encrypted under the old names stays readable (certificates until renewed, licenses, backups, key files);
    wrapped data keys are rewrapped and every endpoint configuration is signed again by the migration;
  - the CSS prefix `--fl-` stays; the old `fleetify-*` packages on ghcr.io and the `FLEETIFY_*` repository variables are
    replaced by `fleeto-*` and `FLEETO_*`;
  - the release is marked `rollback: restore`.
- [done] Agent self-update, installed only with a valid Steaan release signature:
  - decided while building: the signed release manifest lists every agent and watchdog binary with its SHA-256 and size
    instead of a signature file next to each binary; the agent verifies the manifest against the release public keys
    compiled into it and the download against the listed hash. Same trust, one signature per release, and the manifest
    was already signed outside CI;
  - three update rings chosen in the policy (decided 2026-09-15): Preview (at once), Standard (after 7 days), Delayed
    (after 14 days), **counted from the moment the instance installs the release** (decided 2026-09-15: the binaries
    ship in the instance image; decided while building: in the gateway image too, which serves the downloads, and
    `install.sh` hands the verified manifest to the gateway);
  - an admin can pause a release or release it to every ring at once (Settings, Agent updates, with the rollout per
    ring and the failed updates);
  - the watchdog installs the update and rolls back a failed one; decided while building: an update counts as
    succeeded only when the new version connects to the gateway within 5 minutes, a rolled back version is never
    retried on that endpoint, older versions are never installed, a random delay of up to 10 minutes spreads the
    downloads, and the gateway limits downloads (20 at a time, 12 per endpoint per hour);
  - the agent installs a missing watchdog and updates the watchdog once it runs the new release itself;
  - Authenticode code signing of the Windows binaries comes later (decided 2026-09-15).
- [done] Watchdog service on **every supported platform**, Windows and Linux (decided 2026-09-15): a second service
  with its own certificate for the same endpoint, always connected. Agent and watchdog restart each other; alerts
  "Agent service stopped" and "Watchdog stopped" on managed endpoints, separate from the offline alert. Built for
  Windows as `fleeto-watchdog`; decided while building: the Linux watchdog is built with the Linux agent (below),
  since the Linux agent has no service yet. Also decided while building: the agent requests the watchdog certificate
  over its own session (the signer checks that the endpoint has a valid agent certificate and that the watchdog key
  differs), "Watchdog stopped" only for an endpoint whose watchdog connected before, and a watchdog session never
  receives configurations or jobs.
- [done] Linux agent for **Ubuntu LTS (22.04, 24.04), Debian 12 and newer (including Proxmox VE hosts) and the RHEL
  family (RHEL, Rocky Linux, AlmaLinux 8 and 9)** (decided 2026-09-15): systemd service and the watchdog as a second
  systemd service (service control, supervision and self-update on Linux), install command generated in the UI, inventory, the check catalog on Linux, systemd services offered in the check dialog, key storage (TPM where
  available, otherwise a root-only file), scripts in sh and bash as root. Decided while building:
  - service control goes through `systemctl` instead of the D-Bus API, so the agent needs no extra library; a unit an
    administrator disabled or masked is reported and never started again by the other service;
  - the TPM key is an ECDSA P-256 key created inside the TPM under the owner storage key, stored as the key blob the TPM
    itself encrypted (no persistent handle); CI tests it against a software TPM;
  - binaries in `/opt/fleeto-agent`, state in `/var/lib/fleeto/{agent,watchdog}`, units `fleeto-agent.service` and
    `fleeto-watchdog.service`, logging to the journal and to the state directory;
  - the install command picks the architecture itself and downloads with curl or wget; it runs the installer from a
    directory under `/opt`, because `/tmp` is mounted without exec permission on hardened endpoints;
  - inventory: distribution and kernel from `/etc/os-release` (a Proxmox VE host names Proxmox), hardware from DMI,
    packages from dpkg or rpm without the maintainer email address, systemd services with the start type vocabulary of
    every platform (enabled, static, generated and indirect are `automatic`, disabled is `manual`, masked is `disabled`);
  - the disk check skips images, container layers and network shares, and reports a filesystem mounted twice once.
- [done] Agents for **amd64 and arm64** (decided 2026-09-15), on Windows and Linux, in the release pipeline and the install
  command: the release manifest lists eight binaries (agent and watchdog for `windows-amd64`, `windows-arm64`,
  `linux-amd64` and `linux-arm64`), the instance serves them at `/agent/download/<platform>`, and each install command
  picks the architecture of the endpoint itself.
- [done] Run a script on a selection of endpoints, with a notification to every admin above a configurable number of
  endpoints (deferred from 0.2.0). Decided while building:
  - the endpoint list gets a checkbox per row and one in its header; the selection holds only endpoints the list shows, so
    filtering or another site never leaves an endpoint selected out of sight, and the run starts from the right-click menu
    ("Run script on 12 endpoints");
  - the threshold is instance-wide, in Settings, Scripts (a run spans clients and sites, so a policy value would be
    ambiguous). Default: more than 10 endpoints; 0 turns the email off. The run dialog says beforehand that admins will be
    told;
  - the email is queued in the same transaction as the jobs, so a run that exists is always reported; it names the
    technician, the script and version, the endpoint count, the first ten host names and how many endpoints were skipped;
  - a run on more than one endpoint also writes one audit entry for the batch, next to the entry per job;
  - after starting, a run window shows the state per endpoint, updated live, with the skipped endpoints and their reason;
    selecting an endpoint there opens its output.
- [done] Output cap for job output per policy instead of the fixed 50 MiB (deferred from 0.2.0). Decided while building: the
  cap is chosen in whole mebibytes in the policy (1 MiB to 200 MiB, default 50 MiB); the signer reads it from the effective
  policy of the endpoint's site and puts it in the signed job, so what web wrote on the job row never decides it; agent and
  gateway hold 200 MiB as an absolute ceiling whatever a policy or a payload says; a policy that is not the default shows
  its cap in the policy list.
- [done] Run a script as the logged-on user instead of SYSTEM or root (deferred from 0.2.0). Decided while building:
  - the account is chosen **per run** in the run window ("System (SYSTEM or root)" or "The signed-in user"), not on the
    script and not in the policy: the same script is useful in both places, and running as a user is never more than the
    service account may do anyway. It is signed with the job, so the agent runs what the signer decided and nothing else;
  - the session is the **active** one, a remote desktop session included; on Windows the console session wins when someone
    is signed in there, on Linux a graphical session wins over a text one and root is never chosen;
  - an endpoint where nobody is signed in **fails the job at once** with that reason instead of waiting: the technician
    sees it in the job list and starts it again later;
  - the script is not written in the agent's own directory, which the user may not read, but in a directory the user may
    read and not change (Windows: under `C:\ProgramData\Fleeto` with SYSTEM, the administrators and that user only;
    Linux: a root-owned directory under `/tmp` with the script owned by the user, mode 0400). The user can read the script
    while it runs; the run window says so;
  - the script runs from the user's own profile or home directory, with that user's environment, and on Windows in
    `winsta0\default`, so it can show a window; `CreateProcessAsUser` is called directly because Go's process API cannot
    name a desktop;
  - script checks from the signed configuration always run as the agent's own account, never as a user.
- [done] Fleeto icon for the installed web app: `manifest.webmanifest` (name Fleeto, standalone display, theme colour
  teal `#0F766E`) with PNG icons made from the favicon mark (192 and 512 px, plus a maskable 512 px), an
  `apple-touch-icon` (180 px) and the `theme-color` meta tag in `App.razor`. Decided while building: the icons are
  generated from the same geometry as `favicon.svg` by `tools/dev/build-icons.cs` (run with `dotnet run`, no image
  library and no design tool), so the mark cannot drift from the favicon; `manifest-src 'self'` was added to the Content
  Security Policy, and a test reads the sizes from the PNG headers and checks that every icon the manifest names
  exists.
- [done] Remove the container images of the failed `v0.2.0-alpha.1` release from ghcr.io (2026-09-16: web, gateway, signer, workers and tool; the release build stopped before the Caddy image).
- Not supported for now: **macOS** (decided 2026-09-15): no macOS agent, watchdog, remote control or remote terminal.
  It may come later when there is demand (see Later).

## 0.2.2 — Agent update visibility and the user a script runs as (released 2026-09-16)

Found while testing `v0.2.1-alpha.5` on the test VPS (2026-09-16): an agent without a watchdog waited silently, first for its
retry after a failed attempt and then for its update ring, so neither the agent log nor the UI said why nothing happened.

- [done] The agent logs once per release why it does not install it yet: waiting for the update ring, waiting for the next
  attempt after a failure (with the time), or a version that was rolled back before. The watchdog logs the same for the agent
  it installs, and the agent also logs when it waits to update the watchdog until it runs the release itself.
- [done] The endpoint detail states what the watchdog and the agent are waiting for instead of "Not installed yet: the agent
  installs it with the next release offer", for example "Waiting for the update ring: release 0.2.2 reaches the Standard ring on
  23 Sep 2026 14:00" or "Next attempt to install 0.2.2 after 16 Sep 2026 13:06". The installer reports the reason and the
  seconds until the wait ends with its update state (`waiting`), also for the random delay.
- [done] A transient failure (the gateway or signer could not answer right now) is retried within minutes with backoff instead
  of after one hour; a refusal or a defective download keeps the one-hour wait. Backoff from 1 minute, doubling up to the hour,
  with jitter; the gateway marks a watchdog certificate error `temporary` when the signer did not answer.

Also found while testing (2026-09-16): on an endpoint with several signed-in users, such as a remote desktop server, "The
signed-in user" runs the script as the console user or else the first active session Windows lists, which the technician cannot
predict.

- [done] Choose the user in the run window: the agent reports the signed-in users with their sessions, the technician picks one,
  and the choice is signed with the job. When that user is no longer signed in, the job fails with that reason. For a run on
  one endpoint; the user is identified by SID or uid, and the signer signs a choice only for an agent from 0.2.2.
- [done] "All signed-in users": one job per signed-in user, with the result and output per user. Web creates the jobs from the users
  the agent last reported, so each is signed for one user; a user who has left by the time the job arrives fails that job. Also for
  a run on a selection of endpoints; a run creates at most 500 jobs.

Found while installing `v0.2.2-alpha.1` on the test VPS (2026-09-16): the WAL spool had filled the disk, the migrations failed
and the rollback crashed PostgreSQL halfway through dropping the database, so the instance had to be restored by hand.

- [done] WAL archiving removed (decided 2026-09-16): it could not be restored without base backups and filled the disk. The
  nightly dump is the backup; up to 24 hours of changes can be lost until point-in-time recovery exists (Not yet scheduled).
- [done] install.sh checks free disk space before an update, restores into a separate database and replaces the instance
  database only after a complete restore, keeps the backup of an update it could not roll back, and retries that update on the
  next run.

## 0.3.0 — Remote control

Two kinds of session, both opened from the right-click menu of the endpoint list and from the endpoint detail, each in its own
popup window: **Remote control** (take over the screen) and **Remote background** (terminal, files, services and processes
without touching the screen). Managed endpoints only, admins and technicians, never read-only.

Decisions (2026-09-16, with the developer):

- **Transport**: a WebSocket relay through the gateway on port 443, no WebRTC and no prototype. The browser connects to
  `wss://<fqdn>/relay/...` (Caddy routes it to the gateway), the agent opens a separate mTLS WebSocket per session to
  `agents.<fqdn>`, so screen traffic never blocks the agent's control connection. Traffic passes the VPS anyway (TURN would
  too), UDP is often blocked at customers, and frame acknowledgements keep latency bounded. WebRTC returns only if
  measurements demand it.
- **End-to-end encryption** as in ARCHITECTURE.md §4: a single-use session token from the signer with the browser's
  ephemeral X25519 key, the agent's key signed with its certificate key, AES-256-GCM frames with sequence numbers. Every
  participant has its own key exchange with the agent. Tested against a hostile relay (tamper, replay, reorder, swapped key,
  token for another endpoint or instance).
- **Image**: tiles with change detection (sharp text, same Go code on Windows and Linux, adaptive quality), then H.264
  through Media Foundation on Windows (hardware encoder where present) decoded with WebCodecs, falling back to tiles
  automatically when it is not available.
- **Keyboard** must never change characters on the way, also on the Windows sign-in screen with a different layout on the
  technician's PC (AZERTY vs QWERTY): keys are sent as physical keys (scan codes) with character translation to the
  endpoint's active layout, a Unicode fallback for characters the layout lacks, and "Type clipboard" to enter a password
  where pasting is not possible. Ctrl+Alt+Del button: the agent sets `SoftwareSASGeneration=1` at install and update; a
  customer GPO that overrides it wins, and the button then says why it does not work.
- **Clipboard** in both directions for text (a must). Files from the technician's PC to the endpoint by pasting or dragging
  into the remote window (they appear on the endpoint clipboard like RDP); files copied on the endpoint show "N files copied,
  download" in the window, because a browser cannot put files on the local clipboard.
- **Several technicians** work in the same remote control session at the same time: each sees the others' pointers, the
  banner names all of them, every join is its own token and audit entry.
- **Windows session**: a choice when opening, default the console (including the sign-in screen and UAC); every signed-in RDP
  session is listed by user. Screen capture with DXGI desktop duplication and a GDI fallback (RDP sessions, VMs), several
  monitors selectable.
- **Consent and banner**: servers (Windows and Linux) never prompt and show no banner. Workstations follow the policy:
  consent prompt on/off (default off), banner with the technician names on/off (default on), consent timeout (default
  30 seconds) after which access is granted; an explicit refusal ends the session. Remote background never prompts.
- **Remote background**: admins and technicians may do everything (same accepted risk as the terminal towards script
  approval). Served by the watchdog, so it also works when the agent is broken.
  - Terminal as SYSTEM or root: PowerShell and cmd on Windows (ConPTY on Windows 10 1809 and Server 2019 or newer, a simpler
    terminal without PTY on Server 2016), the root shell on Linux.
  - File explorer: browse, refresh, download, upload, rename, delete, copy and paste within the endpoint. Streamed through the
    relay, never stored on the server, resumable after a drop, at most 10 GB per file (policy).
  - Services (Windows and systemd): list, start, stop, restart, startup type. Processes (Windows and Linux): list with CPU,
    memory and user, end a process.
- **Limits**: a session without input closes after 30 minutes (warning 2 minutes before, policy), no maximum duration.
- **Reason** is optional when opening or joining a session.
- **Audit** per session and per participant (who, endpoint, kind, Windows session, start, end, reason) and per action in a
  background session (file, service or process action with its target). No terminal content.
- **Recording** of sessions and terminal transcripts moved to Not yet scheduled.
- **Platforms**: Windows 10 and Server 2016 or newer; Linux X11 (Wayland shows that remote control is not supported, remote
  background works); Linux without a graphical session gets remote background only.
- **Public API**: sessions go onto `API-WAITLIST.md`.
- **Test builds**: a pre-release `v0.3.0-alpha.N` after every step that can be tested on real endpoints (commit and tag after
  asking).

Steps:

1. [done] **Foundation and remote terminal** (alpha.1): documentation of the decisions above (CLAUDE.md, ARCHITECTURE.md), data
   model (`RemoteSessions`, participants, actions), policy settings (consent, banner, timeout, clipboard, idle timeout, file
   size), signer rules for session tokens, gateway relay (browser route through Caddy, agent and watchdog session sockets,
   revocation drops sessions), the shared end-to-end crypto in Go and the browser with hostile relay tests, tier and
   cross-client tests at every layer, the Remote background window with the terminal served by the watchdog.
   Decided while building (2026-09-16):
   - The instance stores no certificates, only their public key fingerprints: the endpoint sends its certificate key with its
     signed session key, and the browser accepts it only when its fingerprint is one web gives. The trust still comes from the
     instance, and certificates issued before 0.3.0 work.
   - The idle timeout is built with the terminal, because a forgotten SYSTEM terminal is the first risk it covers; the policy
     dialog shows only that setting for now. Consent, banner, clipboard and file size are stored with the policy and appear in the
     dialog with the step that uses them (no placeholder UI).
   - The gateway listens for the browser side on its own loopback port (`RELAY_PORT`, allocated by install.sh from the top of
     the port range); Caddy proxies `/relay/*` there.
   - xterm.js 6.0.0 is served from the instance (`wwwroot/lib/xterm`, verified against the npm integrity hash).
   - On Windows Server 2016 (no ConPTY) the terminal runs the shell with redirected streams: the window edits the line and sends
     it with Enter; full-screen programs do not work there.
2. [done] **Remote background complete** (alpha.2; fixes found on the test VPS in alpha.3 and alpha.4): file explorer (browse, download, upload, rename, delete, copy within the
   endpoint) with resumable, flow-controlled transfers up to the policy's file size cap; services (list, start, stop, restart,
   start type); processes (list with CPU, memory and user; end one); every action audited over the endpoint's control session.
   Decided while building (2026-09-17):
   - Files, services and processes run over the same encrypted session as the terminal (request/response and binary transfer
     frames), so they need no second connection and no server access to the content.
   - The endpoint reports each action (file, service, process) to the gateway over its own watchdog control session, so the
     audit entry comes from the endpoint, not the browser, and never carries a file's content. Stored in `RemoteSessionActions`.
   - Downloads stream to disk with the File System Access API where the browser has it, and fall back to a Blob otherwise; a
     transfer resumes from a byte offset after the session is re-established.
   - The endpoint starts sending a download when it answers the request, so the browser keeps frames of a transfer it does not
     know yet; a download without acknowledgements for 2 minutes stops and closes the file (found in alpha.2: downloads never
     finished and the open file could not be deleted).
3. [done] **Remote control on Windows** (alpha.5): a helper the agent starts as SYSTEM in the chosen Windows session (console,
   sign-in screen, UAC and RDP sessions), screen capture with monitor choice, the tile codec with flow control, mouse and a
   layout-safe keyboard, Type clipboard, Ctrl+Alt+Del, stuck-key release, automatic reconnect and the viewer window.
   Decided while building (2026-09-17):
   - Capture is GDI (BitBlt) for now: it works on every desktop (sign-in screen, UAC, RDP sessions and VMs without a GPU). DXGI
     desktop duplication moves to step 5, where the Direct3D code it needs is built for H.264 anyway.
   - Automatic reconnect opens a **new** session in the same window (same reason and Windows session), so the audit log shows
     two sessions; the token is single use and a session ends when nobody is connected, so resuming the same one would change how
     the gateway and workers end sessions. Up to three tries with a short, growing delay.
   - The agent starts one helper process per session (`fleeto-agent remote-helper`) as SYSTEM in the Windows session, over
     anonymous pipes only it inherits, in a job object that ends it with the agent. It follows the input desktop, so the sign-in
     screen and UAC are shown and can be used, and follows the console to another session (fast user switching).
   - Ctrl+Alt+Del is handled by the agent service (SendSAS), not the helper; the agent sets `SoftwareSASGeneration=1` when it is
     missing, and a group policy that sets it otherwise wins, with the reason shown on the button.
4. [done] **Clipboard, several technicians, consent and banner** (alpha.7): text clipboard both ways, files by paste or drag and
   download notice, joining a running session with each other's pointers, consent prompt, banner and timeout on workstations,
   idle timeout.
   Decided while building (2026-09-17):
   - Only the **first technician** of a session is asked for consent; everyone who joins a running session sees the screen at once,
     and the banner names them. With nobody signed in on the shown Windows session (the sign-in screen) access is granted at once.
     When the prompt cannot be shown, the session ends (the policy asked for consent).
   - At most **one remote control session per Windows session**: opening Remote control where one runs joins it, with its own token
     and key exchange. One helper serves everyone: one screen, one monitor choice, one banner. The helper sends the next frame when
     every technician drew the last one, except a technician more than 3 seconds behind; a technician whose connection falls
     512 frames behind is disconnected.
   - Files pasted or dropped into the window wait in `C:\ProgramData\Fleeto\RemoteClipboard\<session>` (SYSTEM and administrators,
     the signed-in user of the Windows session reading only) and are **deleted when the session ends**, like RDP; the agent also
     clears the folder when it starts. They are placed on the clipboard as a copy, so pasting never moves them away.
   - The consent prompt is a message box the agent service shows on the Windows session (`WTSSendMessage`), default button No; the
     banner and the clipboard live in the helper, on a desktop thread of their own. The agent service, not the helper, writes the
     pasted files, so the helper still touches no file or network.
   - The clipboard switch of the policy applies to every endpoint; consent and banner to workstations only. Clipboard text is
     synchronised up to 512 KB. The browser takes the technician's clipboard from its paste event, so no clipboard permission is
     needed; the paste shortcut reaches the endpoint after the text.
   - The agent that serves remote control must run 0.3.0-alpha.7 or later: an older one would ignore the consent prompt and banner,
     so web and the signer refuse it.
5. **H.264 on Windows**: Media Foundation encoder with WebCodecs, automatic fallback, latency measured (under
   100 ms on a LAN) and a poor link simulated.
6. **Linux X11**: capture, XTEST input with keysym mapping, X selections for the clipboard, banner and consent
   window on workstations, the Wayland message.
7. **Release 0.3.0**: concurrent sessions through the gateway under load, security review of the new code, API waiting list,
   changelog, tag `v0.3.0`.

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
- Sign-in with Microsoft Entra ID (OpenID Connect) next to local accounts: an admin configures the
  tenant, client id and client secret (or certificate) in Settings; users are linked to a local user
  with its role and client restriction (no automatic account creation without an admin decision).
  The secret is stored encrypted in the database like every other secret.
- The Entra ID client secret or certificate uses the expiring-credentials warnings from 0.2.0; after
  expiry the warning states that sign-in with Entra ID stopped working.

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

## Not yet scheduled

Wanted, but not in a version yet: the version is chosen once the open questions are answered.

- Point-in-time recovery (removed WAL archiving on 2026-09-16 until this exists): physical base backups (`pg_basebackup`)
  plus WAL archiving to the off-VPS destination, both encrypted like the nightly dump, a bounded spool on the VPS, and a
  restore procedure that is rehearsed. Open questions: how often a base backup runs, the storage it needs per instance, and
  the recovery point the customers need.

- Recording of remote control sessions and remote terminal transcripts (moved out of 0.3.0 on 2026-09-16; 0.3.0 keeps the
  audit per session, participant and action). Open questions: video or events only, storage per hour in PostgreSQL,
  retention, who may watch a recording.

- Servicedesk ticket reference on notes (moved out of 0.2.1 on 2026-09-15). **Not yet scheduled**: the developer works out
  with the Servicedesk team how both products should work together before anything is built. Starting point from
  2026-09-15, to be confirmed in that alignment:
  - an integration with the **Steaan Servicedesk**, configured in Settings, Integrations (URL and API credential, stored
    encrypted);
  - Fleeto fetches the ticket number through the Servicedesk API and shows it on the note as a clickable ticket number
    that opens the ticket in the Servicedesk;
  - open questions for the alignment: how a note finds its ticket (and whether the Servicedesk creates or links notes),
    whether the link goes one way or both ways (ticket to endpoint, alerts to tickets), which API the Servicedesk offers
    and how it authenticates, and how this relates to the Ticksy hook for alert-to-ticket under Later.
  - When built: the ticket reference on notes goes into the public API or onto `API-WAITLIST.md` in the same commit.

## Later (not planned for 1.0)

- macOS: not supported for now; it may come when there is demand (decided 2026-09-15). It would need an Apple Developer
  account for signing and notarisation, a launchd agent and watchdog, and the Screen Recording and Accessibility
  permission flow for remote control.
- Write access in the public API for the resources a technician can change in the UI: only when there is demand
  (decided 2026-09-15).
- Wayland support on Linux. (File transfer moved into 0.3.0 on 2026-09-16.)
- Whitelabel beyond the FQDN: customer logo and product name in UI and email.
- Steaan management server: central issue, renewal and revocation of licenses, fetched by
  instances over an API; later also an overview of all instances and their versions.
- SNMP for network devices.
- Ticksy hook for alert-to-ticket.
- Mobile device management, network topology mapping: noted, not planned.
