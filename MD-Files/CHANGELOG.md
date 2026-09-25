# Changelog

All notable changes to Fleeto are documented here, newest first, following the
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) layout and semantic versioning.

This file holds the `Unreleased` section and the **two most recent released versions**.
When a third released version is added, the oldest entry moves to the top of
`CHANGELOG-ARCHIVE.md` in the same commit.

## [Unreleased]

### Added

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
- Client templates set the policy, patch policy and monitoring templates of the client itself as well as of each site;
  a site follows the client unless it has its own. The sites fold open one at a time.
- The history Action1 keeps per endpoint of a deployment, its "Automation History", opens from the Patches tab of the
  endpoint: operation, time, status and details, newest first, refreshed while the deployment runs.
- Tags on clients, in the way of Proxmox. Type a tag when you create a client, or later under Client settings, Edit
  tags, or pick one that exists; a new tag gets a color from its name. Tags show next to the client name in the clients panel (two, and the
  number of the others) and in the client header; the tag button next to the search filters on one or more tags, and
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

### Changed

- "Rename client" is "Edit client", and "Policy and monitoring" of a site is part of "Edit site".
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
- An alert about two endpoints using the same agent identity resolves on its own once it has been open for 24 hours
  without a new second connection.
- Email through Microsoft Graph gets its token the same way as the tests in Settings, so a refusal from Microsoft reads
  the same in both places.

### Fixed

- A deployment with automatic restart waited 30 hours instead of 30 minutes before Action1 restarted the endpoint: Action1
  reads the time in minutes, and Fleeto sent seconds.
- "Deploy all missing updates" installs the updates again. Action1 installed only updates approved in its own console
  and answered "No updates are applicable"; Fleeto now asks for every missing update, as it already did for chosen ones.
- The check history dialog no longer keeps a handler after it closes, and the remote control and remote background
  windows always clean up after themselves.
- The gateway refuses a certificate from an unknown authority also when it runs on Windows, instead of failing on it.
- The build has no warnings left.

### Security

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
