# Fleeto — Patch management by Fleeto (0.9.0)

> The design and build plan of patch management by the Fleeto agent itself: on Linux (Ubuntu, Debian, AlmaLinux and
> Proxmox VE) and on Windows, servers and workstations, for endpoints where Action1 is not used (`ROADMAP.md`, 0.9.0). Like
> `VULNERABILITIES.md` and `DASHBOARDS.md`, this file states what is decided, where the code goes and in which order it is
> built; once a step is built, what it delivers moves into `ARCHITECTURE.md` and this file keeps what is still open.
>
> Written 2026-11-04 as a proposal, from the code and the documentation as they are at `v0.8.0-alpha.16`, and **decided with the
> developer on 2026-11-04**: every recommendation of §14, with four changes from the review (§14, decisions 2, 7, 8 and 11). The rules
> are in `CLAUDE.md` (Patch management). Step 0 of the build plan (§13) is built.

## 1. What it is, and what it is not

Fleeto today shows patch state and starts deployments, and Action1 does the work (`CLAUDE.md`, Patch management). From 0.9.0 the
Fleeto agent can do that work itself, for the endpoints the customer does not put under Action1:

- **Scanning**: the agent reads which updates are missing, with the tools of the operating system (the Windows Update Agent on
  Windows, apt on Ubuntu, Debian and Proxmox VE, dnf on AlmaLinux), and reports them. The Patches tab, the dashboard tile, the
  "Missing updates" check and the public API show them in the shape they already have for Action1.
- **Installing**: a technician deploys updates from the Patches tab or the endpoint list, or a patch policy installs them on its
  schedule in rounds, as a signed job the agent carries out and reports on, with a restart when the policy or the technician allows it.
- **Next to Action1, never on top of it** (§2): one endpoint is patched by Action1 or by Fleeto, decided by its patch policy.

Fleeto keeps **no package catalog and no update files**: the Windows Update Agent, apt and dnf of the endpoint decide what exists,
exactly as they do for an administrator at the keyboard. Fleeto adds the schedule, the selection, the restart rule, the record and
the overview. That is the honest scope of an RMM, and it is why this stays small enough to secure (§11).

It is not:

- **Third-party applications on Windows** (Chrome, Adobe, 7-Zip): Action1 covers those today ("Applications" source); Fleeto's own
  engine covers what Microsoft offers through the Windows Update Agent, which includes Office and SQL Server through Microsoft Update
  (§4). winget from a service account is unreliable (it runs per user) and is not planned for 0.9.0; noted under "Not yet
  scheduled" (§14, decision 6).
- **A WSUS**: an endpoint that a group policy points at a WSUS server keeps using it; Fleeto installs what that server approves (§4).
- **The guests of a Proxmox VE host**: a VM or container is an endpoint of its own when it runs the agent (as `VULNERABILITIES.md`
  §1). Hosts and VMs discovered through an integration, without an agent, get nothing.
- **Synology DSM**: DSM updates itself; the agent never touches its packages (`SupportedProducts`).
- **Feature updates of Windows** (24H2 to 25H2) and **drivers**: left out by default, a patch policy can include drivers; feature
  updates come in a later version (§14, decision 5).
- **Rollback**: Fleeto shows what was installed; uninstalling an update is a job or a remote terminal, as it is for an administrator.
- **Snapshots before patching**: not in 0.9.0; a task before the round can do it where the customer wants it.

## 2. Next to Action1

*Decided 2026-11-04* (§14, decisions 1 to 3):

- **The engine is a property of the patch policy.** `PatchPolicy.Engine` is `Action1` (every existing policy) or `Fleeto`. The
  effective patch policy of an endpoint, the most specific link for its class (`EffectivePolicyRules`, unchanged), decides who
  patches it, so one endpoint can never be in a round of both: it has at most one patch policy, and that policy has one engine.
  No new switch on clients, sites or endpoints, and the links on three levels, the class slots and the client templates stay as
  they are.
- **One rule says which engine an endpoint has** (`PatchEngineRules.EngineFor`, built in step 0, in C#, SQL and EF Core kept equal
  by `PatchEngineRuleTests` like `EffectivePolicyRules`): *none* for an endpoint without a Fleeto agent or one that is (or behaves as)
  agent-only; *Action1* when the agent reports an Action1 agent on the endpoint or Action1 reports the endpoint (a patch state with an
  Action1 id); otherwise *Fleeto* on Windows, Windows Server, Ubuntu, Debian, AlmaLinux and Proxmox VE (`EndOfLife.ProductOf`); *none*
  on Synology DSM (it updates itself) and on anything else, each with its reason (`PatchEngineReason`). Whether the agent is new enough
  to scan and install is a separate check (`PatchRules.Problem`, step 1), so the rule holds in plain SQL. The engine of the policy only
  decides what Fleeto *does*; what Fleeto *knows* follows the endpoint:
  - **Scanning follows the endpoint, not the policy**: every managed endpoint with engine *Fleeto* is scanned, also without a patch
    policy, so the Patches tab, the compliance tile and "Missing updates" work on a Linux server that nobody schedules yet, as they
    do on a Windows endpoint Action1 reports without a policy. Without a patch policy nothing is installed by itself, as today.
  - **A Fleeto policy on an endpoint with an Action1 agent installs nothing.** The tab says "Patched by Action1; this patch policy
    is for patching by Fleeto" and offers the existing "Remove the Action1 agent"; the moment the Fleeto agent no longer reports
    the Action1 agent, the policy takes effect. Fleeto never patches an endpoint twice, and it never removes an Action1 agent by
    itself. An Action1 policy on a Linux endpoint keeps doing what it does today: nothing, shown plainly.
  - **"Turn automatic Windows Update off"** keeps its meaning for both engines: only the rounds of the policy install updates. For
    an Action1 policy `WindowsUpdateRules.Applies` stays as it is (the Action1 agent must be installed); for a Fleeto policy it
    applies when the engine of the endpoint is *Fleeto*. The agent code (`internal/winupdate`) does not change.
- **Compliance counts both engines.** `covered` on the dashboard tile and in the summary counts endpoints with patch state from
  either engine; `notPatched` becomes the managed endpoints of a supported platform without an engine, each with its reason on the
  tab. A Linux endpoint is no longer left out of the counts: once its agent scans, saying it is covered is true.
- **The rule in `CLAUDE.md`** ("Fleeto does not maintain its own package catalog or patch engine") becomes: Fleeto keeps no catalog
  of its own; it patches with the package manager or the Windows Update Agent of the endpoint where Action1 is not used, and
  delegates to Action1 where it is. `SupportedProducts` gets a patching line per operating system (§9); the test that every
  operating system has one keeps it honest.

## 3. Principles that shape it

From `CLAUDE.md` (security, robustness, performance, in that order), applied to patching:

- **Nothing a user types reaches an endpoint as a command.** A scan has no input. An installation is a signed job of a new type whose
  body fleeto-signer composes itself from the deployment and the policy, like the Action1 agent installation and the restart step:
  identifiers, filters and switches, never a script. The agent passes package names and update identities to apt, dnf or the
  Windows Update Agent as arguments after validating their form (§11), never through a shell string.
- **Only the workers write a job without a user**, and only for a round of an enabled Fleeto patch policy that applies to the
  endpoint, enforced by the origin triggers and checked again by the signer before it signs (§8), exactly as for automatic Action1
  agent installations and task runs.
- **Managed endpoints only, at every layer**: web, the signer, the gateway and the agent each refuse. The scan interval is part of
  the signed configuration and is never set on an agent-only configuration (the signer refuses one that carries it), like the
  storage scan interval; the agent ignores a scan request on an agent-only configuration.
- **Never lie about state.** Everything the tab shows comes from a scan the agent did, with its time; a scan that failed shows its
  cause (a held apt lock, an unreachable repository, a Proxmox enterprise repository without subscription, an interrupted dpkg) rather
  than an empty list that looks clean. Patch state older than the alert threshold is a `patch_state` alert, as it is for Action1.
- **Degrade gracefully.** A scan waits for the package lock and gives up with its reason. An installation the agent asked a restart
  for survives the restart (§5); an installation the agent did not finish for another reason is reported as such, never run again
  by itself, and the next scan tells the truth about what was installed. The package manager is never killed mid-transaction.
- **Nothing new leaves the VPS**: web and the workers fetch nothing for this feature. The endpoint talks to its own repositories or
  Windows Update, as it does today for an administrator.
- **No database write per request**: scan reports are stored once per scan, missing updates are replaced per endpoint with COPY as
  the vulnerability findings are, and every loop is a `WorkerLoop`.

## 4. Agent: scanning

A scan reads what is missing and reports it. It is scheduled by the signed configuration, runs after every installation, and runs on
request. It never installs anything.

Protocol, additive in `agent.proto` so an older agent keeps working and the tab says "Update the agent to see missing updates":

| Message | Meaning |
|---|---|
| `AgentConfig.patch_scan_interval_hours` | Hours between two scheduled scans; 0 is none. Set by `AgentConfigBuilder` only for a managed endpoint with engine *Fleeto* (§2). Never on `TIER_AGENT_ONLY`. |
| `AgentConfig.turn_off_distribution_updates` | Linux: turn unattended-upgrades or dnf-automatic off while the policy asks for it (§5, §14 decision 9). |
| `AgentConfig.microsoft_update` | Windows: register the Microsoft Update service while a Fleeto patch policy asks for it (§4, §14 decision 15). Applied with the configuration, never by a scan. |
| `PatchScanRequest { request_id }` | "Scan now", unsigned like `StorageScanRequest`: it can only start the scan the agent already does. Honoured on a managed configuration only, at most once per 15 minutes, one scan at a time. |
| `PatchScanReport` | One scan: `scan_id` (the idempotency key), `started_at`, `duration_ms`, `engine` (`windows_update`, `apt`, `dnf`), `problem` (empty when the scan read everything), `restart_required`, `source` (§4, Windows: `windows_update`, `microsoft_update`, `wsus` with the server name), `updates[]` (at most 2,000) and `request_id`. Kept on the agent's disk until `PatchScanAck` arrives after the durable write. |
| `PatchUpdate` | One missing update: `id` (Windows: `UpdateID:RevisionNumber`; Linux: `name:architecture`), `title`, `version` (Linux: the candidate version; Windows: empty), `installed_version` (Linux), `kb_numbers[]`, `classification` (Windows: the category name; Linux: `security` or `other`), `severity` (Windows: `MsrcSeverity`; Linux: empty, the server knows it, below), `released_at` (Windows: `LastDeploymentChangeTime`), `cves[]` (Windows: `CveIDs`), `advisories[]` (Linux: the USN, DSA or ALSA when the tool names one), `origin` (Linux: the archive, such as `jammy-security`, `stable-security`, `baseos`), `size_bytes`, `downloaded`, `restart_required`, `held` (apt hold, dnf versionlock), `mandatory` (Windows `IsMandatory`), `is_driver`, `is_feature_update`. |
| `PatchScanAck { scan_id }` | Sent after the report is committed; the agent then deletes it. |
| `Inventory.time_zone` | The time zone of the endpoint, for rounds in the endpoint's own time (§7): the IANA name on Linux (`/etc/timezone`, else the target of `/etc/localtime`), the Windows name on Windows (`GetDynamicTimeZoneInformation`), converted on the server with `TimeZoneInfo.TryConvertWindowsIdToIanaId`. |

Per platform (`agent/internal/patch`, one file per engine behind one interface, so the job and the scan share the code):

- **Windows**: the Windows Update Agent through COM. `go-ole` is already in the module graph (`yusufpapurcu/wmi` depends on it), so no
  new third-party package: `Microsoft.Update.Session`, `CreateUpdateSearcher`, `Search("IsInstalled=0 and IsHidden=0 and Type='Software'")`,
  and `Type='Driver'` only when the policy includes drivers. The search runs on a goroutine locked to its thread with
  `CoInitializeEx(COINIT_MULTITHREADED)`, as SYSTEM in session 0, which the agent already is (the API refuses installations from a
  remote session, not from a local service). **Source**: by default `ServerSelection = ssDefault`, which follows what the endpoint
  has: a WSUS from a group policy, else Windows Update. With the switch "Also Microsoft Update" on a Fleeto patch policy (default on,
  §14 decision 15) the signed configuration carries `AgentConfig.microsoft_update`, and the agent registers the Microsoft Update
  service when it applies that configuration (`IUpdateServiceManager2.AddService2` with the service id
  `7971f918-a847-4430-9279-4a52d1efe18d` and flags 7), which brings Office, SQL Server and .NET updates that plain Windows Update does
  not offer; never when a WSUS is configured. **A scan changes nothing on the endpoint** (decided 2026-11-04, §14 decision 2): it
  searches the services the endpoint already has, so an endpoint scanned without a patch policy is never registered with Microsoft
  Update. On Linux the scan refreshes the package lists (`apt-get update`, the metadata cache of dnf), as unattended-upgrades and
  dnf-automatic do every day; it installs and configures nothing. Per update the fields of the table above, `RebootRequired` from
  `Microsoft.Update.SystemInfo`, and the pending-restart reasons the agent already reads for the "Pending restart" check. Windows
  10, 11, Server 2016 or newer; Server Core included.
- **Ubuntu, Debian, Proxmox VE** (dpkg): `apt-get -q update` first, with `-o DPkg::Lock::Timeout=300` so a running unattended-upgrades
  or apt-daily is waited for rather than failed on, then `apt-get -s -q -o Debug::NoLocking=1 dist-upgrade` and the `Inst`, `Conf` and
  `Remv` lines: package, installed version, candidate version, origin and archive
  (`Inst libssl3 [3.0.2-0ubuntu1.10] (3.0.2-0ubuntu1.12 Ubuntu:22.04/jammy-security [amd64])`). **`dist-upgrade`, never `upgrade`**:
  Proxmox VE requires it (a plain upgrade can leave a host half updated), and on Ubuntu and Debian it is what unattended-upgrades
  computes as well; a package the simulation would remove is reported as such and shown on the tab. `security` when the archive
  ends in `-security` or the origin is `Debian-Security`, `UbuntuESM` or `UbuntuESMApps`; `held` from `apt-mark showhold`. The
  existing `PackageInfo` fields (restart required, Pro services, the apt lists) are read again with the scan. An `apt-get update`
  that fails on one repository (the Proxmox enterprise repository without a subscription answers 401) is a scan with a `problem`
  naming that repository; the other lists are still used. An interrupted dpkg (`dpkg --configure -a` needed) is a `problem` too.
- **AlmaLinux** (rpm): `dnf -q check-update` (exit code 100 means updates; 0 none; 1 a problem, reported) for the packages with
  their candidate version and repository, `dnf -q updateinfo list` for the advisory, type and severity per package, and the CVEs
  per advisory from the server (below); `held` from the versionlock plugin where it is installed; restart required from
  `needs-restarting -r` as today. AlmaLinux 8, 9 and 10 all ship dnf 4 (AlmaLinux 10 checked 2026-11-05, not dnf 5 as assumed); the exact output formats are captured when
  building, like the inventory captures in `agent/internal/inventory/testdata`.
- **What the server adds on Linux** (`PatchScanWriter`, §7): the severity and the CVEs of a package update come from the vulnerability
  findings the instance already keeps (`VULNERABILITIES.md` §7): every open finding of the endpoint whose fixed version is at or below
  the candidate version is fixed by the update, so the update carries those CVEs, the highest of their severities, and the advisory of
  the finding; its release date is the published date of that advisory where the feed record carries one
  (`VulnerabilityFeedRecord.PublishedAt`, checked 2026-11-04), otherwise the date Fleeto first saw the update missing (§6). An update that fixes no
  finding is `other` with severity `Unspecified`, as an Action1 update without a severity. Nothing leaves the instance for it.
- **Cost and pacing**: one scan at a time, at the lowest CPU and I/O priority like the storage scan; a scan runs at most 15 minutes
  (`apt-get update` on a slow mirror included) and reports a `problem` beyond it. Default interval 6 hours (Action1 syncs
  every 4), always after an installation, and on "Scan now". A report is sent after every scan, also when nothing changed (it is
  small); the server replaces the state per endpoint.

## 5. Agent: installing

An installation is a job of the new type `JOB_TYPE_PATCH_INSTALL` with `JobPayload.patch_install`, carried, verified, deduplicated,
reported and audited like every job (`ARCHITECTURE.md` §4, Job). The body:

```
PatchInstallJob {
  scope: ALL_MISSING | SPECIFIED
  updates[]: { id, title, version }        // SPECIFIED: exactly these, nothing else
  filter {                                  // ALL_MISSING: what the policy or the deployment selects
    classifications[], severities[], excluded_titles[], security_only,
    include_drivers, released_before (deferral, §6), microsoft_update
  }
  restart: NEVER | IF_REQUIRED             // with notice_seconds and message
  timeout_seconds                           // for the whole job; the package manager is never killed (below)
}
```

The agent:

- **Verifies** the job as every job (signature, instance, endpoint, validity, managed configuration) and refuses it with the reason
  when its applied configuration does not scan (`patch_scan_interval_hours` 0): patch management by Fleeto is not on for the
  endpoint, so no installation runs on it, whoever signed it. An agent older than `PatchRules.MinimumAgentVersion` refuses the type,
  which the server knows and says before it writes the job (`PatchRules.Problem`, like `RestartRules.Problem`).
- **Selects** fresh: it scans again first, then takes the named updates (SPECIFIED; a named update that is no longer missing is
  reported as already installed, never an error) or applies the filter to what is missing (ALL_MISSING). The selection rules are pure
  functions (`patch/select.go`), tested on captured scans. What it selected is the first lines of the output.
- **Windows**: accepts the EULA of each selected update (`AcceptEula`; an installation without it fails), downloads with
  `IUpdateDownloader` (progress as output lines), waits while `IUpdateInstaller.IsBusy`, refuses with "Restart the endpoint first"
  when `RebootRequiredBeforeInstallation` is set, installs with `IUpdateInstaller.Install`, and reports per update the
  `ResultCode` and `HResult` of its `IUpdateInstallationResult`, with `RebootRequired` of the whole result. One line per update,
  `Installed KB5031354 Cumulative Update ... — Succeeded`, in the shape the deployment history shows (§6).
- **Ubuntu, Debian, Proxmox VE**: `DEBIAN_FRONTEND=noninteractive apt-get -y -q -o Dpkg::Options::=--force-confdef
  -o Dpkg::Options::=--force-confold -o DPkg::Lock::Timeout=600`, then `install --only-upgrade name=version ...` for the exact
  selection (SPECIFIED, and ALL_MISSING with a filter), or `dist-upgrade` for ALL_MISSING without a filter (what Proxmox VE asks of
  its administrators; it also installs the new dependencies a kernel or pve-manager needs). Held packages are left alone and listed.
  No `autoremove` unless the policy asks (§6). The output of apt is the output of the job, so a technician reads what dpkg said.
- **AlmaLinux**: `dnf -y -q upgrade name-version ...` for the selection, `dnf -y -q upgrade` or `upgrade --security` for ALL_MISSING;
  excluded names as `--exclude=`; versionlock respected by dnf itself.
- **Restart**: when the result says a restart is required and the job says `IF_REQUIRED`, the agent restarts the endpoint the way the
  restart step of a task does (`internal/jobs/restart.go`): it records the boot time and the deadline next to the job, shows the
  notice to a signed-in user (the message of the policy, or `PatchRules.RebootMessage`), runs the fixed restart command after the
  notice, and completes the job after it started again with a newer boot time, then scans. With `NEVER`, the job ends with "Restart
  required" as its last line and `restart_required` in its result; the tab shows it, and the "Pending restart" check does what it
  does today. Fleeto restarts no endpoint outside a patch policy that allows it or a deployment a technician started with the restart on.
- **The notice on a workstation is never skipped** (decided 2026-11-04, §14 decision 7): on a workstation with a signed-in user the
  agent always shows the notice for the time of the policy (`RebootTimeoutMinutes`) before it restarts, also when the policy switches
  the message off (`RebootMessage` off only silences servers and workstations nobody is signed in to). There is no consent prompt: the
  owner of the patch policy decides, as with a deployment through Action1, and `CLAUDE.md` records it.
- **A Proxmox VE host is never restarted by a round** (decided 2026-11-04, §14 decision 11): a round sends `NEVER` to a Proxmox VE host
  whatever the policy says, because two nodes of one cluster restarting in the same round lose the quorum of the cluster. The host
  installs and its tab says "Restart required"; a technician restarts it, or starts a deployment by hand with the restart on, which is
  a choice for that one host.
- **Recovery after a start**: `recover` in `internal/jobs/manager.go` treats a patch job like a restart job: a boot time newer than the
  recorded one completes it (the installation survived the restart it asked for; on Windows the "Working on updates" phase is part of
  that restart), the deadline not yet passed keeps waiting, otherwise it times out. A patch job without a restart record that was
  running when the agent stopped is `interrupted` and `lost` as every job (`CLAUDE.md`, Robustness): running it again could be worse
  than not knowing; the scan after the start shows what was installed.
- **Timeout**: the job's timeout comes from the policy or the deployment (default 4 hours, at most 24 as for scripts). At the timeout
  the agent reports `timed_out` with "The package manager was still working; the next scan shows what was installed", and lets apt, dnf
  or the Windows Update Agent finish: killing dpkg or msiexec mid-transaction leaves an endpoint that no longer updates at all (§14,
  decision 8). One patch job runs at a time per endpoint, in one of the four job slots. **The slot is freed at the timeout** (decided
  2026-11-04): the agent keeps watching the process it started, refuses a new patch job with "An earlier installation is still
  running" while that process lives, and scans as soon as it ends, so a package manager that hangs costs one slot for the length of the
  timeout and never for good.
- **Distribution updates off** (Linux): with `turn_off_distribution_updates` the agent does what `internal/winupdate` does
  on Windows: on dpkg it writes `/etc/apt/apt.conf.d/99fleeto` with `APT::Periodic::Unattended-Upgrade "0";` and a marker, and removes
  the file when the configuration no longer asks for it or at uninstall; on rpm it stops and disables `dnf-automatic.timer` and
  `dnf-automatic-install.timer` where they were enabled, records which in the marker, and enables them again the same way. A file or
  timer the agent did not write is never touched; the state is reported with the inventory like `windows_update` (§14, decision 9).

## 6. Data model

Additive and expand-only (`ARCHITECTURE.md` §7): 0.8.0 must still run on the schema. Names follow the existing patch tables, every
client-owned row with its own `ClientId` and composite foreign keys.

- **`PatchPolicies`**: `Engine` (`Action1`, `Fleeto`; default `Action1`, so every existing policy keeps its meaning). What the
  existing columns mean for engine `Fleeto`:

  | Column | Fleeto engine |
  |---|---|
  | Schedule (`ScheduleKind`, `WeekDays`, `MonthDay`, `MonthWeek`, `MonthWeekday`, `StartMinute`, `EndpointLocalTime`) | The rounds (§7), computed by Fleeto with the daylight saving rules of maintenance windows; endpoint time from `Inventory.time_zone`, UTC when the agent does not report one, which the tab says |
  | `Scope`, `UpdateTypes`, `Severities`, `ExcludedNames` | The filter of the installation job. `UpdateTypes` are mapped to the categories of the Windows Update Agent (`PatchPolicyRules.WindowsCategories`); `Severities` to `MsrcSeverity` on Windows and to the severity of the fixed findings on Linux; `ExcludedNames` match the title (Windows) or the package name (Linux) |
  | `UpdateSources`, `ExcludedVendors`, `RequireApproval`, `AutoInstallAgent` | Not offered for engine `Fleeto` (no third-party source, no vendors, no approval console, no Action1 agent); the dialog hides them, the validation refuses them (no placeholder UI) |
  | `InstallDelayDays` | Deferral: an update is installed only `InstallDelayDays` after its release date (Windows), the published date of its advisory (Linux) or, without either, after Fleeto first saw it missing (`EndpointMissingUpdates.FirstSeenAt`). Pilot policies with 0 days and production policies with 7 are the rings |
  | `AutoReboot`, `RebootMessage`, `RebootMessageText`, `RebootTimeoutMinutes` | The restart of §5; the timeout is the notice |
  | `RetryHours` | The validity of the round's jobs (`ValidUntil`, at most 7 days, which is already the maximum): an endpoint that is off at the start gets its job when it connects within the window |
  | `TurnOffWindowsUpdate` | As today, for engine *Fleeto* of the endpoint (§2) |

  New columns for engine `Fleeto`, each with a check constraint and ignored by engine `Action1`: `LinuxSecurityOnly` (default off:
  `security_only`), `IncludeDrivers` (default off), `MicrosoftUpdate` (default on, §4), `TurnOffDistributionUpdates` (default off),
  `AutoRemove` (default off), `InstallTimeoutMinutes` (30 to 1,440, default 240), `RandomDelayMinutes` (0 to 720, default 30, like a
  task: the share of an endpoint spreads the start of a round). `PatchPolicyRules.Validate` validates per engine.
- **`EndpointPatchStates`**: `Engine` (`Action1`, `Fleeto`), `ScannedAt?`, `ScanProblem?` (1,000 characters), `ScanSource?`,
  `ScanRequestedAt?`, `AgentVersion`. For engine `Fleeto` the `External*` columns stay empty (they are strings), `ProductLastSeenAt`
  is the last scan, `RebootRequired` is what the scan said. `ScheduledPolicyId` and `ScheduledSince` keep their meaning for the
  "Scheduled patching" check; `AutomationId` stays null. The row exists from the first scan and is removed when the engine changes
  to *none* or Action1 takes over, as it is removed today when Action1 stops reporting.
- **`EndpointMissingUpdates`**: `FirstSeenAt` (also set for Action1 updates from now on, harmless), `Advisory?`, `Classification`
  (`Security`, `Other`, or the Windows category), `Origin?`, `SizeBytes?`, `Downloaded`, `Held`, `IsDriver`, `IsFeatureUpdate`. The
  id of a Fleeto update is its `PatchUpdate.id`; `ApprovalStatus` stays empty. Replaced per endpoint per scan with COPY; rows that
  stay keep their `FirstSeenAt` and `Id` (matched on the update id), so the deferral and a chosen update survive a scan.
- **`PatchScans`**: one row per report (`Id` = `scan_id`, `ClientId`, `EndpointId`, `StartedAt`, `ReceivedAt`, `DurationMs`,
  `Engine`, `Source`, `Problem?`, `RestartRequired`, `UpdateCount`, `RequestId?`, `UpdatesJson` capped at 2,000, `ProcessedAt?`).
  The gateway writes it idempotently and acks (`PatchScanAck`), then raises a new notification `fleeto_patch_scans`; the workers turn
  it into patch state (§7) and sweep for unprocessed rows, so a lost notification delays the state and never loses it. The newest row
  per endpoint stays; older ones go after 7 days.
- **`PatchDeployments`**: `Engine`. For engine `Fleeto` the `External*` columns stay empty and the states are owned by the workers
  from the jobs (§7); `Scope` keeps its three values. **`PatchDeploymentTargets`**: `JobId?` (the installation job), `Restart`
  (`NotRequired`, `Required`, `Restarted`, `Refused`). **`PatchDeploymentSteps`** are written from the lines of the job (§7), so
  the history on the tab works unchanged. **`PatchDeploymentUpdates`** name the chosen updates for `Specified`, as today.
- **`Jobs`**: `Type` gains `PatchInstall`; `PatchDeploymentId?` and `PatchDeploymentTargetId?` bind the job to its deployment (a
  job of a deployment runs at most once per target: unique index), frozen by `TR_Jobs_Binding`. `ScriptName` is "Install updates",
  `ScriptSha256` the hash of the composed body as for the Action1 jobs, so the history can show that what ran is what was signed.
- **`InventorySnapshots`**: `TimeZone?`, `DistributionUpdates?` (the state of §5, like `WindowsUpdate`).
- **`PatchRequests`**: kind `Scan` next to `Sync` and `RemoveAgent`; for engine `Fleeto` a sync *is* a scan.
- **Audit**: `patch_deployment.started` (a technician), `patch_round.started` (the system, per client and round, with the policy),
  `patch_deployment.completed` with the counts, `patch_scan.requested`; a changed engine on a policy as a policy change. Never a
  package list in the audit log.
- **Retention**: missing updates are current state; deployments, their targets and steps are kept as the job history (13 months), the
  job output 90 days, scans 7 days. Deleting an endpoint or client removes everything of it.

## 7. Workers

- **`PatchScanWriter`** (woken on `fleeto_patch_scans`, every minute as a sweep): per unprocessed scan, on Linux joins the findings
  of the endpoint for severity, CVEs and advisory (§4), writes `EndpointPatchStates` and replaces `EndpointMissingUpdates`, keeps
  `FirstSeenAt`, evaluates the "Missing updates" checks with the existing `MissingUpdateAge` (the check loses its "Windows only"
  platform in `CheckCatalog`; its minimum severity and release date rules apply as they are), rebuilds the compliance counts the
  dashboard reads, and resolves or opens the `patch_state` alert (no scan for more than 7 days while the agent is online, or a
  scan that keeps failing with the same `problem`). `PatchStateWriter` of Action1 is not touched; both write the same tables.
- **`PatchRoundService`** (a `WorkerLoop`, every minute, woken on `fleeto_jobs` and a new notification `fleeto_patch_policies` that
  web raises when a patch policy or a patch policy link changes): for every enabled
  patch policy with engine `Fleeto`, the occurrences of its schedule per time zone (`PatchPolicies.Occurrences`, written like
  `AutomationTasks.Occurrences` with the daylight saving rules of `MaintenanceWindows`; for `EndpointLocalTime` one occurrence per
  distinct endpoint time zone). At an occurrence it writes, in one transaction per client, one `PatchDeployment` of scope
  `PatchPolicy` and engine `Fleeto` with a target per endpoint the policy applies to (`EffectivePolicies`, managed, engine *Fleeto*
  by `PatchEngineRules`, agent new enough), and per target a `Job` of type `PatchInstall` with `AutomationTasks.AutomaticInitiator`
  and the policy's name as initiator, due at the occurrence plus the endpoint's share of `RandomDelayMinutes` (`AutomationTasks.DelayFor`),
  valid until the occurrence plus `RetryHours`, its signing request and the audit entry. An occurrence runs once per client (unique
  index like `AutomationTaskRuns`), whatever the workers retry; an endpoint whose previous round is still open is skipped with the
  reason. The gateway delivers the job when the endpoint connects (today's catch-up), so an endpoint that is off at 20:00 gets its
  round when it comes back within the window.
- **Limits per engine**: `PatchRules` holds the limits of Action1 today (at most 200 endpoints and 100 updates per deployment, followed
  for 24 hours, retried for 1,440 minutes) and says that Fleeto patches nothing itself. In step 3 it gets the limits per engine: a
  deployment a technician starts keeps 200 endpoints and 100 updates for both engines; a round of engine `Fleeto` has no endpoint limit
  (one per occurrence and client, written in batches) and is followed for the validity of its jobs plus an hour (`RetryHours`, at most 7
  days), not 24 hours; its comment is rewritten then.
- **Following a deployment of engine `Fleeto`**: `PatchDeploymentService` gets a branch per engine (or a sibling, decided when
  building): a target follows its job (`queued` and `delivered` are *Pending*, `running` *Running*, `succeeded` *Succeeded* with
  the restart state from the result, `failed` *Failed* with the last line of stderr, `refused`, `expired` and `lost` *Unknown* with
  the reason); the steps of the target are the per-update lines of the job output (`PatchDeploymentSteps`: position, time, `Install`,
  the result word, the title), read when the job ends; the deployment completes when every target ended, with `FollowUntil` the
  validity plus an hour, and the counts in the audit entry. The Action1 path is unchanged.
- **"Scheduled patching"**: `ScheduledPatchingChecks` counts from `ScheduledSince` of the endpoint's current Fleeto policy and resets
  on a succeeded target of a round, as it does today from an Action1 run; `PatchRoundService` sets `ScheduledPolicyId` and
  `ScheduledSince` when a policy first applies to an endpoint.
- **`PatchRequestService`**: kind `Scan` (and `Sync` on an endpoint with engine `Fleeto`) sets `ScanRequestedAt` on the patch state
  and notifies `fleeto_patch_scan_requests`; the gateway sends `PatchScanRequest` to the live agent while the request is under 15
  minutes old and no scan arrived after it, like `InventoryRequest` and the storage scan request; one per endpoint a minute.
- **`AgentConfigBuilder`**: `patch_scan_interval_hours` and `turn_off_distribution_updates` from `PatchEngineRules` and the effective
  patch policy; a configuration is signed again when the engine of an endpoint changes (an Action1 agent appears or disappears in
  the inventory, a patch policy link changes, a policy changes engine or its switches: the existing `ConfigChangeScope` cases of
  `TurnOffWindowsUpdate`), and the signer refuses an agent-only configuration that carries either.
- **Performance**: at 10,000 endpoints with 100 missing updates each a scan every 6 hours is a million rows replaced a day, in COPY per
  endpoint like the findings; the tile and the summary read counts per site that `PatchScanWriter` maintains, never the rows. The
  ingest is measured with `Fleeto.LoadTest` before the step is done.

## 8. Signer

`JobHandler` accepts `PatchInstall` and, before it signs, checks from the database (never from what web or the workers wrote on the
job alone):

- **For a technician's job** (a deployment of scope `AllMissing` or `Specified` web wrote): the permission `patches.deploy`, the
  groups reaching the endpoint, the managed tier with the license, the platform and agent version (`PatchRules.Problem`), the engine
  of the endpoint *Fleeto* (`PatchEngineRules` in SQL), the deployment and target the job names (frozen by `TR_Jobs_Binding`), and
  for `Specified` the updates of the deployment present in `EndpointMissingUpdates` of the endpoint (a chosen update that is no longer
  missing is left out, not refused).
- **For a round** (no user, `TR_Jobs_Origin` lets the workers write a `PatchInstall` job only with a deployment of scope `PatchPolicy`
  that the workers wrote, and `TR_SigningRequests_Origin` only ask the signature of such a job): the policy of the deployment is still
  the endpoint's effective patch policy, enabled, engine `Fleeto`, applying to its class; the occurrence of the deployment is within
  the retry window of the policy; the endpoint is managed and its engine is *Fleeto* now (so a round never reaches an endpoint that got
  an Action1 agent since); and no other job of the same round is signed for the endpoint.
- **It composes the body itself** (`PatchInstallJob` of §5) from the deployment (scope, the chosen updates with their titles and
  versions, the restart choice) and, for a round, the policy (filter, `released_before` from `InstallDelayDays`, restart, timeout),
  signs it with the context `fleeto-job-v1` as every job, and stores the hash. A deployment that changed since web or the workers asked
  (impossible by the binding trigger, checked anyway) is refused with the reason. The output cap is 1 MiB, the timeout the one of the
  policy or deployment, the validity at most 7 days.
- Rate limits and audit as for every job. The accepted risk of `ARCHITECTURE.md` §5 holds: whoever controls web can get installations
  of what the operating system vendor offers signed, within these rules, which is the class of action Action1 deployments already are.

## 9. UI

- **Patches tab**, one tab for both engines, managed endpoints only. A line states the engine and its source: "Patched by Fleeto
  (Windows Update, Microsoft Update)", "Patched by Fleeto (apt, Proxmox VE 8)", "Patched by Action1", or why neither ("Agent-only",
  "Update the agent to 0.9.0 to patch this endpoint", "Patched by Action1; this patch policy is for patching by Fleeto", "DSM updates
  itself"), the last scan with its `problem`, "Restart required", the next round of the patch policy and the last one. The list of
  missing updates: Windows as today (KB, title, classification, severity, release, CVEs, size, downloaded); Linux per package (package,
  installed, candidate, origin, security or other, severity with the CVEs it fixes, advisory, held, removed by the upgrade). "Scan now"
  (permission `patches.sync`, relabelled "Sync or scan now"; once per endpoint a minute). "Deploy updates" opens the existing dialog
  (everything missing or the ticked updates, restart on or off, for engine `Fleeto` also "Security updates only" on Linux), which for
  engine `Fleeto` writes the deployment, its targets, the jobs and their signing requests in one transaction, like a script run on a
  selection; the admin notice above the threshold of Settings, Scripts applies. The history of a deployment shows the steps from the
  job and opens the job output (`jobs.view`). The endpoint list action on a selection does the same per client and engine.
- **Patch policies** (Settings, Templates): the engine is chosen first in the dialog ("Patched by Action1" or "Patched by Fleeto") and
  shown as a chip in the list; the dialog offers only the options of the engine (§6). Changing the engine of a linked policy is allowed
  (`patchpolicies.edit`): the configurations of its endpoints are signed again and, for a policy that leaves Action1, its automations are
  removed by `PatchAutomationService` as for a deleted policy. The list counts endpoints per engine and shows the next round.
- **Dashboard**: the `patches.compliance` tile counts both engines (§2); the counter "not patched" opens the endpoints with their reason.
  No new tile in 0.9.0.
- **Endpoint list**: the column "patches" of `EndpointColumns` counts both engines; the tooltip names the engine.
- **Permissions**: none new. `patches.view`, `patches.sync`, `patches.deploy`, `patchpolicies.view` and `patchpolicies.edit` keep their
  roles; `jobs.view` opens the output of an installation.
- **Vocabulary** (`branding-fleeto.md` §6, in the commit of step 1): "patch policy" becomes "When and how updates are installed, by Fleeto
  itself or by Action1, linked to a client, site or endpoint"; new: **scan** (the agent reading which updates are missing: "Scan now",
  "Last scan"; never "check" for it, a check is a monitoring rule) and **round** (one scheduled run of a patch policy on its endpoints:
  "Next round", "Last round"; in the API and the data a round is a deployment of scope `patch_policy`).
- **Supported products** (`SupportedProducts`): Windows and Windows Server "By Fleeto, or through Action1"; Ubuntu, Debian, AlmaLinux and
  Proxmox VE "By Fleeto (apt, dnf)"; Synology DSM unchanged. The end-of-life report is not affected.

## 10. API

In the same commit as each step (`CLAUDE.md`, Public API):

- `GET /api/v1/endpoints/{endpointId}/patches` gains `engine` (`action1`, `fleeto`, `none` with `reason`), `scannedAt`, `scanProblem`,
  `scanSource`, `restartRequired`; every entry of `missing` gains `firstSeenAt`, `classification`, `advisory`, `origin`, `held`,
  `sizeBytes`, `downloaded`; the Linux `id` is `name:architecture`. Documented in `API.md` with the OpenAPI test.
- `GET /api/v1/endpoints/{endpointId}/patch-deployments` gains `engine`, and each target `jobId` and `restart`.
- `API-WAITLIST.md`: "Scan now" (`POST /api/v1/endpoints/{endpointId}/patch-scan`), the engine and the Fleeto options of a patch
  policy (added to the existing patch policy entries), and the summary's `covered` now counting both engines (the existing entry
  changes its note).

## 11. Security review

- **No new way to run code.** The agent gains calls into the Windows Update Agent, apt and dnf with validated arguments and nothing
  else: a Linux package name matches `^[a-z0-9][a-z0-9+.-]+$` (Debian policy; rpm names allow uppercase and `_`, checked per engine),
  a version the characters dpkg and rpm allow, a Windows update identity a GUID and a revision; a job whose body fails that check
  is refused before anything runs. Arguments are passed as an argument vector, never a shell line. The restart is the fixed command of
  the restart step.
- **What a compromised component can do**: web can get installations of vendor updates signed within the signer's rules (the same class
  as an Action1 deployment today); the gateway can trigger extra scans (unsigned, rate-limited, read-only) and never an installation;
  the workers can write rounds only for an enabled Fleeto policy that applies, which the signer checks again. An agent-only endpoint
  gets nothing at any layer.
- **Untrusted input**: titles, advisories and problems from a scan are shown as text, cut to length, never as HTML or Markdown; at most
  2,000 updates per scan; `UpdatesJson` is capped on the gateway as job output is.
- **Personal data**: a scan carries none; job output can (apt prints user names of mail notifications, for instance) and follows the
  job output rules. The audit log holds counts and policy names, never a package list.
- **Restarts** happen only as §5 says, with the notice (never skipped on a workstation with a signed-in user), never by a round on a
  Proxmox VE host, and are audited through the job; a workstation gets no consent prompt (as a deployment through Action1 today; the
  policy owner decides), which `CLAUDE.md` records as a decision.
- **Egress**: nothing new from the VPS. Microsoft Update registration on an endpoint is the endpoint's own choice through its policy and
  is reported on the tab.

## 12. Tests

- **Go**: parsers on captured real output (`apt-get -s dist-upgrade` and `apt-get update` of Ubuntu 22.04, 24.04 and 26.04, Debian 12 and
  13, Proxmox VE 8 and 9, the enterprise repository without a subscription included; `dnf check-update` and `updateinfo list` of
  AlmaLinux 8, 9 and 10), the selection rules (filters, deferral, exclusions, held, SPECIFIED with an update no longer missing), the job
  recovery after a restart (reusing the restart tests with a patch record), the distribution-updates switch with the marker on a
  temporary root, and the Windows flow against an interface over the COM calls with a fake, so it runs on Linux too; the first real
  run is on the test endpoints.
- **C#**: `PatchEngineRules` in C#, SQL and EF Core kept equal by a test; `PatchPolicies.Occurrences` with the daylight saving cases of
  `MaintenanceWindows` and endpoint time zones; `PatchPolicyRules.Validate` per engine; the signer refusals (policy off, engine changed,
  Action1 agent appeared, agent too old, outside the retry window, a second job of the round, a technician without `patches.deploy` or
  without the endpoint in their groups, agent-only); the origin and binding triggers for every role; cross-client reads and writes on
  every new table; the state and step derivation from job states; the "Missing updates" and "Scheduled patching" results; the
  compliance counts; the API document test; the branding grep.
- **Load**: `Fleeto.LoadTest` with 10,000 simulated agents sending a scan of 100 updates each, measured on the gateway and on
  `PatchScanWriter`; a round of 10,000 endpoints written by `PatchRoundService` within a minute.
- **Test VPS**: every alpha on real endpoints: an Ubuntu server, a Proxmox VE host on the no-subscription repository, an AlmaLinux VM,
  a Windows 11 workstation without Action1 and a Windows Server; a scheduled round with a restart on each.

## 13. Build plan

Each step is an alpha on the test VPS; 0.9.0 is released as a whole. The order puts the read-only part first, so the most value
arrives with the least risk, and the part that changes endpoints last.

0. [done 2026-11-04] **Decisions** (§14), the vocabulary, the `Engine` column (`PatchPolicies.Engine`, default `Action1`, migration
   `PatchPolicyEngine`) and `PatchEngineRules` with `PatchEngines.Query` and `PatchEngineRuleTests`; `PatchAutomationService` makes
   automations only for policies of engine `Action1`; `CLAUDE.md` (Patch management, Product scope) and `ROADMAP.md` updated to what was
   decided. No agent change and no UI: the engine is chosen in the dialog from step 4.
1. [built 2026-11-04, not yet on the test VPS] **Scanning on Linux** (§4, §6, §7): the protocol, `agent/internal/patch` for apt and dnf with the captured outputs, `Inventory.time_zone`,
   the scan interval in the signed configuration, the gateway write and ack, `PatchScans`, `PatchScanWriter` with the join on the
   findings, the Patches tab for a Linux endpoint, "Scan now", the compliance counts, "Missing updates" on Linux, the `patch_state`
   alert for stale scans, `SupportedProducts`, `API.md` and the waiting list, the load test. Nothing is installed yet.
2. **Scanning on Windows** (§4): the Windows Update Agent through COM, the source switch, the tab for a Windows endpoint with engine
   *Fleeto*, the conflict line for an endpoint with an Action1 agent.
3. **Installing by hand** (§5, §8): the job type, `PatchRules`, the signer, the agent flows on apt, dnf and Windows, the restart
   inside a job and its recovery, the deployment, targets and steps from the job, the dialog and the endpoint list action, the audit
   entries, the API changes on deployments.
4. **Patch policies with engine Fleeto** (§6, §7): the dialog per engine, the new columns, `PatchPolicies.Occurrences`,
   `PatchRoundService`, the origin triggers for rounds, the signer rules for a round, "Scheduled patching", `TurnOffWindowsUpdate` for
   engine *Fleeto*, the distribution-updates switch, the next and last round on the tab and in the list.
5. **Finish**: drivers and "Security updates only" options, deferral by `FirstSeenAt`, the endpoint list column and tooltip, the
   vocabulary and tone pass, `ARCHITECTURE.md` §2, §4 and §9 with what was built, `CHANGELOG.md`, the release.

Every step has its tests before it is called done, and nothing in it has a button for a later step.

## 14. Decisions (2026-11-04)

Taken with the developer on 2026-11-04: every recommendation of the proposal, with four changes from the review (decisions 2, 7, 8 and
11, marked *changed*).

1. **Where the engine lives**: on the patch policy (§2). It reuses the links on three levels and the class slots, needs no new UI for
   linking, and makes "never both on one endpoint" true by construction.
2. **Scanning without a patch policy**: every managed endpoint with engine *Fleeto* is scanned. *Changed*: a scan changes nothing on
   the endpoint; registering Microsoft Update belongs to applying a Fleeto patch policy (`AgentConfig.microsoft_update`), never to a
   scan (§4).
3. **A Fleeto policy on an endpoint with an Action1 agent** installs nothing and says so (§2).
4. **Linux selection by default**: everything (`dist-upgrade`), with "Security updates only" as a policy option (§6).
5. **Drivers and feature updates on Windows**: drivers behind a policy option, feature updates not in 0.9.0.
6. **Third-party applications on Windows**: not in 0.9.0, noted under "Not yet scheduled" in `ROADMAP.md`.
7. **Restart rules** as §5 (policy `AutoReboot`, the notice and the message; a manual deployment chooses; never otherwise; no consent
   prompt). *Changed*: on a workstation with a signed-in user the notice is always shown, whatever the policy says about the message.
8. **The timeout never kills the package manager** (§5). *Changed*: the job slot is freed at the timeout, and a new patch job is refused
   while the earlier process still runs.
9. **Turning unattended-upgrades and dnf-automatic off** (§5): a policy switch, off by default, with the marker approach of
   `internal/winupdate`.
10. **Endpoint time zones** from the agent (`Inventory.time_zone`), UTC until the agent reports one.
11. **Proxmox VE clusters**: a round spreads its start with `RandomDelayMinutes`; a limit per site ("at most N endpoints of a site at
    once") is noted for later. *Changed*: a round never restarts a Proxmox VE host; the tab says "Restart required" and a technician
    restarts it, or deploys by hand with the restart on (§5).
12. **Snapshots or backups before a round**: not in 0.9.0 (§1); a task before the round covers it where needed.
13. **Retention**: scans 7 days, the newest per endpoint kept; deployments as job history.
14. **Naming**: "Patched by Fleeto" and "Patched by Action1" in the UI, "scan" and "round" in the vocabulary (§9), this file as
    `PATCHING.md`.
15. **Windows Update source**: follow the endpoint (WSUS when a group policy sets it), with "Also Microsoft Update" as a policy option,
    default on.
16. **Default scan interval**: 6 hours, after every installation and on request.

## 15. Open

**What step 1 built differently from §4 to §7** (2026-11-04), so the next steps start from the code:

- The classification of a Linux update is stored in the existing `UpdateType` (`Security Updates`, `Regular Updates`, the words of
  `PatchPolicyRules.UpdateTypes`), not in a new `Classification` column; a new column follows only if Windows needs its own categories
  (step 2). `Downloaded`, `SizeBytes`, `IsDriver` and `IsFeatureUpdate` are Windows fields and come with step 2.
- "Scan now" is a table of its own, `PatchScanRequests`, like `StorageScanRequests`, instead of a kind `Scan` on `PatchRequests`: the
  workers of 0.8.0 read every pending `PatchRequest` and would fail on a kind they do not know, which breaks an image rollback.
- `EndpointPatchStates` got `ScanFailingSince`: the patch state alert counts from the first failed scan while no scan ever read the
  updates, so one failed scan of a new endpoint is not an alert at once.
- The compliance counts (dashboard tile, client and site, endpoint list column) read the patch state rows as before; a Fleeto state
  whose scans never read the updates is left out of them. No separate count table.
- Whether the agent is new enough is `PatchScanRules.AgentScans` (`MinimumAgentVersion` `0.9.0-alpha.1`), outside `PatchEngineRules`.
  The configuration carries the scan interval for every endpoint whose engine is Fleeto, also with an older agent (which ignores it); the
  workers ask for the configuration again when the engine of an endpoint becomes or stops being Fleeto.
- Windows endpoints without Action1 keep the Action1 view of the Patches tab (with "Install Action1 agent") until step 2 scans them.
- apt reports a package "kept back" (phased updates, or a held package) as held; the tab says "Held". On Debian the origin is the
  label of the release (`oldstable-security`), on Proxmox VE `Proxmox Debian Repository:stable`.
- The parsers of the agent are tested on real captures of Ubuntu 24.04, Proxmox VE 8 (no-subscription) and AlmaLinux 9 (2026-11-04) and AlmaLinux 10 and the update of Ubuntu (2026-11-05,
  `agent/internal/patch/testdata/captured`), which they read without a change, and on representative output for the rest. Still to
  capture: **Debian 12 and 13** (to do when a Debian endpoint runs; none does yet), Ubuntu 22.04 and `apt-get update` of Ubuntu, a Proxmox
  VE host with the enterprise repository answering 401 and with Proxmox packages pending, `dnf versionlock`. AlmaLinux 10 was captured on 2026-11-05: it ships dnf 4, not dnf 5, and its output reads like AlmaLinux 9.
- An upgrade Ubuntu defers due to phasing is listed as held and counted as missing; whether a phased update should count is to look at
  on the test VPS.
- The load test has its scenario (`Fleeto.LoadTest --patch-scans-per-hour --patch-updates`) but has not run at 10,000 agents yet.

To check while building, in the step that needs it:

- Step 1: (done 2026-11-05: AlmaLinux 10 ships dnf 4)
  and of `apt-get -s dist-upgrade` on a Proxmox VE 9 host; how `/etc/localtime` is set on the test endpoints.
- Step 2: that the Windows Update Agent search from session 0 returns the same set as the Settings app on the test endpoints, with and
  without the Microsoft Update service; the behaviour on an endpoint a WSUS group policy covers.
- Step 3: how long "Working on updates" takes on the test Windows endpoints against the notice and the deadline; what apt prints to
  stderr during a normal upgrade (so a succeeded job does not look like a failure in the history).
- Step 4: whether a round should also count as maintenance for its endpoints while it runs (the offline alert during the restart, as
  for a restart step of a task today: not special-cased). Parked until a round has run on the test VPS.
- Later: Windows endpoints in the vulnerability overview can now come from the CVEs of missing updates for endpoints with engine
  *Fleeto* (`ROADMAP.md`, Not yet scheduled, lists it for Action1); third-party applications; feature updates; a limit per site for
  clusters.
