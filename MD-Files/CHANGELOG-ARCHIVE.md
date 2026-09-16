# Changelog archive

Released versions older than the two kept in `CHANGELOG.md`, newest first. Entries are
moved here verbatim when `CHANGELOG.md` would otherwise hold three released versions, in
the same commit as the new release. Archived entries are never edited.

## [0.0.0] — 2026-09-14

### Added

- Documentation set: `CLAUDE.md` in the repository root; `ARCHITECTURE.md`, `ROADMAP.md`,
  `CHANGELOG.md`, `CHANGELOG-ARCHIVE.md` and `branding-fleeto.md` in `MD-Files/`.
- Deployment model: one instance per customer with its own FQDN, several instances per VPS.
- Product model: client → site → endpoint, policies, monitoring templates, client templates
  (linked, not copied).
- Licensing per endpoint with two tiers: agent-only (free) and managed.
- Remote control built in, with two-way clipboard.
- Public REST API with scoped API keys.
- Decision to delegate patch management to Action1 instead of a home-grown patch engine.
- Secrets policy: everything encrypted in the database, one root key per instance outside it.
- Install and update through a single `install.sh`, with the FQDN chosen at install time.
