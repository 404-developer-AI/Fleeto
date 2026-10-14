# Fleeto — Roadmap

> Planned versions and what each one delivers. Version rules are in `CLAUDE.md`
> (Versioning and changelog). Scope per version is a target, not a promise: a version
> ships when its items are done and tested, and items may move between versions. Dates
> are added only once a version is in progress.

## Current

Released versions, from 0.0.0 to 0.5.0, are described in `CHANGELOG.md` and `CHANGELOG-ARCHIVE.md`; this file keeps only
what is not released yet. The latest release is **0.5.0** (2026-09-24, `v0.5.0`): sign-in with Microsoft Entra ID.

**0.6.0** — in progress: refactors, fixes and everything the developer wants before the next round of testing, features
included (decided 2026-09-24), tested and released as a whole. Pre-releases `v0.6.0-alpha.N` on the first test VPS.

**Platforms**: Windows and Linux. macOS is not supported for now; it may come later when there is demand (decided
2026-09-15, see Later).

**Deployment**: Steaan runs every instance (SaaS, decided 2026-09-15); releases are GitHub Releases of the public
repository, with the images as private packages on ghcr.io (public since 2026-09-21).

Markers: [done] built and tested, [open] still to do.

## 0.6.0 — Refactors, fixes, clean-up and what the developer wants before testing

Started as a release without new features: what the versions before it left behind. On 2026-09-24 the developer made it a
large release instead: everything wanted before the next round of testing goes into 0.6.0, features included, and it is
tested and released as a whole. An item is written down here the moment it is found or asked for, with what it costs and
why it matters. Logs, search and retention were 0.6.0 until 2026-09-23 and are now under "Not yet scheduled".

- [done] Build warnings: `RemoteControl.razor` hides `Time` of `FleetoPageBase` (CS0108), a duplicate `using` in
  `GraphEmailTests` (CS0105), and three `SqlQueryRaw` calls that EF flags (EF1003, in `EndpointHealthService`,
  `CheckCatalogTests` and `EndpointCheckRuleTests`) — check each one and either use `SqlQuery` or suppress it with a
  reason. Done: the page uses the inherited `Time`, and the three statements are named fields built from constant SQL,
  like every other statement (fixed fragments, never input, so no suppression was needed). The build has no warnings.
- [done] Two token requests for Microsoft (found 2026-09-24): `GraphEmailSession` (email delivery) and `MicrosoftGraphClient`
  (Settings) each build the client credentials request and translate the AADSTS codes. Let email delivery use
  `MicrosoftGraphClient.GetTokenAsync`, so a new refusal code is handled in one place. Small, but touches email delivery,
  so it waits for a release without features. Done: `GraphEmailSession` asks `MicrosoftGraphClient` for its token,
  and `GraphMail.Credential` is the one mapping from the email settings to the credential, used by delivery and the test.
- [done] A pass over the pages that inherit `FleetoPageBase`: 0.4.0 had one that replaced the inherited clean-up instead of
  running its own next to it, so look for the same shape elsewhere. Done over all 28: every override calls the base and
  cleans up what it subscribed. Two things fixed: `CheckHistoryDialog` called `base.OnInitialized()` a second time from
  `OnInitializedAsync`, which subscribed it twice to the time zone and unsubscribed it once (the subscription in the base is
  now idempotent as well); and the two remote pages, which Blazor disposes only through `DisposeAsync`, now run the base
  clean-up in a `finally`, so a browser error during disposal can no longer skip it.
- [done] Whatever the open items of 0.1.0 and its known limitations still held that is not hardening work (0.7.0).
  Gone through on 2026-09-24: the open items are either done or already part of 0.7.0, and the four known limitations that
  still hold are the next four items, in this order (agreed with the developer).
- [done] **The key ring of web encrypted at rest.** The ASP.NET Core data protection keys (they protect the authentication
  cookies and antiforgery tokens) are stored unencrypted on the volume of web, so whoever reads that volume can forge a
  session. Encrypt them with the root key through the same envelope encryption as every other secret. Done: every key is
  sealed with a data key of its own purpose (`keyring`, created by `migrate`), and a key stored unencrypted is revoked and
  deleted when web starts, so nothing it protected is accepted any more. Everybody signs in again once after the update.
  Found while testing it: on Windows, building a chain for a certificate whose issuer is not trusted throws instead of
  answering false; the gateway now treats that as a chain that is not trusted (fail closed), as Linux already did.
- [done] **Duplicate identity alerts resolve on their own.** The alert for a certificate connecting twice at once (a cloned
  VM) stays open until somebody resolves it. It resolves on its own after 24 hours without a new duplicate connection
  (decided 2026-09-24). Done: the endpoint events worker resolves an alert that has been open for 24 hours when no duplicate
  connection came in during them, with its reason and the usual resolve notifications.
- [done] **Backups over 5 GB to S3.** Uploads use one `PutObject`, which S3 caps at 5 GB per file. Use a multipart upload
  above a threshold: it needs only `s3:PutObject`, so the write-only credentials stay enough. Done: above 128 MB in parts of
  64 MB or more (at most 10,000), each part with its own attempts and time limit. A failed upload is aborted when the
  credentials allow it (`s3:AbortMultipartUpload` removes only unfinished uploads, never a backup); otherwise its parts stay
  until a lifecycle rule for incomplete uploads removes them, which the backup settings now ask for.
- [done] **Email during a mass outage.** When many endpoints go offline at once, every recipient gets an email per alert.
  Throttle or combine them into a digest (moved into 0.6.0 by the developer on 2026-09-24). Decided with the developer on
  2026-09-24: per email address, the first 5 alert emails within 10 minutes go out on their own, the rest as one digest per
  10 minutes for as long as it lasts, grouped by client and site; resolves are combined the same way; Slack and Teams
  channels get the same digest per channel, and generic webhooks keep one message per alert. Done as described in
  `ARCHITECTURE.md` §4 (Notifications during a flood).
- [done] **Tags on clients** (asked for by the developer on 2026-09-25). Mark clients with colored tags, as Proxmox does
  with VMs, visible in the clients panel. Decided with the developer on 2026-09-25: typed freely on a client and created
  on first use, with a color from its name that an admin can change in Settings; a fixed palette rather than any color,
  so every tag stays readable; the clients panel filters on tags, and the public API returns them and filters on one.
  Done: tables `Tags` (instance-wide name and color) and `ClientTags` (client-owned), at most 10 tags per client, the
  filter keeps clients that carry every chosen tag, and a user limited to clients only sees the tags of those clients.
  Renaming, recoloring and deleting are for admins who see every client. Tags on sites and endpoints are not planned.
  Tags can also be given when a client is created (asked for by the developer the same day), in the same transaction.
- [done] **Remote control only where there is a desktop** (asked for by the developer on 2026-09-25). A Linux server without
  a desktop offered remote control, which could only fail. Decided with the developer on 2026-09-25: the agent reports
  whether the endpoint has a graphical desktop; without one only remote background is offered, on Linux and also on
  Windows Server Core and Nano Server. The two remote buttons on the endpoint detail are removed: both sessions open from
  the right-click menu of the endpoint list only. Done: `Inventory.desktop` from the agent, `InventorySnapshots.Desktop`,
  the rule in `RemoteSessionRules.SupportsRemoteControl`, `desktop` on Inventory in the public API. An agent older than 0.6.0
  reports nothing and keeps remote control until it updates.
- [done] **"Deploy all missing updates" installed nothing** (found testing 0.6.0 on 2026-09-25): Action1 answered "No
  updates are applicable" for an endpoint missing seven, while choosing the same seven worked. Cause: Action1 defaults
  `require_update_approval` to "yes" for `scope: "All"` and then installs only updates approved in its console. Fixed by
  sending "no", so both kinds of deployment install what Fleeto showed.
- [done] **Deployment history from Action1** (asked for by the developer on 2026-09-25): the "Automation History" Action1
  keeps per endpoint of a deployment, shown in Fleeto. Done: the workers read it into `PatchDeploymentSteps` when the
  state of an endpoint changes and every 5 minutes while it runs; the Patches tab opens it per deployment. The deployments
  are listed above the deploy buttons, five rows and then a scrolling box, so a running one is seen before another starts.
- [done] **Action1 follows the clients and sites of Fleeto** (asked for by the developer on 2026-09-25). Decided with the
  developer on 2026-09-25: a switch in Settings, Integrations; a new client gets an Action1 organization (or the unmapped one
  with its name), every site of a mapped client an endpoint group with the endpoints of that site, and renaming or deleting a
  client or site does the same in Action1. Done: `Integrations.FollowClients`, `IntegrationMappings.SyncedName`,
  `IntegrationSiteGroups` and the queue `IntegrationOperations` (web writes it in the same transaction as the change, the
  workers call Action1 and retry; Settings shows what waits and lets an admin dismiss it). Action1 removes an organization
  only once it holds no endpoints, so that deletion waits and says why. An endpoint enrolled again under another client is
  moved to that client's organization (asked for by the developer the same day): the patch sync sees which organization
  reports its Action1 id, the Fleeto endpoint that reported that id last decides where it belongs, and the workers move it
  with `POST /endpoints/managed/{orgId}/{endpointId}/move`. Only between organizations that are mapped. The organization
  carries the client code before the client name, `[ACME] Acme Corporation` (asked for by the developer the same day), also
  one that was mapped by hand, so every organization is recognisable by its code.
- [done] **Patch policies, and policies on every level** (asked for by the developer on 2026-09-25). Manage the automations of
  Action1 from Fleeto, with no option Action1 cannot do, and link policies, patch policies and monitoring templates to a
  client, a site and an endpoint, endpoint over site over client, with defaults in client templates. Decided with the
  developer the same day: monitoring templates add up over the levels; without a patch policy Fleeto schedules nothing;
  Fleeto manages only the automations it made and shows the others as a warning; an automation aims at endpoints, not site
  groups; "Policy and monitoring" of a site became part of Edit site. Done: `PatchPolicies` (Settings, Patch policies), the
  links `ClientPolicies`, `EndpointPolicies`, `ClientMonitoringTemplates`, `ClientPatchPolicies`, `SitePatchPolicies`,
  `EndpointPatchPolicies` and the links of the client itself in client templates; the rule of the effective policy in one
  place (`EffectivePolicyRules`, C#, SQL and EF Core kept equal by a test) instead of sixteen copies of "site, else default";
  maintenance windows per endpoint instead of per site; `PatchAutomationService` in the workers with
  `IntegrationAutomations`, and the automations Fleeto does not manage on `IntegrationMappings.OtherAutomationsJson`; Edit
  client (was Rename client), Edit site and Policies on an endpoint, all with one `LinkEditor`. Found while building: a
  deployment with automatic restart sent the restart delay in seconds where Action1 reads minutes (fixed).
- [done] **Servers and workstations** (asked for by the developer on 2026-09-26). Mark a policy, patch policy and monitoring
  template as for all endpoints, servers or workstations. Decided with the developer the same day: the mark is on the policy
  itself and holds wherever it is linked, and a client or site can hold one per class. Done: `AppliesTo` on `Policies`,
  `PatchPolicies` and `MonitoringTemplates`, the class slot in the keys of the client and site links, the per-class columns
  of client templates, the rule per class in `EffectivePolicyRules` (C#, SQL and EF Core, kept equal by a test), and
  "Different for servers and workstations" in the `LinkEditor`.
- [done] **Missing updates as in Action1** (asked for by the developer on 2026-09-27). The list shows name, installed and
  latest version, release date, status (approval in Action1, chosen by the developer), update type, vulnerabilities and
  security severity; the Restart column went, because Action1 answers "possibly" for every update. Found on the way: the
  severity sits in `versions[0]` of Action1's answer, so Fleeto read every update as unspecified (fixed).
- [done] **Alert on old missing updates** (asked for by the developer on 2026-09-27). Decided with the developer the same day:
  a check in the monitoring template rather than an option of the patch policy; "released" is the vendor's release date
  (Action1 has no approval date); a warning and a critical threshold in days; optionally a minimum severity, declined
  updates never count; one alert per endpoint and check; an endpoint without patch state gets no result. Done: check type
  `MissingUpdates` evaluated by the workers after every patch sync (`MissingUpdateChecks`, rule `MissingUpdateAge`), left
  out of the agent configuration, no interval and no run now, and INSERT on `CheckResults` for the workers.
- [done] **Alert badge in the clients panel** (asked for by the developer on 2026-09-28): the badge next to a client or site
  only counts open alerts outside maintenance, with a bell and the color of the worst, and a site no longer shows its
  number of endpoints in the same place, which read as a number of alerts.
- [done] **Storage analysis** (asked for by the developer on 2026-09-28: "like TreeSize, to follow which folder grows").
  Decided with the developer the same day: servers and workstations, folders and files, a Storage tab and a check, and
  the master file table on NTFS from the start. Done: `StorageScanReport`, `StorageScanRequest` and `StorageScanAck` in the
  protocol and the interval in the signed configuration; `agent/internal/storage` (MFT reader, walk, background priority,
  spool); `StorageScans` and `StorageScanRequests` with their grants and retention; the gateway stores and acknowledges
  after the commit and delivers requests; `StorageScanService` in the workers (folder growth, scans on low disk space);
  the Storage tab, the folder history, the interval on the policy; check type `FolderGrowth` evaluated by the workers.
- [done] **Process list of CPU and memory** (asked for by the developer on 2026-09-28). Decided the same day: a list only when
  the value reaches the lowest threshold of the check, and on Run now; with the user of each process; the top three,
  without users, in the alert. Done: `CheckResult.processes` and the server-set parameter `process_list_at` in the
  protocol, the agent measures the processes over the sample window, `CheckResults.ProcessesJson` with a partial index, the
  dots and the list under the chart of the check history.
- [done] **Where a monitoring template can be linked** (asked for by the developer on 2026-09-28): clients, sites, endpoints
  or more of them, refused elsewhere, also in a client template, and a level still in use cannot be switched off. The
  Checks tab shows where each check comes from with an icon and a tooltip.
- [done] **Instance health** (asked for by the developer on 2026-09-28: "a health tile for the VPS, with why and what to do,
  and a log to give to an AI"). Decided the same day: admins only; problems go to the notification channels an admin
  chooses; the diagnostics can be copied and downloaded; pg_stat_statements switched on. Done: `InstanceHealthService` in
  the workers (samples every 5 minutes from /proc and the database, rules in `InstanceHealthRules`, issues that open,
  escalate and resolve), `InstanceHealthSamples` and `InstanceHealthIssues`, the channel option, the dashboard tile, the
  page in Settings and the diagnostics report, and install.sh creating the extension.
- [done] **Patching from the Patches tab** (asked for by the developer on 2026-10-13: sync an endpoint, install and remove the
  Action1 agent, see whether the scheduled round of a patch policy happened and when the next one is). Decided with the
  developer the same day: the agent is removed through the Action1 API, with a signed job when Action1 does not report it;
  Sync reads what Action1 knows now (Action1 cannot make an endpoint scan for missing updates); a round that fails or never
  runs is a check in a monitoring template. Done: `PatchRequests` with `PatchRequestService` in the workers (sync of one
  endpoint, removal through `DELETE /endpoints/managed`), `next_run` and `last_instance` of every automation of Fleeto
  read with the automations (`ScheduledDeployments`), a run becomes a deployment of scope `PatchPolicy` followed for the
  retry window of its policy, `EndpointPatchState.AutomationId`/`ScheduledSince`, check type `ScheduledPatching`
  (`ScheduledPatchingChecks`), job type `Action1AgentRemoval` composed by the signer, and the install job that stops when an
  Action1 agent is there. To verify against a real tenant in the next test build: the time zone Action1 means in `next_run`
  of an automation in endpoint time, and the status words of a scheduled run per endpoint.
- [done] **Install the Action1 agent automatically** (asked for by the developer on 2026-10-13: a switch on the patch policy,
  with an error and its reason when it fails and green when it worked). Decided with the developer the same day: a system
  initiator rather than the user who switched it on, retries after 1, 6 and 24 hours, no reinstall after a removal through
  Fleeto, and a check for monitoring templates. Done: `PatchPolicy.AutoInstallAgent`, `Action1AgentInstallService` and
  `Action1AgentInstallStates` in the workers, the signer rule for a job without a user, `TR_Jobs_Origin` and the workers'
  case of `TR_SigningRequests_Origin`, check type `Action1Agent`, exit codes of the install job, the state on the Patches
  tab and the counts in the patch policy list. To verify on the test VPS: an installation on a real endpoint end to end,
  and the reason shown for a download that fails.
- [done] **Four requests from testing** (asked for by the developer on 2026-10-13, built as proposed the same day):
  - *One date and time field everywhere*: `DateTimeField`, `DateField` and `TimeField` on the browser's own inputs replace
    MudDatePicker and MudTimePicker in the hold, maintenance, patch policy, maintenance window, audit log and secret end
    date fields. "Starts at" of a patch policy stays a time of day: its schedule repeats and an Action1 automation has no
    start date.
  - *Backups made outside Fleeto*: `BackupSettings.ExternalDescription`, no backups by the workers, healthy in instance
    health and on the dashboard, an accepted risk in ARCHITECTURE §5.
  - *Refresh on an endpoint*: `Endpoints.InventoryRequestedAt`, `fleeto_inventory_requests`, the gateway sending
    `InventoryRequest`, once a minute per endpoint.
  - *A service that does not exist*: the agent reports -1, parameter `missing_ok` ("No alert when the service does not
    exist"), and without it a problem like a stopped service.
  To verify on the test VPS: the fields in Chrome and Edge, Refresh on an online endpoint, a service check for a service
  that is not installed with and without the option.
  After testing alpha.16 (2026-10-14): saving the backups notifies `fleeto_instance_health` so the workers measure at once,
  and a removed agent in Settings, Agent uninstalls shows "Listed until" (a week after the removal) instead of the expiry.
  The Linux install command starts with `$(command -v sudo)` instead of `sudo`, so it also runs on Proxmox VE (root, no sudo).
- [done] **Save a script larger than 32 KB** (found by the developer testing alpha.17 on 2026-10-14, a script of 40 KB): the
  Blazor Server connection took at most 32 KB from the browser. `CircuitLimits.MaxReceiveMessageBytes` (1 MiB, four times the
  largest script) is set on the hub.
- [done] **Reinstall the Action1 agent after Fleeto removed it** (found by the developer testing alpha.16 on 2026-10-14): Action1
  leaves `agent.guid` behind on uninstall. The agent reports it only with an "Action1 Agent" entry in the installed programs,
  `Clear-Action1Id` in both jobs removes a left-over one, and `Action1Exception.IsGone` reads a 400 "not found" as not held.
  To verify on the test VPS: Remove Action1 agent once more on the server (with alpha.17 it clears the id), Refresh, then Install.
- [done] **Run a library script interactively in remote background** (asked for by the developer on 2026-10-14; decided the same
  day: it follows the approval of the policy, and it runs as SYSTEM or root, or as the signed-in user). Done: `RemoteScriptService`
  and `remote_session.script`, `TerminalScriptDialog`, service `script` on the terminal channel with `Hello.scripts`,
  `jobs.StageInteractive` and `remote.OpenScript` in the watchdog, the action `terminal.script`. On Windows a script as the signed-in
  user gets line input (redirected streams), as SYSTEM a pseudo console. To verify on the test VPS: a script with Read-Host as
  SYSTEM and as the signed-in user on Windows, a script with `read` on Linux, and an approval policy hiding an unapproved script.
- [done] **Uninstall the Fleeto agent of a deleted endpoint** (asked for by the developer on 2026-10-13: an offline agent is
  removed when it comes back, within a term, but leaves the lists at once). Decided the same day: 30 days, also on agent-only
  endpoints, also when a client is deleted, checked by default, a list in Settings. Done: `AgentUninstall` with the
  certificate fingerprints, signing request kind `AgentUninstall` and `AgentUninstallHandler`, `UninstallCommand` in the
  agent protocol (context `fleeto-uninstall-v1`), `DISCONNECT_CODE_RECONNECT`, the uninstall session of the gateway, the
  agent and watchdog starting `fleeto-agent uninstall` in a process of their own, Settings, Agent uninstalls, retention. To
  verify on the test VPS: a deleted Windows and Linux endpoint, online and offline, removing itself. Verified on alpha.16
  (2026-10-14): a deleted Windows endpoint and a deleted Linux endpoint removed their agents, and an endpoint deleted while
  offline removed its agent once it came back (2026-10-14).
- [done] **Remove the Action1 agent with the endpoint** (asked for by the developer on 2026-10-13, checked by default):
  deleting an endpoint and switching it to agent-only offer it. Done: `IntegrationOperationKind.RemoveEndpoint` written by
  web (inside the deletion's transaction), carried out by `PatchRequestService`, an option on `ConfirmDialog`. Verified on
  alpha.16 (2026-10-14): switching to agent-only and deleting both removed the Action1 agent.

## 0.7.0 — Hardening

- Load test at 10,000 simulated agents on the full ingest path; fix what breaks. Document
  the per-instance footprint and how many instances fit on one VPS size.
- External security review of enrollment, signing, secrets handling, licensing, remote
  control and multi-tenancy.
- Gateway benchmark at 10,000 connections (open since 0.1.0, tested with 200 simulated agents).
- Key ceremony written out and rehearsed (release and license keys, root key, signer key, backup key pair), and the
  production release and license keys on hardware tokens (open since 0.1.0; test keys are in use since 2026-09-15).
- Two instances side by side on one VPS, each on its own FQDN; restore of a backup onto a fresh VPS tested, also for
  several instances on one VPS; unattended reboot recovery verified with several instances on one VPS (open since 0.1.0).
- GDPR deliverables: data inventory, data processing agreement template, breach procedure,
  client and endpoint deletion with full purge, recording retention.

## 1.0.0 — General availability

- Everything above complete, tested and documented, including the subjects under "Not yet scheduled": they are part of the
  v1 scope in `CLAUDE.md` and each one needs a version of its own before 1.0.0 (decided 2026-09-23).
- Product name, trademark and domain checks done.
- Pricing per managed endpoint decided.
- API field names frozen.

## Not yet scheduled

Wanted, but not in a version yet: the version is chosen once the open questions are answered. These subjects stay part of
  the v1 scope in `CLAUDE.md` (decided 2026-09-23), so each one gets a version before 1.0.0.

- Patch management on Linux (planned as 0.4.1, moved here on 2026-09-23): the Fleeto agent reads the id of the Action1
  agent on a Linux endpoint as it already does on Windows, and Linux endpoints get patch state, missing updates,
  deployments and a place in the compliance counts. Until then a Linux endpoint shows no patch data at all and stays out
  of the counts, because calling it uncovered would be untrue.

- The integrations other than Action1 (planned as 0.5.0, moved here on 2026-09-23): Sophos Central (endpoint health,
  detections), Veeam (backup job status), Proxmox VE and VMware vCenter (host and VM inventory and health), and
  integration health on the dashboard. The connector architecture and the rules they follow are in `CLAUDE.md`
  (Integrations) and `ARCHITECTURE.md` §11; Action1 is the worked example.

- Logs, search and retention (planned as 0.6.0, moved here on 2026-09-23): log and event collection by the agent with
  full-text search under a second at 10,000 endpoints, retention policies per data type with continuous aggregates for
  the dashboards, and a global search across clients, sites, endpoints and notes.

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
- Authenticode code signing of the Windows agent and watchdog binaries (decided 2026-09-15, 0.2.1); it comes next to
  the release manifest check, not instead of it.
- Wayland support on Linux. (File transfer moved into 0.3.0 on 2026-09-16.)
- Whitelabel beyond the FQDN: customer logo and product name in UI and email.
- Steaan management server: central issue, renewal and revocation of licenses, fetched by
  instances over an API; later also an overview of all instances and their versions.
- SNMP for network devices.
- Ticksy hook for alert-to-ticket.
- Mobile device management, network topology mapping: noted, not planned.
