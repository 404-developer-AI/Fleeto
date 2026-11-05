# Changelog

All notable changes to Fleeto are documented here, newest first, following the
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) layout and semantic versioning.

This file holds the `Unreleased` section and the **two most recent released versions**.
When a third released version is added, the oldest entry moves to the top of
`CHANGELOG-ARCHIVE.md` in the same commit.

## [Unreleased]

## [0.9.0] — 2026-11-05

Patch management by the Fleeto agent itself, next to Action1, on Windows, Windows Server, Ubuntu, Debian, AlmaLinux and Proxmox VE,
for endpoints without the Action1 agent: scans every 6 hours with the Windows Update Agent, apt or dnf of the endpoint, installations by
hand from the Patches tab and the endpoint list, and rounds of patch policies for Fleeto, each a job signed by fleeto-signer. One rule
gives every endpoint its engine, so one endpoint is never patched by both. The design and every decision are in `PATCHING.md`. Released
without a round on the test VPS: what still has to be checked on real endpoints (installations and restarts on every platform, the
scan as SYSTEM, WSUS, Microsoft Update registration, the systemd scope, Debian) is listed in `PATCHING.md` section 15, and the load
test at 10,000 agents has not run. A failed update restores the backup made before it (`deploy/release-rollback` is `restore`), since
0.8.0 does not know the installation jobs and deployments of 0.9.0.

### Added

- Patch management by Fleeto itself (0.9.0 step 0): a patch policy now says who installs its updates, Action1 or Fleeto. Every
  existing patch policy stays with Action1, and nothing can choose Fleeto yet: the choice appears in the patch policy dialog once the
  Fleeto agent can scan and install. One rule decides which engine an endpoint has, so one endpoint is never patched by both: none on
  an agent-only endpoint, Action1 wherever its agent runs or Action1 reports the endpoint, otherwise Fleeto on Windows, Windows Server,
  Ubuntu, Debian, AlmaLinux and Proxmox VE. The design and the decisions are in `PATCHING.md`.

- Missing updates on Linux, read by the Fleeto agent itself (0.9.0 step 1). On a managed Ubuntu, Debian, AlmaLinux or Proxmox VE
  endpoint without an Action1 agent, the agent reads which updates are missing every 6 hours, with apt or dnf of the endpoint and at
  the lowest priority; it installs and configures nothing. The Patches tab shows them per package: the installed and the new version,
  where it comes from, security or regular, the CVEs it fixes and their severity from the vulnerabilities Fleeto already knows of the
  endpoint, the advisory, and packages that are held back or that the upgrade would remove. "Scan now" asks the agent to scan at
  once, once per 15 minutes. A scan that could not read everything says why (a held package lock, a repository that answered with an
  error such as the Proxmox enterprise repository without a subscription, an interrupted dpkg) instead of showing an empty list, and an
  endpoint whose scans never read the updates is not counted as up to date. These endpoints now count in patch compliance on the
  dashboard and in the endpoint list, and the "Missing updates" check works on Linux; an update without an advisory date counts from
  the day Fleeto first saw it missing. An endpoint whose scans are older than 7 days while it is online opens a patch state alert.
  Installing follows in a later step.
- Missing updates on Windows, read by the Fleeto agent itself (0.9.0 step 2). On a managed Windows endpoint without an Action1 agent
  (Windows 10 and 11, Windows Server 2016 or newer, Server Core included) the agent asks the Windows Update Agent which updates are
  missing, every 6 hours and on "Scan now". It searches where the endpoint already looks: the WSUS server of a group policy, Microsoft
  Update when it is registered (Office, SQL Server, .NET), otherwise Windows Update; a scan downloads, installs and registers nothing.
  The Patches tab shows per update the title and KB, the release date, the update type, the CVEs, the MSRC severity, the size and
  whether it is downloaded already, with where the scan searched. A search that fails says why (Windows Update not reachable, the
  service disabled, a WSUS server that refuses) with the code. Drivers and feature updates are not searched. A client that uses Action1
  still offers "Install Action1 agent" on such an endpoint; once the agent runs there, Action1 patches it instead.
- Installing updates by the Fleeto agent itself (0.9.0 step 3). "Deploy all missing updates", "Deploy chosen updates" and "Deploy updates"
  on a selection of endpoints now also work on endpoints Action1 does not patch: every endpoint gets a signed job that the Fleeto agent
  carries out with the Windows Update Agent, apt or dnf of the endpoint. fleeto-signer composes what the job installs from the
  deployment itself (identifiers and switches, never a command) and checks again that Fleeto patches the endpoint, that the agent is
  new enough and which of the chosen updates are still missing. On Linux the deployment can install security updates only. With the
  restart switched on, a signed-in user gets the message and 30 minutes before the endpoint restarts; without it the job says that a
  restart is required. The package manager is never killed: an installation that takes longer than 4 hours times out while apt, dnf
  or Windows Update finishes, and the next scan shows what was installed. The history of the deployment shows per update whether it
  was installed, failed, already installed or held, and the job output what the package manager did. A selection with endpoints of
  both engines becomes a deployment in Action1 for its endpoints and one by Fleeto for the others.
- The agent reports the time zone of the endpoint with its inventory; rounds of a patch policy in endpoint time use it.
- Patch policies for patching by Fleeto (0.9.0 step 4). The patch policy dialog asks first who installs the updates, "Patched by Fleeto"
  or "Patched by Action1", and offers only the options of that engine. A patch policy for Fleeto installs in rounds on its schedule,
  in UTC or in each endpoint's own time (with daylight saving): the workers write one round per client and occurrence with an
  installation per endpoint, spread over a random delay (30 minutes by default) and valid for the retry window of the policy, so an
  endpoint that is off at the start gets its round when it connects within that window. fleeto-signer checks every installation of a
  round again (the policy is enabled, patches by Fleeto and is still the endpoint's patch policy, the moment is an occurrence of its
  schedule) and composes it from the policy. Its options: update types, severities and names to leave out (with `*`), security updates
  only on Linux, drivers and Microsoft Update on Windows, the wait after release, the restart with its message and notice, the time an
  installation may take (4 hours by default; the package manager is never stopped), turning automatic Windows Update off and turning
  unattended-upgrades or dnf-automatic off. A round never restarts a Proxmox VE host, so a cluster keeps its quorum; its tab says a
  restart is required. An endpoint whose last scan found nothing the policy installs, whose agent is too old or whose earlier
  installation has not ended is answered at once with the reason. The Patches tab shows the patch policy, the next round in the
  endpoint's time and the last round; the list of patch policies shows who patches and, for Fleeto, the next and the last round. The
  "Scheduled patching" check counts the rounds by Fleeto as it counts those of Action1.
- Drivers, security updates only and the wait after release (0.9.0 step 5): with drivers on, the scans of a Windows endpoint also search
  drivers; a wait after release counts from the release date of a Windows update, the published date of a Linux advisory or, without
  either, the day Fleeto first saw the update missing, and an installation by a round installs only the updates the signer lists as old
  enough. The patches column of the endpoint list names in its tooltip whether Action1 or a scan of the Fleeto agent reported the state.
- The Fleeto agent registers Microsoft Update on a Windows endpoint only while a patch policy for Fleeto asks for it and no WSUS serves
  the endpoint, and removes only its own registration again. It turns unattended-upgrades (with a file of its own in
  `/etc/apt/apt.conf.d`) or the dnf-automatic timers off the same way, never touching what somebody else configured, and reports
  where they stand.

### Changed

- Only a patch policy patched by Action1 becomes an Action1 automation.
- The Patches tab of an endpoint without patch management says why: agent-only, DSM updates itself, or an operating system Fleeto
  does not patch.
- The public API: `GET /api/v1/endpoints/{endpointId}/patches` also answers for an endpoint the Fleeto agent scans, with `engine`,
  `scanTool`, `scanProblem`, `scanSource` and `scanSourceServer`, and every missing update with `advisory`, `origin`, `held`, `removal`,
  `firstSeenAt`, `sizeBytes`, `downloaded` and `isFeatureUpdate`.
- Supported products: Ubuntu, Debian, AlmaLinux and Proxmox VE show their missing updates by Fleeto, and Windows and Windows Server
  by Fleeto as well as through Action1.
- A patch policy for patching by Fleeto on an endpoint Action1 patches says on the Patches tab that it installs nothing there.
- The public API: a deployment has `engine` and `securityOnly`, and each of its endpoints `jobId` and `restart`.
- On Linux the agent runs apt and dnf of an installation in a systemd scope of their own, so stopping or updating the agent never stops
  the package manager in the middle of a transaction; its output goes through a file, so it does not depend on the agent either.
- Automatic Windows Update off now also works on endpoints Fleeto patches itself, where no Action1 agent is needed.

### Fixed

- The public API answered the patch engine of `GET /api/v1/endpoints/{endpointId}/patches` as a number instead of `action1` or `fleeto`
  (found before release).

## [0.8.0] — 2026-11-04

Vulnerability reporting: security advisories of the CCB on the dashboard, the CVEs of the packages of managed Linux endpoints matched
inside the instance with actively exploited CVEs and EPSS, the Vulnerabilities overview, tab and tile, and the end-of-life report of
the operating systems Fleeto supports, with a PDF per client. The web interfaces of Proxmox VE and Synology DSM in the technician's own
browser through Fleeto Connect, and Synology DSM as a Linux endpoint with checks for a NAS. The dashboard designer, column layouts of
the endpoint list, an endpoint description, the consent prompt of remote control in the words of a policy, and the start and end of
every remote session in the audit log. Patch management on Linux, planned here, moved to 0.9.0. The pre-releases `0.8.0-alpha.1` to
`0.8.0-alpha.16` were test builds on the first test VPS; what they found is in Fixed. Like 0.7.0, no load test of the whole and no
separate security review.

### Added

- Security advisories on the dashboard (0.8.0 step 0): the warnings of the Centre for Cybersecurity Belgium (CCB) of the last 30
  days, newest first, with their date and a link to the warning. A warning published since you last opened the dashboard is
  marked new. Mark a warning as handled with a comment, for example which clients you informed; everybody sees who handled it,
  when and with what comment. The workers read the RSS feed of the CCB every hour; it gives a title, a date and a link, no
  summary and no CVE identifiers. A feed that cannot be fetched for 48 hours is an instance health problem.
- Permissions View security advisories (every default role) and Handle security advisories (admin and technician).
- Vulnerability matching on managed Linux endpoints (0.8.0 step 1, not visible yet: the Vulnerabilities tab, the overview and the
  dashboard tile follow in step 2). The agent reports the source package, epoch, architecture and origin of every package, and the
  distribution, Ubuntu Pro services, Livepatch, whether a restart is required, the age of the apt lists and the enabled module
  streams of AlmaLinux. A package of a module stream is matched only with the errata of its stream, and is not covered when its
  stream is unknown, rather than shown clean. The workers fetch the
  Ubuntu OVAL files of the releases in use, the Debian security tracker and the AlmaLinux errata, and match every managed Ubuntu,
  Debian, Proxmox VE and AlmaLinux endpoint inside the instance; nothing of an endpoint leaves it. A feed that cannot be fetched for
  48 hours is an instance health problem. Load-tested at 10,000 endpoints: 5 minutes for the first match, 80 seconds after a feed
  change.
- Vulnerabilities tab on managed Linux endpoints (0.8.0 step 2): the open vulnerabilities per source package with the installed
  version, the most severe one, how many have a fix and the version to update to, opening the CVEs with their severity (and the
  distribution's own word), state, fixed version, advisory and when Fleeto first saw them. The running kernel is a group of its own;
  vulnerabilities the distribution does not count as open and packages the security data does not cover are listed apart. The tab
  says when the endpoint needs a newer agent, runs a release that gets no fixes any more, misses its package lists or needs a
  restart. Links go to the pages of Ubuntu, Debian and AlmaLinux only.
- Vulnerabilities in the sidebar: per CVE the managed Linux endpoints it affects, for every client, one client or one site, filtered on
  severity, on having a fix to install and on CVE or package, opening the list of those endpoints. Windows endpoints are not in it.
- Dashboard tile Vulnerabilities: Linux endpoints missing a critical fix, and those on a release no longer supported.
- Settings, Vulnerabilities: the state of every feed in use, "Refresh now", and how many months ended findings are kept (default 13).
- Permissions View vulnerabilities (every default role) and Vulnerabilities (administration).
- Actively exploited vulnerabilities and exploit prediction (0.8.0 step 3): a CVE that CISA's catalog of known exploited
  vulnerabilities lists is marked "Actively exploited" (with ransomware use when CISA knows it), and every CVE shows its EPSS score, the
  probability of exploitation in the next 30 days. While CISA's catalog cannot be fetched, the exploited vulnerabilities of ENISA's EUVD
  stand in, and Settings, Vulnerabilities says so. The workers fetch both once a day; a feed that cannot be fetched for 48 hours is an
  instance health problem.
- The Vulnerabilities tab lists actively exploited vulnerabilities first after the ones with a fix, then by severity and EPSS, and shows
  the CVSS score where the distribution gives one. The overview lists actively exploited CVEs first, filters on them and counts the
  endpoints that have one; the dashboard tile counts Linux endpoints missing a critical fix or with an actively exploited vulnerability.
- History tab of Linux endpoints: the vulnerabilities as they were found, fixed, removed with their package or withdrawn, searchable on
  CVE or package.

- Proxmox web interface (0.8.0): on a managed Proxmox VE host, "Proxmox web interface" in the right-click menu of the endpoint list opens
  the real web interface of Proxmox in your own browser, with every console, upload and setting, without a VPN or an open port. Fleeto
  Connect on your PC carries it through an end-to-end encrypted remote session; the agent connects to the web interface on the host itself
  (`127.0.0.1:8006`) and nothing else, and the TLS of Proxmox runs from Fleeto Connect to the host, pinned to the certificate the agent read,
  so Fleeto never sees the pages or your Proxmox password. You sign in with your Proxmox account. The window proposes to install Fleeto
  Connect when it does not start: for Windows 10 and 11 a program that installs itself for your account (not signed with Authenticode yet,
  so SmartScreen warns on its first run), for Ubuntu desktop a `.deb`, both with their SHA-256 and listed in the signed release manifest.
  Fleeto Connect asks once per instance whether it may connect to it. Audited as a remote session, with the claim of Fleeto Connect.
- Permission Proxmox web interface (admin and technician).
- Dashboard designer (0.8.0, `DASHBOARDS.md`): design dashboards in Settings, Dashboards and link them to roles. A user sees the first
  published dashboard in the list that is linked to one of its roles, otherwise the built-in default dashboard, which looks as the
  dashboard did before. The designer has the tile types on the left, the grid in the middle and the settings of the selected tile
  on the right: drag a tile onto the grid or click it, move and resize it with the mouse or with the arrow keys (Shift to resize),
  remove it with Delete, undo and redo with Ctrl+Z and Ctrl+Y. The grid has 12 columns; tiles never overlap, the tiles below move
  down and, with "Pull tiles up", back up. On a narrower screen the tiles flow over 6 columns, on a phone they stand under each
  other. Changes are kept as a draft until you publish; the last 20 published versions can be made the draft again. "Preview as"
  shows the dashboard as a user of a role sees it. Copy a dashboard (also the default one), export it as a file and import it on
  another instance; clients, sites and tags the other instance does not have are left out.
- Tile settings: a title of its own, a scope (chosen clients, chosen sites, clients with a tag, servers or workstations, or the
  scope of the dashboard), an accent color, thresholds from which a counter turns warning or critical, filters per tile type
  (severity and kind of alert, alerts on hold, tier, failed only, period, grouping), hiding on small screens and hiding while there
  is nothing to show. A scope only narrows: a tile never shows more than the groups of the user give.
- New tiles: Endpoints (all, managed or agent-only), Endpoints in maintenance, Alerts on hold, Licenses free, Endpoints offline
  list, Agents out of date list, Task runs (running and failed), Client status (a cell per client, or per site when the scope holds
  one client, colored by its worst state), the charts Alerts opened per day, Endpoints by group and Top clients by open alerts, and
  the content tiles Heading, Text (markdown) and Link. The workers count the alerts opened per client and day every 10 minutes for
  the chart, kept 400 days.
- The dashboard reads only the tiles it shows, each on its own: a tile that fails says so and can be tried again, and a tile
  reloads only on its own live events. Each read is measured by the performance monitor.
- Permission Dashboards (administration, admin only), and the button "Design dashboards" on the dashboard for who holds it.
- A Synology NAS, which has no `/etc/os-release`, shows its operating system as DSM names itself, such as "Synology DSM
  7.1.1-42962 Update 8", read from `/etc.defaults/VERSION`, instead of "Linux".
- The Synology web interface (`PROXMOX-WEB.md` §8): the web interface of DSM on a managed Synology NAS, opened from the right-click menu
  of the endpoint list and shown in the technician's own browser through Fleeto Connect, like the Proxmox web interface. A remote
  session kind of its own (`SynologyWeb`) with its own permission "Synology web interface" (admin and technician); the agent connects it
  to the default HTTPS port of DSM, `127.0.0.1:5001`, and nothing else, and says so when nothing answers there. The claim of Fleeto
  Connect records which web interface it opens (migration `SynologyWebInterface`, expand only). Needs agent and Fleeto Connect
  0.8.0-alpha.9 or later; the download of Fleeto Connect needs either permission.
- The Linux install command creates `/opt` when it is missing (Synology DSM has none), and the install dialog explains how to run it as
  root in the Task Scheduler of DSM, so SSH can stay off on a NAS.
- Three checks for what a NAS is watched for, on Linux endpoints, read by the agent on the endpoint itself: **RAID state** (every software
  RAID array of `/proc/mdstat`: healthy, rebuilding as a warning, degraded or inactive as critical; data scrubbing is healthy; with `*`
  on Synology DSM not `md0` and `md1`, which have a slot for every bay), **Disk health** (SMART read by smartctl: failing as critical,
  reallocated, pending or uncorrectable sectors and NVMe media errors as a warning) and **Disk temperature** (°C from SMART, 50 and 60 by
  default). smartctl runs with `-n standby`, so a sleeping disk is never woken and keeps its last result; the serial number of a disk is
  never sent. In the public API as `raid_state`, `disk_health` and `disk_temperature`. An older agent reports that it does not know them.
- Endpoint description (decided 2026-11-04): give an endpoint a description of at most 200 characters in Edit in the right-click menu
  of the endpoint list, for example what it is for or where it stands. The list shows it in its own column after the host name, the
  search of the list finds it, and the Summary tab shows it. Changing it needs the permission Edit endpoints (formerly "Change the
  endpoint class"; admin and technician); the agent never changes it. Audited with its length, never its text. In the public API as
  `description` of an endpoint (migration `EndpointColumnLayouts`, expand only).
- Column layouts of the endpoint list (decided 2026-11-04): in Settings, Column layouts an admin chooses which columns the endpoint
  list shows and in which order, and links the layout to roles. A user sees the first layout in the list that is linked to one of its
  roles, otherwise the default columns (the list as before, with Description after Hostname). Users cannot change their own columns.
  Besides the columns of before and Description: IP address, Public IP address, Agent version, Open alerts, Patches, CPU, Memory,
  Disk free (the fullest drive) and Uptime. A column never shows more than the permissions of the viewer give (Open alerts needs View
  alerts, Patches needs View patches), and the inventory and patch data are read only for a layout that shows them. Creating,
  changing, reordering and deleting a layout are audited.
- Permission Column layouts (administration, admin only).
- The consent prompt of remote control in your own words (decided 2026-11-04): a policy sets the title, the message and the labels of the
  Allow and Deny buttons, for example in the language of the people at the endpoints; an empty field keeps the default. The prompt
  always names the technician and shows the seconds left itself, so a policy cannot hide who is asking, and Deny stays the default
  button. The signer puts the texts in the session token only when the workstation asks for consent (migration `PolicyConsentTexts`,
  expand only).
- A new consent prompt in the style of Fleeto, drawn by the agent on Windows and on Linux with X11: the title, the technician, the
  message, a teal bar that counts down and the buttons Deny and Allow. Needs agent 0.8.0; older agents keep the plain prompt.
- While the person at the endpoint is asked, the remote control window shows a card over the screen with who is asked and a bar with
  the seconds left, instead of only a line of text above the screen.
- End of life (0.8.0 step 4, decided 2026-11-04): Vulnerabilities has the tabs Linux packages, End of life and Supported products. End of
  life lists per client the managed endpoints whose operating system no longer gets security updates, or will not within a period you set
  (default 6 months), for Windows, Windows Server, Ubuntu, Debian, AlmaLinux, Proxmox VE and Synology DSM. The release is recognised from
  what the agent already reports, so no agent update is needed. The workers fetch the dates from endoflife.date once a day; those of
  Synology DSM come from Synology's Software Life Cycle Policy, kept in Fleeto. A paid extension (Windows ESU, Ubuntu Pro, Extended LTS) is
  shown with its end; mark per endpoint that the client bought it (permission Edit endpoints) and the endpoint counts as covered until the
  extension ends. Ubuntu Pro counts by itself where the agent reports it. Download the report of a client as a PDF.
- The end-of-life report runs on a schedule set in Settings, Vulnerabilities (daily, weekly or monthly at an hour, default monthly on the
  1st at 07:00) and with "Refresh now" on the tab. After every scheduled run it goes to the notification channels that chose "Also send
  end-of-life reports", with the clients each channel receives. Dates that cannot be fetched for 7 days are an instance health problem.
  Migration `EndOfLifeReport`, expand only.
- Supported products: per operating system whether Fleeto offers monitoring, patching, remote control, remote background, vulnerabilities
  and a web interface, with their limits.
- The audit log records the start and the end of every remote session (remote control, remote background and the web interfaces):
  `remote_session.started` when the first technician connects and `remote_session.ended` when the session ends, with its kind, the
  endpoint, the reason and how long it lasted. The end is by the technician who left last, or by the system when the gateway restarted
  or nobody was connected any more. Both also show on the History tab of the endpoint. The joins and leaves of each technician are
  recorded as before; the details of every remote session entry are now camelCase, like the other entries.

### Fixed

- The console of a VM, container or host in the Proxmox web interface did not connect in any browser ("Connection closed",
  code 1006): the noVNC page of Proxmox always opens a `wss://` connection, which cannot reach the plain HTTP address on loopback that
  Fleeto Connect serves. Fleeto Connect now opens that page with `encrypt=0`, so noVNC connects with `ws://` to Fleeto Connect, which
  carries it over its own TLS to the host as before. A Fleeto Connect older than 0.8.0-alpha.16 is now refused for the Proxmox web
  interface, and the window of the session offers the current one to install (the Synology web interface still accepts 0.8.0-alpha.9).
- On a screen wider than 1920 pixels the pages stood centred, far from the sidebar. They now start beside the sidebar, and the Clients
  and Settings workspaces use the whole width.
- Editing a task showed the id of the script of a step in its dropdown instead of the name of the script.
- An agent installed again on Windows after an uninstall, without a restart in between, lost its binary at the next restart: the uninstall
  had left the deletion of its own running binary to Windows for that restart, by path. Installing the agent and every start of the agent
  and the watchdog now cancel such deletions in the program folder, and a watchdog or agent that finds the binary of the other service
  missing puts its previous version back, after which the update ring installs the current one again.
- Vulnerability feeds were never fetched: the workers could not create the temporary tables their import uses, and the failure
  stopped every feed without showing a reason. The workers now get `TEMPORARY` on the instance database (also on existing
  instances, at the update), and a feed that cannot be stored shows as failing with its reason while the other feeds are still
  fetched.
- The diagnostics report of Instance health named statements by everything since pg_stat_statements was last reset, which can be
  weeks: a loop fixed long ago stayed on top with millions of calls. It now ranks them by what they did in the last 24 hours,
  counted from the statement snapshot the performance monitor took a day ago, states the period, when the statistics were last
  reset and the calls an hour of every statement. Without a snapshot of a day ago it says the totals reach back to the reset.
- One refused fetch of a blocklist (a 429 of the CrowdSec Console, fetched every 6 hours) opened an instance health problem: its
  one call of the hour failed 100%. A host is now critical, and an instance health problem, only when enough of its calls fail;
  a single 429 is a warning on Settings, Performance. The advice names what calls the host instead of always the Action1 budget.
- The diagnostics report said "last success never" for backups made outside Fleeto; it now says Fleeto makes none itself.
- Every update pulled a new set of images and install.sh never removed the old ones, so the disk of a VPS filled with a set per
  release. After a successful update install.sh removes the Fleeto images that no container uses and that no instance, no previous
  configuration of an instance (what a rollback returns to) and not the host proxy refers to.
- On Linux with a systemd older than 232 (219 on Synology DSM 7 and CentOS 7) the agent installed and enrolled, then exited at
  every start with "'run' is started by the service manager", so the watchdog was never installed: it recognised a start by
  systemd only by `INVOCATION_ID`, which those versions do not set. The agent and the watchdog now also recognise their unit in the
  control group of the process. An uninstall started from Fleeto on such a version, which refused `systemd-run --collect`, now
  tries again without the options an older systemd-run does not know.
- A right-click on an endpoint in a long list sometimes did nothing, for example on the next endpoint after one was moved to another
  site with Edit: MudBlazor places a menu only while its anchor is in view, also a menu that opens at the cursor, and the anchor of
  the endpoint menu sat below the list, scrolled out of view. The menu then opened invisibly and stayed open, so every later
  right-click changed nothing. Its anchor is now pinned to the window, and a right-click closes a menu left open before it opens it
  at the cursor. (The change of 0.8.0-alpha.10, closing the menu before its action, did not help and is undone.)
- The CrowdSec blocklist was hardly ever refreshed: its community plan allows one pull of an integration a day and answered 429 to
  50 of the 62 pulls in a week, because Fleeto fetched it every 6 hours and an hour after every failure. It is now fetched once a
  day, also after a failure; saving the integration in Settings, Security still fetches it at once.
- The counts per site and CVE of the vulnerability overview were rebuilt every 10 minutes whether or not anything had changed, with
  an ANALYZE of the findings each time: about 40% of all database time of the test instance. Without a match they are now rebuilt
  only when a managed Linux endpoint was deleted, moved to another site or client, or left managed, or a feed changed.
- A vulnerability feed whose file changed only outside the releases in use (most changes) was still replaced in full and raised its
  version, so every endpoint was matched again: 183 full replacements in a week, 23% of the database time. The workers now keep a
  hash of the records they made of a download and replace nothing when it is the same. Migration: `VulnerabilityFeeds.ContentHash`.
- The performance report counted about 39,000 exceptions a week in every component that were not failures: the database
  notification listener ends a quiet wait with a timeout Npgsql throws and catches itself. Those are no longer counted; a timeout
  anywhere else still is.
- The tasks loop of the workers ran without rest, about 45 passes a second, once a task had a schedule that ended (a task that runs
  once, after its run, or one past its end date) or for two minutes after an alert of one of its checks: every pass wrote the time
  into the state of the task, and the notification of that change woke the loop again at once. On the test instance that was 16 million
  database commands in six hours and 16% CPU of the workers on average. A pass now writes the state only when something in it
  changed.
- After a restart of the workers every loop ran its first pass in the same second: some 40 passes queued 150 work items on a thread
  pool that starts with one thread per core, so each first pass took 2 to 9 seconds. Each loop now waits a fixed share of the first
  20 seconds (never more than its own interval) before its first pass; a notification still runs it at once.
- The counts per site and CVE of the vulnerability overview were rebuilt after every match, also when the match changed no finding
  (most matches follow an inventory change that leaves the findings as they were), each time with an ANALYZE of the findings: a
  third of all database time of the test instance. They are now rebuilt only after a match that opened, changed or ended a finding,
  and the findings are analysed only when a tenth of them (at least 1,000) changed since their last analysis.
