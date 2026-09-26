# Fleeto — API waiting list

> Everything Fleeto can show or do that is **not** in the public API (`API.md`) yet. Rules are in `CLAUDE.md` (Public
> API), decided 2026-09-15:
>
> - A commit that adds or changes a feature (data or an action a user can see or use in Fleeto) and does not add it to
>   the API adds an entry here **in the same commit**, even when the API work comes much later.
> - When the API part is built, the entry is removed here and documented in `API.md` in the same commit.
> - A feature is not done until it is in `API.md` or on this list.
> - Things that must never be in the API stay listed under "Not exposed on purpose", with the reason, so nobody adds
>   them by accident.

## How to write an entry

One row per feature, in the table of its area. Keep it concrete enough to build from:

- **Feature**: what it is, in the words of the UI.
- **Since**: the Fleeto version that added or last changed it.
- **API shape**: the endpoint(s) and fields it would need, following the conventions in `API.md` (read-only unless write
  access has been decided).
- **Notes**: tier, role, personal data, performance or security points to keep in mind.

## Read access: waiting

### Endpoints and checks

| Feature | Since | API shape | Notes |
|---|---|---|---|
| Check history: hour, day, week, month and year per check and target (average, lowest, highest, errors, no responses) | 0.2.0 | `GET /api/v1/endpoints/{endpointId}/checks/{checkId}/history?target=&range=hour\|day\|week\|month\|year` returning points `{time, min, avg, max, values, errors, noResponses}` | Managed only (409 otherwise). Read the hourly and daily rollups, never raw results. |
| Thresholds and alert settings of a check as they apply to the endpoint: warning and critical threshold, failures before alert, and which values are adjusted | 0.1.0 | Add `warningThreshold`, `criticalThreshold`, `failuresBeforeAlert`, `unit` and per value `adjusted` to Check | Additive change to `GET /api/v1/endpoints/{endpointId}/checks`. |
| Endpoint history: connection events (connected, disconnected, duplicate identity) | 0.1.0 | `GET /api/v1/endpoints/{endpointId}/events` (paged, newest first) | Kept as long as the retention of endpoint events. |
| Endpoint audit trail on the History tab (tier changes, moves, notes added, jobs started, maintenance) | 0.1.0 | `GET /api/v1/endpoints/{endpointId}/audit` (paged) without IP addresses | Same content as the History tab; details never contain note bodies. |
| Agent certificates of an endpoint: active certificates, expiry of the newest, revoked certificates with reason | 0.1.0 | `GET /api/v1/endpoints/{endpointId}/certificates` or `certificateExpiresAt` on Endpoint | Never certificate or key material; fingerprint and dates only. |
| Monitoring templates linked to one endpoint, on top of those of its site | 0.1.0 | `monitoringTemplates: [{id, name}]` on a new endpoint configuration resource | Together with "Monitoring templates" below. |
| Endpoint list filters "With open alerts" and "In maintenance" | 0.2.0 | `hasOpenAlerts=true`, `inMaintenance=true` on `GET /api/v1/endpoints` | Same rules as the endpoint list in the UI (alerts on hold do not count). |
| Pending configuration and applied configuration version | 0.1.0 | `configVersion`, `appliedConfigVersion` on Endpoint | `configurationPending` is already on EndpointChecks. |
| Watchdog of an endpoint: installed version, online, last seen, and the service state of agent and watchdog as the other service reports it | 0.2.1 | `watchdog: {version, online, lastSeenAt}` and `services: [{component, state, detail, reportedAt}]` on Endpoint | Additive. Service detail is free text from the endpoint. |
| Signed-in users of an endpoint as the agent last reported them: per user the SID or uid, the account name and the sessions (console or remote), with the time of the report | 0.2.2 | `signedInUsers: {reportedAt, users: [{id, account, sessions: [{id, console}]}]}` on Endpoint | Additive. Personal data: consider leaving it out of the list endpoint. |
| Remote session history of an endpoint: kind (remote control, remote background), for remote control the Windows session shown, started by, reason, created, started and ended with the reason, and per participant the technician, state, connected and ended times | 0.3.0 | `GET /api/v1/endpoints/{endpointId}/remote-sessions` (paged, newest first) returning `{id, kind, windowsSession, startedBy, reason, createdAt, startedAt, endedAt, endReason, participants: [{userName, state, connectedAt, endedAt, endReason}]}` | Managed and agent-only (history stays readable). Never tokens, keys or IP addresses. Reasons are free text. `windowsSession` is null for remote background and 0 (the screen) for remote control on Linux (0.3.0 step 6). |
| Actions inside a remote session: file, service and process actions with their target (remote background), clipboard file transfers and the consent answer (remote control) | 0.3.0 | `actions: [{time, action, target, detail}]` on a single session `GET /api/v1/remote-sessions/{sessionId}` | Written from 0.3.0 step 2 (remote background, including `terminal.open`) and step 4 (`clipboard.upload`, `clipboard.download`, `consent.granted`, `consent.refused`, `consent.timeout`, `consent.not_asked`, `consent.failed`). Terminal content, clipboard text and file content are never stored. |
| Agent and watchdog update status per endpoint: version being installed, downloading, installing, installed, failed or rolled back, with the cause; from 0.2.2 also the release the installer waits to install, why (update ring, next attempt, random delay, the agent's own update, rolled back before) and until when | 0.2.1 | `updates: [{component, version, state, detail, at, wait: {version, reason, until, reportedAt}}]` on Endpoint | Additive. |
| History Action1 keeps per endpoint of a deployment: operation, time, status and details, as the Patches tab shows it | 0.6.0 | `GET /api/v1/endpoints/{endpointId}/patch-deployments/{deploymentId}/history` returning `{readAt, steps: [{time, operation, status, details}]}` | Read-only data; belongs with the deployments that are already in `API.md`. Managed endpoints and agent-only alike (history stays readable). |

### Alerts and maintenance

| Feature | Since | API shape | Notes |
|---|---|---|---|
| Who acknowledged or put an alert on hold, and when the hold was set | 0.1.0 | `acknowledgedBy`, `heldAt`, `heldBy` on Alert | User names are personal data; already visible to read-only users in the UI. |
| Alerts changed since a moment, for efficient synchronisation | 0.2.1 | `updatedSince=<timestamp>` on `GET /api/v1/alerts`, ordered by `updatedAt` | Needs an index on `(UpdatedAt, Id)`. |
| Maintenance windows of a policy (days, start time, duration, time zone, applies to) and upcoming occurrences | 0.2.0 | Part of "Policies" below; `GET /api/v1/sites/{siteId}/maintenance-windows` for the next occurrences | |

### Templates, policies and scripts

| Feature | Since | API shape | Notes |
|---|---|---|---|
| Policies: settings, script approval required, maintenance windows, update ring (0.2.1), job output cap (0.2.1), remote session settings (0.3.0: consent prompt and its timeout, banner, clipboard, idle timeout, file size cap), which clients, sites and endpoints use them (0.6.0), the default policy | 0.3.0, 0.6.0 | `GET /api/v1/policies`, `GET /api/v1/policies/{policyId}`; `appliesTo` (`all`, `server`, `workstation`, 0.6.0) on a policy; `policies: [{appliesTo, id, name}]` on Client and Site (one for every endpoint or one per class), `policy: {id, name, level}` on Endpoint, where `level` says where the effective policy is linked (`endpoint`, `site`, `client`, `default`) | Global policies are instance-wide: a client-limited key sees only global and own-client policies. |
| Patch policies: schedule, time zone, all or filtered updates with their filters, approval and delay, restart and its message and delay, retry window, enabled; which clients, sites and endpoints use them; the Action1 automation per client with its state and last error; the automations of an organization Fleeto does not manage | 0.6.0 | `GET /api/v1/patch-policies`, `GET /api/v1/patch-policies/{id}` with `appliesTo`; `patchPolicies: [{appliesTo, id, name}]` on Client and Site, `patchPolicy: {id, name, level}` on Endpoint (null without one) | Same scope rule as policies. Never the Action1 automation body beyond what the policy says. |
| Monitoring templates with their check definitions (type, parameters, interval, thresholds, failures before alert, applies to) and the clients (0.6.0), sites and endpoints they are linked to | 0.1.0, 0.6.0 | `GET /api/v1/monitoring-templates`, `GET /api/v1/monitoring-templates/{id}` with `appliesTo` (0.6.0); `monitoringTemplates: [{id, name}]` on Client and Site | Same scope rule as policies. The source `client_template` of a check is in `API.md`. |
| Client templates: the policy, patch policy and monitoring templates of the client itself (0.6.0), the sites with theirs, and the clients created from them | 0.1.0, 0.6.0 | `GET /api/v1/client-templates`, `clientTemplate: {id, name}` on Client | Instance-wide; decide whether a client-limited key may read them. |
| Script library: scripts, versions, language, timeout, approval state and approver | 0.2.0 | `GET /api/v1/scripts`, `GET /api/v1/scripts/{scriptId}/versions` | The script body can hold sensitive commands: decide whether keys read bodies or only metadata and the hash. |

### Instance and administration

| Feature | Since | API shape | Notes |
|---|---|---|---|
| License usage: state, managed endpoints in use and available, expiry, grace period | 0.1.0 | `GET /api/v1/license` | Never the signed license document. |
| Agent updates: the release the instance offers, installed at, paused or released to every ring, when each update ring gets it, agents per version, updates that failed | 0.2.1 | `GET /api/v1/agent-release` | Admin data. Never the manifest signature or binaries. |
| Dashboard summary: endpoints online and offline, open alerts by severity, agents out of date, recent jobs, and patch compliance of the instance, a client or a site (0.4.0: `patchCompliance: {covered, compliant, missingCritical, notPatched}` as the dashboard tile and the compliance line show it) | 0.4.0 | `GET /api/v1/summary` (optionally `clientId` or `siteId`) | Must read the same aggregates as the dashboard, not scan raw tables. `covered` counts the endpoints Action1 reports on, so managed Windows endpoints only: Linux endpoints stay out of it while patch management does not cover them. |
| Tags of the instance with their color and the number of clients that carry them, as Settings, Tags shows them | 0.6.0 | `GET /api/v1/tags` returning `{name, color, clientCount}` | A client-limited key sees only the tags of its clients, counted over its clients. Tags per client are in `API.md`. |
| Instance information: Fleeto version, FQDN, agent host name | 0.1.0 | `GET /api/v1/instance` | Useful for integrations that talk to several instances. |
| Audit log with filters (action, actor, target, client, period) | 0.1.0 | `GET /api/v1/audit` (paged on the audit id) | Admin data: decide whether keys need an "audit" permission first. Contains IP addresses. |
| Backups: last successful backup, failed runs, destination configured | 0.1.0 | Part of `GET /api/v1/summary` or `GET /api/v1/backups` | Never destination credentials. |
| Expiring credential warnings (Microsoft Graph secret or certificate) | 0.2.0 | Part of `GET /api/v1/summary`: `{name, expiresAt, expired, stopsWorking}` | Never the credential itself. |
| Notification channels with routing rules (clients, minimum severity, resolves) and last webhook delivery | 0.2.0 | `GET /api/v1/notification-channels` | Admin data. Never webhook URLs, signing secrets or email content. |
| Users and roles | 0.1.0 | `GET /api/v1/users` (name, email, roles, two-factor enabled, and from 0.5.0 whether the user signs in with Entra ID and with which account) | Decide first whether this belongs in the API at all: personal data and a map of who can do what. |
| Sign-in with Microsoft Entra ID: whether it is configured and switched on, for which tenant and app registration, when its client secret expires, and how many users are linked (0.5.0) | 0.5.0 | `GET /api/v1/sign-in` returning `{provider, enabled, tenantId, clientId, clientSecretExpiresAt, linkedUsers, microsoftHandlesSecondFactor}` | Admin data. Never the client secret, and never the object id of a linked account: it identifies a person in the customer's tenant. |
| Integrations: which external product is configured, whether its last attempt worked, and which of its tenants maps to which client (0.4.0: Action1, with its region). From 0.6.0 also whether Action1 follows the clients and sites, why that last failed, the endpoint group of each site, and the changes that wait for Action1 | 0.4.0, 0.6.0 | `GET /api/v1/integrations` returning `[{type, enabled, region, status, statusMessage, lastAttemptAt, lastSuccessAt, followClients, followMessage, mappings: [{clientId, clientCode, tenantId, tenantName, siteGroups: [{siteId, groupId}]}], waiting: [{kind, name, attempts, lastError, nextAttemptAt}]}]` | Admin data. Never the credentials, not even the client id: it is half of a credential pair. |

## Write access: waiting for demand

Decided 2026-09-15: the API is read-only; write access comes only when there is demand. Every action below exists in the
UI. When write access is decided, each needs: the same role (a technician-level permission on the key), tier and license
checks as the UI, idempotency for retries, an audit entry per change, and a scope check that a client-limited key cannot
touch instance-wide data.

| Feature | Since | API shape | Notes |
|---|---|---|---|
| Acknowledge, put on hold (at most 7 days), end hold and resolve an alert | 0.1.0 | `POST /api/v1/alerts/{alertId}/acknowledge`, `/hold` `{until}`, `/end-hold`, `/resolve` | |
| Run a check now, and reset a check | 0.1.0 | `POST /api/v1/endpoints/{endpointId}/checks/{checkId}/run` `{reset}` | Managed only; the same limits on repeated runs as the UI. |
| Start, change and end maintenance mode of a client, site or endpoint | 0.2.0 | `PUT /api/v1/{clients\|sites\|endpoints}/{id}/maintenance` `{endsAt, reason}`, `DELETE` to end | |
| Create, edit (author only) and delete (admin only) a note | 0.1.0 | `POST /api/v1/endpoints/{endpointId}/notes`, `PATCH`/`DELETE /api/v1/notes/{noteId}` | A key is not a user: decide who the author is. |
| Switch endpoints between agent-only and managed | 0.1.0 | `POST /api/v1/endpoints/tier` `{endpointIds, tier}` | All or nothing; consumes licenses. |
| Override or clear the class of an endpoint; move an endpoint to another site of the same client | 0.1.0 | `PATCH /api/v1/endpoints/{endpointId}` `{classOverride, siteId}` | |
| Revoke the agent of an endpoint; delete an endpoint; enroll again | 0.1.0 / 0.2.0 | `POST /api/v1/endpoints/{endpointId}/revoke` `{reason}`, `DELETE /api/v1/endpoints/{endpointId}` | High impact: consider keeping these UI-only. |
| Run a library script on endpoints (with validity and the account it runs as), cancel a job | 0.2.0 | `POST /api/v1/jobs` `{scriptId, endpointIds, validity, runAs, runAsUserId, allSignedInUsers}`, `POST /api/v1/jobs/{jobId}/cancel` | Highest risk in the API: runs as SYSTEM or root, or as the signed-in user (0.2.1, `runAs` values as on Job in `API.md`), optionally a chosen user on one endpoint (0.2.2, a user id from the signed-in users of that endpoint) or all signed-in users (0.2.2, `allSignedInUsers`: one job per reported user, at most 500 jobs per run). The signer must re-check everything as for the UI; consider a separate key permission and approval. A run answers with the batch id, the skipped endpoints and their reason, and notifies the admins above the threshold, exactly as the UI does (0.2.1). |
| The jobs of one run, by batch id | 0.2.1 | `GET /api/v1/jobs?batchId=` on the existing jobs endpoint | Reads what the run window shows. `batchId` is already a field on Job in `API.md`. |
| The threshold above which a script run notifies every admin | 0.2.1 | Part of a future `GET /api/v1/settings` for admins | Instance-wide admin setting, edited in Settings, Scripts. |
| Checks per endpoint: disable a template check, adjust interval, thresholds and failures before alert, add or delete an endpoint-only check, link extra monitoring templates | 0.1.0 | `PUT /api/v1/endpoints/{endpointId}/checks/{checkId}/adjustment`, `POST /api/v1/endpoints/{endpointId}/checks`, `PUT /api/v1/endpoints/{endpointId}/monitoring-templates` | Managed only. |
| Create, rename and delete clients (delete needs the typed client code; from 0.6.0 creating takes the tags of the client); detach from a client template | 0.1.0, 0.6.0 | `POST /api/v1/clients` `{code, name, clientTemplateId, tags}`, `PATCH`/`DELETE /api/v1/clients/{clientId}` | Delete purges all client data: consider keeping UI-only. |
| Set the tags of a client (created on first use); rename, recolor and delete a tag | 0.6.0 | `PUT /api/v1/clients/{clientId}/tags` `{names}`; `PATCH`/`DELETE /api/v1/tags/{name}` `{name, color}` | Setting tags is admin or technician; renaming, recoloring and deleting is instance-wide, so never for a client-limited key. Reading tags is in `API.md`. |
| Create, edit and delete sites; link a policy and monitoring templates | 0.1.0 | `POST /api/v1/sites`, `PATCH`/`DELETE /api/v1/sites/{siteId}`, `PUT /api/v1/sites/{siteId}/links` | |
| Link a policy, a patch policy and monitoring templates to a client, a site or an endpoint (null inherits); links from a client template can be replaced, not removed | 0.6.0 | `PUT /api/v1/{clients\|sites\|endpoints}/{id}/links` `{policy: {all, server, workstation}, patchPolicy: {all, server, workstation}, monitoringTemplateIds}` (an endpoint takes `all` only) | Admin or technician. Monitoring templates on an endpoint need a managed endpoint. A patch policy change reaches Action1 through the workers. |
| Create, edit, copy and delete patch policies | 0.6.0 | `POST /api/v1/patch-policies`, `PATCH`/`DELETE /api/v1/patch-policies/{id}`, `POST /api/v1/patch-policies/{id}/copy` `{name, clientId}` | Only Action1's own options and values (`PatchPolicyRules`); deleting removes its automations from Action1. |
| Create enrollment tokens and install commands; revoke tokens | 0.1.0 | `POST /api/v1/sites/{siteId}/enrollment-tokens` | Returns a secret once; decide whether this belongs in the API. |
| Create, edit, copy and delete policies, monitoring templates (with checks) and client templates | 0.1.0 | CRUD under `/api/v1/policies`, `/monitoring-templates`, `/client-templates` | Linked, not copied: a change applies to every user of the template. |
| Create scripts, save versions, approve a version (second admin with a fresh authenticator code), delete scripts | 0.2.0 | CRUD under `/api/v1/scripts` | Approval needs a person with two-factor authentication: never through an API key. |
| Start a deployment of updates on endpoints: every missing update, or updates chosen on one endpoint, with or without an automatic restart | 0.4.0 | `POST /api/v1/patch-deployments` `{endpointIds, updateIds, autoReboot}` answering the batch id, one deployment per client and the skipped endpoints with their reason, as the UI does | Managed endpoints only, admin or technician. It installs software on customer machines through Action1 and may restart them, so it needs the same checks as the UI and a decision on whether a key may do it at all. `updateIds` are Action1 package ids from `missing` on PatchState, and only for a single endpoint. Reading deployments is in `API.md`. |
| Install the Action1 agent on a Windows endpoint that patch management does not cover yet | 0.4.0 | `POST /api/v1/endpoints/{endpointId}/patch-agent` answering the job id | Managed Windows endpoints, admin or technician. It installs software as SYSTEM, so it needs the same checks as the UI; the signer composes and signs the script itself from the installer link of the client's organization. The link is configuration, not API data (see "Not exposed on purpose"). |
| Pause an agent release, resume it, release it to every ring | 0.2.1 | `POST /api/v1/agent-release/pause`, `/resume`, `/release-to-all` | Admin actions that change what every endpoint installs: consider keeping them UI-only. |

## Not exposed on purpose

These stay out of the API. Changing that needs an explicit decision recorded in `CLAUDE.md`.

| Feature | Reason |
|---|---|
| API keys (create, list, revoke) | A key must not be able to create or extend keys; key management stays with an admin in Settings. |
| Sign-in configuration (0.5.0): saving the Entra ID tenant, client id and client secret, switching the sign-in on or off, linking or unlinking a user, adding a user from Microsoft, and searching the users of the tenant | Holds a secret and decides who can sign in at all; account security belongs in the UI with two-factor authentication. A search of the tenant would turn an API key into a directory reader of the customer's own staff. Reading the configuration is on the waiting list above. |
| Testing an app registration from Settings (0.5.0): the sign-in and Microsoft Graph email | Checks a credential the API never sees; admin work in Settings, like the connection test of an integration. |
| Email settings (SMTP, Microsoft Graph), backup destination, license loading | Hold secrets or change the instance itself; admin work in Settings with two-factor authentication. |
| Integration credentials (0.4.0): saving the Action1 client id and secret, testing the connection, mapping organizations to clients, and the agent installer link per organization; from 0.6.0 switching on that Action1 follows the clients and sites, and dismissing a change that waits for Action1 | Holds a secret and changes what the instance talks to; admin work in Settings. Reading the configuration is on the waiting list above. |
| Secrets of any kind: webhook URLs and signing secrets, enrollment tokens after creation, TOTP seeds, password hashes, signing keys, certificates' private keys, the root key | CLAUDE.md, Secrets: write-only, never returned after creation. |
| Signed job payloads and signatures, signing requests | Internal to the signer, gateway and agent. |
| First-admin setup, sign-in, two-factor setup, user administration actions | Account security belongs in the UI. |
| Opening, joining or relaying remote control and remote background sessions (0.3.0) | Interactive, end-to-end encrypted sessions that need the browser's ephemeral key and a person at the keyboard; an API key can never be a participant. The session history is on the waiting list above. |
