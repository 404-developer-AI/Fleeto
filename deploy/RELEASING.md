# Releasing Fleeto

A release is a tagged commit `vX.Y.Z` that passes CI. A pre-release for testing (`vX.Y.Z-alpha.N`, for example
`v0.2.0-alpha.2`) goes through the same pipeline and signing; install.sh orders it below `X.Y.Z` (semantic versioning),
so the final release is an update, and only a VPS that already runs a pre-release (or has no release to choose from)
picks up pre-releases by itself.

Steaan runs every instance (SaaS, decided 2026-09-15), so releases are **GitHub Releases** of the repository
`404-developer-AI/Fleeto` (public since 2026-09-21; the images on ghcr.io stay private). Server images and the agent share the version number. CI builds and pushes the images and
creates a draft release with the unsigned files; a Steaan release manager signs them outside CI with the release key
and publishes the release. No private key is ever available to CI, a runner or a VPS.

## 1. Prepare the release commit

1. Update `<Version>` in `Directory.Build.props`, `MD-Files/CHANGELOG.md` and, if needed, `MD-Files/ROADMAP.md`.
2. Set `deploy/release-rollback`:
   - `images` when the previous release runs on the new schema (expand/contract followed; the default);
   - `restore` when it does not. install.sh then asks for confirmation and restores the pre-update dump on failure.
3. Make sure the repository variables `FLEETO_LICENSE_PUBLIC_KEYS` and `FLEETO_RELEASE_PUBLIC_KEYS` (named
   `FLEETIFY_*` before 0.2.1) hold the
   public keys (semicolon-separated base64, current key first, then standby keys). Test keys for a test VPS are made
   with `fleeto-tool release keygen` and `license keygen`; production keys live on a hardware token.
4. Merge to `main`, wait for CI, then tag: `git tag -a vX.Y.Z -m "Fleeto X.Y.Z" && git push origin vX.Y.Z`.

## 2. What the release workflow produces

`.github/workflows/release.yml` builds the six images for linux/amd64 and pushes them to
`ghcr.io/404-developer-ai/fleeto-{web,gateway,signer,workers,tool,caddy}:X.Y.Z` with the public keys compiled in.
The packages stay private; VPSes pull them with a read-only token. Before the images it builds the agent stage on its own
(`build/agent-binaries`: `fleeto-agent.exe` and `fleeto-watchdog.exe`), and after pushing it copies the binaries out of
the web and gateway images and fails when they differ from that build. It captures the image digests and creates a **draft**
GitHub release `vX.Y.Z` (marked pre-release for `-alpha.N` versions) with:

```
install.sh        bundled: version, release public keys (PEM), embedded templates
manifest.json     fleeto-tool release manifest: version, image digests, install.sh SHA-256, rollback, agent binaries
SHA256SUMS
```

The same files are kept as the workflow artifact `fleeto-X.Y.Z-unsigned` for 30 days. A draft is invisible to install.sh.

## 3. Sign and publish

On the workstation that holds the release private key (a hardware token for production keys):

```
pwsh deploy/sign-release.ps1 -Version X.Y.Z -Key <path>/release-signing.key
```

The script:

1. downloads `install.sh`, `manifest.json` and `SHA256SUMS` from the draft and checks the hashes;
2. shows the manifest (rollback mode, the six digests and the SHA-256 of every agent binary) and checks that it names this
   version, that install.sh has the hash it lists and that it lists agent binaries. Compare the digests and hashes with the
   Release workflow log before confirming;
3. checks that the key's public half is the first key in `FLEETO_RELEASE_PUBLIC_KEYS`, so the install.sh of this
   release accepts the signatures;
4. signs both files (`fleeto-tool release sign`; the signature is the raw 64-byte ed25519 signature in `<file>.sig`),
   verifies them (`fleeto-tool release verify`), uploads `install.sh.sig` and `manifest.json.sig` and, after
   confirmation, publishes the release.

With a hardware token, sign through OpenSSL instead and upload the signatures with `gh release upload`:
`openssl pkeyutl -sign -rawin -provider pkcs11 -inkey "pkcs11:object=steaan-release" -in <file> -out <file>.sig`, then
verify with `openssl pkeyutl -verify -rawin -pubin -inkey steaan-release.pub -in <file> -sigfile <file>.sig` and publish
with `gh release edit vX.Y.Z --draft=false`.

Then run `install.sh --check` on a test VPS, update a test instance, and only then update the customer instances.

## Agent binaries

The Windows agent and watchdog are built with the release public keys and the version compiled in, reproducibly
(`-trimpath`, no VCS stamp, empty build id, pinned Go image), in the same Dockerfile stage of the web image (enrollment
downloads) and the gateway image (agent updates, 0.2.1). The signed manifest lists each binary with its SHA-256 and size
under `agentBinaries`; the binaries themselves carry no separate signature.

- install.sh copies the manifest and its signature it verified to `/opt/fleeto/<instance>/release/` (restored with the
  previous release on a rollback). The gateway reads them read-only and offers them to agents.
- An agent or watchdog installs a binary only when the manifest signature verifies against the release keys compiled
  into it, the version is newer than the installed one, and the downloaded file has the listed size and SHA-256. A
  compromised instance can therefore withhold an update but never push a binary of its own.
- Authenticode signing of the Windows binaries is planned separately (0.2.1 roadmap); it does not replace this check.

For local development, `pwsh tools/dev/build-agent.ps1 -Sign [-Version 0.2.1-dev.2]` builds both binaries into
`agent/dist/windows-amd64/` and writes `agent/dist/manifest.json(.sig)` signed with the development release key; the
development gateway serves that directory. Add `-Platform all` to build every released platform (`windows-amd64`,
`windows-arm64`, `linux-amd64`, `linux-arm64`), which a manifest for a Linux test endpoint needs.
