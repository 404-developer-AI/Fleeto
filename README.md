# Fleeto

Remote monitoring and management (RMM) by Steaan. Internal code name: Fleetify.

Status: 0.1.0, first usable release (not yet tagged). Windows agent, enrollment with mTLS, agent-only and managed
endpoints with licensing, checks and alerts, dashboard, backups. See [`MD-Files/ROADMAP.md`](MD-Files/ROADMAP.md).

## Where to start

| File | What it is |
|---|---|
| [`CLAUDE.md`](CLAUDE.md) | Rules, priorities, conventions, product model. Read this first. |
| [`MD-Files/ARCHITECTURE.md`](MD-Files/ARCHITECTURE.md) | Components, data model, flows, security architecture, install and update. |
| [`MD-Files/ROADMAP.md`](MD-Files/ROADMAP.md) | Planned versions. |
| [`MD-Files/CHANGELOG.md`](MD-Files/CHANGELOG.md) | Unreleased changes and the two most recent versions. |
| [`MD-Files/CHANGELOG-ARCHIVE.md`](MD-Files/CHANGELOG-ARCHIVE.md) | Older versions. |
| [`MD-Files/branding-fleeto.md`](MD-Files/branding-fleeto.md) | Names, colors, vocabulary, tone of voice. |
| [`deploy/README.md`](deploy/README.md) | VPS installation, updates, backups and restore. |
| [`deploy/RELEASING.md`](deploy/RELEASING.md) | Building, signing and publishing a release. |

## Local development on Windows (no Docker)

Docker is only used on the VPS and in CI. Locally every component runs as a plain .NET process.

### Prerequisites

- .NET SDK 10.0
- PostgreSQL 17 running on `localhost:5432` (TimescaleDB optional)
- Go 1.27 (for the agent)
- PowerShell 7 (`pwsh`)
- A trusted ASP.NET development certificate: `dotnet dev-certs https --trust`

### First time

```powershell
pwsh tools/dev/setup-dev.ps1          # asks for the password of the PostgreSQL user 'postgres'
```

The script is idempotent. It creates, outside the repository, `%LOCALAPPDATA%\Fleetify\dev` with the root key,
signer key and database passwords (readable only by you), the PostgreSQL roles and the database `fleetify_dev`,
development license and release signing keys, a development license for `localhost`, and
`Directory.Build.local.props` (public keys only, gitignored). It then builds the solution, applies the migrations and
prints a one-time setup link for the first admin.

### Run

```powershell
pwsh tools/dev/start-dev.ps1          # builds, migrates, starts signer, gateway, workers and web in separate windows
```

| URL | What |
|---|---|
| https://localhost:7100 | Web UI |
| https://localhost:7200 | Agent gateway (mTLS) |
| http://localhost:5201/health | Gateway health |

Then:

1. Open the setup link from `setup-dev.ps1` and create the first admin. Two-factor authentication is mandatory; keep
   an authenticator app at hand. A new link: run `setup-dev.ps1` again while no admin exists.
2. Settings, Licensing: load `%LOCALAPPDATA%\Fleetify\dev\dev-license.txt` (25 managed endpoints for `localhost`).
3. Create a client, a site and an enrollment token on the site page. The install command is shown once.
4. Build the agent: `pwsh tools/dev/build-agent.ps1`.
5. Run the agent on this PC. Either paste the install command in an elevated PowerShell (installs the Windows service
   `fleetify-agent`), or run it without admin rights in the foreground:

   ```powershell
   agent\dist\windows-amd64\fleetify-agent.exe run --foreground --state-dir .\agent-dev --key-store file `
       --server localhost:7200 --token fet_... --ca-fingerprint <fingerprint from the install command>
   ```

   `fleetify-agent.exe status --state-dir .\agent-dev` shows the enrollment state. Remove the service again with
   `fleetify-agent.exe uninstall` (elevated).
6. Switch the endpoint to managed and link a monitoring template to the site to see checks and alerts.

Emails are written to `%LOCALAPPDATA%\Fleetify\dev\emails` until SMTP is configured in Settings, Email.

To start over with an empty instance, drop the database `fleetify_dev` and run `setup-dev.ps1` again.

### Tests

```powershell
dotnet test Fleetify.slnx                     # needs the local PostgreSQL; creates and drops fleetify_test_* databases
cd agent; go test ./...; go vet ./...
```

The .NET integration tests connect as the superuser from `FLEETIFY_TEST_ADMIN_CONNECTION`
(default `Host=localhost;Username=postgres;Password=postgres`). Load test: see the top of
`tests/Fleetify.LoadTest/Program.cs`.

## Ground rules

- Documentation, code and UI are in English.
- Secrets never enter this repository. `.gitignore` blocks the usual suspects; CI fails on a detected secret.
- Conventional commits. Semantic versioning, tags `vX.Y.Z`.
