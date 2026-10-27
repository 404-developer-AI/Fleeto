# Dashboard designer (0.8.0)

Dashboards an admin designs in Fleeto itself and links to roles: which tiles a user sees, how large they are and where they stand.
Decided with the developer on 2026-10-27; this file is the design. Built on 2026-10-27 for the test build `v0.8.0-alpha.7`;
`MD-Files/ARCHITECTURE.md` §5 (Dashboards) describes it as part of web.

## 1. Decisions (2026-10-27)

- **Place on the roadmap**: now, before the rest of 0.8.0 (before patch management on Linux; the open tests of the Proxmox web
  interface go on in parallel).
- **Shared dashboards only.** A dashboard is made by a user with the permission "Edit dashboards" and linked to roles. Users do not
  make or change dashboards of their own. Personal dashboards can come later without changing the data model (a dashboard with an
  owner instead of roles).
- **One dashboard per user, priority wins.** Dashboards are kept in one ordered list. A user sees the first dashboard in that order
  that is linked to one of its roles; a user whose roles have none sees the built-in default dashboard. No switcher.
- **Our own grid, no library.** Dragging, resizing and placing tiles is code of Fleeto (`wwwroot/js/dashboard-grid.mjs`), on a CSS
  grid with pointer events. No new package in the UI of a product with high privileges.
- **Tiles follow permissions, a dashboard never grants anything.** A tile shows only when the user holds its permission and only the
  data its groups give, wherever it is placed. Linking a dashboard to a role changes what the role sees laid out, never what it may see.

## 2. What it is and is not

- A dashboard is a **layout of tiles** with settings per tile, of the whole instance. It holds no data of its own: every tile reads
  live data through the same services and scope as the rest of the UI.
- The **built-in default dashboard** is defined in code (`DefaultDashboard`), like the built-in roles: the dashboard of today, laid out
  in the new grid. It cannot be changed, only copied into a dashboard of the instance. A new release can place a new tile on it.
- The **expiring-credential warnings** stay above every dashboard as they are now, not as a tile: a warning that stops sign-in or
  email must not be removable by a layout.
- Not a report builder, not a query language and not a way to show data from outside Fleeto. Every tile is a tile type in the catalog
  in code.

## 3. The tile catalog

Every tile type is an entry in one catalog in code (`DashboardTiles`, in `Fleeto.Core`), in the way of `Permissions`:

| Field | Meaning |
|---|---|
| `Key` | Stable key, stored in layouts (`alerts.open-list`). Never renamed; a removed tile keeps its key reserved. |
| `Group`, `Name`, `Description` | How the palette of the designer shows it. |
| `Permissions` | The permissions a user needs to see it (all of them); empty only for content tiles (text, heading, link). |
| `MinSize`, `MaxSize`, `DefaultSize` | In grid columns and rows. |
| `Options` | The settings the tile offers. One settings record (`TileSettings`) serves every type; validation clears what a type does not offer. |
| `Source` | The data source in web (`DashboardTileSources`); content tiles and the security advisories (which load themselves) have none. |
| `LiveEvents` | The `LiveUpdates` events after which the tile reloads (endpoint status, alerts, jobs, ...). |
| `Since` | The version that added it. |

**The rule for the future** (also in `CLAUDE.md`, Conventions): every new part of the dashboard is a tile type in this catalog, with its
permission, sizes and settings, never markup in a page. A feature that adds a tile decides whether the default dashboard shows it
(`DefaultDashboard`); a dashboard of the instance gets it only when somebody adds it, like a custom role gets a new permission only when
somebody turns it on. A test fails when a tile type has no renderer, no permission (outside the content group), sizes that do not fit
the grid, or when the default dashboard does not validate.

### Tile types at the start

Counters (one number, a state color and a line of detail; larger sizes show more detail):

- Endpoints online, Endpoints offline, Agents out of date, Open alerts, Patch compliance, Vulnerabilities, Licenses, Instance health,
  Security, Backups: the tiles of today.
- New: Endpoints (by class, operating system and tier), Endpoints in maintenance, Alerts on hold, Managed licenses free.

Lists (rows up to the height of the tile, a link to the full page):

- Open alerts (filter on severity, alert kind, held or not), Recent activity, Recent jobs (filter on result), Security advisories:
  the lists of today.
- New: Endpoints offline (longest offline first), Agents out of date, Running and failed tasks.

Overviews:

- **Client status**: a cell per client (or per site of a chosen client) colored by its worst state (critical, warning, maintenance,
  offline, ok), with the open alert count; clicking opens the client in the Clients workspace.
- **Charts**: alerts opened per day by severity (7, 30 or 90 days), endpoints by operating system or class (bar), top clients by open
  alerts. Charts read summaries, never raw tables (see §6).

Content (no permission, no data):

- **Heading** (a title across the grid to make sections), **Text** (markdown through `NoteMarkdown`, at most 4,000 characters, no
  HTML, no images), **Link** (to a page of Fleeto or an `https://` address, opened in a new tab for an outside address; never
  `javascript:`, `data:` or another scheme).

### Settings every tile has

- **Title**: the name of the tile type by default, or a title of its own (at most 60 characters).
- **Scope**: all clients the user sees, or a narrower choice: chosen clients, chosen sites, clients with a tag, servers or workstations.
  A scope only narrows: the user still sees nothing outside its groups, and a tile whose scope holds nothing the user sees shows its
  empty state, never data outside its groups.
- **Accent**: none, or a color from the fixed palette of Fleeto for the border of the tile.
- **Thresholds** (counters): from which value the tile turns warning and critical, where the tile type allows it (for example agents
  out of date only from 5).
- **Hide on small screens**.

The dashboard as a whole has a **scope** with the same choices; a tile follows it unless it has its own.

## 4. The grid

- **12 columns** on a wide screen, rows of 64 pixels, a gap of 12 pixels. A tile has `x`, `y`, `w`, `h` in columns and rows.
- **Placing**: dragging a tile moves the tiles it lands on down; tiles never overlap. "Pull up" (on by default per dashboard) moves
  every tile up as far as it can after a change, so no holes remain.
- **On the screen of the user** a tile it may not see is left out and the tiles below move up into its place, so a user with fewer
  permissions sees a closed layout and no empty spaces.
- **Narrower screens** (container queries on the dashboard, so the width of the sidebar counts): up to 1100 pixels the grid has 6 columns
  and the tiles flow in reading order like text, each at most 6 columns wide, at the first free place after the tile before it
  (`DashboardGrid.Narrow`); up to 600 pixels the tiles stand under each other in reading order, full width, without the tiles hidden on
  small screens.
- **Keyboard**: in the designer a selected tile moves with the arrow keys and resizes with Shift and the arrow keys, so the designer
  works without a mouse.

## 5. The designer

Settings, Dashboards (administration, permission "Edit dashboards"):

- **The list**: every dashboard in priority order, which roles it is linked to, and per role which dashboard its users see now
  (a role linked to two dashboards sees the higher one; the list says so). Arrow buttons move a dashboard up or down in the order
  (decided while building: buttons work with the keyboard and need no drag on a table). New, copy (also of the default dashboard),
  rename, delete, export and import.
- **Linking to roles** on the dashboard itself; a role can be linked to several dashboards, the order decides.
- **The designer** opens full width with three parts: the palette of tile types on the left (search, grouped, a tile the chosen roles
  cannot see is marked as such), the grid in the middle, the settings of the selected tile on the right. A tile is added by dragging it
  from the palette or by clicking it (placed at the first free spot).
- **Draft and publish**: changes are kept as a draft, saved on every change, so closing the window loses nothing. Users keep seeing the
  published version until somebody with the permission publishes. "Discard draft" goes back to the published version.
- **Undo and redo** (Ctrl+Z, Ctrl+Y) inside the designer, up to 50 steps.
- **Preview as role**: the grid shows the dashboard as a user with that role would see it, with live data: tiles without its
  permission are left out and the rest moves up. The data is that of the editor itself, never of somebody else's groups. While
  designing, a tile the linked roles cannot see is faded on the grid and marked in the palette.
- **The grid script** (`wwwroot/js/dashboard-grid.mjs`) turns pointer and key input into a cell and asks the server; the server places
  the tile with `DashboardGrid` and renders every position. While a tile is dragged the other tiles move as the server answers and a
  dashed outline shows where it lands.
- **History**: the last 20 published versions with who published them and when; one can be made the draft and published again. Web
  removes older versions when it publishes (rather than the workers: it is one delete in the same transaction).
- **Export and import** as a JSON file (`format` `fleeto-dashboard`, at most 256 KB), so a dashboard can move to another instance of
  Steaan. An import becomes a new dashboard with only a draft, linked to no role. References to clients, sites and tags that do not
  exist in this instance are dropped with a note, unknown tile types refused with their key.

## 6. Performance

- **Only the tiles on the dashboard are read.** Each tile type has a data source (`IDashboardTileSource`); the page asks only the
  sources of the tiles it shows, in parallel, and tiles with the same source and scope share one read. The current `DashboardService`,
  which reads everything for everybody, is split into these sources.
- **Each tile on its own**: a tile that fails shows "Could not load" with a retry and the cause in the log; the others are not affected.
  A slow tile shows its skeleton while the rest is there.
- **Live**: a tile reloads only on the events of its own `LiveEvents`, debounced as today. A dashboard with 40 tiles does not run 40
  queries on every alert.
- **Charts and counts over time** read a daily count kept by the workers (`DashboardDailyCounts`, per client and UTC day: alerts opened
  by severity, counted again for today and yesterday every 10 minutes, kept 400 days), never a scan of alerts per request. A user that
  sees a client in part gets the alerts of its own endpoints counted instead, so a chart never counts an endpoint outside its groups.
  Endpoints by group and Top clients read the endpoints and the open alerts, which are small. Each source is measured by the
  performance monitor as `tile:<source>`.
- **Limits**: at most 60 tiles per dashboard and 100 dashboards per instance.

## 7. Security

- Permission **"Edit dashboards"** (`dashboards.manage`, Administration group): admin only by default. Like every administration
  permission it counts only for a user whose groups give every client, because tile scopes name clients and sites.
- The layout is validated on the server on every save, publish and import against the catalog: known tile keys, sizes within the
  bounds of the type, inside the grid, no overlap, settings of the right type and range, scopes that name existing clients, sites and
  tags, text and link tiles as in §3. The JSON is never trusted from the browser; the grid script only proposes positions.
- **A dashboard never widens access**: tiles are filtered by the permissions of the viewing user and their data by its groups, in the
  data sources, as today. A test proves that a role without `alerts.view` on a dashboard with the open alert list gets neither the tile
  nor its data.
- Text tiles render through the same sanitizing markdown as notes; link tiles allow only relative paths of Fleeto and `https://`.
- Audit: create, publish, delete, import, link and unlink of a role, and the order, with the name of the dashboard; never the text of
  a text tile.
- **Rollback (expand/contract)**: a tile key the running release does not know (a layout saved by a newer release) is skipped on
  screen and kept on save, so an image rollback never breaks or loses a dashboard.

## 8. Data and API

- `Dashboards`: `Id`, `Name` (unique, at most 80 characters), `Description`, `Priority` (unique), `PublishedLayout` (jsonb),
  `DraftLayout` (jsonb, null when there is no draft), `PublishedVersion`, `PublishedAt`, `PublishedBy`, `UpdatedAt`, `UpdatedBy`,
  `CreatedAt`. Of the instance, no `ClientId`: tile scopes refer to clients by id and are resolved within the scope of the viewer.
- `DashboardRoles`: `DashboardId`, `RoleId` (primary key on both, cascade on deleting either).
- `DashboardVersions`: `DashboardId`, `Version`, `Layout` (jsonb), `PublishedAt`, `PublishedBy`; web keeps the last 20 when it publishes.
- The layout JSON: `{ "schema": 1, "pullUp": true, "scope": {...}, "tiles": [ { "id", "type", "x", "y", "w", "h", "settings": {...} } ] }`,
  with `schema` so a later release can migrate it.
- A deleted client, site or tag is left out of a scope when the dashboard is shown and the next time it is saved; a scope that names
  nothing that exists any more follows the dashboard's (or, for the dashboard, every client). A tag that exists but that no client
  carries shows nothing, never everything.
- Public API: dashboards are not in the API; on `MD-Files/API-WAITLIST.md` as read access to the dashboard of the caller's role when
  there is demand. The data of the tiles stays reachable through its own endpoints (`/api/v1/summary` on the waiting list).

## 9. Build plan

Built 2026-10-27 for the test build `v0.8.0-alpha.7`.

1. [done] **Catalog and sources**: `DashboardTiles`, the tile components (`Components/Dashboards`), `DashboardTileSources` and
   `DashboardService` split per source, `DefaultDashboard`, and the dashboard rendered through it. Tests: the catalog rules of §3, every
   source against the permission and the groups (`DashboardRuleTests`, `DashboardServiceTests`).
2. [done] **Data and resolution**: the tables and migration `Dashboards` (new tables only, so the release before runs on the schema), the
   permission, `DashboardRules.Pick`, Settings, Dashboards with order, roles, copy, delete, export, import and audit.
3. [done] **Designer**: `dashboard-grid.mjs` (drag, resize, keyboard, palette drop; Node tests in `tests/browser/dashboard-grid.test.mjs`),
   push down, pull up, the 6-column and stacked layouts, palette, tile settings, draft and publish, undo and redo, preview as role,
   history, and server validation with tests.
4. [done] **Tile settings and new tiles**: scope, title, accent, thresholds, filters; the new counters and lists, client status,
   heading, text, link.
5. [done] **Charts**: `DashboardDailyCounts` and `DashboardCountService` (a `WorkerLoop`), the three charts.
6. [done] **Docs**: `MD-Files/ARCHITECTURE.md`, `MD-Files/CHANGELOG.md`, `MD-Files/API-WAITLIST.md`, vocabulary in
   `MD-Files/branding-fleeto.md` (dashboard, tile), `CLAUDE.md`.
7. [open] **With the developer on the test VPS**: the designer in a real browser (drag, resize, keyboard, palette drop, preview,
   publish), the layouts at 6 columns and on a phone, and the default dashboard against the dashboard of `v0.8.0-alpha.6`. The
   designer was checked with tests and the grid styling with screenshots of a static page, not in a signed-in browser.

## 10. Open

- Whether a user may later choose between the dashboards of its roles (now: no, priority wins).
- Whether charts need more than the three of step 5 before the release.
- A layout stored by a newer release with a value an older release does not know (a new kind of alert in a filter) cannot be read by
  the older one; the user then sees the default dashboard until the update, rather than nothing.
