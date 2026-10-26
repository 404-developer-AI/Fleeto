# Fleeto — Vulnerability reporting (0.8.0)

> The design and build plan of vulnerability reporting, the first part of 0.8.0; the second part is patch management on Linux
> (`ROADMAP.md`). This file states what is decided, where the code goes and in which order it is built. Once a step is built,
> what it delivers moves into `ARCHITECTURE.md` and this file keeps only what is still open. Written 2026-10-26.

## 1. What it is, and what it is not

Two things, built in this order:

- **Security advisories on the dashboard** (§2): the warnings a national CSIRT or a vendor publishes, as information for the
  team. Not matched to endpoints.
- **Vulnerabilities on Linux endpoints** (§3 to §9): which known vulnerabilities (CVEs) affect the packages installed on a
  managed Linux endpoint, which version fixes each one, and where in a client the same vulnerability occurs. First knowing what
  is vulnerable, then patching it (later in 0.8.0, still to be discussed).

Decided with the developer on 2026-10-26:

- **Part of 0.8.0**, before patching on Linux.
- **Matched inside the instance.** The workers download the advisory feeds and match them against the inventory in Fleeto
  itself. The software inventory of an endpoint never leaves the instance: the OSV query API would send every package list to
  osv.dev (Google, United States), against "data stays in the EU" (`CLAUDE.md`, GDPR). Only public feeds come in.
- **Distribution packages only.** A package that came from another repository (nginx.org, download.docker.com, EPEL, a PPA, a
  package installed from a file) is not in the advisories of the distribution. Fleeto shows it as **not covered** rather than
  guessing from upstream versions, which on a distribution that backports fixes gives false findings. Matching those through
  NVD or GitHub advisories is not planned.
- **Shown in three places**: a Vulnerabilities tab on the endpoint, a tile on the dashboard and an overview per client and site.
  No check and no alerts in this version (the developer did not choose them; they can follow as a check like "Missing updates").
- **Ubuntu, Debian, AlmaLinux and Proxmox VE first** (Proxmox VE and Debian added by the developer the same day; Debian
  because its feed is needed for Proxmox VE anyway). Every other distribution, and a release the distribution no longer
  supports, shows "not covered", never an empty list that looks clean.

Supported releases in this version:

| Distribution | Releases | Feeds (§5) |
|---|---|---|
| Ubuntu | 22.04, 24.04, 26.04 LTS; 20.04 and 18.04 only with Ubuntu Pro (their standard support ended) | Ubuntu OVAL |
| Debian | 12 bookworm, 13 trixie | Debian security tracker |
| AlmaLinux | 8, 9, 10 | OSV `AlmaLinux` |
| Proxmox VE | 8 (Debian 12 bookworm), 9 (Debian 13 trixie) | Debian security tracker for the Debian packages (§4) |

It is not:

- A scanner of container images, snaps, flatpaks, language packages (pip, npm, gems) or binaries outside the package manager.
  They are out of scope and the tab says so.
- A scanner of the guests of a Proxmox host: a VM or container is an endpoint of its own when it runs the Fleeto agent.
- A replacement for patching: Fleeto shows the fixed version, it does not install it in this step.
- A judgement on Windows: Windows stays with Action1, which reports missing updates with their severity. Whether its
  vulnerability data can join the overview per client is checked before step 2 (§9).

## 2. Step 0: security advisories on the dashboard

Built on 2026-10-26; how it works is in `ARCHITECTURE.md` §5, Security advisories. This section keeps the decisions.

Asked for by the developer on 2026-10-26: the warnings of the Belgian Centre for Cybersecurity (CCB, `ccb.belgium.be`) on the
dashboard, so the technicians of the customer see every day which vulnerabilities were published and can start their own
communication with their clients. Built first, because it does not depend on the matching of §7. Decided the same day:

- **A panel on the dashboard only.** No email, no notification channel, no daily digest: the technicians read it in Fleeto.
- **No link to endpoints.** A warning is information for the team, never matched to the inventory, neither by CVE nor by
  product name. It stays that way when the matching of §7 exists, unless the developer decides otherwise.
- **English only, no language setting** (checked 2026-10-26): the feed declares `<language>en</language>`, the language
  paths (`/nl/advisories.xml`, `/fr/advisories`) answer 403, and no Dutch or French warning was found. The setting is not built
  (no placeholder UI); `Advisories` keeps a language column so a source in another language needs no migration.
- **A source is a kind, the CCB the only one in 0.8.0** (decided 2026-10-26). `Advisories` carries its source, so another source
  is a parser, not a redesign; an admin switches sources on and off in Settings once there is more than one. Candidates for a
  later version, each built only once it has a usable feed and its terms of use allow fetching it: the **Proxmox security
  advisories** (`PSA-YYYY-NNNNN-R`, forum threads in `forum.proxmox.com/forums/security-advisories.26/`, no machine-readable feed;
  they cover the Proxmox management stack, its kernel and QEMU, which the Debian feeds of §4 do not), **NCSC-NL** (publishes
  CSAF, so no scraping) and ENISA's **EUVD**.

How it works (CCB):

- **Fetching** (checked 2026-10-26 with a User-Agent that names Fleeto): the RSS feed `https://ccb.belgium.be/advisories.xml`
  answers 200 and holds the 10 most recent warnings, each with a title, a link, a `guid` (the link) and a publication date; its
  `description` is the fixed text "CCB Advisories", so there is no summary. Every other path of the site, the warning pages and
  `robots.txt` included, answers 403 from the bot protection of its CDN to a client that is not a browser. Fleeto does not get
  around that: it **reads the RSS feed only**, every hour, like `IpFeedService` (timeouts, retries with backoff, the circuit
  breaker), and shows the title, the date and the link. CVE identifiers and a summary are not shown for the CCB until it offers
  them in a form Fleeto may fetch (§12). The source id is the `guid`; a warning the feed drops stays in Fleeto until its
  retention ends, because the feed holds only 10 items.
- **Untrusted input**: everything from a source is shown as plain text, never as HTML or Markdown, and the only link Fleeto
  renders is the one to the warning on the host of its source (`ccb.belgium.be`), checked against that host.
- **The panel** on the dashboard: the warnings of the last 30 days, newest first, each with its source, title, date, CVE
  identifiers (when its source gives them, which the CCB does not) and a link. A warning published since the user last opened
  the panel is marked new (per user, stored on the user, not in the browser).
- **Handled with a comment**: a technician marks a warning as handled with a required comment ("clients with pgAdmin
  emailed"), shown under it, like the comment on an alert action. The panel shows who handled it and when. Instance-wide: a
  warning belongs to no client. The audit log holds the length of the comment, never its text.
- **Permissions**: `advisories.view` (admin, technician and read-only by default) and `advisories.handle` (admin and technician).
- **Data**: `Advisories` (source, source id, language, title, summary when the source gives one, published, CVE identifiers,
  link, fetched at) and `AdvisoryHandlings` (advisory, user, comment, handled at). Kept 13 months.
- **No Settings page in 0.8.0** (decided while building, 2026-10-26): with one source there is nothing to switch, so the panel
  itself states the age of its data and why the last fetch failed, and the instance health problem below warns admins. Switching
  sources on and off comes to Settings with the second source.
- A feed that cannot be fetched for 48 hours is an instance health problem; the panel shows the age of its data.
- **Public API**: reading the warnings goes on `API-WAITLIST.md` in the commit of this step.
- **Vocabulary**: "advisory" and "vulnerability" are not in `branding-fleeto.md` §6 yet; added there in the commit of this step.

## 3. Why the distribution's own advisories

Ubuntu, Debian and AlmaLinux backport security fixes into the version they shipped. `nginx 1.18.0-6ubuntu14.6` on Ubuntu 22.04
is patched for CVEs that NVD lists as affecting "1.18.0". Comparing upstream versions against NVD would report them as open.
The advisories of the distribution state per release which package version fixes each CVE, in the version scheme of that
distribution, so a comparison with dpkg or rpm rules is exact.

## 4. Proxmox VE

Proxmox VE is Debian with Proxmox's own repository on top (`download.proxmox.com`, the enterprise or the no-subscription
repository; the `Origin` of both is `Proxmox`). `/etc/os-release` says `debian`; the agent recognises Proxmox VE by the installed
`pve-manager` package and reports its version (§6).

- **The Debian packages** (openssl, openssh, glibc, ...) are matched against the Debian security tracker of the Debian release
  under it. Proxmox itself refers to Debian for these.
- **The Proxmox packages** (`pve-manager`, `qemu-server`, `pve-qemu-kvm`, `proxmox-backup-client`, ...) have no machine-readable
  advisories and show as not covered, with their origin `Proxmox`. Their advisories can reach the dashboard panel of §2.
- **The kernel** of Proxmox VE (`proxmox-kernel-*`, `pve-kernel-*` on older releases) is built by Proxmox from the Ubuntu kernel,
  not from the Debian one: Debian's kernel advisories do not apply to it, so it shows as not covered, never matched as `linux`.
- The guests are not scanned (§1). Hosts discovered through the Proxmox integration, without an agent, get no findings.

## 5. Sources

| Source | What it gives | How the workers fetch it | Refresh |
|---|---|---|---|
| Ubuntu OVAL, per release | Per CVE and release: the source package with its pocket (`jammy`, `esm-infra/focal`, `esm-apps/focal`), the first fixed version, the state ("is affected and has been fixed", "might be affected and may need fixing"), Ubuntu's priority, CVSS where known, the USN, and the kernels with their fixed versions | `security-metadata.canonical.com/oval/com.ubuntu.<release>.cve.oval.xml.bz2`, only for the releases endpoints of the instance run | Every 6 hours |
| OSV, ecosystem `AlmaLinux` | Per errata (`ALSA-*`) and release: the **binary** packages, the fixed version, the CVEs it fixes (`related`), the severity only as the first word of its summary ("Important: openssh security update") | `https://storage.googleapis.com/osv-vulnerabilities/AlmaLinux/all.zip`, then `AlmaLinux/modified_id.csv` | Every 6 hours, full download weekly |
| Debian security tracker | Per source package, CVE and release: the status (resolved, open, undetermined), the fixed version, the urgency (`unimportant`, `end-of-life`, ...) and `nodsa` | The JSON of `security-tracker.debian.org/tracker/data/json` | Every 6 hours |
| CISA KEV | CVEs that are actively exploited | The JSON feed of the catalog | Daily |
| EPSS | Per CVE the probability of exploitation in the next 30 days | The daily CSV | Daily |

**Ubuntu from OVAL instead of OSV and VEX** (decided 2026-10-26, measured while building step 1): the OSV zip of Ubuntu is 791 MB
and needs the VEX tarball (72 MB, every release) for the status, per instance, and a VPS holds several. The OVAL file of one
release has all of it in one place: 12 MB for jammy, 11 MB for noble, 4 MB for resolute, 8 MB for focal (bzip2; about 300 MB of
XML once unpacked, so it is read as a stream, never loaded). What it does not have, checked against VEX for the CVEs of 2025 on
jammy: OVAL knows two states. "Under investigation" (deferred, needs-triage) and "affected" both read "might be affected and may
need fixing", and "not affected" is left out. On Ubuntu Fleeto therefore shows a CVE Ubuntu is still investigating as "no fix
yet", which is not fixable and so does not make an endpoint look bad, rather than in the "Not open" group of §7. A fix that
needs Ubuntu Pro is a package in an `esm-*` pocket.
The files exist only as bzip2, and .NET has no bzip2 decoder: the workers use `BZip2Stream` of SharpCompress (decided
2026-10-26), a maintained MIT package, referenced by fleeto-workers only.

**Debian borrows Ubuntu's priority** (decided 2026-10-26): the urgency of the security tracker is "not yet assigned" for about 80%
of the CVEs, and its JSON carries no CVSS, so most findings on Debian and Proxmox VE would be Unknown. Where Debian has no urgency,
Fleeto takes the priority Ubuntu gave the same CVE and shows it as such ("Ubuntu: high"); the workers then fetch the OVAL file of
the newest Ubuntu LTS release also when no endpoint runs Ubuntu.

**AlmaLinux module streams** (revised 2026-10-26 after the real output of an AlmaLinux 8.5 endpoint): OSV has no field for the
stream, but the summary of every security errata of a module build names it (`Important: virt:rhel and virt-devel:rhel security
update`: all 476 of AlmaLinux 8 and 124 of AlmaLinux 9 in the zip of 2026-10-26), and the parser keeps those streams with the record.
`%{MODULARITYLABEL}` turned out to be empty on all 59 module packages of the test endpoint, so the agent also reports the streams
enabled in `/etc/dnf/modules.d`. A module build is compared only with the errata of its stream: the one of its label when it has
one, otherwise the ones enabled on the endpoint (podman of `container-tools:rhel8` never with an errata of `container-tools:3.0`).
A module build whose module has no stream enabled, or on an endpoint whose agent reports no streams, is not covered rather than
shown clean. A package outside streams is compared only with fixed versions outside module builds. An errata of a module build
whose summary names no stream falls back to the version of the stream (`nginx:1.20` with fixed versions starting with `1.20`).

Checked on real records on 2026-10-26 (`UBUNTU-CVE-2024-6387`, `ALSA-2024:4312`, `DEBIAN-CVE-2024-6387`):

- **OSV carries no triage status.** An Ubuntu or Debian record has `introduced` and `fixed` events and nothing for "not
  affected", "deferred" or "ignored"; a release missing from `affected[]` can mean any of them. That is why Ubuntu was planned with its VEX
  data (now OVAL, above) and Debian uses the security tracker, which has the status and the urgency (`DEBIAN-CVE-*` in OSV says "not yet
  assigned" for urgency where the tracker knows it).
- **Ubuntu and Debian list source packages** (`openssh`, not `openssh-server`); Ubuntu adds `ecosystem_specific.binaries` with the
  binary names and versions and `ecosystem_specific.availability` for a fix that needs Ubuntu Pro. The ecosystem string carries
  the release: `Ubuntu:22.04:LTS`, `Ubuntu:Pro:18.04:LTS`, `AlmaLinux:9`.
- **AlmaLinux lists binary packages** (`openssh`, `openssh-clients`, `openssh-server`, ...), with no source package and no
  structured severity or CVSS. Matching on AlmaLinux is therefore by binary name (§7).
- **AlmaLinux publishes only what is fixed.** An errata exists once a fixed package is released, so on AlmaLinux Fleeto knows
  missing security updates, not vulnerabilities without a fix. The tab says so (§9). Red Hat's VEX data (CSAF) has the unfixed
  state of RHEL, but is not applied to AlmaLinux (decided 2026-10-26): it is data of another distribution, AlmaLinux sometimes
  fixes earlier or differently, and a vulnerability without a fix is not something a technician can patch. It can follow later.
- Ubuntu's OVAL has one definition per CVE and release (the CVE files, not the USN files), so a CVE is never counted twice; the
  USN that fixed it is in its references and shown as one. Like OSV it names source packages, so Ubuntu is matched on the
  source package as planned.

How they are fetched:

- The exact URLs and formats of the VEX tarball, the security tracker, KEV and EPSS are checked when building; EPSS moved host
  in 2025. KEV has a fallback in ENISA's EUVD, which also lists exploited vulnerabilities, in case the CISA catalog stops (its
  funding was in doubt in 2025).
- Only the releases that endpoints of the instance run are stored: a feed record for Ubuntu 16.04 is skipped when no endpoint
  runs it.
- **A zip cannot be read record by record from the network**: its directory is at the end. The workers download it to a file on
  a scratch volume (like the one of `pg_dump`, not the `/tmp` tmpfs) and then read it entry by entry without extracting it.
  Its size is measured when building step 1.
- The workers fetch like `IpFeedService`: timeouts, retries with backoff, the circuit breaker, a feed that looks broken (empty,
  far smaller than the previous one, unparseable) is refused and the previous data stays. A feed that cannot be fetched is an
  instance health problem after 48 hours; the findings stay and show their age.
- NVD is not used: its API is rate limited, its backlog left many CVEs without analysis in 2024 and 2025, and the distribution
  feeds already carry what Fleeto needs.

## 6. Agent

The inventory of 0.1.0 reads dpkg and rpm but sends only name, version and publisher. Matching needs more. Additive fields in
`agent.proto`, so an older agent keeps working and its endpoint shows "Update the agent to see vulnerabilities":

| Field | Ubuntu, Debian, Proxmox VE (dpkg) | AlmaLinux (rpm) |
|---|---|---|
| `SoftwareItem.source_name` | `${source:Package}` | the name part of `%{SOURCERPM}` |
| `SoftwareItem.source_version` | `${source:Version}` (can differ from the binary version) | the version-release part of `%{SOURCERPM}` |
| `SoftwareItem.epoch` | the epoch of `${Version}` (dpkg keeps it inside the version) | `%{EPOCHNUM}` |
| `SoftwareItem.architecture` | `${Architecture}` | `%{ARCH}` |
| `SoftwareItem.origin` | the origin decided from the apt lists (below) | `%{VENDOR}` |
| `SoftwareItem.module_stream` | empty | `%{MODULARITYLABEL}` (AlmaLinux 8 modules; empty in practice, §5) |
| `PackageInfo.module_streams` | empty | the enabled streams of `/etc/dnf/modules.d/*.module`, as `name:stream` |
| `OsInfo.distribution_id` | `ID` from `/etc/os-release` (`ubuntu`, `debian`) | `ID` (`almalinux`) |
| `OsInfo.distribution_version` | `VERSION_ID` (`22.04`, `12`) | `VERSION_ID` (`9.4`, matched on the major version) |
| `OsInfo.distribution_variant` | `proxmox-ve` with the version of `pve-manager` | empty |
| `OsInfo.ubuntu_pro_services` | the enabled Pro services (`esm-infra`, `esm-apps`), from `/var/lib/ubuntu-advantage/status.json` | empty |
| `OsInfo.livepatch` | whether Canonical Livepatch is active, and the fixes it applied | whether kpatch is active |
| `OsInfo.restart_required` | `/var/run/reboot-required` exists | `needs-restarting -r` says so |

- **Origin on Ubuntu and Debian, by name.** The apt lists in `/var/lib/apt/lists` hold per pocket only the newest version, so
  an installed version that has been superseded (exactly the vulnerable one) is in no list. The agent therefore decides by
  package name: it reads each `*_Packages` file with the `Origin` of its `Release` file. A package whose name only appears
  under `Ubuntu`, `UbuntuESM`, `UbuntuESMApps` or `Debian` is covered. A name that also appears under another origin (`nginx`,
  `Docker`, a PPA, `Proxmox`) belongs to the origin whose version matches the installed one, or else to the other origin when
  the installed version is newer than the distribution's; that rule is tested on real cases. A name in no list (a `.deb`
  installed from a file) is not covered. The maintainer field cannot decide it: a package synced unchanged from Debian keeps
  its Debian maintainer.
- **Lists missing or old**: an endpoint without apt lists (minimal images remove them) shows "Package lists missing; run apt
  update", never "not covered" for every package. Lists older than 30 days are shown on the tab.
- **Cost**: the lists are parsed again only when one of their files changed (mtime and size), so an inventory run without an
  apt update reuses the result. Measured on a desktop with many repositories when building.
- **Origin on AlmaLinux**: `%{VENDOR}` is set when a package is built. `AlmaLinux` and `CloudLinux` are covered: the early
  AlmaLinux 8 builds came from the build system of CloudLinux and carry its name (322 of 814 packages on the test endpoint).
  `CentOS` (left from a conversion), `nginx inc.`, `Docker`, `Fedora Project` (EPEL) and an empty vendor are not.
- **Kernels**: several kernel packages are installed at once (apt keeps two, dnf three). The agent reports which one runs
  (`uname -r` mapped to its package).
- The software list stays capped at 5,000 items. The hash of the inventory covers the new fields, so a changed origin is sent.

## 7. Matching in the workers

- **Version comparison** in C#, ported from the reference implementations and tested against their test sets: dpkg's
  `verrevcmp` (epoch, upstream version, revision, the `~` rule) and rpm's `rpmvercmp` (with epoch and release, and `^` and `~`).
  These two are the only place where a version is compared; a wrong comparison is a wrong finding, so the test set includes
  the edge cases of both projects.
- **What is compared**: on Ubuntu and Debian the source package with its source version, since their feeds speak in source
  packages; on AlmaLinux each installed binary package with its version against the errata that names it, then grouped by
  source package for display. On AlmaLinux 8 a package of a module stream is only compared with errata of the same stream.
- **Index**: per ecosystem and release, per package, the affected ranges and the fixed version of every advisory.
- **An endpoint is matched** when its inventory changes (the hash changes) and when a feed changes a package it has. A
  feed update recomputes only the endpoints that have a changed package, found through an index of `(release, package)` to
  endpoints that the workers keep from the stored inventory.
- **A finding is one CVE on one source package of one endpoint**, so KEV and EPSS (per CVE) apply directly and the overview
  counts the same unit on Ubuntu, Debian and AlmaLinux. It holds the installed version, the fixed version (or "no fix yet" when
  the distribution lists it as affected without a fix, or "fix only in Ubuntu Pro"), the advisory that fixes it (USN, DSA,
  DLA, ALSA) as a reference, the severity, the binary packages from that source that are installed, first seen, and how it
  ended.
- **How a finding ends**, each kept apart so the history never lies: *fixed* (a later inventory has the fixed version),
  *removed* (the package is gone), *withdrawn* (the feed no longer lists it, or now marks it not affected). Only fixed counts as
  remediated.
- **Severity on one scale**, with the distribution's own word shown next to it:

  | Fleeto | Ubuntu priority | Debian urgency | AlmaLinux errata |
  |---|---|---|---|
  | Critical | critical | (from CVSS ≥ 9.0) | Critical |
  | High | high | high | Important |
  | Medium | medium | medium | Moderate |
  | Low | low, negligible | low, unimportant | Low |
  | Unknown | untriaged | not yet assigned | — |

  Debian assigns urgency to few CVEs; where it has none the CVSS score from the record decides, and otherwise it is Unknown.
  The CVSS score is shown where a record carries one. KEV and EPSS are shown next to it and sort the list.
- **Fixable and not fixable** are counted apart (decided 2026-10-26, also for a fix only Ubuntu Pro ships; the tab shows how many
  of those an endpoint has, as the case for Pro towards the client): a fix the endpoint can install, and "no fix yet" or "fix only in Ubuntu Pro"
  on an endpoint without the Pro service that ships it. A default Ubuntu installation has hundreds of low CVEs without a fix;
  they are shown but do not make an endpoint look as bad as one missing a critical fix.
- **Not open on purpose**: CVEs the distribution marks as not affecting the release, those under investigation ("deferred",
  "needs-triage") and those it decided not to fix ("ignored", Debian's `unimportant` and `end-of-life`) are shown in a separate
  collapsed group with their status, not counted as open.
- **Kernels**: only the running kernel and the newest installed one are matched; older installed kernels get no findings.
  Kernel CVEs (several thousand a year since the kernel became a CNA in 2024) form a group of their own on the tab. A fix
  applied by Livepatch or kpatch is counted as fixed for the running kernel; "Restart required" is shown when the running
  kernel is older than the newest installed one.
- **Release no longer supported**: Fleeto keeps a table of the end of standard support and of extended support per release.
  An endpoint on a release past it (or past standard support without the Pro service that extends it) shows "Release no longer
  supported" on the tab and in the summary, never a clean list.

## 8. Data model

New tables in the naming of the existing ones, each client-owned table with its own `ClientId` and composite foreign keys
(`CLAUDE.md`, multi-tenancy):

- `VulnerabilityAdvisories`: per feed record (`UBUNTU-CVE-2024-1234`, `ALSA-2024:1234`, a Debian tracker entry): ecosystem,
  release, summary, severity, CVSS, published and modified, its CVEs, its status where the feed has one. Instance-wide, not
  client-owned.
- `VulnerabilityAffected`: per advisory, release and package the affected ranges, the fixed version, whether the fix needs
  Ubuntu Pro (and which service) and the module stream.
- `VulnerabilityExploits`: per CVE the KEV flag with its date and the EPSS score with its date.
- `EndpointVulnerabilities`: the findings of §7 (`ClientId`, `SiteId`, `EndpointId`, CVE, source package, installed and fixed
  version, severity, fixable, state, first seen, ended at, how it ended).
- `EndpointVulnerabilitySummaries`: per endpoint the counts per severity, fixable and not, KEV, "not covered", whether the release
  is still supported, the time of the last match and of the feeds it used. The dashboard tile and the overview per client read
  this table, never the findings.
- `VulnerabilityFeeds`: per source last fetched, last changed, record count, last error.

Retention: open findings are current state; ended findings are kept 13 months for the History tab and the overview, under a
retention setting of their own. Deleting an endpoint or client removes its findings with it.

## 9. UI

- **Vulnerabilities tab** on a managed Linux endpoint: the open findings, sorted by KEV, severity and EPSS, grouped by source
  package with the binary packages under it, the kernel as a group of its own, with the fixed version and a link to the
  advisory of the distribution. Collapsed groups for "Not open" (§7) and "Not covered" (packages from other repositories with
  their origin). A banner states when the feeds were last updated, when the endpoint was last matched, and where it applies:
  "Release no longer supported", "Package lists missing", "Restart required", and on AlmaLinux "Only security updates that
  AlmaLinux has released are shown". A Windows endpoint has no tab (Patches covers it).
- **Overview per client and site**: per CVE the number of affected endpoints, filterable on severity, fixable and KEV, opening
  the list of those endpoints.
- **Windows in the overview** (decided 2026-10-26): Action1 shows vulnerabilities with their CVEs per organization in its
  console; whether its API offers them, and with which permission of the credentials, is checked before step 2. If it does, the
  workers read them within the Action1 budget of `CLAUDE.md` and the overview counts Windows and Linux endpoints per CVE
  together, with the Windows findings coming from Action1 and linking to the Patches tab; the Vulnerabilities tab stays Linux
  only. If it does not, the overview is Linux only and says so.
- **Dashboard tile**: managed Linux endpoints with an open fixable critical or actively exploited finding, and those on a
  release no longer supported, linking to the overview.
- **Permissions** (`Permissions`): `vulnerabilities.view`, given to admin, technician and read-only by default. The groups decide
  where, like every other endpoint tab.
- **Settings, Vulnerabilities** for admins: the state of each feed, a "Refresh now", and the retention of ended findings.

## 10. Licensing, security, API

- **Managed endpoints only** (decided 2026-10-26): an agent-only endpoint keeps its inventory but gets no findings, in line
  with Patches and "Missing updates", which are managed only. Matching skips it, and its findings are removed when it is
  switched to agent-only.
- Nothing from the endpoint leaves the instance; the feeds are public data. Text from a feed is shown as text, never as HTML or
  Markdown, since a feed is untrusted input.
- The audit log records the manual refresh and changes in Settings. Viewing findings is not audited (no personal data).
- **Public API**: reading the findings of an endpoint and the overview of a client goes on `API-WAITLIST.md` in the commit of
  step 2, or into `API.md` if it is built in the same step.

## 11. Build plan

Each step is an alpha on the test VPS; the version is released as a whole with patching on Linux.

0. **Security advisories** (§2), built 2026-10-26: the CCB feed in the workers with a parser tested on saved warnings, the tables, the dashboard
   panel with "new" and "handled", the two permissions, the language setting if the CCB publishes in more than one, the instance
   health problem for a stale feed, the API waiting list entry and the vocabulary.
1. **Agent and matching**, built 2026-10-26 (how it works: `ARCHITECTURE.md` §5, Vulnerabilities on Linux
   endpoints; deviations below): the new inventory fields with tests on real `dpkg-query`, apt list and `rpm` output of Ubuntu 22.04,
   24.04 and 26.04, Debian 12 and 13, Proxmox VE 8 and 9 and AlmaLinux 8, 9 and 10; the feeds of Ubuntu (OSV and VEX), AlmaLinux and Debian; the
   dpkg and rpm version comparison with the reference test sets; the severity mapping and the release support table; the
   tables, migrations (expand only) and matching in the workers; the summary table; the instance health problem for a stale
   feed; a load test of matching at 10,000 endpoints.
2. **UI**: the endpoint tab, the overview per client and site (with the Windows findings of Action1 if its API offers them,
   §9), the dashboard tile, the permission, Settings, Vulnerabilities, and the API waiting list or API entries.
3. **Exploitation and age**: KEV (with the EUVD fallback) and EPSS, sorting on them, the instance health problem for these two
   feeds, the CVSS score on the advisories panel, retention of ended findings, and the History tab entries.

Deviations of step 1 from §6 to §8, decided while building:

- The distribution, Pro services, Livepatch, restart and apt list fields are a message of their own on the inventory
  (`Inventory.packages`), not on `OsInfo`: the agent reads `OsInfo` once per process, and a restart or an apt update changes them
  without one. The running kernel is the kernel version the agent already reports.
- On Ubuntu only the running kernel is matched (OVAL describes a kernel by what `uname -r` reports), not also the newest installed one;
  "Restart required" shows when the newest is not running.
- The records are one table (`VulnerabilityFeedRecords`, one row per CVE, package and release) instead of `VulnerabilityAdvisories`
  and `VulnerabilityAffected`, and `VulnerabilityCves` holds Ubuntu's priority per CVE; KEV and EPSS (step 3) join it there.
- No index of (release, package) to endpoints: a feed change matches every endpoint of the release again, with the records of the
  release held in memory. Measured on 2026-10-26 with `VulnerabilityMatchLoadTests` (10,000 managed endpoints, half Ubuntu 22.04, a
  quarter Debian 12 and a quarter AlmaLinux 9, 60,000 records per feed, about 310 findings per endpoint, PostgreSQL 17 on the
  developer's PC): the first match of every endpoint takes 5 minutes (34 endpoints a second), a changed Ubuntu feed matches its 7,500
  endpoints again in 80 seconds, 1,000 changed inventories take a minute, and a pass with nothing due 80 ms. Fast enough for a loop in
  the background, so the index was not built. The first run measured 17 minutes; new findings are now written with COPY and the ones
  that stay the same are not tracked by EF Core. What is left is mostly the check of the composite foreign key to `Endpoints`, about
  125 µs a row, which stays (tenant separation). 3.1 million findings take 1.1 GB with their indexes and the feed records 35 MB.
- The AlmaLinux zip is read from memory (about 7 MB, at most 256 MB), not from a file on a scratch volume.

## 12. Open

Every question was decided on 2026-10-26 (§2, §5, §7, §9, §10). What is left:

- **Step 1, real output of the other releases**: the parsers are tested on the real output of Ubuntu 24.04, Proxmox VE 8.4
  (Debian 12) and AlmaLinux 8.5 (captured 2026-10-26, `agent/internal/inventory/testdata`). On the Proxmox VE host every package
  is Debian's (613) or Proxmox's (103), none local, and the packages Proxmox rebuilt (`grub2`, `open-iscsi`, `smartmontools`, `frr`)
  go to Proxmox by their installed version. Ubuntu 22.04 and 26.04, a plain Debian 12 and 13, Proxmox VE 9 and AlmaLinux 9 and 10
  still need a capture, and so does a real `/etc/dnf/modules.d` (its test uses the shape dnf writes). A plain Debian first: Ubuntu
  keeps older kernel ABIs (`linux-image-6.8.0-53-generic`) in its lists, so the running kernel of the test endpoint is the
  distribution's; whether Debian does too, or an older running kernel ends up "local" and not covered, is not known yet (the Proxmox
  VE host runs the Proxmox kernel).
- **To check before building**:
  - Step 0 (checked 2026-10-26, §2): the CCB publishes in English only, and only its RSS feed is reachable for Fleeto. Its
    disclaimer reserves copying a substantial part of the site to its prior written permission. The developer asks the CCB
    whether Fleeto may show the titles and links of the feed, and whether it offers the CVE identifiers in a feed Fleeto may
    fetch; until it answers, step 0 is built with the RSS items only (decided 2026-10-26).
  - Step 2: whether the Action1 API offers its vulnerability data (§9).
- **Later, not in 0.8.0**:
  - A check with an alert ("Vulnerabilities older than N days of severity X or KEV"), decided after the tab has been used on the
    test VPS; it would work like "Missing updates", evaluated by the workers after every match.
  - Other sources of the advisories panel (§2).
  - Red Hat's VEX data for AlmaLinux (§5).
- **With patching on Linux**: how vulnerability reporting hands over to it (the fixed version is what a deployment would
  install), discussed when patching on Linux is designed.
