# Patch scan test data

Output of the commands a Linux patch scan runs (`LC_ALL=C`), used by `parse_test.go` and `engine_test.go`.

**Windows has no files here.** A Windows scan reads the Windows Update Agent through COM (`scan_windows.go`); everything after that
(the source decision from the registry, the mapping of an update to `PatchUpdate`, the classification by category id, the HRESULT of
a failed search, the DECIMAL of `MaxDownloadSize` and the DATE of `LastDeploymentChangeTime`) is pure code in `wua.go`, tested in
`wua_test.go` with a fake session and registry on every platform. The updates in those fakes are representative: titles, KB numbers
and category ids in the shape the Windows Update Agent gives them (a cumulative update, a feature update, a Defender definition
update, a driver, a classification name in Dutch that the id turns into English). `scan_windows_test.go` reads the real Windows
Update Agent of the machine it runs on, read-only, only with `FLEETO_WUA_LIVE=1`: one search for missing updates exactly as a scan
does, and one for installed updates, so the VARIANT handling is checked on real values also when nothing is missing (2026-11-05 on
the development PC, Windows 11 with Microsoft Update registered: no missing updates; 20 installed updates, each with a release date,
a size and the English classification). Still to check on the test endpoints: a Windows Server with missing updates, an endpoint a
WSUS group policy covers, and the search as SYSTEM in session 0.

| Directory | Commands |
|---|---|
| `ubuntu-22.04` | `apt-get -q update`, `apt-get -s -q -o Debug::NoLocking=1 dist-upgrade` (security, updates, ESM Apps, a new dependency, a removal, a foreign architecture, kept back), `apt-mark showhold`, `dpkg-query -W` |
| `ubuntu-24.04` | `apt-get -s ... dist-upgrade` with upgrades deferred due to phasing (apt 2.7) |
| `debian-12` | `apt-get -s ... dist-upgrade` (Debian-Security, new kernel) |
| `proxmox-ve-8` | `apt-get -q update` with the enterprise repositories answering 401 without a subscription, `apt-get -s ... dist-upgrade` from pve-no-subscription |
| `almalinux-9` | `dnf -q check-update` (a wrapped long name, an Obsoleting Packages section), `dnf -q updateinfo list --updates` (dnf 4), `rpm -q --qf`, `dnf -q versionlock list` (dnf 4 plugin), the stderr of a repository that cannot be read |
| `dnf5` | `dnf -q check-update`, `dnf -q updateinfo list --updates` and `dnf -q versionlock list` of dnf 5, which no supported operating system ships yet (AlmaLinux 10 has dnf 4); kept so the parsers stay tolerant of it |

**`captured/` holds real output** of the test endpoints (2026-11-04, `LC_ALL=C`), tested in `captured_test.go`:

| Directory | Commands |
|---|---|
| `captured/ubuntu-24.04` | `apt-get -s -q -o Debug::NoLocking=1 dist-upgrade` (security and updates, two origins, an upgrade deferred due to phasing, the autoremove hint), `apt-mark showhold` (empty) |
| `captured/ubuntu-24.04` (2026-11-05) | `apt-get -q update` with every list read |
| `captured/proxmox-ve-8` | `apt-get -q update` on the no-subscription repository with every list read, `apt-get -s ... dist-upgrade` (Debian-Security only, epochs), `apt-mark showhold` (empty) |
| `captured/almalinux-10` | `dnf -q check-update` and `dnf -q updateinfo list --updates` (2026-11-05): AlmaLinux 10 ships **dnf 4**, so the output has the shape of AlmaLinux 9 |
| `captured/almalinux-9` | `dnf -q check-update` (a leading empty line, repositories named `baseos-proxy` and `appstream-proxy`, an epoch), `dnf -q updateinfo list --updates` (dnf 4) |

Not captured yet, so still only representative: Ubuntu 22.04, **Debian 12 and 13** (no Debian endpoint runs
yet), a Proxmox VE host with the enterprise repository answering 401, Proxmox packages in the simulation, `rpm -q` and `dnf versionlock`.

**The other files are representative, not captured.** They were written from the documented formats of apt (`pkgSimulate`, which prints
`Label:Version/Archive` per origin), dnf 4 and dnf 5, with realistic package names and versions, and must be replaced by real
captures from the test endpoints (an Ubuntu server, a Proxmox VE host on the no-subscription repository, an AlmaLinux 9 and an
AlmaLinux 10 VM) before 0.9.0 is released, as `agent/internal/inventory/testdata` was. Least certain, verify first:

- **dnf 5** (`dnf5`): the column layout of `check-update`, whether `updateinfo` is still accepted as an alias of `advisory`
  together with `--updates`, the columns of `advisory list` (`Name Type Severity Package Issued`), and the format of `versionlock list`.
  The parsers are tolerant (fields, not columns), but the exact lines are not confirmed.
- `versionlock` together with `check-update`: a version lock normally hides the package from `check-update`, so the test data of
  AlmaLinux 9 lists `tzdata` in both only to exercise the held flag.
- Proxmox VE: the label of its repository (`Proxmox Debian Repository`, from the InRelease file in `inventory/testdata/proxmox-ve-8.4`)
  as the simulation prints it.
