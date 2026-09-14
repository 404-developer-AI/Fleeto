# Fleeto — Branding Guide

> Single source of truth for names, colors, typography and tone for **Fleeto**,
> the RMM product by Steaan. Derived from the Steaan company branding guide
> (`branding.md`); where this document is silent, that document applies.
> Working name — pending domain and trademark checks (BOIP/EUIPO, `.app`/`.com`/`.be`/`.nl`).

## 1. Brand hierarchy

| Level | Name | Where | Notes |
|---|---|---|---|
| Company | **Steaan** | `steaan.com` | Umbrella brand, "Focused software for IT teams, built in Belgium." |
| Product | **Fleeto** | `fleeto.app` (to register) | Remote monitoring and management. Always "Fleeto · by Steaan" when the company is mentioned. |
| Internal code | **Fleetify** | repo, namespaces, Docker | Never user-visible. Mirrors the Migravo/Migrify convention. See §7. |

Sibling products: Migravo (mailbox migration), Ticksy (servicedesk). Same design tokens across all three.

## 2. Name usage

- Write **Fleeto** with a capital first letter only, never all-caps, never with a suffix like "App", "RMM" or "Cloud".
- Product pages, page titles and email sender name use **Fleeto**. Company pages use **Steaan**.
- The eyebrow line on product pages is `Fleeto · by Steaan` (middle dot, spaces on both sides).
- Do not abbreviate. No "FLT", no "Fl.".
- "RMM" may be used in customer-facing text once the full term has been introduced ("remote monitoring and management (RMM)").

## 3. Taglines and messaging

| Context | Text |
|---|---|
| Fleeto tagline | Monitor and manage every endpoint with confidence. |
| Fleeto lede | Fleeto keeps an eye on your Windows, Linux and macOS endpoints, your Proxmox and VMware hosts, and the tools around them — with checks that run on your schedule, from every few seconds to once a month. |
| Primary call to action | Start for free with agent-only endpoints |
| Secondary call to action | See how it works |
| Reassurance line | No credit card required · Cancel anytime |

Messaging pillars, in priority order:

1. **Trust**: security first — signed agents, encrypted transport, full audit trail.
2. **Reliability**: the platform stays up and tells the truth; an alert means something.
3. **Speed**: dashboards and log search respond instantly, even at 10,000 endpoints.
4. **Any platform**: Windows, Linux, macOS, Proxmox, VMware, and API integrations for the rest.

Avoid: fear-based wording ("before disaster strikes"), exclamation marks, superlatives, "AI-powered" fluff.

## 4. Color

Identical to the Steaan design tokens (see company `branding.md` §4). Light mode only,
solid surfaces, no gradients, no purple. Primary teal `#0F766E`, stone neutrals,
radius 8/12px, text-tinted shadows.

Fleeto-specific status colors (endpoint and check states):

| State | Color | Use |
|---|---|---|
| Online / OK | `--mg-primary-tint` bg, `--mg-primary` text | Status chips |
| Job running | `--mg-primary-tint-strong` bg | Active operations |
| Warning | Amber (MudBlazor default) | Threshold breached, degraded |
| Offline / Error | `#DC2626` | Failed checks, unreachable endpoints |
| Unknown / Paused | Neutral gray chip | Maintenance mode, never seen |

Token prefix in the Fleeto codebase is `--fl-`, values identical to the `--mg-` tokens.
When Steaan tokens change, both codebases change in the same commit.

## 5. Typography, logo, icons

- Typography: identical to company guide §5 (Inter 300–700, headings 600, body 16px site / 14px app).
- Logo: no wordmark. Name in Inter 600 next to a 32×32 rounded square (radius 7–8px) in teal, containing a white **activity/heartbeat line** icon (Material `MonitorHeart` outlined or equivalent). Favicon: same mark, `site/favicon-fleeto.svg`.
- The mark is always teal on white or white on teal. Minimum size 24px; below that, name only.
- App icons: Material Icons, Outlined variant only.

## 6. Vocabulary

Use these terms consistently in UI, docs and email; do not introduce synonyms.

| Term | Meaning |
|---|---|
| **instance** | One Fleeto installation with its own URL, database and licenses, belonging to one customer (an IT team or MSP). Customers never see other instances. |
| **client** | A customer of the IT team; identified by a client code and a client name. The top of the hierarchy inside an instance. |
| **site** | A group of endpoints within a client; where policies and monitoring templates are linked |
| **endpoint** | Anything with an agent or monitored via an integration (server, workstation, hypervisor). Never "device" or "machine" in UI text. |
| **workstation / server** | The two endpoint classes; the tabs in an endpoint list are Servers, Workstations and Mixed (all endpoints together) |
| **agent-only / managed** | The two license tiers of an endpoint. Agent-only is free: visible with inventory, nothing else. Managed uses one license and unlocks everything. Never "free tier" or "premium" in UI text. |
| **license** | One managed endpoint. "3 of 50 licenses in use." |
| **agent** | The Fleeto software installed on an endpoint |
| **remote control** | Taking over an endpoint screen from the browser. Never "remote desktop", "screen sharing" or a vendor name. |
| **API key** | A credential for the public API, created in Settings |
| **check** | A single monitoring rule with an interval (e.g. disk space, every 5 minutes) |
| **alert** | A check that crossed its threshold and needs attention |
| **maintenance mode** | A client, site or endpoint temporarily raising no alerts. "In maintenance until 16:00." Never "snooze", "mute" or "silenced" in UI text. Shown with the neutral gray chip and the outlined Construction icon. |
| **hold** | One alert set aside until a time: "Put on hold", "On hold until 16:00", "End hold". No emails while it lasts; the alert returns when the hold ends. Never "snooze", "mute" or "silenced". Shown with the neutral gray chip. |
| **reset** | A check's state set aside and the check run again: "Reset and run", state "Re-run requested" until the new result arrives. Never shown as OK before it is. |
| **not run yet** | A check that applies to an endpoint but has no result yet |
| **watchdog** | The second Fleeto service on an endpoint that keeps the agent running (0.2.0). Never "helper" or "guardian". |
| **remote terminal** | A command line on an endpoint from the browser (0.3.0). Never "remote shell", "SSH" or "console" in UI text. |
| **job** | One execution of a script, patch run or task on an endpoint |
| **policy** | Agent behaviour settings linked to a site |
| **monitoring template** | A named set of checks with thresholds, linked to a site |
| **client template** | A blueprint of sites, policies and monitoring templates used when creating a client |
| **integration** | A connected external product (Action1, Sophos, Veeam, ...) |
| **note** | Free-form text (markdown) attached to an endpoint |

Intervals are written in plain language: "every 30 seconds", "every 5 minutes", "once a month".

## 7. Internal naming (Fleetify)

User-visible text says Fleeto; the following use **Fleetify** and must not be renamed:

- .NET namespaces and projects (`Fleetify.Web`, `Fleetify.Core`, `Fleetify.Infrastructure`, `Fleetify.Agent`)
- Repository name, Docker image, container and volume names
- CSS bundle and token prefix `--fl-`
- Environment variables and secret names (`FLEETIFY_ROOT_KEY_FILE`, ...)
- Log file names `fleetify-{Date}.log`
- Agent service names on endpoints (`fleetify-agent`, and `fleetify-watchdog` from 0.2.0)

Check: `grep -rn "Fleetify" src/**/*.razor` should return identifiers only, never a string a customer can see.

## 8. Tone of voice

Company guide §8 applies in full: English UI, direct and calm, second person,
honest about limits, no emoji, no exclamation marks. Fleeto additions:

- Alerts state the cause and the next step. "SRV-DC01 has less than 10% free disk space on C:" beats "Disk problem detected".
- Never cry wolf in copy: reserve red for genuine failure states.
- Timestamps in the user's own time zone; relative times ("2 min ago") switch to absolute dates after 24 hours.

## 9. Email and screenshots

Company guide §9 and §10 apply unchanged: sender name **Fleeto**, no subject prefixes,
same email card layout and hard-coded color constants, plain-text alternative always,
anonymised screenshots only (no real endpoint names, sites or tenants), no stock photos.
