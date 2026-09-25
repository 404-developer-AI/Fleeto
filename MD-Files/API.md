# Fleeto — Public API

> The contract of the public REST API of a Fleeto instance, written so that a developer or an AI can build an
> integration from this file alone. Rules for keeping it complete are in `CLAUDE.md` (Public API): every API change
> updates this file in the same commit, and a test fails when an endpoint in the OpenAPI document is not documented
> here (a section titled `### GET /api/v1/...`) or is documented here but not built. Features of Fleeto that are not
> in the API yet are listed in `API-WAITLIST.md`.

API version: **v1**. Available from Fleeto **0.2.1**. Read-only.

## Contents

1. Overview
2. Authentication
3. Rate limits
4. Conventions
5. Pagination
6. Errors
7. Access: client scope, license tier and audit
8. Endpoints: clients, sites, endpoints, alerts, jobs
9. Objects
10. Values of enumerations
11. Example: a full synchronisation
12. OpenAPI document
13. Version history

## 1. Overview

Every Fleeto instance has its own URL (for example `https://rmm.customer.example`) and serves the API on that URL under
`/api/v1`. The API gives read access to the data of the instance: clients, sites, endpoints with status and
inventory, checks, alerts, jobs and notes. It cannot change anything; there are only `GET` requests.

The hierarchy is **client → site → endpoint**. A client is a customer of the IT team; a site groups endpoints of one
client; an endpoint is a computer with the Fleeto agent. An endpoint is either **agent-only** (free: status and
inventory only) or **managed** (licensed: checks, alerts, jobs, notes and everything else).

Base URL: `https://<instance FQDN>/api/v1`. Only HTTPS.

## 2. Authentication

Every request (except the OpenAPI document) needs an **API key** in the `Authorization` header:

```http
GET /api/v1/clients HTTP/1.1
Host: rmm.customer.example
Authorization: Bearer flt_3f0c9a8e5d2b4c1a9e7f6d5c4b3a2918_q2V0aW5nLXNlY3JldC1leGFtcGxlLW5vdC1yZWFs
Accept: application/json
```

- An admin creates keys in Fleeto under **Settings, API keys**. The key is shown **once**; Fleeto stores only a hash and
  cannot show it again. Store it in the secret store of the tool that uses it, never in source code.
- Format: `flt_<id>_<secret>`. `<id>` is 32 hexadecimal characters, `<secret>` is 43 base64url characters (256 random
  bits). The fixed `flt_` prefix lets secret scanners recognise a leaked key.
- A key is **read-only** and has, per key:
  - a **client scope**: all clients (also clients created later), or a chosen list of clients;
  - an **expiry**: 30 days, 90 days, 1 year or none;
  - a **revocation**: an admin can revoke a key at any time; it stops working on its next request.
- The key is only accepted in the `Authorization` header with the scheme `Bearer`. A key in the URL or a browser session
  cookie is never accepted.
- A missing, malformed, unknown, revoked or expired key gets `401 Unauthorized` with the header
  `WWW-Authenticate: Bearer` and error code `unauthorized` (see Errors). Do not retry a 401 without changing the key.

## 3. Rate limits

| Limit | Value | Applies to |
|---|---|---|
| Per API key | 120 requests per minute (token bucket: a burst of up to 120 requests, then one request every 0.5 seconds) | Every authenticated request |
| Per address | 300 requests per minute (IPv6: per /64) | Every request under `/api/v1`, before the key is checked |

Values are the defaults of an instance; Steaan can change them per instance (`PublicApi:RequestsPerMinute` and
`PublicApi:AddressRequestsPerMinute`).

Over a limit the answer is `429 Too Many Requests` with error code `rate_limited` and a `Retry-After` header in seconds.
Wait at least that long before the next request. Good practice: page through lists with `limit=200` instead of fetching
single items, run a full synchronisation at most every few minutes, and fetch inventories only for endpoints whose
data you need.

## 4. Conventions

- **JSON** in UTF-8. Responses have `Content-Type: application/json; charset=utf-8`, errors
  `application/problem+json`.
- **Field names** are camelCase (`clientId`, `lastSeenAt`).
- **Values of enumerations** are lowercase snake_case strings (`agent_only`, `pending_signature`). The allowed values
  are listed per field and in section 10.
- **Ids** are UUIDs in the standard 36-character form (`77c5d95d-1a92-4205-aec2-eb63037f8bde`). They never change.
- **Timestamps** are ISO 8601 in UTC with a `Z` and up to 7 fractional digits (`2026-09-15T17:03:12.968782Z`).
  Timestamps are set by the Fleeto server, never taken from the endpoint clock.
- **Nulls are explicit**: every documented field is always present; a value that is not known or does not apply is
  `null`. Lists are `[]` when empty, never `null`.
- **Numbers** are JSON numbers. Byte counts are integers (64-bit), check values are floating point.
- **Forward compatibility**: new fields and new values of enumerations can be added in a minor Fleeto release without a
  new API version. Ignore fields you do not know, and treat an unknown enumeration value as "other". Removing or
  renaming a field, or changing its meaning, needs a new API version (`/api/v2`). Field names are frozen from Fleeto
  1.0.0; before 1.0.0 a breaking change is possible but is listed in section 13.
- **Query parameters**: write names as documented (`clientId`); values are case-sensitive (`state=open`, not `Open`). Unknown query parameters are ignored.
- **Caching**: responses carry `Cache-Control: no-store` because they contain personal data (user names, IP addresses,
  notes). Do not cache them in shared caches.
- **Methods**: only `GET`. Any other method gets `405` with error code `method_not_allowed`.

## 5. Pagination

Lists use **keyset pagination** with an opaque cursor:

| Query parameter | Type | Default | Meaning |
|---|---|---|---|
| `limit` | integer, 1 to 200 | 50 | Items per page. |
| `cursor` | string | none | The `nextCursor` of the previous page. Leave out for the first page. |

Every list answers with a page object:

```json
{
  "items": [ ... ],
  "nextCursor": "djF8QUNNRXw3N2M1ZDk1ZDFhOTI0MjA1YWVjMmViNjMwMzdmOGJkZQ"
}
```

- `nextCursor` is `null` on the last page. Keep requesting with `cursor=<nextCursor>` until it is `null`.
- Pass the **same filters** with every page. A cursor is only valid for the list and filters it came from.
- A cursor is opaque: do not build, parse or store it for longer than a synchronisation run. Its format may change
  between Fleeto releases. An invalid cursor gets `400 invalid_parameter`; start again without a cursor.
- Pages are stable while you page: an item added or removed during the run can be missed or appear at a page boundary,
  but no item is returned twice and paging never falls back to the first page.
- Lists have no total count.

Order per list: clients by `code`, sites by `name`, endpoints by `hostname` (all ascending, then by id); alerts by
`openedAt`, jobs by `createdAt`, notes by `createdAt` (all newest first, then by id).

## 6. Errors

Errors are [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "The alert does not exist, or this API key cannot read it.",
  "status": 404,
  "detail": "Check the id. A key limited to clients only sees the data of those clients.",
  "code": "not_found"
}
```

| Field | Meaning |
|---|---|
| `type` | A URI for the HTTP status. |
| `title` | What went wrong, in plain English. Can change between releases; show it to people, do not parse it. |
| `status` | The HTTP status code. |
| `detail` | What to do next. |
| `code` | Stable machine-readable error code (table below). Use this in code. |

| Status | `code` | When | What to do |
|---|---|---|---|
| 400 | `invalid_parameter` | A query parameter has a wrong value or type: `limit` out of range, an unknown value for `state`, `class`, `tier` or `severity`, a malformed UUID, a search text over 100 characters, an invalid cursor. | Fix the request. The `detail` names the parameter when it is known. |
| 401 | `unauthorized` | No key, a malformed or unknown key, or a revoked or expired key. | Use a valid key. Do not retry with the same key. |
| 404 | `not_found` | The path does not exist, the id does not exist, the object belongs to a client outside the key's scope, or an endpoint has not reported an inventory yet. | Check the id and the key's client scope. |
| 405 | `method_not_allowed` | A method other than `GET`. | Use `GET`. |
| 409 | `endpoint_not_managed` | Checks or notes of an agent-only endpoint, or of any endpoint while the instance license has expired past its grace period. | Nothing to fetch; the endpoint has no checks or notes to show. |
| 429 | `rate_limited` | A rate limit was reached. | Wait for `Retry-After` seconds. |
| 500 | `internal_error` | Something failed in Fleeto. | Retry later with backoff; contact the administrator of the instance if it persists. |

## 7. Access: client scope, license tier and audit

- **Client scope.** A key limited to clients sees only those clients and everything under them (sites, endpoints,
  alerts, jobs, notes). Objects of other clients are invisible: lists leave them out, also when you filter on their id,
  and single objects answer `404 not_found`, exactly as for an id that does not exist.
- **License tier.** Agent-only endpoints show their status and inventory. Checks and notes exist only on managed
  endpoints and answer `409 endpoint_not_managed` for an agent-only endpoint. `effectiveTier` on the endpoint tells you
  which applies: an endpoint stored as managed behaves as agent-only once the instance license has expired past its
  14-day grace period. Alerts and jobs stay readable as history after an endpoint becomes agent-only.
- **Audit.** Every API request is written to the audit log of the instance with the key's name, the path, the query
  string, the status and the client address. Admins see it under Settings, Audit log.

## 8. Endpoints

Fourteen endpoints, all `GET`. Shared query parameters `limit` and `cursor` are described in section 5; objects in
section 9.

| Method and path | Returns |
|---|---|
| `GET /api/v1/clients` | Page of Client |
| `GET /api/v1/clients/{clientId}` | Client |
| `GET /api/v1/sites` | Page of Site |
| `GET /api/v1/sites/{siteId}` | Site |
| `GET /api/v1/endpoints` | Page of Endpoint |
| `GET /api/v1/endpoints/{endpointId}` | Endpoint |
| `GET /api/v1/endpoints/{endpointId}/inventory` | Inventory |
| `GET /api/v1/endpoints/{endpointId}/patches` | PatchState |
| `GET /api/v1/endpoints/{endpointId}/patch-deployments` | Array of Deployment |
| `GET /api/v1/endpoints/{endpointId}/checks` | EndpointChecks |
| `GET /api/v1/endpoints/{endpointId}/notes` | Page of Note |
| `GET /api/v1/alerts` | Page of Alert |
| `GET /api/v1/alerts/{alertId}` | Alert |
| `GET /api/v1/jobs` | Page of Job |
| `GET /api/v1/jobs/{jobId}` | Job |
| `GET /api/v1/jobs/{jobId}/output` | JobOutput |

### GET /api/v1/clients

Lists the clients the key can read, ordered by client code.

| Query parameter | Type | Required | Meaning |
|---|---|---|---|
| `search` | string, at most 100 characters | no | Part of the client code or name, case-insensitive. |
| `tag` | string, at most 32 characters | no | Only clients with this tag: the full tag name, case-insensitive. |
| `limit`, `cursor` | | no | Pagination (section 5). |

Response `200`: a page of [Client](#client). Errors: 400, 401, 429.

```http
GET /api/v1/clients?search=acme&tag=contract-gold&limit=2
```

```json
{
  "items": [
    {
      "id": "77c5d95d-1a92-4205-aec2-eb63037f8bde",
      "code": "ACME",
      "name": "Acme Manufacturing",
      "tags": [
        { "name": "contract-gold", "color": "amber" },
        { "name": "manufacturing", "color": "blue" }
      ],
      "siteCount": 2,
      "endpointCount": 48,
      "maintenance": null,
      "createdAt": "2026-09-01T08:12:40.113Z",
      "updatedAt": "2026-09-10T14:02:11.5Z"
    }
  ],
  "nextCursor": null
}
```

### GET /api/v1/clients/{clientId}

Gets one client.

| Path parameter | Type | Meaning |
|---|---|---|
| `clientId` | UUID | The id of the client. |

Response `200`: a [Client](#client). Errors: 401, 404, 429.

### GET /api/v1/sites

Lists the sites the key can read, ordered by name.

| Query parameter | Type | Required | Meaning |
|---|---|---|---|
| `clientId` | UUID | no | Only the sites of this client. |
| `limit`, `cursor` | | no | Pagination (section 5). |

Response `200`: a page of [Site](#site). Errors: 400, 401, 429.

```json
{
  "items": [
    {
      "id": "6567477f-f767-40f0-8b42-3390636a803f",
      "clientId": "77c5d95d-1a92-4205-aec2-eb63037f8bde",
      "name": "Monitoring",
      "description": "Servers and workstations with full monitoring",
      "endpointCount": 41,
      "maintenance": {
        "startedAt": "2026-09-15T15:00:00Z",
        "endsAt": "2026-09-15T17:00:00Z",
        "startedBy": "Jane Technician",
        "reason": "Firewall replacement"
      },
      "createdAt": "2026-09-01T08:12:40.113Z",
      "updatedAt": "2026-09-01T08:12:40.113Z"
    }
  ],
  "nextCursor": null
}
```

### GET /api/v1/sites/{siteId}

Gets one site.

| Path parameter | Type | Meaning |
|---|---|---|
| `siteId` | UUID | The id of the site. |

Response `200`: a [Site](#site). Errors: 401, 404, 429.

### GET /api/v1/endpoints

Lists endpoints with their status, ordered by hostname. Filters combine with AND.

| Query parameter | Type | Required | Meaning |
|---|---|---|---|
| `clientId` | UUID | no | Only endpoints of this client. |
| `siteId` | UUID | no | Only endpoints of this site. |
| `class` | `workstation` or `server` | no | The class that applies (`class` in the response, an override by a technician included). |
| `tier` | `agent_only` or `managed` | no | The stored tier (`tier` in the response, not `effectiveTier`). |
| `online` | `true` or `false` | no | Only online or only offline endpoints. |
| `search` | string, at most 100 characters | no | Part of the hostname, case-insensitive. |
| `limit`, `cursor` | | no | Pagination (section 5). |

Response `200`: a page of [Endpoint](#endpoint). Errors: 400, 401, 429.

```http
GET /api/v1/endpoints?clientId=77c5d95d-1a92-4205-aec2-eb63037f8bde&class=server&online=false
```

```json
{
  "items": [
    {
      "id": "ed836aaa-a879-4456-964e-ccb96274fa15",
      "clientId": "77c5d95d-1a92-4205-aec2-eb63037f8bde",
      "siteId": "6567477f-f767-40f0-8b42-3390636a803f",
      "hostname": "SRV-DC01",
      "class": "server",
      "detectedClass": "server",
      "classOverridden": false,
      "tier": "managed",
      "effectiveTier": "managed",
      "source": "agent",
      "online": false,
      "lastSeenAt": "2026-09-15T16:58:02.41Z",
      "os": { "platform": "windows", "name": "Windows Server 2022 Standard", "version": "10.0.20348" },
      "architecture": "amd64",
      "agentVersion": "0.2.1",
      "enrolledAt": "2026-09-02T09:30:00Z",
      "publicIpAddress": "203.0.113.24",
      "publicIpSeenAt": "2026-09-15T08:01:13Z",
      "openAlertCount": 1,
      "heldAlertCount": 0,
      "maintenance": null,
      "createdAt": "2026-09-02T09:30:00Z",
      "updatedAt": "2026-09-15T16:58:02.41Z"
    }
  ],
  "nextCursor": null
}
```

### GET /api/v1/endpoints/{endpointId}

Gets one endpoint with its status.

| Path parameter | Type | Meaning |
|---|---|---|
| `endpointId` | UUID | The id of the endpoint. |

Response `200`: an [Endpoint](#endpoint). Errors: 401, 404, 429.

### GET /api/v1/endpoints/{endpointId}/inventory

Gets the latest inventory the agent reported: hardware, disks, network interfaces, installed software and services.
Available for agent-only and managed endpoints.

| Path parameter | Type | Meaning |
|---|---|---|
| `endpointId` | UUID | The id of the endpoint. |

Response `200`: an [Inventory](#inventory). Errors: 401, 404 (also when the endpoint has not reported an inventory yet;
the `title` says which), 429.

```json
{
  "endpointId": "ed836aaa-a879-4456-964e-ccb96274fa15",
  "receivedAt": "2026-09-15T08:01:20Z",
  "manufacturer": "Dell Inc.",
  "model": "PowerEdge R650",
  "serialNumber": "7XK2Q93",
  "cpu": { "model": "Intel(R) Xeon(R) Silver 4314 CPU @ 2.40GHz", "cores": 16, "logicalProcessors": 32 },
  "memoryTotalBytes": 137438953472,
  "bootTime": "2026-09-01T02:14:55Z",
  "domain": "acme.local",
  "loggedOnUser": "ACME\\administrator",
  "disks": [ { "mount": "C:", "filesystem": "NTFS", "totalBytes": 479069872128, "freeBytes": 201863462912 } ],
  "networkInterfaces": [ { "name": "Ethernet0", "macAddress": "00:50:56:a1:2b:3c", "ipAddresses": [ "10.0.0.10", "fe80::250:56ff:fea1:2b3c" ] } ],
  "software": [ { "name": "Microsoft SQL Server 2022", "version": "16.0.1000.6", "publisher": "Microsoft Corporation", "installDate": "20260902" } ],
  "services": [ { "name": "MSSQLSERVER", "displayName": "SQL Server (MSSQLSERVER)", "startType": "automatic", "state": "running" } ],
  "action1AgentId": "ef17c844-5b7c-4b32-9724-f2716b596639",
  "desktop": "graphical"
}
```

### GET /api/v1/endpoints/{endpointId}/patches

Gets the patch state of a managed endpoint as Action1 last reported it (from Fleeto 0.4.0). Fleeto does not patch itself:
Action1 does, and this is what it says.

Answers `409 endpoint_not_managed` for an agent-only endpoint, and `404 not_found` when the endpoint does not exist or
patch management does not cover it — no Action1 integration for its client, no Action1 agent on the endpoint, or Action1
does not know it yet. Counts are refreshed every four hours; the list of missing updates is read for endpoints that miss
something, which can be one pass behind the counts. `detailUpdatedAt` says when that list was read.

```json
{
  "endpointId": "2b5f1b7a-6d0c-4f0e-9bcd-3f9a0f1d7e21",
  "coverage": "active",
  "compliant": false,
  "missingCritical": 1,
  "missingOther": 2,
  "rebootRequired": false,
  "productLastSeenAt": "2026-09-20T18:41:02Z",
  "productAgentVersion": "2.0.33",
  "updatedAt": "2026-09-20T19:02:11Z",
  "detailUpdatedAt": "2026-09-20T19:02:14Z",
  "missing": [
    {
      "id": "Microsoft_Windows_Server_2022_1570243626751_builtin",
      "name": "2026-09 Cumulative Update for Windows Server 2022",
      "vendor": "Microsoft",
      "version": "10.0.20348.2700",
      "kbNumber": "KB5034123",
      "severity": "critical",
      "rebootNeeded": true
    }
  ]
}
```

### GET /api/v1/endpoints/{endpointId}/patch-deployments

Lists the deployments of updates that touched a managed endpoint, newest first (from Fleeto 0.4.0). A deployment is
started by a technician in Fleeto and carried out by Action1; what every endpoint did is what Action1 reports, not what
Fleeto expects. A deployment covers one client, so a run over several clients appears as one deployment per client with
the same `batchId`.

| Path parameter | Type | Meaning |
|---|---|---|
| `endpointId` | UUID | The id of the endpoint. |

| Query parameter | Type | Default | Meaning |
|---|---|---|---|
| `limit` | integer | 10 | Deployments to return, 1 to 50. |

Response `200`: an array of [Deployment](#deployment), which is empty when this endpoint has never been part of one.
Errors: 401, 404 (the endpoint does not exist), 409 `endpoint_not_managed`, 429.

```json
[
  {
    "id": "f6c2a1d4-9b3e-4b7a-8c15-2d4e6f8a0b12",
    "batchId": "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d",
    "clientId": "c0a80101-0000-4000-8000-000000000001",
    "scope": "specified",
    "autoReboot": false,
    "state": "completed",
    "statusMessage": null,
    "requestedByName": "Sam Jansen",
    "requestedAt": "2026-09-21T09:14:52Z",
    "completedAt": "2026-09-21T09:38:10Z",
    "updates": [ "Google Chrome 126.0.6478.115" ],
    "targets": [
      {
        "endpointId": "2b5f1b7a-6d0c-4f0e-9bcd-3f9a0f1d7e21",
        "hostname": "ACME-WS-014",
        "state": "succeeded",
        "message": null
      }
    ]
  }
]
```

### GET /api/v1/endpoints/{endpointId}/checks

Gets every check that applies to a managed endpoint, with its current state: the checks of the monitoring templates of
its site, of monitoring templates linked to the endpoint, and checks that exist only on this endpoint. A check with
several targets (for example free disk space on every drive) has one item per target.

| Path parameter | Type | Meaning |
|---|---|---|
| `endpointId` | UUID | The id of the endpoint. |

Response `200`: an [EndpointChecks](#endpointchecks). Errors: 401, 404, 409 `endpoint_not_managed`, 429.

```json
{
  "endpointId": "ed836aaa-a879-4456-964e-ccb96274fa15",
  "configurationPending": false,
  "items": [
    {
      "checkId": "c1a2b3c4-d5e6-4f70-8192-a3b4c5d6e7f8",
      "name": "Free disk space",
      "type": "disk_free",
      "target": "C:",
      "status": "warning",
      "value": 12.4,
      "detail": "59.4 GB free of 446.2 GB",
      "error": "",
      "lastResultAt": "2026-09-15T16:45:00Z",
      "intervalSeconds": 900,
      "source": "site_template",
      "monitoringTemplateName": "Windows servers",
      "adjusted": false,
      "openAlertId": "0b9d7e1c-8a2f-4c3d-9e8f-7a6b5c4d3e2f",
      "parameters": { "drive": "*" }
    },
    {
      "checkId": "9e8d7c6b-5a49-4382-b1c0-d9e8f7a6b5c4",
      "name": "SQL Server running",
      "type": "service_running",
      "target": "",
      "status": "not_run_yet",
      "value": null,
      "detail": "",
      "error": "",
      "lastResultAt": null,
      "intervalSeconds": 60,
      "source": "endpoint",
      "monitoringTemplateName": null,
      "adjusted": false,
      "openAlertId": null,
      "parameters": { "service": "MSSQLSERVER" }
    }
  ]
}
```

### GET /api/v1/endpoints/{endpointId}/notes

Lists the notes of a managed endpoint, newest first. Notes are markdown written by technicians.

| Path parameter | Type | Meaning |
|---|---|---|
| `endpointId` | UUID | The id of the endpoint. |

| Query parameter | Type | Required | Meaning |
|---|---|---|---|
| `limit`, `cursor` | | no | Pagination (section 5). |

Response `200`: a page of [Note](#note). Errors: 400, 401, 404, 409 `endpoint_not_managed`, 429.

```json
{
  "items": [
    {
      "id": "0fdfaa47-7f13-4f8e-999e-6f931e534569",
      "endpointId": "ed836aaa-a879-4456-964e-ccb96274fa15",
      "authorName": "Jane Technician",
      "body": "Replaced the **RAID controller battery**. Next check in March.",
      "createdAt": "2026-09-15T17:03:12.968782Z",
      "editedAt": null
    }
  ],
  "nextCursor": null
}
```

### GET /api/v1/alerts

Lists alerts, newest first (by `openedAt`).

| Query parameter | Type | Required | Meaning |
|---|---|---|---|
| `state` | `open`, `acknowledged`, `on_hold` or `resolved` | no | Leave out for every state. `open` and `acknowledged` leave out alerts on hold; `on_hold` returns unresolved alerts on hold (open or acknowledged). |
| `severity` | `warning` or `critical` | no | Only alerts of this severity. |
| `clientId` | UUID | no | Only alerts of this client. |
| `endpointId` | UUID | no | Only alerts of this endpoint. |
| `limit`, `cursor` | | no | Pagination (section 5). |

Response `200`: a page of [Alert](#alert). Errors: 400, 401, 429.

To follow new and changed alerts, poll `GET /api/v1/alerts?state=open` and `?state=on_hold` and compare `updatedAt`,
or page through all alerts newest first and stop at an `openedAt` you already processed and whose `updatedAt` has not
changed. For real-time delivery use a webhook notification channel (Settings, Notification channels) instead of
polling.

```json
{
  "items": [
    {
      "id": "0b9d7e1c-8a2f-4c3d-9e8f-7a6b5c4d3e2f",
      "clientId": "77c5d95d-1a92-4205-aec2-eb63037f8bde",
      "endpointId": "ed836aaa-a879-4456-964e-ccb96274fa15",
      "kind": "check",
      "checkId": "c1a2b3c4-d5e6-4f70-8192-a3b4c5d6e7f8",
      "target": "C:",
      "severity": "warning",
      "state": "open",
      "onHold": false,
      "heldUntil": null,
      "title": "SRV-DC01 has less than 15% free disk space on C:",
      "detail": "59.4 GB free of 446.2 GB",
      "openedAt": "2026-09-15T16:45:00Z",
      "updatedAt": "2026-09-15T16:45:00Z",
      "acknowledgedAt": null,
      "resolvedAt": null,
      "resolvedReason": null
    }
  ],
  "nextCursor": null
}
```

### GET /api/v1/alerts/{alertId}

Gets one alert.

| Path parameter | Type | Meaning |
|---|---|---|
| `alertId` | UUID | The id of the alert. |

Response `200`: an [Alert](#alert). Errors: 401, 404, 429.

### GET /api/v1/jobs

Lists jobs (script runs on endpoints), newest first (by `createdAt`). A run on several endpoints is one job per endpoint
with the same `batchId`.

| Query parameter | Type | Required | Meaning |
|---|---|---|---|
| `clientId` | UUID | no | Only jobs of this client. |
| `endpointId` | UUID | no | Only jobs of this endpoint. |
| `state` | a [job state](#job-state) | no | Only jobs in this state. |
| `limit`, `cursor` | | no | Pagination (section 5). |

Response `200`: a page of [Job](#job). Errors: 400, 401, 429.

```json
{
  "items": [
    {
      "id": "5d4c3b2a-1908-4f7e-a6d5-c4b3a2918070",
      "batchId": "a1b2c3d4-e5f6-4071-8293-a4b5c6d7e8f9",
      "clientId": "77c5d95d-1a92-4205-aec2-eb63037f8bde",
      "endpointId": "ed836aaa-a879-4456-964e-ccb96274fa15",
      "type": "script",
      "script": {
        "id": "f0e1d2c3-b4a5-4697-8879-6a5b4c3d2e1f",
        "versionId": "0a1b2c3d-4e5f-4a6b-9c7d-8e9f0a1b2c3d",
        "name": "Clear print spooler",
        "versionNumber": 3,
        "language": "powershell",
        "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
      },
      "runAs": "service",
      "runAsAccount": null,
      "runAsChosenAccount": null,
      "state": "succeeded",
      "result": "exited",
      "exitCode": 0,
      "problem": null,
      "initiatedBy": "Jane Technician",
      "createdAt": "2026-09-15T14:00:00Z",
      "validUntil": "2026-09-16T14:00:00Z",
      "deliveredAt": "2026-09-15T14:00:01Z",
      "startedAt": "2026-09-15T14:00:01Z",
      "completedAt": "2026-09-15T14:00:07Z",
      "output": { "state": "complete", "truncated": false, "bytes": 212 }
    }
  ],
  "nextCursor": null
}
```

### GET /api/v1/jobs/{jobId}

Gets one job.

| Path parameter | Type | Meaning |
|---|---|---|
| `jobId` | UUID | The id of the job. |

Response `200`: a [Job](#job). Errors: 401, 404, 429.

### GET /api/v1/jobs/{jobId}/output

Gets the output of a job as text: the first megabyte of stdout and of stderr, decoded as UTF-8 (bytes that are not
valid UTF-8 become the replacement character `U+FFFD`). While the job runs the output grows; fetch again later.

| Path parameter | Type | Meaning |
|---|---|---|
| `jobId` | UUID | The id of the job. |

Response `200`: a [JobOutput](#joboutput). Errors: 401, 404, 429.

```json
{
  "jobId": "5d4c3b2a-1908-4f7e-a6d5-c4b3a2918070",
  "state": "complete",
  "stdout": "Stopping spooler...\nDeleted 14 files.\nSpooler running.\n",
  "stdoutBytes": 52,
  "stderr": "",
  "stderrBytes": 0,
  "shortened": false,
  "truncated": false
}
```

## 9. Objects

Every field is always present. "Nullable" means the value can be `null`.

### Page

| Field | Type | Meaning |
|---|---|---|
| `items` | array | The objects of this page. |
| `nextCursor` | string, nullable | Pass as `cursor` for the next page; `null` on the last page. |

### Client

| Field | Type | Meaning |
|---|---|---|
| `id` | UUID | |
| `code` | string | Short unique code in uppercase, for example `ACME`. |
| `name` | string | |
| `tags` | array of [Tag](#tag) | The tags of the client, ordered by name; empty when it has none. |
| `siteCount` | integer | Sites of the client. |
| `endpointCount` | integer | Endpoints of the client. |
| `maintenance` | [Maintenance](#maintenance), nullable | The client's own maintenance while it is active; `null` otherwise. |
| `createdAt` | timestamp | |
| `updatedAt` | timestamp | Also changes when the tags of the client change. |

### Tag

A tag on a client (0.6.0). Tags are typed on a client in the UI; a tag name is unique within the instance regardless of
case, and every client that carries it shows the same name and color. A client has at most 10 tags.

| Field | Type | Meaning |
|---|---|---|
| `name` | string | 1 to 32 characters: letters, digits and `-` `_` `.` `+`, no spaces. |
| `color` | string | The palette color, see [Tag color](#tag-color). |

### Site

| Field | Type | Meaning |
|---|---|---|
| `id` | UUID | |
| `clientId` | UUID | |
| `name` | string | Unique within the client. |
| `description` | string, nullable | |
| `endpointCount` | integer | Endpoints of the site. |
| `maintenance` | [Maintenance](#maintenance), nullable | The site's own maintenance while it is active; `null` otherwise. |
| `createdAt` | timestamp | |
| `updatedAt` | timestamp | |

### Maintenance

An active maintenance of a client or site. While a client, site or endpoint is in maintenance, its endpoints open and
escalate no alerts; open alerts stay open and still resolve.

| Field | Type | Meaning |
|---|---|---|
| `startedAt` | timestamp | |
| `endsAt` | timestamp, nullable | `null`: until someone turns it off. |
| `startedBy` | string, nullable | Name of the user who started it. |
| `reason` | string, nullable | Free text entered when it was started. |

### EndpointMaintenance

The maintenance that applies to an endpoint now. Of several active maintenances (its own, its site's, its client's, a
maintenance window of its policy) the one that lasts longest is shown.

| Field | Type | Meaning |
|---|---|---|
| `source` | [maintenance source](#maintenance-source) | Where it comes from. |
| `startedAt` | timestamp | |
| `endsAt` | timestamp, nullable | `null`: until someone turns it off. |
| `startedBy` | string, nullable | Name of the user who started it; `null` for a policy window. |
| `reason` | string, nullable | Free text, or the name of the maintenance window. |
| `policyName` | string, nullable | For `policy_window`: the name of the policy. |

### Endpoint

| Field | Type | Meaning |
|---|---|---|
| `id` | UUID | |
| `clientId` | UUID | |
| `siteId` | UUID | |
| `hostname` | string | |
| `class` | [endpoint class](#endpoint-class) | The class that applies: `detectedClass`, unless a technician overrode it. |
| `detectedClass` | [endpoint class](#endpoint-class) | Derived from the operating system edition. |
| `classOverridden` | boolean | `true` when a technician set the class by hand. |
| `tier` | [tier](#tier) | The stored license tier. |
| `effectiveTier` | [tier](#tier) | The tier the endpoint behaves as: `agent_only` for every endpoint once the license has expired past its grace period. |
| `source` | [endpoint source](#endpoint-source) | |
| `online` | boolean | `true` while the agent has a live connection. |
| `lastSeenAt` | timestamp, nullable | Last message from the agent; `null` if it never connected. |
| `os` | object | `platform` (`windows`, `linux`), `name` (for example `Windows Server 2022 Standard`), `version`. |
| `architecture` | string | For example `amd64` or `arm64`. |
| `agentVersion` | string | Version of the Fleeto agent. |
| `enrolledAt` | timestamp | |
| `publicIpAddress` | string, nullable | The address the agent connected from last time (personal data). A private address (for example `172.16.10.96`) when the agent reaches the instance inside a private network, such as the same LAN with local DNS or a VPN; its public IP is then not known. |
| `publicIpSeenAt` | timestamp, nullable | |
| `openAlertCount` | integer | Unresolved alerts, not counting alerts on hold. |
| `heldAlertCount` | integer | Unresolved alerts on hold. |
| `maintenance` | [EndpointMaintenance](#endpointmaintenance), nullable | `null` when the endpoint is not in maintenance. |
| `createdAt` | timestamp | |
| `updatedAt` | timestamp | |

### PatchState

The patch state of one managed endpoint (0.4.0), read from Action1 and stored per endpoint. Fleeto keeps no patch history
of its own; Action1 has it.

| Field | Type | Meaning |
|---|---|---|
| `endpointId` | UUID | |
| `coverage` | string | [Patch coverage](#patch-coverage). `inactive` means Action1 no longer patches this endpoint, so the counts below say nothing about its real state. |
| `compliant` | boolean | True when the endpoint misses no update Action1 knows about. |
| `missingCritical` | integer | Missing updates the vendor calls critical. |
| `missingOther` | integer | Missing updates of every other severity. |
| `rebootRequired` | boolean | Action1 says the endpoint waits for a restart to finish its updates. |
| `productLastSeenAt` | timestamp, nullable | When Action1 last had contact with the endpoint. An endpoint it has not seen for over a week opens an alert of kind `patch_state`. |
| `productAgentVersion` | string | The version of the Action1 agent on the endpoint. Action1 updates its own agent; Fleeto only reports what is there. |
| `updatedAt` | timestamp | When Fleeto last read the counts. |
| `detailUpdatedAt` | timestamp, nullable | When Fleeto last read `missing`. Null while no detail has been read. |
| `missing` | array | The missing updates, most severe first: `id` (the id in Action1, which a deployment names), `name`, `vendor`, `version`, `kbNumber` (Windows only, else empty), `severity` ([Patch severity](#patch-severity)), `rebootNeeded`. Empty for a compliant endpoint, and empty when the detail has not been read yet while the counts say something is missing. |

### Deployment

One deployment of updates started in Fleeto and carried out by Action1 (0.4.0). It belongs to one client, because Action1
runs a deployment inside one organization.

| Field | Type | Meaning |
|---|---|---|
| `id` | UUID | |
| `batchId` | UUID | Shared by every deployment started in one action, also across clients. |
| `clientId` | UUID | The client of the deployment. |
| `scope` | string | [Deployment scope](#deployment-scope). |
| `autoReboot` | boolean | True when Action1 may restart an endpoint by itself to finish the updates. |
| `state` | string | [Deployment state](#deployment-state). |
| `statusMessage` | string, nullable | Cause and next step when something went wrong, in the wording an admin sees in Fleeto. Null while nothing is wrong. |
| `requestedByName` | string | The technician who started it (personal data). |
| `requestedAt` | timestamp | When it was started in Fleeto. |
| `completedAt` | timestamp, nullable | When every endpoint had an answer, or Fleeto stopped following it. Null while it runs. |
| `updates` | array of string | The chosen updates with their version, for a deployment of scope `specified`. Empty for `all_missing`, where Action1 decides what is missing at the moment it runs. |
| `targets` | array | One per endpoint: `endpointId`, `hostname` (as it was when the deployment started), `state` ([Deployment target state](#deployment-target-state)) and `message` (what Action1 said, null when it said nothing). |

### Inventory

Reported by the agent. The values come from the endpoint as reported and are empty strings or `0` when the agent could
not read them.

| Field | Type | Meaning |
|---|---|---|
| `endpointId` | UUID | |
| `receivedAt` | timestamp | When Fleeto received this inventory. |
| `manufacturer`, `model`, `serialNumber` | string | Hardware identification. |
| `cpu` | object | `model` (string), `cores` (integer), `logicalProcessors` (integer). |
| `memoryTotalBytes` | integer | Physical memory. |
| `bootTime` | timestamp, nullable | Last start of the operating system. |
| `domain` | string | Windows domain or DNS domain; on Linux the realm the endpoint is joined to. Empty when none. |
| `loggedOnUser` | string | User of the interactive session, empty when nobody is logged on (personal data). |
| `disks` | array | Per disk: `mount` (`C:` or `/var`), `filesystem`, `totalBytes`, `freeBytes`. |
| `networkInterfaces` | array | Per interface: `name`, `macAddress`, `ipAddresses` (array of strings, IPv4 and IPv6). |
| `software` | array | Installed software, from the registry on Windows and from dpkg or rpm on Linux: `name`, `version`, `publisher`, `installDate` (as the operating system reports it, often `yyyyMMdd`; may be empty, as it is for dpkg packages). |
| `action1AgentId` | string | The id of the Action1 agent installed on the endpoint, read on the endpoint itself (0.4.0). Empty when Action1 is not installed, when the agent is older than 0.4.0, or on Linux, where Fleeto does not read it yet. Patch management uses it to match an endpoint to its Action1 record. |
| `desktop` | string, nullable | Whether the endpoint has a graphical desktop (0.6.0): `graphical`, or `none` for a Linux endpoint without a display manager, graphical session or X server and for Windows Server Core and Nano Server. `null` while the agent is older than 0.6.0. Remote control is offered only when it is not `none`; remote background works either way. |
| `services` | array | Services: Windows services, or systemd services on Linux (`name` without the `.service` suffix, `displayName` is the unit description). Per service: `name`, `displayName`, `startType` (`automatic`, `automatic_delayed` (Windows), `manual`, `disabled`, or empty when unknown; a systemd unit that is enabled, static, generated or indirect is `automatic`, one that is disabled is `manual` and a masked one is `disabled`) and `state` (`running`, `stopped`, `starting`, `stopping`, `paused` (Windows), or empty when unknown). Sorted by display name. |

### EndpointChecks

| Field | Type | Meaning |
|---|---|---|
| `endpointId` | UUID | |
| `configurationPending` | boolean | `true` while the agent has not applied the latest check configuration yet; states can lag behind a change. |
| `items` | array of [Check](#check) | Ordered by check name, then target. |

### Check

| Field | Type | Meaning |
|---|---|---|
| `checkId` | UUID | The check definition. The same id appears in `Alert.checkId`. |
| `name` | string | |
| `type` | [check type](#check-type) | |
| `target` | string | What the result is about when a check has several (a drive, a certificate); empty otherwise. |
| `status` | [check status](#check-status) | |
| `value` | number, nullable | The last measured value in the unit of the type; `null` when not run yet, re-run requested or not a number. |
| `detail` | string | Human-readable result, for example `59.4 GB free of 446.2 GB`. |
| `error` | string | Why the check could not run; empty when it ran. |
| `lastResultAt` | timestamp, nullable | When the last result was received. |
| `intervalSeconds` | integer | How often the check runs. |
| `source` | [check source](#check-source) | Where the check comes from. |
| `monitoringTemplateName` | string, nullable | For template checks: the monitoring template. |
| `adjusted` | boolean | `true` when the interval, thresholds or failures before alert are adjusted for this endpoint. |
| `openAlertId` | UUID, nullable | The unresolved alert of this check and target. |
| `parameters` | object of strings | The settings of the check, per type (see [check type](#check-type)). |

### Alert

| Field | Type | Meaning |
|---|---|---|
| `id` | UUID | |
| `clientId` | UUID | |
| `endpointId` | UUID | |
| `kind` | [alert kind](#alert-kind) | |
| `checkId` | UUID, nullable | For `check` alerts: the check; `null` for other kinds or when the check was deleted. |
| `target` | string | The target of the check; empty when not applicable. |
| `severity` | [severity](#severity) | |
| `state` | [alert state](#alert-state) | |
| `onHold` | boolean | `true` while the alert is on hold: no emails and not counted as open. |
| `heldUntil` | timestamp, nullable | When the hold ends; `null` when not on hold. |
| `title` | string | Cause in one sentence, for example `SRV-DC01 has less than 15% free disk space on C:`. |
| `detail` | string | More detail; may be empty. |
| `openedAt` | timestamp | |
| `updatedAt` | timestamp | Last change of any field. |
| `acknowledgedAt` | timestamp, nullable | |
| `resolvedAt` | timestamp, nullable | |
| `resolvedReason` | string, nullable | Why it was resolved, in plain English, for example `Check returned to OK`, `Reset by Jane Technician` or `Resolved manually by Jane Technician`. Show it; do not parse it. |

### Job

| Field | Type | Meaning |
|---|---|---|
| `id` | UUID | |
| `batchId` | UUID | Jobs started together share it. |
| `clientId` | UUID | |
| `endpointId` | UUID | |
| `type` | string | `script`. More types may be added. |
| `script` | object | The script as it was when the job was created: `id` (UUID, nullable: `null` after the script was deleted), `versionId` (UUID, nullable), `name`, `versionNumber` (integer), `language` ([script language](#script-language)), `sha256` (hex SHA-256 of the script body). |
| `runAs` | [job run as](#job-run-as) | The account the script ran under on the endpoint. |
| `runAsAccount` | string, nullable | For `logged_on_user`: the account the agent ran the script under, as `DOMAIN\name` on Windows and the user name on Linux, once the job started. `null` for `service` and before the start. |
| `runAsChosenAccount` | string, nullable | For `logged_on_user`: the user the technician chose to run the script as, in the same form as `runAsAccount`. The job runs only in a session of that user and fails when they are not signed in. `null` when no user was chosen: the agent then takes the console session first. A run for all signed-in users creates one job per user with this field set, all with the same `batchId`. From Fleeto 0.2.2. |
| `state` | [job state](#job-state) | |
| `result` | [job result](#job-result), nullable | What the agent reported when the job ended; `null` before. |
| `exitCode` | integer, nullable | Exit code of the script. |
| `problem` | string, nullable | Why a job was refused or could not run. |
| `initiatedBy` | string | Name of the user who started the job. |
| `createdAt` | timestamp | |
| `validUntil` | timestamp | A job that has not started by then expires. |
| `deliveredAt`, `startedAt`, `completedAt` | timestamp, nullable | |
| `output` | object | `state` ([job output state](#job-output-state)), `truncated` (boolean: the output exceeded the limit and was cut off on the endpoint), `bytes` (integer: output bytes stored, both streams). |

### JobOutput

| Field | Type | Meaning |
|---|---|---|
| `jobId` | UUID | |
| `state` | [job output state](#job-output-state) | |
| `stdout` | string | The first megabyte of standard output. |
| `stdoutBytes` | integer | All stored bytes of standard output. |
| `stderr` | string | The first megabyte of standard error. |
| `stderrBytes` | integer | All stored bytes of standard error. |
| `shortened` | boolean | `true` when `stdout` or `stderr` holds less than is stored (over one megabyte, or a gap while output still arrives). |
| `truncated` | boolean | `true` when the endpoint cut the output off at the output limit. |

### Note

| Field | Type | Meaning |
|---|---|---|
| `id` | UUID | |
| `endpointId` | UUID | |
| `authorName` | string | Name of the author when the note was written. |
| `body` | string | Markdown, at most 20,000 characters. Treat as untrusted text: render with raw HTML disabled. |
| `createdAt` | timestamp | |
| `editedAt` | timestamp, nullable | When the author last changed it. |

## 10. Values of enumerations

### Endpoint class

`workstation`, `server`.

### Tier

`agent_only` (free: status and inventory), `managed` (licensed: everything).

### Endpoint source

`agent` (an enrolled Fleeto agent), `integration` (a hypervisor host or VM from an integration, later).

### Maintenance source

`endpoint`, `site`, `client`, `policy_window` (a recurring maintenance window of the policy of the endpoint's site).

### Tag color

`red`, `orange`, `amber`, `lime`, `green`, `teal`, `cyan`, `blue`, `pink`, `brown`, `gray`. A new tag gets a color derived
from its name; an admin can choose another one from this list.

### Check status

| Value | Meaning |
|---|---|
| `ok` | Within thresholds. |
| `warning` | Past the warning threshold. |
| `critical` | Past the critical threshold, or a yes/no check that failed. |
| `unknown` | The check could not run (see `error`). |
| `not_run_yet` | The check applies but has no result yet. |
| `rerun_requested` | A technician reset the check; waiting for the new result. |
| `disabled` | The check is switched off for this endpoint. |

### Check source

`site_template` (a monitoring template linked to the site), `endpoint_template` (a monitoring template linked to this
endpoint), `endpoint` (a check that exists only on this endpoint).

### Check type

| Value | Measures (`value`) | `parameters` | Platforms |
|---|---|---|---|
| `cpu_usage` | Average CPU usage, % | none | all |
| `memory_usage` | Physical memory in use, % | none | all |
| `disk_free` | Free space per drive, %; one item per drive | `drive` (`C:`, `/var` or `*`) | all |
| `service_running` | 1 running, 0 not running | `service` | all |
| `uptime` | Days since the last restart | none | all |
| `ping` | Average round-trip time, ms; -1 no reply | `host`, `count` | all |
| `tcp_port` | Connect time, ms; -1 not reachable | `host`, `port`, `timeout_seconds` | all |
| `http` | Response time, ms; -1 failed; target `certificate`: days until the TLS certificate expires | `url`, `expected_status`, `contains`, `timeout_seconds`, `certificate_warning_days`, `certificate_critical_days`, `ignore_certificate_errors` | all |
| `process_running` | Number of running processes with the name | `process` | all |
| `pending_reboot` | 1 no restart pending, 0 restart pending | none | Windows, Linux |
| `file` | `condition` `exists`/`missing`: 1 or 0; `size`: MB; `age`: hours since the last change | `path`, `condition` | all |
| `certificate_expiry` | Days until expiry; one item per certificate | `location` (`store` or `path`), `store`, `path`, `subject` | all |
| `event_log` | Matching events within the window | `log`, `source`, `event_ids`, `level`, `window_minutes` | Windows |
| `security_center` | 1 protection on, 0 off | `component` (`antivirus` or `firewall`) | Windows |
| `script` | Exit code of a library script: 0 ok, 1 warning, other critical | `script` (script id), `language` | per script language |

Parameters that were never set are left out of `parameters`, and the check uses its default.

### Alert kind

`check` (a check crossed its threshold), `offline` (the agent and its watchdog stopped connecting), `duplicate_identity`
(the same agent certificate connected twice at once, for example a cloned virtual machine), `agent_stopped` (the watchdog
is online but the agent is not: its service is stopped or it does not connect; from Fleeto 0.2.1), `watchdog_stopped`
(the agent is online but its watchdog is not; from Fleeto 0.2.1), `patch_state` (patch management does not patch the
endpoint any more, or has not seen it for over a week; from Fleeto 0.4.0).

### Patch severity

`critical`, `important`, `moderate`, `low`, `unspecified`. The words of the vendor of the update, as Action1 reports them;
anything Fleeto does not recognise is `unspecified` rather than something worse or better than it is.

### Patch coverage

`active` (patch management patches this endpoint), `inactive` (it knows the endpoint but does not patch it, for example
because the endpoint is above the licensed number of the Action1 subscription, so its state is not to be trusted).

### Deployment scope

`all_missing` (every update Action1 reports as missing on the endpoints at the moment it runs), `specified` (only the
updates the technician chose, listed in `updates`).

### Deployment state

| Value | Meaning |
|---|---|
| `requested` | Written in Fleeto; not handed to Action1 yet. Normally a matter of seconds. |
| `running` | Action1 accepted the deployment and is working through the endpoints. |
| `completed` | Every endpoint reached an end state. That includes endpoints where the installation failed, so read `targets`. |
| `failed` | Action1 refused the deployment; nothing was installed. `statusMessage` says why. |
| `abandoned` | Action1 did not finish within a day, so Fleeto stopped following it. What happened afterwards is in the Action1 console. |

### Deployment target state

`pending` (handed to Action1, not started on this endpoint), `running`, `succeeded`, `failed`, `unknown` (the deployment
ended without Action1 saying what happened here, or it used a word Fleeto does not know, which is then in `message`).

### Severity

`warning`, `critical`.

### Alert state

`open`, `acknowledged` (a technician is on it), `resolved`. Whether an unresolved alert is on hold is the separate
field `onHold`.

### Job state

| Value | Meaning |
|---|---|
| `pending_signature` | Created; waiting to be signed by the instance's signing service. |
| `queued` | Signed; delivered as soon as the endpoint is online. |
| `running` | The endpoint started it. |
| `succeeded` | Finished with exit code 0. |
| `failed` | Finished with another exit code, timed out, or could not start. |
| `expired` | Not started before `validUntil`. |
| `refused` | Refused by the signing service or the agent; see `problem`. |
| `lost` | Started, but no result arrived within its timeout: the outcome is unknown. |
| `cancelled` | Cancelled by a technician before delivery. |

### Job result

`exited`, `timed_out`, `refused`, `failed_to_start`, `interrupted` (the agent stopped while the job ran).

### Job run as

| Value | Meaning |
|---|---|
| `service` | SYSTEM on Windows, root on Linux: the account the agent service itself runs as. |
| `logged_on_user` | The user of the active session on the endpoint. A job fails with `failed_to_start` when nobody is signed in. |

### Job output state

`none` (no output yet), `receiving`, `complete`, `incomplete` (part of the output never arrived).

### Script language

`powershell`, `batch` (Windows), `sh`, `bash` (Linux).

## 11. Example: a full synchronisation

Pull all endpoints with their open alerts into another tool. Python with `requests`; the same pattern works in any
language.

```python
import os, time, requests

BASE = "https://rmm.customer.example/api/v1"
session = requests.Session()
session.headers["Authorization"] = "Bearer " + os.environ["FLEETO_API_KEY"]

def get(path, **params):
    while True:
        response = session.get(BASE + path, params=params, timeout=30)
        if response.status_code == 429:
            time.sleep(int(response.headers.get("Retry-After", "5")))
            continue
        if response.status_code >= 400:
            problem = response.json()
            raise RuntimeError(f"{problem['code']}: {problem['title']} {problem['detail']}")
        return response.json()

def pages(path, **params):
    cursor = None
    while True:
        page = get(path, limit=200, **params, **({"cursor": cursor} if cursor else {}))
        yield from page["items"]
        cursor = page["nextCursor"]
        if cursor is None:
            return

clients = {c["id"]: c for c in pages("/clients")}
for endpoint in pages("/endpoints"):
    client = clients[endpoint["clientId"]]
    print(client["code"], endpoint["hostname"], "online" if endpoint["online"] else "offline",
          endpoint["openAlertCount"], "open alerts")

for alert in pages("/alerts", state="open"):
    print(alert["severity"], alert["title"])
```

With curl:

```bash
curl -sS -H "Authorization: Bearer $FLEETO_API_KEY" "https://rmm.customer.example/api/v1/endpoints?online=false&limit=200"
```

## 12. OpenAPI document

`GET /api/v1/openapi.json` returns an OpenAPI 3.1 document of this API, generated by the instance from its code. It
needs no API key (it describes the API, not data) and is rate limited per address. Import it into Postman, Insomnia or a
client generator. This file (`API.md`) adds what the document cannot carry well: conventions, error codes, meanings of
values and examples.

## 13. Version history

| Fleeto | API | Change |
|---|---|---|
| 0.6.0 | v1 | `desktop` on Inventory (additive). |
| 0.6.0 | v1 | `tags` on Client and the `tag` filter on `GET /api/v1/clients` (additive). |
| 0.2.2 | v1 | `runAsChosenAccount` on Job: the user the technician chose for a `logged_on_user` job (additive). |
| 0.2.1 | v1 | `runAsAccount` on Job: the signed-in user a `logged_on_user` job ran as (additive). |
| 0.2.1 | v1 | `runAs` on Job: the account the script ran under on the endpoint (additive). |
| 0.2.1 | v1 | Alert kinds `agent_stopped` and `watchdog_stopped` (additive). |
| 0.2.1 | v1 | First version: read-only access to clients, sites, endpoints (status, inventory, checks, notes), alerts and jobs (with output). API keys with client scope, expiry and revocation; rate limits per key and per address; audit per request. |
