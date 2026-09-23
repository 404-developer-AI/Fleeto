# Changelog

All notable changes to Fleeto are documented here, newest first, following the
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) layout and semantic versioning.

This file holds the `Unreleased` section and the **two most recent released versions**.
When a third released version is added, the oldest entry moves to the top of
`CHANGELOG-ARCHIVE.md` in the same commit.

## [Unreleased]

### Added

- 0.5.0: Signing in with Microsoft Entra ID, for the people of your own organization. An admin configures the tenant, the
  app registration and its client secret in Settings, Sign-in, which also names the redirect URI to register, and links a
  Fleeto user to an Entra ID account in Settings, Users. The sign-in page offers "Sign in with Microsoft" only once that is
  configured and switched on. A token from another Microsoft tenant is refused, and so is a guest of your tenant: the people
  of the clients you manage sign in with a local account. Fleeto creates no users of its own from a sign-in, and matches on
  the object id of the account rather than on an email address. Configuring the sign-in, linking and unlinking, and the way
  every sign-in came in are in the audit log. Fleeto warns before the client secret expires; after it expires local accounts
  keep working.
- 0.5.0: Every sign-in keeps two factors, whichever way it comes in. When the token of a sign-in with Entra ID says
  Microsoft asked for a second factor, Fleeto does not ask for its authenticator code on top; when it does not say so, the
  code is asked as always. A linked user has no Fleeto password at all — linking removes it, and the password form answers a
  linked account exactly as it answers a wrong password, so it tells nobody which accounts sign in with Microsoft. One admin
  always keeps a password as the way in when Microsoft is unavailable: the last such admin cannot be linked, demoted or
  deleted, with a message that says why.
- 0.5.0: An admin can set a password for a user in Settings, Users. Needed after removing an Entra ID link, and it is what
  the sign-in page has always pointed at for somebody who lost their password. Their open sessions end, and two-factor
  authentication is untouched: a password alone is never enough.

### Changed

- Where Fleeto says it cannot install the Action1 agent on a Linux endpoint, it now names what to do instead (install it
  from the Action1 console) rather than promising a Fleeto version. Patch management on Linux and the integrations other
  than Action1 moved to "Not yet scheduled" on 2026-09-23; 0.5.0 is sign-in with Microsoft Entra ID and 0.6.0 is a release
  of refactors and fixes.

## [0.4.0] — 2026-09-23

Patch management through Action1: Fleeto shows per endpoint, client and site what Action1 reports about updates, deploys the
missing ones and alerts when a patch state cannot be trusted. Fleeto keeps no catalog and no patch engine of its own. Built in
three steps between 2026-09-20 and 2026-09-22. The pre-releases `0.4.0-alpha.1` to `0.4.0-alpha.4` were test builds on the first
test VPS and on real Windows endpoints; what they found is in Fixed. Windows only: a Linux endpoint shows no patch state and stays
out of the compliance counts until 0.4.1, because calling it uncovered would be untrue.

### Added

- Settings, Integrations for admins: the Action1 enterprise of the instance with its client id, client secret and
  region, a connection test, and the mapping of Action1 organizations to clients. One organization belongs to one client
  and one client to one organization, so patch state can never land under another client. The client secret is write-only:
  stored encrypted and bound to its row, never shown again, never in the audit log. A blank secret when editing keeps the
  one that is stored. The workers make every call to Action1 and read its organizations again every four hours, so the
  names stay current. Fleeto stays well under the request budget Action1 recommends (20 a minute for the whole instance)
  and waits as long as Action1 asks after a "too many requests" answer.
- Patch state from Action1 per managed endpoint: the Patches tab on the endpoint detail shows whether it is up to
  date, how many updates it misses (critical and other), whether a restart is pending and which updates are missing with
  their severity. The dashboard has a patch compliance tile, and the clients workspace shows compliance of the selected
  client or site. The workers read it every four hours, matched on the Action1 agent id of the endpoint and only within
  the client the organization is mapped to; detail is read for endpoints that miss something. An endpoint Action1 no
  longer patches (above the licensed number of the subscription) or has not seen for a week opens an alert, because its
  state cannot be trusted; missing updates themselves are state, not an alert. Agent-only endpoints have no patch state,
  endpoints in maintenance open no alert, and `GET /api/v1/endpoints/{endpointId}/patches` gives the same data in the API.
- Deploying updates from Fleeto: on the Patches tab of an endpoint every missing update or the updates a technician
  ticks in the list, and from the endpoint list every missing update on a selection. The deployment runs in Action1; Fleeto
  shows per endpoint what Action1 reports, live, and keeps the run in the history of every endpoint it touched. Restarting
  is a choice per deployment and is off by default; with it on, the signed-in user gets a message and half an hour before
  Action1 restarts the endpoint. Managed endpoints only, admins and technicians, audited like a job. A deployment Action1
  refuses says why and installs nothing; one Action1 has not finished after a day is no longer followed and says so instead
  of claiming success. `GET /api/v1/endpoints/{endpointId}/patch-deployments` gives the same data in the API.
- Installing the Action1 agent from Fleeto on a Windows endpoint that patch management does not cover yet, from the
  Patches tab. It is a signed job whose script Fleeto writes itself: fleeto-signer composes it from the installer link of
  the client's Action1 organization and refuses when that link changed since the technician asked. The link is read from
  Action1 where Action1 hands it out and can otherwise be pasted per organization in Settings, Integrations. The
  installation is silent and never restarts the endpoint.
- The agent reports the id of the Action1 agent installed next to it on a Windows endpoint, read from the endpoint
  itself. It is shown on the endpoint's Summary tab and as `action1AgentId` on the inventory in the public API. Patch
  management matches an endpoint on it instead of on the host name, which is not unique across clients and changes.
  Linux follows in 0.4.1, together with patch management for Linux endpoints.

### Changed

- The Fleeto repository on GitHub is public from 2026-09-21, so CI runs on free GitHub Actions minutes; the release
  images on ghcr.io stay private. `install.sh` still asks for both read-only tokens: the release token now mainly
  lifts the rate limit of the GitHub API, the packages token reads the images. Two addresses of a test VPS were
  taken out of the git history in the same step, which changed the commit of every release from 0.2.1 on. The code is
  source-available under the Business Source License 1.1, not open source: production use stays with Steaan and every
  published version turns into Apache-2.0 on 2030-09-21. `SECURITY.md` states how to report a vulnerability privately,
  and GitHub secret scanning with push protection is on.

### Fixed

- Settings, Integrations releases the page when it is closed. Its own clean-up replaced the one it inherits instead of
  running next to it, so the page stayed subscribed to the time zone of the signed-in user after it was closed. Found while
  preparing the release.
- The workers may change the organization mapping of an integration again. They keep the name of an Action1
  organization current, but had read-only rights on that table, so a renamed organization made the four-hourly refresh
  fail on an instance. Found while building 0.4.0 step 3.
- The connection test of an integration no longer ends in "Action1 did not answer in time" on an instance. fleeto-web
  runs on a network without outbound access, so it can never reach an external product; it now records what an admin asked
  for and the workers, which do have outbound access, make the call and write the result back. The page shows "Testing"
  until the answer is there. Found while testing 0.4.0-alpha.1.

## [0.3.0] — 2026-09-20

Remote control and remote background: taking over the screen of a Windows or Linux endpoint, and a terminal, files, services and
processes without touching it, all end-to-end encrypted between the browser and the endpoint. Built in seven steps between
2026-09-16 and 2026-09-20. The pre-releases `0.3.0-alpha.1` to `0.3.0-alpha.17` were test builds on the first test VPS and on real
Windows and Linux endpoints; what they found is in Fixed. The last step load-tested the relay (200 sessions through one gateway in
CI, 800 on the development laptop) and reviewed every line of the new code; what that found is in Security.

### Added

- Remote control of a Windows endpoint: take over its screen from the right-click menu or the endpoint detail, in its own
  window. Choose the Windows session first — the console (with the sign-in screen and UAC) or a signed-in RDP session — and, once
  connected, the monitor. The screen is shown with change detection (sharp text), the mouse and keyboard work, and the keyboard
  keeps the right characters whatever the layout on either side, also on the sign-in screen. Buttons for Ctrl+Alt+Del and "Type
  clipboard", and the session reconnects on its own when the connection drops. Managed Windows endpoints with the Fleeto agent of
  0.3.0, admins and technicians. Everything in the window is encrypted between the browser and the endpoint; the gateway only
  passes it on. The session is recorded in the audit log.
- Remote control on Linux endpoints with X11: the screen of the console (the sign-in screen too when it runs on X11), mouse and
  keyboard with the endpoint's own layout, the clipboard with text and files, the banner and the consent prompt on workstations, and
  several technicians in one session. A Wayland session cannot be shown; the window says so, and remote background works. The agent's
  processes on the display run as nobody or as the user of the session, never as root. Needs the Fleeto agent of 0.3.0.
- Remote control on Windows sends the screen as H.264 where the endpoint can encode it (Media Foundation: the GPU's hardware
  encoder, else the Microsoft encoder in Windows) and every technician's browser decodes it (WebCodecs). The bit rate follows the link,
  and the tiles remain the automatic fallback: on an endpoint without Media Foundation, an encoder that fails, a browser without H.264
  or a browser whose decoder fails. The window shows the codec, frames a second, bit rate and an estimate of the latency.
- Remote control captures a whole monitor with DXGI desktop duplication, which is many times faster than GDI on a large screen;
  GDI stays for all monitors together, rotated monitors, RDP sessions and wherever duplication is not available.
- Remote control clipboard. Text copied on either side is available on the other: text copied on the endpoint is put on the
  technician's clipboard, and the technician's clipboard goes to the endpoint when they paste (Ctrl+V or Shift+Insert) in the window.
  Files pasted or dragged into the window are placed on the endpoint clipboard, like RDP, and pasted there with Ctrl+V; they wait in a
  folder only the signed-in user of the endpoint can read and are deleted when the session ends. Files copied on the endpoint are
  offered for download in the window (folders are not). Transfers follow the policy's file size cap and are audited as
  `clipboard.upload` and `clipboard.download`. The policy switch "Synchronise the clipboard in remote control" turns it off.
- The remote control window explains the two clipboard steps the first times a technician uses them: pasting files says they travel
  to the endpoint first and are pasted there with Ctrl+V once they arrive, and files copied on the endpoint say they cannot go on the
  technician's own clipboard and are saved with the download button. Each explanation has "Got it" and "Do not show this again"; the choice
  is remembered in that browser.
- Several technicians in one remote control session. Opening Remote control on a Windows session where a session already runs
  joins it: one screen, one monitor choice, input from each technician, every join with its own token, key exchange and audit entry.
  The window lists the technicians and shows where the others point. A technician whose connection cannot keep up is disconnected so
  the others keep their screen.
- Consent prompt and banner for remote control on workstations, per policy. With "Ask the signed-in user before remote control
  on workstations" on, the first technician of a session waits until the user signed in on the shown Windows session allows it; a
  refusal ends the session, no answer within the consent timeout (10 to 300 seconds, default 30) grants access, and with nobody signed
  in access is granted at once. Technicians who join later are not asked again. The banner at the top of the endpoint's screen names
  every technician in the session (on by default). Servers never ask and show no banner. The answer is audited (`consent.granted`,
  `consent.refused`, `consent.timeout`, `consent.not_asked`, `consent.failed`).
- Remote background with a terminal as SYSTEM or root. Opened from the right-click menu of a managed endpoint or the
  Remote background button on the endpoint detail, in its own window: PowerShell or Command Prompt on Windows (in a pseudo
  console; Windows Server 2016 gets line input), the login shell on Linux, served by the watchdog, so it also works when the
  agent is broken. The shell ends when the window closes or the session ends; a new terminal can be opened in the same session.
  An optional reason is stored with the session. Needs the watchdog of Fleeto 0.3.0.
- Remote background now has a file explorer, services and processes next to the terminal, in the same encrypted window.
  Files: browse the endpoint, download a file (streamed to disk, resumable), upload a file (up to the policy's cap), and create,
  rename, delete and copy within the endpoint. Services: list them and start, stop, restart or change the start type. Processes:
  list them with CPU, memory and user, and end one. Everything runs over the encrypted session; the gateway sees only ciphertext.
- Every file, service and process action a technician takes is recorded in the audit log, reported by the endpoint over
  its own control session (never a file's content). Stored in `RemoteSessionActions` (part of the `RemoteSessions` migration).
- Remote sessions are end-to-end encrypted between the browser and the endpoint. fleeto-signer signs a single-use token
  per technician's connection, valid 60 seconds, with the browser's ephemeral X25519 key; the endpoint signs its own key with
  its certificate key, and the browser accepts that key only by the fingerprint the instance recorded. Frames are AES-256-GCM
  with a counter nonce. The gateway relays them unread: browsers connect to `wss://<fqdn>/relay/` through the host proxy, the
  endpoint to `/v1/relay/` with its client certificate. Tier checks in web, signer, gateway and on the endpoint.
- A remote session without input closes after the policy's idle timeout (5 to 480 minutes, default 30), with a warning
  2 minutes before; in a session with several technicians each one's own connection closes.
- The policy dialog shows every remote session setting: idle timeout, file size cap, clipboard, banner, consent prompt and its
  timeout.
- Remote sessions, their participants and actions are stored (migration `RemoteSessions`, additive) and audited:
  `remote_session.requested`, `.signed`, `.joined`, `.left` and `.refused`. Sessions that never connect end after 5 minutes;
  the history is kept 13 months. The policy stores the remote session settings of later steps (consent, banner, clipboard,
  file size).
- install.sh gives every instance a loopback port for the relay (`RELAY_PORT`) and routes `/relay/*` to it.

### Fixed

- The clipboard of a remote control session is served by a process that runs as the user signed in on the Windows session, so text
  and files travel both ways again. The clipboard of a session belongs to that user: Windows Explorer hands its copied files out through
  OLE, and the agent, which runs as SYSTEM, could neither read what was copied there nor replace it — a file copied on the endpoint was
  never offered for download, and text from the technician never arrived. The screen, mouse and keyboard stay with the helper that runs as
  SYSTEM, because those need the sign-in screen and UAC. Without a signed-in user the window says the clipboard needs one.
- A file copied on the endpoint is offered for download when the program that copied it puts the files on the clipboard through
  OLE, which Windows Explorer does: the clipboard then holds a marker only and the files are made when they are asked for. The endpoint
  now asks the clipboard's data object, the way an ordinary application does, when the plain clipboard holds nothing.
- A file copied on the endpoint is offered for download even when Windows reports only the first step of the copy. A program
  copying files empties the clipboard first and puts the files on it in a second step; the endpoint now looks again after a change that
  held nothing. When the endpoint copies files that are not on disk (a compressed folder, a cloud folder), the window says so instead of
  staying empty, and points at Files in a remote background session.
- Files a technician pasted into a remote control session are no longer offered back to them as files copied on the endpoint.
  They stay on the endpoint clipboard, so after the helper of the session started again — its window no longer owning the clipboard —
  the last pasted file appeared as a download instead of what was copied on the endpoint.
- Files copied on the endpoint are offered to the technician even when Windows does not report the change: the endpoint now also
  checks its clipboard every second. The check ran only while the banner was shown, so on a server (which never shows a banner) a copy
  on the endpoint could go unnoticed.
- A remote control session whose Windows session ends (the user signs out) no longer starts the helper again in a session that is
  gone: the technician is told the session ended, and a console that moves to another session no longer counts as a helper that keeps
  failing.
- Pasting files into a remote control window a second time pasted them on the endpoint instead of sending them again. While the
  technician's own clipboard held the files, every Ctrl+V uploaded them once more and the shortcut never reached the endpoint, so
  nothing appeared in the folder there.
- A failure in the banner and clipboard window of the endpoint can no longer stop the helper that shows the screen, and the
  endpoint says so at once when it cannot start the banner and clipboard of its Windows session. The agent log now names why the
  helper stopped and what the endpoint clipboard holds (counts only, never its content).
- Remote sessions no longer stay "in a session" when the gateway claimed a participant and never paired it; the workers end those
  after five minutes, and every failure after the claim ends the participant.
- A remote session token is no longer accepted in the seconds after it expired (the check now uses the time the token arrived).
- Typing a long text on a Linux endpoint no longer stalls the helper, and input that a terminal does not read no longer blocks the
  session (with it, its idle timeout and its end).
- One session serves at most 16 requests and 16 transfers at a time, so a browser cannot fill the endpoint's memory or its handles.
- A folder can no longer be copied into one of its own folders without end, and a video frame or a screen size that cannot be real is
  refused instead of filling the browser's memory.
- The connection pools of the containers fit the database of an instance (gateway 40, web 30, workers 15, signer 5): under load they used to
  ask PostgreSQL for more connections than it accepts, and a remote session then failed with "too many clients".

### Security

- What the signer signs can no longer be changed after it was requested. Database triggers keep the binding columns of jobs, remote
  sessions and their participants as they were written, and web records what it asked for in the signing request, which the signer compares
  with the rows. A compromised gateway could otherwise have pointed a job or a session at another endpoint, or put its own key in a remote
  session token and taken over the session.
- A watchdog certificate is issued only when the agent signed the request with the key of its own certificate. A compromised gateway
  could otherwise have obtained a watchdog identity and played the endpoint in a remote session. Agents older than 0.3.0-alpha.17 get no new
  watchdog certificate; existing watchdog certificates keep working and renew as before.
- The gateway can no longer change the tier, client, site or class of an endpoint.
- An upload in a remote session no longer follows a link with the name of its temporary file, and refuses a folder that is a link, so
  a user of the endpoint cannot make the agent overwrite a file of their choice. An upload that did not arrive whole never replaces the file.
- Files pasted into a remote control session wait in folders that are created with their access list in one step, below a base folder
  owned by administrators (Windows) or in /run (Linux), so nobody can put a link in their place.
- A file copied on the endpoint is opened with the rights of the user who copied it, so the person at the endpoint cannot have a
  technician download a file they may not read themselves. The clipboard process of that user can only send clipboard frames.
- Text copied on the endpoint goes on the technician's own clipboard by itself only right after they copied in the remote control
  window; otherwise the window offers it with a button. Someone at the endpoint can no longer put text on a technician's clipboard unasked.
- Remote sessions that arrive at the same moment can no longer exceed the limit of sessions per endpoint.
- One technician can request at most 20 remote sessions a minute, so the instance's signing budget stays available to everyone.
- Linux: the agent accepts only an X server of root or of the user of the screen, and its children check that the display socket
  belongs to it. Files of a session (its cookie, a copied file) are read with the rights of that user, never as root. Consent is asked when
  the screen is locked, and a session state that cannot be read counts as somebody being there.
- The service actions of 0.2.1 are stricter: `systemctl` is called with `--` before the unit, and a service name may not start with a dash.
