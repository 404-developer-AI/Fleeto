# Fleeto

Remote monitoring and management (RMM) by Steaan. Internal code name: Fleetify.

Status: design phase, documentation only. Version 0.0.0.

## Where to start

| File | What it is |
|---|---|
| [`CLAUDE.md`](CLAUDE.md) | Rules, priorities, conventions, product model. Read this first. |
| [`MD-Files/ARCHITECTURE.md`](MD-Files/ARCHITECTURE.md) | Components, data model, flows, security architecture, install and update. |
| [`MD-Files/ROADMAP.md`](MD-Files/ROADMAP.md) | Planned versions. |
| [`MD-Files/CHANGELOG.md`](MD-Files/CHANGELOG.md) | Unreleased changes and the two most recent versions. |
| [`MD-Files/CHANGELOG-ARCHIVE.md`](MD-Files/CHANGELOG-ARCHIVE.md) | Older versions. |
| [`MD-Files/branding-fleeto.md`](MD-Files/branding-fleeto.md) | Names, colors, vocabulary, tone of voice. |

## Ground rules

- Documentation, code and UI are in English.
- Secrets never enter this repository. `.gitignore` blocks the usual suspects; CI will fail on a detected secret.
- Conventional commits. Semantic versioning, tags `vX.Y.Z`.
