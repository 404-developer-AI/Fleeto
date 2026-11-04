# Proxmox web interface (0.8.0)

The real web interface of Proxmox VE, opened from Fleeto on a managed Proxmox VE endpoint, through the agent, without a VPN and
without a port open on the host. Nothing is rebuilt: the technician's browser shows the pages of the host's own `pveproxy`, with every
console, upload and setting as when they browse to `https://<host>:8006`. Decided with the developer on 2026-10-27; this file is the
design, built on 2026-10-27 for the test build `v0.8.0-alpha.6`; `MD-Files/ARCHITECTURE.md` §4 and §5 describe it as part of the
remote sessions.

## 1. Decisions (2026-10-27)

- **A tunnel through a helper on the technician's PC** (option 1 of three). The browser cannot speak TLS over a WebSocket, so a small
  program, **Fleeto Connect**, runs on the technician's PC, opens a local address for the browser and carries its connections through the
  remote session relay to the agent, which connects to `127.0.0.1:8006` on the host. Rejected: a reverse proxy on a subdomain of the
  instance (the gateway would read every page and the Proxmox password, which breaks the end-to-end rule of remote sessions), and a
  browser streamed through remote control (a Proxmox host has no desktop).
- **Proposed when it is missing.** Opening the web interface without Fleeto Connect installed shows how to install it for the technician's
  operating system and then tries again. Fleeto Connect exists for **Windows** (10 and 11, x64) and **Ubuntu desktop** (24.04 and newer, x64).
- **Place on the roadmap**: the second part of 0.8.0, after vulnerability reporting and before patch management on Linux.
- **Windows without Authenticode for now.** The integrity of Fleeto Connect comes from the release manifest signed with the Steaan
  release key, like the agent binaries, and the download page shows the SHA-256. SmartScreen warns on the first run until Steaan has a
  code signing certificate, which stays an open item on the roadmap.
- **Ubuntu as a `.deb`** downloaded from the instance and installed with apt (one sudo), which registers the `fleeto-connect:` link.
- **Permission "Proxmox web interface"** (`remote.proxmox`), in the Remote group; admin and technician by default, read-only not.

## 2. What it is and is not

- Only **managed** endpoints whose agent reports Proxmox VE (its operating system name starts with `Proxmox VE `), with an agent new
  enough to serve it, on which `/usr/bin/pveproxy` exists.
- The agent connects to **one fixed target**: `127.0.0.1:8006` (then `[::1]:8006`). Never another host or port, never a target named by
  the server, the helper or the technician: a tunnel to a host and port of choice would be a way into the client's network.
- The technician signs in to Proxmox with their own Proxmox account (or Proxmox's own SSO). Fleeto never stores, sends or fills in a
  Proxmox credential, and never sees one: the TLS of `pveproxy` runs between Fleeto Connect and the host, inside the end-to-end encryption
  of the session.
- Not a way to reach the web interface of a VM or another device behind the host. A cluster is reached through any of its hosts, as in
  Proxmox itself.

## 3. The flow

1. **Open.** The technician chooses "Proxmox web interface" in the right-click menu of the endpoint list. Web checks the permission, the
   groups, the tier, Proxmox VE and that the agent is online and new enough, then writes a `RemoteSessionClaim` (a secret of 256 bits,
   stored as its SHA-256, valid 50 seconds, one use) and the audit entry `remote_session.requested`. A popup window like the remote sessions
   opens and starts `fleeto-connect://open?instance=<host of the web URL>&claim=<claim id>.<secret>`. No session or participant exists yet.
2. **Fleeto Connect is missing.** The window follows the claim every second. When nothing claimed it after 8 seconds it shows the downloads,
   the one for the operating system of the browser first, with the steps, the SHA-256 and "Try again", which opens a new claim. Whether
   Fleeto Connect is there is decided by the server (was the claim taken), not by what a browser says about a link it could not open; a claim
   taken later than 8 seconds (the technician answered a question first) still connects.
3. **Claim.** Fleeto Connect generates an ephemeral X25519 key pair and posts the claim, its public key, its version and platform to
   `https://<fqdn>/connect/v1/claim` (web, anonymous, 20 a minute per address, the instance's public TLS certificate checked by the system
   trust store). Web compares the hash of the secret in constant time, checks its age, the version of Fleeto Connect and the key, and takes
   the claim once (the update that sets `ClaimedAt` wins). It checks the user of the claim again from the database as for a signed-in user
   (exists, not locked out, roles, groups) and the endpoint as at the start, then writes the `RemoteSession` of kind `ProxmoxWeb` (served by
   the agent), the participant with the id of the claim and the key of Fleeto Connect, the `SigningRequest` and the audit entry
   `remote_session.claimed` (version, platform and address of Fleeto Connect), through the same code as a remote session opened in the
   browser, and waits for the signer. The single-use and immutable binding rules of remote sessions therefore apply unchanged. A Fleeto
   Connect older than `RemoteSessionRules.MinimumConnectVersion` is refused with the way to the current one, and the link still works.
4. **Sign.** fleeto-signer signs a `RemoteSessionToken` of kind `PROXMOX_WEB` for the agent with the rules of a remote session plus the
   permission `remote.proxmox`, Proxmox VE on the endpoint and an agent of `0.8.0-alpha.6` or later. Web returns to Fleeto Connect the token,
   its signature, the fingerprints of the agent's valid certificates, the relay address (`wss://<host>/relay/v1/sessions/<participant>`, or
   `Remote:RelayUrl` in development) and the host name and id of the endpoint.
5. **Relay and keys.** Fleeto Connect accepts a relay on the instance's own host only, opens it with the instance URL as Origin, and does
   what the browser does for a remote session (ARCHITECTURE.md §4 Remote session, steps 3 to 7) with the Go implementation the agent tests
   against a hostile relay: the agent's key is accepted only by a fingerprint web gave, and every frame is AES-256-GCM.
6. **Hello.** The agent connects to `pveproxy` and reads the certificate it serves from a TLS handshake (whatever pveproxy uses:
   `pveproxy-ssl.pem`, an ACME certificate or `pve-ssl.pem`), and names its SHA-256 in the Hello of the session, with kind `proxmox_web`.
   Fleeto Connect accepts the TLS of `pveproxy` only with that certificate: no warning in the browser and no blind trust of a self-signed one.
7. **Local address.** Fleeto Connect listens on `127.0.0.1` and `[::1]` on a port the system chooses, under a host name of the endpoint below
   `localhost`: `http://<host name>-<first 8 of the endpoint id>.localhost:<port>/`. Browsers resolve every name below `localhost` to loopback
   and treat it as a secure context; one name per endpoint keeps the Proxmox sign-in cookies of two hosts apart (cookies are scoped by host,
   not by port) and lets the cookie of a host last across sessions. Plain HTTP to the browser; the encryption that matters runs from Fleeto
   Connect to the host. A request for any other host name (DNS rebinding) gets 403.
8. **Only this browser.** Fleeto Connect opens the default browser at `http://<name>.localhost:<port>/.fleeto-connect/<one-time value>`, which
   sets an HttpOnly, SameSite=Strict cookie and redirects to `/`. A request without that cookie, from another local user or another program,
   gets 403. The one-time value works once.
9. **Proxy.** Every HTTP request and WebSocket (the noVNC and xterm.js consoles) becomes a channel in the session (frames `0x20` open, `0x21`
   opened, `0x22` data, `0x23` close): the agent opens a TCP connection to `127.0.0.1:8006` (then `[::1]:8006`) per channel, Fleeto Connect
   speaks TLS to `pveproxy` over it and forwards the request with `Host`, `Origin` and `Referer` set to `127.0.0.1:8006` and its own cookie
   removed. At most 64 channels at once per session. In this kind the agent ignores every terminal, file, service, process and screen frame;
   the watchdog refuses the kind.
10. **End.** The session ends when the technician chooses "End session" in the window or closes it (web stops an unclaimed link and marks
    the participant revoked, which the gateway ends at once by notification and otherwise within a minute), when Fleeto Connect is stopped,
    when no request reached the host for the idle timeout of the policy (default 30 minutes; the Proxmox page polls while it is open, so an
    open tab counts as activity), and in every case a remote session ends (revocation, agent-only, gateway restart). Audited as
    `remote_session.requested`, `.claimed`, `.signed`, `.joined` and `.left`; never a URL, a request or a page.

## 4. Fleeto Connect

- One Go program in the agent codebase (`agent/cmd/fleeto-connect`, package `agent/internal/connect`), sharing `agent/internal/remote` for
  the token, handshake and frames. No GUI toolkit: the window in Fleeto shows the state and ends the session; Fleeto Connect asks one
  question and shows errors with a Windows message box or zenity on Ubuntu.
- **Which instances.** A link can come from any web page, so the first time an instance is named Fleeto Connect asks the technician whether
  it may connect to that host and remembers the answer per user. A page that is not the instance cannot make it connect to a server that
  shows a fake Proxmox sign-in. Without zenity the answer is no.
- **Windows**: `fleeto-connect.exe` (no console window) installs itself per user when it is started without a link (no administrator):
  into `%LOCALAPPDATA%\Programs\Fleeto Connect`, the link handler under `HKCU\Software\Classes\fleeto-connect`, and an uninstall entry
  under `HKCU` (`fleeto-connect.exe --uninstall`).
- **Ubuntu**: `fleeto-connect_<version>_amd64.deb` (`~` for the `-` of a pre-release) with `/usr/bin/fleeto-connect` and a `.desktop` file
  for `x-scheme-handler/fleeto-connect`; depends on `xdg-utils` and `desktop-file-utils`, recommends `zenity`.
- **Distribution**: built in the `connect` stage of the web image (`/app/connect`, reproducible like the agent; the `.deb` by
  `agent/packaging/fleeto-connect/build-deb.sh`), listed as `connectFiles` with their SHA-256 and size in the signed release manifest
  (older parsers ignore the field), checked in the web image by the Release workflow, and served by web at `/connect/download/windows` and
  `/connect/download/ubuntu` to users with the permission. No self-update: an outdated Fleeto Connect is refused at the claim.
- Settings and log: `%APPDATA%\Fleeto Connect\instances.json` and `%LOCALAPPDATA%\Fleeto Connect\fleeto-connect.log` on Windows,
  `~/.config/fleeto-connect/` and `~/.cache/fleeto-connect/` on Ubuntu; the log never holds the secret of a link.

## 5. Security

- Same token, relay, end-to-end encryption, single use and tier checks in web, signer, gateway and agent as the remote sessions; a token of
  kind `PROXMOX_WEB` is served by the agent only and opens no terminal, file, screen or other frame.
- The fixed target is enforced by the agent, not by anything it is told.
- **Accepted risk, the claim link.** The secret travels in the `fleeto-connect:` link, which the operating system passes to Fleeto Connect
  on its command line; on Linux another local user can read a command line. Whoever claims it first within 50 seconds gets the tunnel for
  that technician. Mitigations: one use, 50 seconds, the audit entry with the claiming address, the 403 for other local users of the address,
  and a Proxmox sign-in still needed. An attacker who runs code on the technician's PC is outside what Fleeto can defend.
- **Accepted risk, a compromised web container** can start a session as for a remote session (ARCHITECTURE.md §5 Remote control); it still
  cannot read the TLS between Fleeto Connect and the host.
- The gateway relays bytes it cannot read; it sees that a technician has the web interface of a host open, as for every remote session.

## 6. Data and API

- `RemoteSessionKind.ProxmoxWeb` (stored by name) and `REMOTE_SESSION_KIND_PROXMOX_WEB = 3` in the protocol.
- `RemoteSessionClaims` (migration `ProxmoxWebInterface`, a new table only, so the release before runs on the schema): `Id` (also the
  participant's), `ClientId`, `EndpointId` (composite foreign key to the endpoint), `UserId`, `UserName`, `Reason`, `SecretHash` (32 bytes,
  check constraint), `CreatedAt`, `ExpiresAt`, `ClaimedAt`, `ClaimedFrom`, `ClaimedBy`, `Problem`. Web may select, insert and update; the
  workers remove a claim after a day.
- Permission `remote.proxmox` ("Proxmox web interface"), in the technician role.
- Public API: opening a session is not exposed on purpose, with the remote sessions (`MD-Files/API-WAITLIST.md`, Not exposed on purpose).
  The claim endpoint `/connect/v1/claim` belongs to Fleeto Connect and is not part of `/api/v1`.

## 7. Build plan

Built 2026-10-27 for the test build `v0.8.0-alpha.6`.

1. [done] **Tunnel**: the frames and the fixed target in the agent, Fleeto Connect's local proxy with the pinned certificate, the name below
   localhost and the cookie gate; Go tests against a fake `pveproxy` (HTTPS, a cookie of Proxmox, a WebSocket console), a relay in between,
   another certificate, other kinds of session and the channel limit.
2. [done] **Server**: the session kind, the claim table and endpoint, the signer rule, the permission, gateway and version gates, the window
   with the install proposal, audit, and tests (signer, web claim flow, workers clean-up).
3. [done] **Packaging**: per-user install on Windows, the `.deb`, the Release workflow and manifest, the downloads.
4. **On a real host with the developer.** Tested 2026-10-27 with `v0.8.0-alpha.6` on Ubuntu desktop and Chrome: the install proposal, the
   `.deb`, the sign-in to Proxmox (the Secure cookie is kept on `http://<name>.localhost`), and an upgrade of the host from the web
   interface, whose shell (xterm.js) opened without problems.
   Tested by the developer up to 2026-11-04 on Windows with Edge and Firefox: the web interface works. A VM console (noVNC) failed in
   every browser with "Connection closed (code: 1006)": the noVNC page of Proxmox always opens `wss://`, which cannot reach the plain
   HTTP address of Fleeto Connect (found 2026-11-04 by watching the console in Chrome; with `encrypt=0` in its address noVNC opens
   `ws://` and the console of the VM appeared). Fleeto Connect now sends a request for that page (`/?console=...&novnc=1` without
   `encrypt`) to the same address with `encrypt=0`; the leg to pveproxy keeps the TLS of Fleeto Connect. The xterm.js consoles follow the
   protocol of the page and never needed it. An ISO upload is not tested (the developer decided it need not be).
   Tested by the developer with `v0.8.0-alpha.16` on 2026-11-04: the VM console connects.

## 8. The Synology web interface (0.8.0)

The web interface of DSM on a Synology NAS, opened in the same way (asked and decided with the developer on 2026-11-01, as part of Synology
DSM as an endpoint platform in `MD-Files/ROADMAP.md`). Everything in this file holds for it, with these differences:

- **Its own kind and permission.** The remote session kind `SynologyWeb` (protocol `REMOTE_SESSION_KIND_SYNOLOGY_WEB`) and the permission
  `remote.synology` ("Synology web interface"), in the Remote group, admin and technician by default. The permission of one web interface
  never opens the other: web checks it per kind, fleeto-signer again from `RemoteSessionRules.Permission`.
- **Only on a Synology NAS.** The agent reports the operating system of DSM as DSM names itself ("Synology DSM 7.1.1-42962 Update 8", from
  `/etc.defaults/VERSION`, because DSM has no `/etc/os-release`); the menu item, web and the signer offer it only to an endpoint whose
  operating system starts with `Synology DSM `, and the agent only where `/etc.defaults/VERSION` is that of DSM. Agent 0.8.0-alpha.9 or
  later.
- **One fixed target: `127.0.0.1:5001`** (then `[::1]:5001`), the default HTTPS port of DSM. Never port 5000 (HTTP: the TLS of DSM is what
  runs end to end), never a port read from the configuration of the NAS and never one the server names (decided 2026-11-01). On a NAS whose
  admin moved DSM to another port or switched HTTPS off, the session ends with "the Synology web interface only works on the default HTTPS
  port of DSM, 5001".
- **The kind chooses the target, never a frame.** The signed token carries the kind; the agent maps it to its fixed target, and Fleeto
  Connect maps the kind the endpoint names in its Hello (`synology_web`) to the address DSM sees in `Host`, `Origin` and `Referer`
  (`127.0.0.1:5001`). A token of the Proxmox web interface cannot reach port 5001, nor one of the Synology web interface port 8006.
- **The claim records its kind.** `RemoteSessionClaim.Kind` (`ProxmoxWeb` or `SynologyWeb`, a check constraint, `ProxmoxWeb` for the rows
  written before it), so the session a claim becomes is of the web interface the window opened.
- **Fleeto Connect 0.8.0-alpha.9 or later.** An older one knows only the Proxmox web interface: its claim of a Synology web interface is
  refused with the download of the current one. The download of Fleeto Connect needs either permission.
- **The window** is the one of the Proxmox web interface (`WebInterfaceWindow`), shown by its own page `/remote/synology/{endpoint}`; the
  text names DSM and the DSM account.
- The technician signs in with their own DSM account and the 2-step verification of DSM. Fleeto never stores, sends or fills in a DSM
  credential.

**Tested on a NAS** (`0.8.0-alpha.9`, 2026-11-01, DSM 7.1.1-42962 Update 8 on the NAS of the developer): DSM accepts the `Host`, `Origin`
and `Referer` of `127.0.0.1:5001`, keeps its session cookie on `http://<name>.localhost` (also after reloading the page) and signs in
without a warning from its IP check. Not tested yet: what the auto block of DSM does after failed sign-ins, which all come from `127.0.0.1`
(a block of `127.0.0.1` would lock every technician out until it expires or is lifted in DSM, a trusted `127.0.0.1` would bypass the auto
block). This section changes first when it does not work as written.
