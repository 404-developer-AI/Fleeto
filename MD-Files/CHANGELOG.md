# Changelog

All notable changes to Fleeto are documented here, newest first, following the
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) layout and semantic versioning.

This file holds the `Unreleased` section and the **two most recent released versions**.
When a third released version is added, the oldest entry moves to the top of
`CHANGELOG-ARCHIVE.md` in the same commit.

## [Unreleased]

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

- A step of a task can be run again on one endpoint, from its Tasks tab (decided 2026-11-05): a step that failed, or one that did not
  run because an earlier step stopped the run, runs on its own in the same run, as the user who asked, without the steps after it.
  The run is judged on the newest attempt of every step, so it succeeds once every step passed, and the alert of the task resolves.
  The Tasks tab also starts a new run of a task on that endpoint only (Run now), and shows every attempt of a step with its output.

- The whole output of a script check (0.9.0). Clicking the status of a script check on the Checks tab shows the output of its
  latest run, stdout and errors apart, with the exit code and when it ran. In the history of a script check a block of the status
  timeline lists its runs, each with its status, exit code, first line and output size, and a run opens its output. Every script
  check has a new setting Output limit (KB), 8 by default and at most 256: a run whose script wrote more says how much it wrote, that
  only the first part is kept and where to raise the limit. The agent sends output that did not change since the last run only once a
  day, so a script that prints the same every run costs almost nothing. Runs of an agent before 0.9.0 still show only their first
  line. Script output can hold personal data: it is shown only to users who see the checks of the endpoint, never put in an alert or
  log, and every time it is shown is in the audit log, without the output.

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
- The file explorer of remote background no longer merges a copied folder into a folder of the same name in the destination; it
  says that the folder exists, so the technician renames or deletes it first.

### Fixed

- A new version of the script of a script check now runs from the next run. Until now the agent kept running the version it had,
  until it restarted or something else of the check changed, because it compared the parameters of the check and not its script.
- The status timeline in the history of a script check judges every block by what the exit codes of the script mean, as the status
  of the check does: an exit code the script counts as success or info is no longer red, and an exit code 0 that it counts as a
  warning or an error no longer looks fine. Blocks of before this version, in the week, month and year, keep the old rule: fine only
  with exit code 0.
- An endpoint whose public address changed while it was connected, such as a VM moved to another Proxmox node by live migration
  behind another NAT, stayed offline for about 15 minutes on Linux, until the operating system gave up the dead connection. The
  agent, the watchdog and remote sessions now give up a connection whose data stays unacknowledged for 2 minutes, the time the
  gateway waits for them, and connect again at once.
- The alert of a task that failed could not be acknowledged, put on hold or resolved from the Tasks tab of the endpoint; it can now,
  with its comment, and the tab shows whether the alert is open, acknowledged or on hold. The Alerts page names failed tasks too.
- The public API answered the patch engine of `GET /api/v1/endpoints/{endpointId}/patches` as a number instead of `action1` or `fleeto`
  (found before release).
- The patch sync of an Action1 organization stopped when two endpoints of one client reported the same Action1 agent id, a cloned VM
  for example. The endpoint that reported the id most recently now gets the patch state, and the sync goes on.
- Test databases can get a suffix (`FLEETO_TEST_DB_SUFFIX`), so test runs from two worktrees no longer drop each other's database.

### Security

Found by a security review of 0.9.0 and each reproduced with a test before it was fixed.

- A standard user on a Windows endpoint could take control of the agent before it was installed: a folder created beforehand under
  `C:\ProgramData\Fleeto` stayed theirs after the installation protected it, so they could write the state of the agent and with it
  the key it trusts, and run scripts as SYSTEM. `C:\ProgramData\Fleeto` and the state folders of the agent and watchdog now belong to
  Administrators with access for the system only; a folder that exists already is taken over or refused, and a link is refused.
- The agent starts only from a state whose certificate its pinned CA issued for that endpoint, so a forged state file cannot load.
- An agent update ran the staged binary to ask its version without checking it again, so a swapped file could run as SYSTEM; the
  binary is now checked through a handle that is held while it runs. The watchdog binary is checked against the manifest before it
  is installed.
- The install took over a state folder of the old agent (`C:\ProgramData\Fleetify\Agent`) that a user could have planted. It now
  does so only while the old service exists and the folders belong to the system.
- A compromised gateway could make agents that enroll pin a signing key of its own. The instance CA now signs the signing key in the
  enrollment response, and the agent checks that signature against its pinned CA.
- A user who may edit monitoring templates or checks could create a script check without the permission to run scripts. Adding,
  editing, switching on, adjusting or copying a script check now needs View scripts and Run scripts.
- Script approval could be switched off for endpoints without the permission to approve scripts: by linking a policy without approval
  on a more specific level, unlinking or deleting the policy, narrowing the endpoints it is for, moving an endpoint, overriding its
  class or changing a client template. Each of these is now refused for such a user when an endpoint would lose approval.
- Remote session connections of a user stayed open after the user was deleted, its two-factor authentication was reset, an admin set
  its password, it was unlinked from Entra ID, or "Microsoft handles the second factor" was switched off. They now end at once, and
  so do those of a user that loses an endpoint because it moved to another site, whose pages reload.
- An agent could report the Action1 agent id of an endpoint of another client, after which Fleeto moved that endpoint into its own
  client's Action1 organization, or forgot its patch state when a removal was answered "not found". A move now goes ahead by itself
  only when the old record was left behind by the same machine, otherwise it waits in Settings, Integrations with its cause; a
  removal touches only the organization it was for. The gateway keeps an Action1 agent id only in the form of a GUID.
- Web and the gateway took forwarded client addresses from every peer in whole private ranges, so a peer that could reach the
  containers could choose the address it appeared to come from, make itself unblockable or get somebody else blocked. They now
  trust only the host proxy: `install.sh` gives the edge network of every instance a fixed subnet (from `10.211.0.0/16`) and
  trusts its gateway address alone. An existing instance gets its subnet on its next update, which recreates its networks. The
  forwarded host is no longer taken from the proxy.
- A blocklist could lock admins out, and `install.sh --unblock` did not help. A blocklist no longer refuses a network a user signed
  in from in the last 30 days, switching a list on is refused when it holds your own address, and `--unblock --allow <address>`
  adds an allowed address.
- An image on any web site pointing at a path such as `/.env` got the visitor's address blocked automatically for a day. Such
  probes, which a browser marks as made for another site, are still reported but never block on their own.
- A session from before a two-factor reset could enrol an authenticator of its own and get a new session. Enrolling now needs a
  session with the current security stamp. Sessions also end at once after a two-factor reset, a password set by an admin, an
  Entra ID unlink, a role change or "Microsoft handles the second factor" switched off, instead of within 5 minutes.
- The group editor could change the reach of a user holding permissions the editor lacks. It now needs the authority to manage every
  member it affects, like changing the groups of one user.
- Taking Approve scripts from a role or unlinking an approver from Entra ID left configurations signed with script checks that user
  had approved. They are now signed again, so those checks follow the approval that still holds.
- Linking a monitoring template that holds a script check now needs View scripts and Run scripts, as adding one does.
- A certificate renewed at the moment an endpoint was revoked or deleted could survive it. Revoking and deleting now wait for an
  issuance in flight and take its certificate along.
- The gateway could write when an endpoint became managed and so start its tasks again. It may now change only the online state and
  what the agent reports; every other column of an endpoint is refused.
- The file explorer of remote background followed links a local user planted, so a copy as SYSTEM or root could write behind a
  junction into a protected folder. Copying, deleting, renaming, creating folders and uploading now refuse any link on the way,
  and the listing shows links with where they point.

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

## [0.7.0] — 2026-10-26

The performance monitor: Settings, Performance shows where Fleeto itself is slow and why, with a report to hand to Claude Code,
built in three steps (the core, Diagnose, and the browser, edge check, benchmark, budgets, comparisons and instance health). The
security monitor: Fleeto counts what attacks it, raises security incidents, blocks addresses by hand, by itself and from public
blocklists and CrowdSec, also in three steps. Next to them: turning automatic Windows Update off from a patch policy, a storage
report for the customer, copying the output of the remote terminal and the process list of a CPU or memory result, several
remote background sessions at once and a search in Settings, Scripts. The pre-releases `0.7.0-alpha.1` to `0.7.0-alpha.13` were
test builds on the first test VPS; what they found is in Fixed. The overhead measurement at 1,000 agents was dropped by the
developer; like 0.6.0, no load test and no separate security review.

### Added

- Settings, Performance: where Fleeto itself is slow and why, for admins. It measures Fleeto, never the endpoints it manages.
  For a period of 15 minutes to 7 days it shows a score per category (hosting, backend code, database queries, database
  maintenance, background and realtime, agent channel), the top bottlenecks ranked by the extra waiting they caused, each with
  its evidence, likely cause, what to change and where to look in the code, a timeline of page interactions and requests, the
  busiest pages, routes, loops, agent messages, database sources and external hosts, the runtime of every component and the VPS.
  Network and frontend were added in step 3.
- The report for Claude Code: copy or download it as Markdown, at most 100 KB, with instructions for this repository first and
  what was not measured last. Everything in it is redacted: SQL without values, and no ids, addresses, tokens, host names or
  client codes. Every export is audited.
- Collection in every component, written once a minute in one batch: page loads and events per route template (the time a
  dialog stays open left out), API and sign-in requests, database commands per page, loop, message and signing request, every
  run of every worker loop, outbound calls per host, agent messages with their size and time to acknowledge, connections, job
  delivery and the remote session relay, signing requests, live updates and notifications, exceptions by type, the runtime
  (CPU, memory, garbage collection, thread pool, database pool) every 10 seconds, the VPS every minute (CPU with steal and
  iowait, load, memory, disk latency, throttling), and statement, database and table statistics from PostgreSQL. Level Basic
  is on by default and costs below 1%; the monitor shows its own cost.
- The Settings tab: the level (Off or Basic), each collector on or off, the slow event threshold, the snapshot intervals,
  retention and the thresholds of every finding, with their defaults. Changing them needs the new permission Manage
  performance monitoring and is audited with the old and new values; viewing needs Performance. Both are administration of
  the instance, held by admins only.
- A deploy marker on the timeline when Fleeto starts on a new version.
- Diagnose (step 2): started from the top of Settings, Performance for 15 minutes to 4 hours (60 by default, the longest a
  setting up to 24 hours), with a countdown, and ending by itself on server time; stopping it early and starting it are
  audited. While it runs every component traces every database statement through Npgsql: its fingerprint (literals replaced by
  `?`, never a value), the page, route, loop or message that sent it, and the method in the code that sent it, found under a
  budget of 50 stack walks a second. One statement sent more often than the threshold N+1 repeats (10) within one interaction,
  request, run or message is an N+1 suspect; a slow event keeps its three heaviest statements; an exception names the class
  that threw it; statement and table statistics are read every 5 and 15 minutes. Outside Diagnose none of this runs.
- New findings: N+1 suspects naming the page, the statement and the method; the statements that cost the components most,
  with who sent them; database sessions idle in a transaction or waiting for a lock (the workers now count the sessions in
  every database snapshot); duplicate indexes. The monitor may cost three times its threshold while Diagnose runs.
- New tabs on Settings, Performance: Pages and API (every page and route, opened to its latency over the period, its
  distribution, its slowest occurrences and the statements it sent), Database (time by source, the statements measured in
  Diagnose, N+1 suspects, PostgreSQL's statement statistics, tables, unused and duplicate indexes, sessions, and the settings
  and I/O of PostgreSQL read live), Server (the VPS and the runtime of each component over the period, exceptions by type),
  Background (when every loop ran, whether pages are slower while a loop runs, loops, external calls, live updates,
  notifications) and Agent channel (connected agents, connections, messages, job delivery, relay, signing).
- Plan and Analyze on the Database tab: the generic plan of a stored statement (EXPLAIN without running it, in a read-only
  transaction of 5 seconds at most, only for a statement that reads, looked up by its id on the server, never text from the
  browser), and ANALYZE of one table. Both need Manage performance monitoring and are audited. They go through two database
  functions granted to web only: one that can only analyze a table of Fleeto, one that only explains, with web's own rights.
- Manual markers on the timeline ("added an index on Jobs"), stored masked; the audit entry holds the length, never the text.
- The export as JSON (with a `schemaVersion`) for tooling, and as a ZIP with the Markdown, the JSON and the generic plans of the
  top statements. The Markdown says how much of the period ran in Diagnose and adds the statements measured in Diagnose with
  their callers, the N+1 suspects, the slowest events with their heaviest statements, the indexes and the sessions.
- The browser (step 3): for 10% of page loads (a setting; every page while Diagnose runs) the browser of a signed-in user measures
  the Web Vitals of each page (largest and first contentful paint, time to first byte, the slowest interaction of a view, layout
  shift, long tasks) and the round trip of the live connection and the connection setup, and sends them over the live
  connection every 30 seconds. Only the route template of the page and the figures are kept, checked against a whitelist with
  ranges and caps, on server time. A new tab Frontend shows them per page in the bands of Core Web Vitals, with the network.
  New findings under Frontend (largest contentful paint, interaction to next paint, layout shift, long tasks per view) and
  Network (round trip, connection setup). The collector Browser can be switched off.
- Edge check and benchmark on the Server tab (step 3), on click and audited (`performance.probe_requested`), run by the workers:
  the edge check times DNS, TCP and TLS to the instance URL and checks the TLS version, HTTP/2, the certificate, compression,
  caching of static files, keep-alive, HSTS and the Content Security Policy, with what to change for each problem; the benchmark
  measures hashing on one core, disk writes with fsync and round trips to the database. Their latest results are in the export.
- Budgets (step 3): lines such as `page /clients load = 800`, `api GET /api/v1/endpoints = 300`, `loop patch-sync = 5000` in the
  Settings tab, each read with a reason when it cannot be; the Overview shows every budget against the period, and a flow over
  its budget is a finding.
- Comparisons (step 3): every period against the one of equal length before it, and, when a new version started in the period,
  the version before against the current one. The Overview shows the largest changes; a flow 25% (a threshold) and 20 ms slower
  than before is a finding.
- Critical findings of the last hour become a problem of Instance health (step 3), as a warning, judged every 15 minutes, at
  most 5 at once, and quiet for 24 hours (a setting) after one resolved; an admin can switch it off. Instance health has a card
  Performance for them.
- "Copy output" above the terminal of a remote background session, on Windows and Linux (asked for by the developer on
  2026-10-19): copies everything the terminal shows as plain text, also what scrolled away (up to 10,000 lines), with lines the
  terminal wrapped joined again. It happens in the browser; the output never passes the server, as before.
- Check history of a CPU or memory usage check, "Copy" above the process list (asked for by the developer on 2026-10-20): copies
  the measurement, the chart of the period shown (hour, day, week, month or year, with thresholds and the dot of the copied list
  filled; added 2026-10-20 after testing `0.7.0-alpha.7`) and its processes (name, PID, user, CPU with a bar, memory) as an image
  in the branding of Fleeto, the same way as the storage summary, and downloads it where the browser does not allow an image on the clipboard. Audited
  (`check.process_list_exported`, the check and the time of the result, never a process or user). The image stays in the UI.
- Storage tab, "Copy summary" and "Export PDF" (asked for by the developer on 2026-10-19), to hand the storage of a drive to the
  customer. The summary is an image in the branding of Fleeto, the same in every mail or ticket it is pasted into: the drive with
  how full it is, its 10 largest folders directly below the drive and its 10 largest files. Where the browser does not allow an
  image on the clipboard, the image is downloaded. The PDF report adds every scanned folder as a tree with its growth over 7 and 30
  days and every scanned file, with the logo, header and page numbers on every page. Fleeto writes the PDF itself, without a new
  library. Both are audited (`storage.report_exported`, the drive and the format, never a path). On the API waiting list.
- "Turn off automatic Windows Update" on a patch policy (decided 2026-10-19), so only the rounds of the policy install updates.
  Action1 has this option only in its console, not in its API, so the Fleeto agent does it on the endpoint itself: on a managed
  Windows endpoint whose enabled patch policy asks for it and that Action1 reports, it sets the Windows Update group policy value
  `NoAutoUpdate` to 1, only when that value is missing, and records under `HKLM\SOFTWARE\Fleeto` that it did. A value a group
  policy sets wins and is never changed. The agent removes its own value again when the policy no longer applies, is switched off
  or no longer asks for it, when Action1 no longer reports the endpoint, when the Action1 agent is not installed (checked on the
  endpoint itself as well) and when the Fleeto agent is uninstalled. The switch travels in the signed agent configuration;
  fleeto-signer refuses an agent-only configuration that carries it. The Patches tab shows where it stands: turned off by Fleeto,
  set by a group policy, not turned off without the Action1 agent, or not reported by an agent older than 0.7.0. On the API
  waiting list.
- Settings, Security (the security monitor, step 1): block an address or network, for an hour up to 30 days or until lifted, with
  a reason, and allow one so that nothing ever blocks it. Web answers a blocked address with a 403 that names it before anything
  else runs; the gateway drops it before the TLS handshake on the agent port and refuses it on the relay. The change takes effect
  within seconds (a database trigger wakes web and the gateway, which keep the list in memory). An allow always wins. Private
  and reserved ranges, which the instance uses itself, can never be blocked or allowed, nor can a network wider than /8 (IPv4)
  or /19 (IPv6), nor the network that holds the admin's own address. Every change is audited. New permissions Security and
  Manage security, administration of the instance, held by admins only. On the API waiting list.
- Security incidents (the security monitor, step 2), on the new first tab of Settings, Security: what the workers found, with the
  cause and the next step, when it was first and last seen, and whether the address was blocked. An admin acknowledges an
  incident or resolves it, both with a required comment kept with it (the audit log holds its length, never its text); an
  incident also resolves by itself after a day without the finding (15 minutes for the set-up findings below). A switch on the
  tab turns automatic blocking off or on; off, incidents still open and notify. A dashboard tile for admins shows the open
  incidents nobody acknowledged and the blocked addresses. A notification channel can choose "Also send security incidents",
  never combined into a digest. On the API waiting list.
- Blocklists (the security monitor, step 3), a tab of Settings, Security: Spamhaus DROP, abuse.ch Feodo Tracker and FireHOL
  level 1, on by default, and blocklist.de, off by default, each switched on or off by an admin with its number of networks, when
  it was last fetched and why a fetch failed, and "Fetch now". CrowdSec as a fifth list: the blocklists a CrowdSec Console account
  subscribes to, through a firewall integration of the Console whose id, user name and password an admin enters (stored
  encrypted, the password never shown again or audited). On the API waiting list.
- Settings, Scripts has a search field: it filters the script library on name, description, language, platform and client,
  every word must match.

### Changed

- Remote background opens a new window with a session of its own every time it is chosen in the right-click menu, so a
  technician can work in several sessions at once on the same endpoint and on others, Windows and Linux alike, up to 8 sessions
  per endpoint. Before, choosing it again brought the open window of that endpoint to the front. Remote control is unchanged.
- An endpoint whose only unresolved alerts are on hold shows like one in maintenance: a green icon in the endpoint list, with
  when the hold ends in its tooltip, and counted in the maintenance badge of its client and site in the clients panel. How a
  hold works is unchanged.
- Migrations `AlertHoldIsMaintenance` and `AlertHoldIndexesRestored`: the first changed the indexes of held alerts for a change
  that was taken back before release; the second restores them as they were.
- `install.sh` grants `pg_monitor` to the workers instead of `pg_read_all_stats`: monitoring rights only, which also cover the
  table statistics and settings the performance monitor reads.
- Instance health reads its statement statistics through the same reader as the performance monitor, so both name the same
  statements.
- The nightly backup dumps the tables of the performance monitor without their rows, so a restore does not bring old
  measurements; its settings are kept. The statements of Diagnose (`PerfFingerprints`) are among them.
- A change of the patch policy linked to a client, site or endpoint, and Action1 starting or stopping to report an endpoint, now
  give its agents a new configuration (unchanged ones are skipped by the signer).
- Migration `TurnOffWindowsUpdate` (expand only): `PatchPolicies.TurnOffWindowsUpdate` and `InventorySnapshots.WindowsUpdate`.
- Migration `PerformanceDiagnose` (expand only): the table `PerfFingerprints`, the heaviest statements of a slow event, the
  sessions of a database snapshot, and the functions `fleeto_perf_analyze` and `fleeto_perf_explain`.
- Migration `PerformanceProbes` (expand only): the table `PerfProbes` for the edge checks and benchmarks, written by web and run
  by the workers, kept 30 days and dumped without rows like the other tables of the monitor.
- Instance health has a component Performance; its card says that critical findings of the performance monitor open a problem
  there.

### Fixed

- The connection ping of the navigation counted as an event of the page it happened on, every 5 seconds per open page, which made
  the events of every page look busier and faster than they were. The ping and the reports of the browser are no longer
  measured as interactions.
- The workers loop that follows patch deployments woke itself up for as long as a deployment was open: every pass announced
  every open deployment on the channel the loop itself listens on, also when nothing had changed, so it ran about 100 times a
  second instead of once a minute (found with the performance monitor on the test VPS on 2026-10-20: 2.2 million runs in 6
  hours, 79% of the database time, and a notification for web, signer and gateway on every run). A pass now announces only the
  deployments it changed, and a deployment Action1 could not take yet waits the poll interval before it is offered again.
- Settings, Performance failed to load for every period longer than the minute rows ("permission denied for table
  WorkerWatermarks", found on the test VPS on 2026-10-20): web reads how far the hourly rollup got, but its database role had
  no access to that table. Web now reads it, and a test reads every period with the privileges of web.
- The notification listener of every component sent the database a ping after every notification, a round trip per
  notification in each of the four components (7 million in 6 hours on the test VPS, with the loop above). A notification
  proves the connection alive, so the listener now pings only after 30 seconds without one.
- fleeto-signer threw and caught an exception on every new database connection (`TypeInitializationException` in
  `Interop.NetSecurityNative`, 77 in 6 hours): Npgsql tries GSS encryption by default and loads a native Kerberos library the
  images do not have. Fleeto signs in to its database with a password, so connections no longer try GSS encryption.
- Check evaluation read the new results of an endpoint by id alone, so TimescaleDB searched every chunk of `CheckResults`,
  the compressed ones too (61 ms per read, 178 ms at the 95th percentile, on the test VPS). The read is now also bounded by
  time, an hour before the cursor last moved, which skips the old chunks and leaves out no result.
- Instance health measured every table every 5 minutes, which takes TimescaleDB about 250 ms for the hypertables. Table sizes
  decide no finding, so it now measures them once an hour.
- The scheduled rounds of a patch policy never appeared under Deployments on the Patches tab, while a deployment started by
  hand did (found on the test VPS on 2026-10-19: the automations were read every hour, but not one scheduled deployment was ever
  made). Action1 gave no usable `last_instance` for the automation. When it names none, the workers now find the round among the
  runs of the organization (`automations/instances`), by its schedule link or by its id, which Action1 builds from the id of the
  automation and the start time; `last_instance` is also read when it comes as an object. When Action1 says the automation ran
  but no run is found, the workers log what Action1 sent, without secrets.
- A table wider than its box could only be scrolled sideways with the scrollbar under its last row, so on a long list the user
  first had to scroll all the way down. While that scrollbar is out of view, a copy of it now stays at the bottom of the visible
  part of the table (the window, a dialog or the box the list scrolls in) and moves the table with it, on every table.

### Security

- The security monitor, step 1: web and the gateway count what they see of attacks per address and minute, in memory, and
  write it once a minute in one batch (`SecuritySignals`, kept 90 days): refused rate limits, failed sign-ins and two-factor
  setups, unknown or used setup links, public API calls with an invalid, unknown, revoked or expired API key (only a wrong
  secret was audited before), agent sessions with a revoked or an unknown certificate (one log line before, which did not tell
  them apart), connections to the agent port that never complete their TLS handshake (Kestrel logged those only at debug
  level), and refused blocked addresses. An IPv4 address counts on its own, an IPv6 address with its /64.
- Scanner detection in web and the gateway: a request for a path Fleeto never serves but scanners always try (`/.env`,
  `/.git/`, `/wp-login.php`, `*.php`, `/actuator`, and more), an exploit attempt in the path or query (path traversal, also
  double encoded, a null byte, Log4Shell, SQL or script injection) or the User-Agent of a scanning tool (sqlmap, Nikto, Nuclei,
  zgrab, and more) gets a bare 404 without the not-found page and counts as a signal (a path that only exists to leak secrets,
  such as `/.env` or `/.git/`, as a sensitive path), and so does any other 404 of an anonymous caller on a path without a route. A real route is never judged by its path, so no page of Fleeto can look like a probe.
- The security monitor, step 2: a workers loop judges the signals and the last hour of the audit log every minute and opens a
  security incident for: a brute force from one address (10 failed sign-ins or codes in 10 minutes), password spraying (5
  accounts from one address in 30 minutes), an attack on one account from 5 or more addresses (20 attempts in an hour), a locked
  admin account, a sign-in from a new network by an admin or a user with remote control, API key guessing (20 in 10 minutes),
  a scan of the agent port (30 refused connections in 10 minutes), a revoked agent certificate that still connects, a
  vulnerability scanner (one sensitive path such as `/.env`, one exploit attempt or scanning tool, or 3 other probes), directory
  enumeration (50 404s in 10 minutes), a rate-limit flood (200 refusals in 10 minutes), a change of who can do what (roles,
  users, API keys, sign-in, credentials, allowed addresses), a script on 100 or more endpoints at once, 10 remote sessions of one
  user in an hour, 5 signing refusals in 10 minutes, and, checked hourly, API keys without an expiry date, keys or admins unused
  for 90 days.
- Automatic blocking: an attack from one address blocks it for an hour, the next time within 30 days for a day, then a week,
  and a scanner at once for a day; never permanently and at most 10,000 at once. Never an allowed address, a private or
  reserved range, a network a user signed in from or a managed endpoint connected from in the last 30 days: those incidents say
  why the address was not blocked. Every automatic block is audited as the security monitor. A scanner, enumeration, flood or
  agent-port scan that Fleeto blocked by itself is shown in Settings, Security but not sent to the notification channels:
  every server on the internet is scanned all day; one that could not be blocked is sent.
- The blocklists are fetched by the workers only, every 6 hours, an hour after a failure, and at once when switched on or asked
  for, with a timeout, a 64 MB ceiling and a circuit breaker per list. A list is cleaned of private and reserved ranges (FireHOL
  level 1 lists 10.0.0.0/8 and 172.16.0.0/12, where the containers of the instance live) and of networks wider than /8 or /19,
  refused when it holds more than 500,000 networks or shrinks to less than half, and otherwise replaces the previous list in one
  transaction; a failed or refused fetch keeps the previous list. Web and the gateway read the entries again only when a list
  changed. An address on a list gets a 403 from web and is refused for enrollment, recovery and the relay; an agent that presents
  a valid certificate always connects, and an allowed address is never blocked by a list. CrowdSec is only ever called at
  `admin.api.crowdsec.net` with the id the admin entered. Fleeto shares nothing with CrowdSec: its community blocklist in
  exchange for signals is only for its own Security Engine with unmodified scenarios of its Hub (checked 2026-10-23), so a
  Console integration is the way in.
- `install.sh --fqdn <name> --unblock` lifts every blocked address of an instance, the way back in for an admin whose own
  address was blocked; audited as `install.sh`.
- The attempt that locks an account is audited as its own action, `user.locked_out`, once. The audit entry of a sign-in holds
  the User-Agent, at most 256 characters. The networks users sign in from (an IPv4 /24 or an IPv6 /64) are remembered with
  the first and last time and forgotten after 400 days without a sign-in.
- The agent and watchdog are built with Go 1.27.3 instead of 1.27.1: nine vulnerabilities of the standard library that their
  code reaches are fixed, among them GO-2026-6809 (crypto/x509 now distrusts compromised intermediate certificates) and
  HTTP/2, HTTP/1 and TLS flaws of net/http and crypto/tls (GO-2026-6603 to GO-2026-6617). `go.mod` asks for Go 1.27.3, CI
  runs govulncheck with the Go of `go.mod`, and a CI step fails when a Dockerfile builds the agent with another Go: CI used to
  check the newest 1.27 while the images built with the pinned 1.27.1.
- The images run .NET 10.0.13 (the security update of 2026-10-13) instead of the runtime of September: the SDK, ASP.NET Core
  and runtime base images are pinned to the 10.0.13 digests, and the Microsoft packages of 10.0.12 moved to 10.0.13.
