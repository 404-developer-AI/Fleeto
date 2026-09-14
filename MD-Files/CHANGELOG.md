# Changelog

All notable changes to Fleeto are documented here, newest first, following the
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) layout and semantic versioning.

This file holds the `Unreleased` section and the **two most recent released versions**.
When a third released version is added, the oldest entry moves to the top of
`CHANGELOG-ARCHIVE.md` in the same commit.

## [Unreleased]

### Added

- Git repository with `.gitignore` (secrets, IDE, .NET, Go), `.gitattributes` (LF, shell
  scripts always LF), `.editorconfig` and `README.md`.
- `fleetify-signer` container in the design: sole holder of the instance signing key and
  internal CA, no listening port, enforces signing rules independently of web.
- Optional four-eyes script approval per policy.
- License grace period of 14 days after expiry.
- Job output protocol: numbered chunks per stream, idempotent storage, per-chunk ack,
  completion message with counts and hashes, output cap per job.
- Migration compatibility policy (expand/contract) with a restore-based rollback for
  releases that cannot comply.

### Changed

- Agent gateway routing decided: SNI passthrough on port 443 for `agents.<fqdn>`.
- Gateway acknowledges agent data only after it is written to Postgres; valkey carries
  notifications only.
- Off-VPS encrypted backups moved from 0.7.0 to 0.1.0 in the roadmap.
- Every client-owned table carries its own `ClientId`, kept consistent by composite foreign
  keys.
- License allocation and tier change run in one serialized transaction.
- Enrollment response relies on the TLS connection validated against the pinned CA
  fingerprint instead of a separate signature.
- License clock-rollback protection documented with its precise threat model.

### Security

- Separate Steaan release signing key (offline, hardware token) for agent binaries,
  `install.sh` and the release manifest; instance signing key limited to jobs, policies,
  check definitions and session tokens.
- Agent certificate revocation (deny list checked on every connection), 90-day certificates
  with renewal, TPM-backed keys where available, duplicate identity detection, CA
  fingerprint in the install command.
- Signed jobs carry `InstanceId`, `EndpointId` and `ValidUntil`.
- Remote control key exchange bound to the signed session token and the agent certificate.
- Backups encrypted per file with ephemeral X25519 key agreement against a backup public key
  whose private half stays offline, HKDF and chunked AES-256-GCM; write-only storage
  credentials.
- `install.sh` verified by signature instead of `curl | sudo bash`; images pulled by digest.
- API key format with 256-bit secret and `flt_` prefix; enrollment tokens stored hashed.
- Documented that the host Caddy holds the TLS keys of every instance FQDN on the VPS, and
  the accepted residual risk of a compromised web container.

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
