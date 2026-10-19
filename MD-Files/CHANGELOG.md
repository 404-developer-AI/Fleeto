# Changelog

All notable changes to Fleeto are documented here, newest first, following the
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) layout and semantic versioning.

This file holds the `Unreleased` section and the **two most recent released versions**.
When a third released version is added, the oldest entry moves to the top of
`CHANGELOG-ARCHIVE.md` in the same commit.

## [Unreleased]

Version 0.7.0, the performance monitor (`ROADMAP.md`, `PERFORMANCE.md`), step 1: the core, and step 2: Diagnose; and turning
automatic Windows Update off from a patch policy.

### Added

- Settings, Performance: where Fleeto itself is slow and why, for admins. It measures Fleeto, never the endpoints it manages.
  For a period of 15 minutes to 7 days it shows a score per category (hosting, backend code, database queries, database
  maintenance, background and realtime, agent channel), the top bottlenecks ranked by the extra waiting they caused, each with
  its evidence, likely cause, what to change and where to look in the code, a timeline of page interactions and requests, the
  busiest pages, routes, loops, agent messages, database sources and external hosts, the runtime of every component and the VPS.
  Network and frontend follow with step 3.
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
- "Copy output" above the terminal of a remote background session, on Windows and Linux (asked for by the developer on
  2026-10-19): copies everything the terminal shows as plain text, also what scrolled away (up to 10,000 lines), with lines the
  terminal wrapped joined again. It happens in the browser; the output never passes the server, as before.
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

### Changed

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

### Fixed

- The scheduled rounds of a patch policy never appeared under Deployments on the Patches tab, while a deployment started by
  hand did (found on the test VPS on 2026-10-19: the automations were read every hour, but not one scheduled deployment was ever
  made). Action1 gave no usable `last_instance` for the automation. When it names none, the workers now find the round among the
  runs of the organization (`automations/instances`), by its schedule link or by its id, which Action1 builds from the id of the
  automation and the start time; `last_instance` is also read when it comes as an object. When Action1 says the automation ran
  but no run is found, the workers log what Action1 sent, without secrets.

## [0.6.0] — 2026-10-18

Refactors, fixes and everything the developer wanted before the next round of testing, features included: policies, patch
policies and monitoring templates on three levels and per class, patch policies as Action1 automations with Action1 following
the clients and sites, storage analysis, instance health, user groups and roles with permissions, tasks that run library
scripts as steps, exit codes per script, uninstalling the agent of a deleted endpoint, and a comment with every alert action
and a history of maintenance. Started on 2026-09-24 as a release without new features and made a large release the same day;
tested and released as a whole. The pre-releases `0.6.0-alpha.1` to `0.6.0-alpha.26` were test builds on the first test VPS
with Windows and Linux endpoints; what they found is in Fixed. Like 0.4.0 and 0.5.0, no load test and no separate security
review: the release asked for neither.

### Added

- A comment with every alert action: acknowledging, putting on hold and resolving an alert ask why, and the comment is required
  (ending a hold takes an optional one). What was done, by whom, when and the comment are shown under the alert on the Alerts
  page and in the History tab of the endpoint. The audit log records the action with the length of the comment, never its text.
- Maintenance history: every period of maintenance mode on a client, site or endpoint is kept, also after it ends, and the
  History tab of an endpoint opens with a Maintenance section, above Alerts, with those of the endpoint, its site and its
  client: when it started and ended (and whether it ended before its planned end), who started it and why. Kept 13 months
  after a period ended; maintenance that runs when this version is installed is included from its start.

- Exit codes per script: Edit on a script in Settings, Scripts lists which exit codes mean success, info, warning or error (for
  example 0 success, 1 and 2 a warning, 3 an error, 4 info; ranges such as 5-8 work). Every job, task step and script check that
  runs the script reads them. A job with info or a warning succeeds and shows a blue Info or an amber Warning label with its exit
  code; a task goes on after such a step, and a run with a warning ends as "Succeeded with warnings" and opens an alert of
  severity warning (raised to the severity of the task when a later run fails); a script check is OK on info, a warning on a
  warning and critical on an error. A code the script does not list keeps what it meant before: for a job 0 succeeds and every
  other code fails, for a script check 0 is OK, 1 a warning and every other code critical. The exit codes are not part of a
  version and need no approval: they change how a result is read, never what runs. Jobs in the API have `exitStatus`.
- Tasks: Settings, Tasks. A task runs scripts from the library by itself on the clients, sites and endpoints it is linked to
  (the links add up), as steps one after the other: on each endpoint a step starts only when the step before it ended, so a long
  step never overlaps the next. A step can also restart the endpoint: Fleeto then waits until it is back with a newer boot time
  (at most the wait of the step) before the next step starts, which needs an agent of this release and the new permission
  Restart endpoints. A failed step stops the run there, unless the step may fail or its script counts its exit code as success,
  info or warning. A task starts on a schedule (once, every few days, weeks on chosen days or months on a day, optionally repeated during
  the day, between a start and an end date, in a time zone of your choice), when an alert of a chosen check opens on an
  endpoint, when an endpoint is switched to managed, or with Run now. Options: a random delay per endpoint, catching up a run an
  offline endpoint missed, skipping or waiting for an endpoint where the previous run still runs, and an alert on the endpoint
  when a run fails (naming the step), resolved by the next successful run. Every run shows per endpoint how far it got and every
  step with its state, exit code and output, and exports as CSV with a row per endpoint and step for collecting inventory. A task can also be linked to a monitoring template: it then runs on every endpoint
  that gets the template, wherever the template is linked; the page of a monitoring template lists, links and unlinks its tasks. The Tasks tab of an endpoint lists the tasks that apply to it, with
  where they are linked, their next run and the result of their last run, and the history of task runs on it; a run opens the
  output of each step. From the tab a task is linked to or unlinked from the endpoint, or created already linked to it. The jobs
  of a run have no user behind them, also for Run now (the run names the user); fleeto-signer signs each step only while the task
  still asks for it, and script approval of the policy applies as to every job. New permissions View tasks, Edit tasks and Run
  tasks now (technicians have them; read-only users can view).

- A check of a monitoring template can run on Windows only or Linux only: "Operating system" next to "Runs on" in the check,
  for check types that run on both, such as a service check for sshd on Linux servers. The template shows it under Runs on,
  for example "Linux servers". Existing checks keep running on both.

- Roles with permissions: Settings, Roles. A role is a set of permissions, such as remote control, remote background, run
  scripts, edit scripts, approve scripts, view or change each endpoint tab, switch the license tier, install or remove the
  Action1 agent, add or delete endpoints, create or delete clients and sites, tag clients, and each administration page.
  Admin, Technician and Read-only are the default roles: they cannot be changed, only copied into a custom role. A user may do
  what its roles give together; its groups still decide where. A permission a later update adds is off in every custom role
  until you give it. Pages, menus and buttons follow the permissions, and the server refuses what a role does not allow,
  fleeto-signer included. The update changes nothing for existing users.

- Groups of users: Settings, Groups. A group gives every client with exceptions, also the clients created later, or the
  clients, sites and endpoints it includes; a user who is not an admin sees what its groups give together, and nothing without
  a group. Settings, Users sets the groups of a user next to its roles and shows under Access what the user sees; Who has
  access on a client, site or endpoint lists who can reach it. A user that sees part of a client manages its endpoints, not
  the client or site as a whole, and changes a shared policy or template only with access to everything it is used on. The
  update puts every existing user in the group "All clients", so nobody gains or loses access; a user added to another group
  leaves it.

- Run a library script in remote background: "Run script" in the session window runs it in the terminal, as SYSTEM, root or the
  signed-in user, with its output live and its prompts answered there. Where the policy requires approval only approved versions
  are offered; every run is in the audit log by name, version and hash. Needs the watchdog of this version.
- Refresh on an endpoint: the round arrow in its header reloads the endpoint and, while it is online, asks the agent for a
  fresh inventory, at most once a minute. The page shows it when it arrives.
- Backups can be made outside Fleeto, for example by Veeam: Settings, Backups, "Made outside Fleeto" with a description of
  how. Fleeto then makes no backups of its own and the dashboard and instance health show the backups as healthy. The form
  warns that an image of the VPS holds every secret of the instance.
- A service check can leave out endpoints without the service: "No alert when the service does not exist". Without it, a
  service that does not exist is a problem like a stopped one, with its own alert title. Needs agents of this version.
- Instance health, for admins: a tile on the dashboard and a page in Settings show disk, memory and CPU of the VPS, the
  database, the workers, backups and notifications, with 7-day trends. A problem says why and what to do, and goes to the
  notification channels where "Also send instance health problems" is switched on. The page copies or downloads a
  diagnostics report to give to a person or an AI: sizes, counts and the costliest database statements, without secrets or
  personal data. install.sh switches on pg_stat_statements for it.

- A monitoring template says where it can be linked: to clients, sites, endpoints or more of them (all three for existing
  templates). Edit client, Edit site, the policies of an endpoint and client templates only offer it there, and a level it
  is still linked on cannot be switched off.
- Every check on the Checks tab of an endpoint shows where it comes from with an icon: a monitoring template of the client,
  of the site or of the endpoint, or a manual check. Hover over the icon for the explanation.

- A CPU or memory usage result at or above the lowest threshold of its check, and every Run now, lists the 10 processes using
  the most CPU or memory, with PID, user and memory. The check history marks those results with a dot; hovering over one
  shows the list below the chart. An alert names the top three processes (without users) in its detail and email.

- Action1 can follow your clients and sites. Switch on "Keep Action1 in step with clients and sites" in Settings,
  Integrations: a new client then gets its own Action1 organization, or the unmapped organization that already has its
  name, and is mapped to it. The organization of every mapped client is named after its client code and name, such as
  `[ACME] Acme Corporation`, also one that was mapped by hand. Every site of a mapped client becomes an endpoint group in Action1 with the endpoints of that
  site. Renaming a client or site renames the organization or group; deleting one deletes it. An endpoint enrolled again
  under another client is moved to the organization of that client, and shows its patch state there at once. Action1 deletes an
  organization only once it holds no endpoints, so until then the deletion waits under "Waiting for Action1" with the reason,
  and an admin can dismiss it there. The Action1 API credentials need permission to manage organizations and endpoints.
- Patch policies, in Settings, Patch policies: when and how Action1 installs updates. A patch policy runs weekly on
  chosen days or monthly on a day or a weekday such as the second Tuesday, at a time in the endpoint's own time zone or
  UTC; installs every missing update or only those matching sources, severities and types, leaving out names or
  vendors; installs only what is approved in Action1 or everything not declined, optionally some days after release;
  restarts or not, with a message and a delay; and keeps trying missed endpoints for a number of hours. Only options
  Action1 itself offers are there. Link a patch policy to a client, a site or one endpoint, and Fleeto keeps one
  automation per client and patch policy in Action1, aimed at exactly the endpoints it applies to, within a minute of
  every change. Endpoints without a patch policy get nothing from Fleeto. When Action1 also has automations Fleeto does not
  manage, editing the client says so. The Action1 API credentials need permission to view and manage automations.
- Policies, patch policies and monitoring templates can be linked to a client, a site and a single endpoint. For the
  policy and the patch policy the most specific one wins: the endpoint's over the site's, the site's over the client's.
  Monitoring templates add up. "Edit client" and "Edit site" hold the three choices, and so does "Policies" in the
  right-click menu of an endpoint; each choice shows what applies without it, such as "Inherit: Servers (from site)".
- Policies, patch policies and monitoring templates say whether they are for all endpoints, servers or workstations. One
  for servers never applies to a workstation, wherever it is linked, and the other way round. A client or site can hold a
  different policy and patch policy for servers and for workstations with "Different for servers and workstations", in
  its edit dialog and in client templates.
- Client templates set the policy, patch policy and monitoring templates of the client itself as well as of each site;
  a site follows the client unless it has its own. The sites fold open one at a time.
- The history Action1 keeps per endpoint of a deployment, its "Automation History", opens from the Patches tab of the
  endpoint: operation, time, status and details, newest first, refreshed while the deployment runs.
- Scheduled deployments: every run of a patch policy in Action1 appears in the deployments of the Patches tab, with the
  result per endpoint and the history Action1 keeps, so you see whether the Monday round with its restart happened. Fleeto
  finds a run within minutes after it was planned and at the latest within the hour, and follows it as long as the patch
  policy lets Action1 retry endpoints. The public API reports them with scope `patch_policy` and `patchPolicyId`.
- The Patches tab shows the patch schedule of the endpoint: its patch policy, the schedule in words, whether Action1
  restarts the endpoint, and when Action1 planned the next deployment. When no deployment is planned it says why: no patch
  policy, a policy that is switched off, an automation Action1 refused, or Fleeto still writing it.
- A Sync button on the Patches tab reads the endpoint from Action1 at once: its patch state, missing updates, the next
  deployment and a scheduled deployment that just ran. Its icon turns until the workers are done; a sync that fails says
  why. One sync per endpoint a minute.
- Remove the Action1 agent from the Patches tab. When Action1 reports the endpoint, Fleeto asks Action1 to remove it from
  its organization, which uninstalls the agent and frees its place in the subscription; otherwise a signed job uninstalls
  the agent. The Action1 API credentials need permission to manage endpoints. Both are in the audit log.
- A "Scheduled patching" check for monitoring templates: the days since a scheduled deployment of the endpoint's patch
  policy last succeeded on it, so a round that fails or never runs raises an alert. Until one succeeds, it counts from the
  day the policy started on the endpoint; an endpoint without a patch policy gets no result.
- A patch policy can install the Action1 agent by itself: switch on "Install the Action1 agent automatically" and Fleeto
  installs the agent of the client's Action1 organization on every managed Windows endpoint the policy applies to that has
  none, as soon as the endpoint is online. The Patches tab shows the installation with its reason when it fails, such as a
  download the endpoint could not make or another installation running at the same time, and turns green once Action1
  reports the endpoint. A failed installation is tried again after 1, 6 and 24 hours; then Fleeto stops until you install
  the agent again by hand. An agent you removed through Fleeto is not installed again by itself. The list of patch
  policies counts installed, pending and failed endpoints per policy, and the installation is in the audit log with the
  patch policy as its initiator.
- An "Action1 agent" check for monitoring templates: not OK when Action1 does not report the endpoint because its agent is
  missing or an installation failed, with the reason, so it can raise an alert.
- Deleting an endpoint or a client can uninstall the Fleeto agent too: "Also uninstall the Fleeto agent" is checked by
  default. The endpoint disappears from every list at once; when its agent or watchdog connects again within 30 days,
  it receives an uninstall command signed by the instance and removes both services, their keys and files, also on an
  agent-only endpoint. Settings, Agent uninstalls (admins) lists the endpoints Fleeto waits for and those that removed
  themselves, with "Stop waiting". An agent older than this version does not know the command and stays as before.
- Deleting an endpoint, or switching it to agent-only, can remove its Action1 agent too: "Also remove the Action1 agent"
  is checked by default when the endpoint has one. Fleeto asks Action1 to remove the endpoint, which uninstalls the agent
  the next time the endpoint is online and frees its place in the Action1 subscription. A removal Action1 refuses waits
  in Settings, Integrations under "Waiting for Action1" with the reason. An endpoint that stays in Fleeto as agent-only does
  not get the agent back by itself when it becomes managed again.
- Tags on clients, in the way of Proxmox. Type a tag when you create a client, or later under Client settings, Edit
  tags, or pick one that exists; a new tag gets a color from its name. Tags show below the client name in the clients panel (three, and
  the number of the others) and in the client header; the tag button next to the search filters on one or more tags, and
  the search finds tag names. Admins rename,
  recolor and delete tags in Settings, Tags, from a fixed palette of eleven colors. The public API returns the tags of a
  client and filters clients on a tag with `tag`.
- Alert notifications during a flood are combined. Per email address, and per Slack or Teams channel, the first five
  alert notifications within ten minutes go out on their own; after that, one email or message every ten minutes lists
  the rest, grouped by client and site, resolves included. Generic webhooks still get every notification on its own, and
  emails that are not about alerts are never combined.
- Backups larger than 128 MB go to S3 as a multipart upload, so a backup is no longer limited to 5 GB. The write-only
  credentials stay enough. Add a lifecycle rule that removes incomplete multipart uploads to the bucket: Settings, Backups
  now asks for it.
- Storage analysis: a managed endpoint scans its drives at the lowest priority (daily by default, set per policy) and on "Scan
  now", reading the NTFS master file table where it can and walking the folders elsewhere. The new Storage tab shows each
  drive with its largest folders as a tree (size on disk, share of the drive, change since the previous scan, 7 and 30
  days), its largest files, and the history of a folder. A Disk free check that turns warning asks for a scan, so the
  folder that fills the disk is known when somebody looks. Scans are kept daily for 30 days, then weekly for 13 months.
- A "Folder growth" check for monitoring templates: it alerts when a drive grew more than a number of GB within a period
  (7 days by default), and its detail names the folder that grew most. Fleeto evaluates it after every storage scan.
- A "Missing updates" check for monitoring templates: it alerts when an update Action1 reports as missing was released
  more than a number of days ago, with a warning and a critical threshold in days and optionally only updates of a minimum
  security severity. Updates declined in Action1 do not count. Fleeto evaluates it after every patch sync, so it has no
  interval and cannot be run now; its alerts are acknowledged and held like those of every other check.

### Changed

- Maintenance mode needs a reason: starting or changing maintenance on a client, site or endpoint refuses an empty one.
- The endpoint list of the Clients workspace fills the page down to its bottom edge and scrolls inside while no endpoint is open
  below it, instead of keeping the height of the list with an endpoint open (which could be only a few rows).
- The History tab of an endpoint: Alerts, Connections and Audit each grow with their rows up to a maximum height and then
  scroll with their headers in view, and each has a search that looks through everything the endpoint has had, not only the
  newest rows shown (the newest 200, where it was 50). Alerts match their title, detail and resolve reason; connections their event and detail; audit entries who,
  the action and the details.
- The exit codes a task step counted as success (alpha.23) moved to the script: the update lists them as success on the script
  of the step, and the step editor no longer has the field.
- Approving a script needs the permission "Approve scripts" instead of the admin role; the approver still has to be somebody
  other than the author, with a fresh code. Switching off "Require approval of scripts" in a policy needs the same permission.
- Settings shows only the pages the roles of the user allow, and the settings button opens the first of them.
- The tabs of an endpoint all fit in view: each is as wide as its label instead of at least 160 pixels, without capitals, and
  where the detail is narrow the icons give way to the labels instead of tabs hiding behind scroll arrows.
- "Rename client" is "Edit client", and "Policy and monitoring" of a site is part of "Edit site".
- The clients panel shows the full name of a client and site, with its tags and maintenance on a line below it. The icon of
  a client and site turns amber when one of its endpoints has an open warning and red when one has a critical alert;
  endpoints in maintenance do not count. In the endpoint list the icon of an endpoint shows the same: green in maintenance
  (whatever its alerts), red with a critical alert, amber with a warning, gray otherwise, with the details on hover; the
  Status column shows only online or offline. The maintenance icon in the badge of a client or site ("all", "1/2") is
  the same green.
- The badge next to a client or site in the clients panel only counts open alerts, with a bell icon, amber or red for the
  worst of them, and leaves out alerts of endpoints in maintenance. A site without alerts no longer shows its number of
  endpoints in the same place, which read as a number of alerts; the tabs of the selection show that number.
- The missing updates of an endpoint show the columns of Action1: name, installed version, latest version, release date,
  status (the approval in Action1: New, Approved or Declined), update type, vulnerabilities (the CVEs, listed on hover)
  and security severity. The Restart column is gone: Action1 says "possibly" for every update. The public API reports
  `installedVersion`, `releaseDate`, `updateType`, `approvalStatus` and `cves` per missing update, and no longer
  `rebootNeeded`.
- The public API reports `client_template` as the source of a check from a monitoring template linked to the client.
- The deployments on the Patches tab of an endpoint are listed above the deploy buttons, so a running deployment is
  seen before another one starts. More than five scroll in a box that can be dragged taller; it is back to five rows on
  the next visit.
- Remote control is offered only on endpoints with a desktop. The agent reports whether an endpoint has one; a Linux
  server without a display manager, graphical session or X server, and Windows Server Core and Nano Server, offer only
  remote background. Agents older than this version keep remote control until they update. The public API shows it as
  `desktop` on the inventory.
- Remote control and remote background open from the right-click menu of the endpoint list only; the two buttons on
  the endpoint detail are gone.
- Every date and time is chosen with the same fields: the browser's own calendar with a time (hold, maintenance), a date
  (audit log, end dates of secrets) and a time of day (patch policy, maintenance windows). They fit in every dialog; the
  clock of the time picker was larger than the dialog of a patch policy.
- An agent reports a service that does not exist as a value of its own instead of an error, so the check no longer reads
  "could not run" for it.
- An alert about two endpoints using the same agent identity resolves on its own once it has been open for 24 hours
  without a new second connection.
- Email through Microsoft Graph gets its token the same way as the tests in Settings, so a refusal from Microsoft reads
  the same in both places.
- The Linux install command works in a root shell without sudo, such as on Proxmox VE: it uses sudo only where it is
  installed, and tells a user who is not root to run it as root or with sudo.
- Saving Settings, Backups makes the workers measure instance health at once, so a changed backup setting no longer shows
  the old problem for up to 5 minutes.
- Settings, Agent uninstalls shows for a removed agent the day it leaves the list (a week after the removal) instead of
  the 30 days Fleeto would have waited.

### Fixed

- A search with `%`, `_` or `\` in it found nothing, for example `WS_01` in the endpoint list: the database read them as
  wildcards and escape characters despite Fleeto escaping them. This affected the endpoint list, the clients panel, the
  audit log, the access picker of groups and the public API (`search` on clients and endpoints).
- The check history drew loose dots and short strokes instead of a line for a check that runs less often than the chart's
  buckets are wide (for example every 5 minutes in the last hour, or every hour in the last day). The line now goes across
  empty buckets up to about one interval of the check; it still breaks where results are missing or the check could not run.
- Remote background on an endpoint switched to managed shortly after it enrolled said the watchdog was not connected until
  the watchdog restarted or up to 5 minutes passed: the gateway updated the tier of the agent's connection, not of the
  watchdog's. Both follow the switch at once now, also when only the watchdog is connected.
- A script that had run as a job could not be deleted: the database refused to clear the script on its jobs. The jobs now keep
  their snapshot (name, version and hash) and lose only the link to the deleted script, as the API already described.
- The processes with the most memory listed with a CPU or memory usage result could be out of order when one of them grew
  while the agent read the list: the list now shows the memory it was ranked by. Needs agents of this version.
- A script larger than 32 KB could not be saved: the connection of the browser refused a message of that size and closed.
  It now takes the largest script the library allows (256 KiB).
- An endpoint whose Action1 agent was removed could not get it back: Action1 leaves its id in the registry when it is
  uninstalled, so the endpoint still looked like it had the agent, showed only "Remove Action1 agent" and its sync failed with
  "Action1 answered 400 (The endpoint was not found)". The agent now reports the id only while the Action1 agent is installed,
  the install and remove jobs clear a left-over id, and that answer of Action1 counts as an endpoint it does not hold.
- An install that finds an agent from before the rename to Fleeto, of another instance, names the command that removes it
  with its full path (`& 'C:\Program Files\Fleetify\Agent\fleetify-agent.exe' uninstall`); the bare `fleetify-agent
  uninstall` it named was not on the PATH and was never found.
- The security severity of a missing update is read again: Action1 reports it with the version it would install, and
  Fleeto looked for it on the update, so every update showed as unspecified.
- A deployment with automatic restart waited 30 hours instead of 30 minutes before Action1 restarted the endpoint: Action1
  reads the time in minutes, and Fleeto sent seconds.
- "Deploy all missing updates" installs the updates again. Action1 installed only updates approved in its own console
  and answered "No updates are applicable"; Fleeto now asks for every missing update, as it already did for chosen ones.
- The check history dialog no longer keeps a handler after it closes, and the remote control and remote background
  windows always clean up after themselves.
- The gateway refuses a certificate from an unknown authority also when it runs on Windows, instead of failing on it.
- The build has no warnings left.
- Installing the Action1 agent is no longer offered, and the job stops without changes, when an Action1 agent is already
  on the endpoint. The Patches tab says when an agent runs there that Action1 does not report for this client.
- The public API listed no jobs at all once an endpoint had a job that installs the Action1 agent: it failed on the type
  of that job. It now reports it as `action1_agent_install`.
- Fleeto reads the Action1 agent installer link of an organization from Action1 again: it asked without the install type
  (`/endpoints/agent-installation/{orgId}/WindowsMSI`), so Action1 never gave one and the link had to be pasted by hand.
  An organization without a link is asked again every hour instead of every day, on its own rather than only with the
  organizations every four hours, and at once when an admin tests the connection. A refusal or an answer without a link
  is logged by the workers. It needs the permission to manage endpoints on the Action1 API credentials.
- Opening an endpoint on the Workstations or Mixed tab no longer jumps back to Servers. Since pages check their permission
  in the database, every navigation briefly replaced the page while it was checked, which reset its tab, search and
  filters. A navigation within the same page now keeps it while it is checked again; a refusal still replaces it.

### Security

- The steps of a task: fleeto-signer signs a step only after the job of the step before it ended and passed (or may fail),
  judged from the jobs the gateway completed, never a step twice in a run (also a unique index); for a run started by hand it
  checks the user's permissions and access again for every step. A restart is a job type of its own that only the workers write,
  only for a restart step of an enabled task, with nothing in it but the wait the signer takes from the step; the agent runs a
  fixed command and reports success only after the endpoint started again with a newer boot time.

- What a user reaches is enforced in the data layer, by fleeto-signer for every job and remote session token, and by the gateway:
  when a user loses an endpoint, its remote session connections to it end and its open pages reload. The license tile on the
  dashboard is for admins only.

- System.Security.Cryptography.Pkcs pinned to 10.0.13 in the workers against CVE-2026-84008 and CVE-2026-70162
  (.NET denial of service); MailKit through MimeKit still asks for 10.0.0.
- Backups made outside Fleeto are an accepted risk, documented in ARCHITECTURE §5: an image of the VPS holds the root key
  and the signer key next to the database, unlike a Fleeto backup, which is encrypted to an offline key.
- The uninstall command of a deleted endpoint is signed by fleeto-signer only for an endpoint that no longer exists and was
  deleted by an admin or technician, names that one endpoint and expires after 30 days; the agent and watchdog accept it
  only with the instance signature they pinned at enrollment. What web and the signer wrote on the request cannot change
  afterwards in the database.
- The workers may now write one kind of job: the automatic installation of the Action1 agent, which has no user behind it.
  The database lets them write no other job, and lets nobody else write a job without a user; fleeto-signer signs it only
  while the patch policy of the endpoint asks for it and the agent was not removed through Fleeto.
- The keys that protect sign-in cookies are stored encrypted with the root key of the instance. Keys stored before this
  version are withdrawn when Fleeto starts, so everybody signs in once more after the update.
- Request paths and the error Microsoft returns at sign-in are written to the log with line breaks and control characters
  made visible, so a crafted request cannot forge a log entry. The gateway logs agent downloads with the file name and
  version of the release it serves instead of the requested values (CodeQL code scanning).

## [0.5.0] — 2026-09-24

Sign-in with Microsoft Entra ID for the people of the organization that owns the instance, with users chosen from its own
tenant, a test and a set-up guide for the app registrations, and a clear choice of who asks the second factor. Planned with
the developer on 2026-09-23 and built in three steps between 2026-09-23 and 2026-09-24. The pre-releases `0.5.0-alpha.1` to
`0.5.0-alpha.6` were test builds on the first test VPS against a real Microsoft tenant; what they found was fixed before this
release: the "Sign in with Microsoft" button was blocked by the Content Security Policy, an admin without a password was held
back by the break-glass rule, and a session with a second factor from Microsoft could open no page. They also showed that
Microsoft puts no `amr` claim in the token, which led to the choice described below.

### Added

- Signing in with Microsoft Entra ID, for the people of your own organization. An admin configures the tenant, the
  app registration and its client secret in Settings, Sign-in, which also names the redirect URI to register, and links a
  Fleeto user to an Entra ID account in Settings, Users. The sign-in page offers "Sign in with Microsoft" only once that is
  configured and switched on. A token from another Microsoft tenant is refused, and so is a guest of your tenant: the people
  of the clients you manage sign in with a local account. Fleeto creates no users of its own from a sign-in, and matches on
  the object id of the account rather than on an email address. Configuring the sign-in, linking and unlinking, and the way
  every sign-in came in are in the audit log. Fleeto warns before the client secret expires; after it expires local accounts
  keep working.
- You decide who asks for the second factor of people who sign in with Microsoft. By default Fleeto asks its own
  authenticator code after a sign-in with Entra ID. When your tenant requires multi-factor authentication, switch on
  "Microsoft handles the second factor of linked users" in Settings, Sign-in, and Fleeto asks no code on top: your tenant is
  then responsible for it. Switching it off ends the sessions of linked users. The test on that page tells you whether your
  tenant has Security Defaults or Conditional Access on, and every sign-in with Entra ID records in the audit log how
  Microsoft says the person signed in. Local accounts always keep the Fleeto code.
- A linked user has no Fleeto password at all — linking removes it, and the password form answers a linked account
  exactly as it answers a wrong password, so it tells nobody which accounts sign in with Microsoft. One admin always keeps a
  password as the way in when Microsoft is unavailable: the last such admin cannot be linked, demoted or deleted, with a
  message that says why. Settings, Users shows "No password" for a user that has none.
- Choose people from your Microsoft tenant. With the User.Read.All application permission on the app registration of
  the sign-in, "Add from Microsoft" in Settings, Users adds a user from an account of your tenant with the roles you give
  it, and linking an existing user picks the account the same way. Only members with an enabled account are offered, never
  guests. Without the permission, you link a user by the object ID of its account, as before.
- "Test settings" on Settings, Sign-in and "Test Microsoft Graph settings" on Settings, Email check the saved app
  registration: that Fleeto can sign in to the tenant with it, which permissions it has, and which it holds beyond what
  Fleeto needs. Both pages have a set-up guide with every step and permission.
- An admin can set a password for a user in Settings, Users. Needed after removing an Entra ID link, and it is what
  the sign-in page has always pointed at for somebody who lost their password. Their open sessions end, and two-factor
  authentication is untouched: a password alone is never enough.

### Changed

- Where Fleeto says it cannot install the Action1 agent on a Linux endpoint, it now names what to do instead (install it
  from the Action1 console) rather than promising a Fleeto version. Patch management on Linux and the integrations other
  than Action1 moved to "Not yet scheduled" on 2026-09-23; 0.5.0 is sign-in with Microsoft Entra ID and 0.6.0 is a release
  of refactors and fixes.
