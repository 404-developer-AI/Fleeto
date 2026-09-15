# Releasing Fleeto

A release is a tagged commit `vX.Y.Z` that passes CI. A pre-release for testing (`vX.Y.Z-alpha.N`, for example
`v0.2.0-alpha.2`) goes through the same pipeline and signing; install.sh orders it below `X.Y.Z` (semantic versioning),
so the final release is an update, and only a VPS that already runs a pre-release (or has no release to choose from)
picks up pre-releases by itself.

Steaan runs every instance (SaaS, decided 2026-09-15), so releases are **GitHub Releases** of the private repository
`404-developer-AI/Fleeto`. Server images and the agent share the version number. CI builds and pushes the images and
creates a draft release with the unsigned files; a Steaan release manager signs them outside CI with the release key
and publishes the release. No private key is ever available to CI, a runner or a VPS.

## 1. Prepare the release commit

1. Update `<Version>` in `Directory.Build.props`, `MD-Files/CHANGELOG.md` and, if needed, `MD-Files/ROADMAP.md`.
2. Set `deploy/release-rollback`:
   - `images` when the previous release runs on the new schema (expand/contract followed; the default);
   - `restore` when it does not. install.sh then asks for confirmation and restores the pre-update dump on failure.
3. Make sure the repository variables `FLEETIFY_LICENSE_PUBLIC_KEYS` and `FLEETIFY_RELEASE_PUBLIC_KEYS` hold the
   public keys (semicolon-separated base64, current key first, then standby keys). Test keys for a test VPS are made
   with `fleetify-tool release keygen` and `license keygen`; production keys live on a hardware token.
4. Merge to `main`, wait for CI, then tag: `git tag -a vX.Y.Z -m "Fleeto X.Y.Z" && git push origin vX.Y.Z`.

## 2. What the release workflow produces

`.github/workflows/release.yml` builds the six images for linux/amd64 and pushes them to
`ghcr.io/404-developer-ai/fleetify-{web,gateway,signer,workers,tool,caddy}:X.Y.Z` with the public keys compiled in.
The packages stay private; VPSes pull them with a read-only token. It captures the image digests and creates a **draft**
GitHub release `vX.Y.Z` (marked pre-release for `-alpha.N` versions) with:

```
install.sh        bundled: version, release public keys (PEM), embedded templates
manifest.json     fleetify-tool release manifest: version, image digests, install.sh SHA-256, rollback
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
2. shows the manifest (rollback mode and the six digests) and checks that it names this version and that install.sh has
   the hash it lists. Compare the digests with the Release workflow log before confirming;
3. checks that the key's public half is the first key in `FLEETIFY_RELEASE_PUBLIC_KEYS`, so the install.sh of this
   release accepts the signatures;
4. signs both files (`fleetify-tool release sign`; the signature is the raw 64-byte ed25519 signature in `<file>.sig`),
   verifies them (`fleetify-tool release verify`), uploads `install.sh.sig` and `manifest.json.sig` and, after
   confirmation, publishes the release.

With a hardware token, sign through OpenSSL instead and upload the signatures with `gh release upload`:
`openssl pkeyutl -sign -rawin -provider pkcs11 -inkey "pkcs11:object=steaan-release" -in <file> -out <file>.sig`, then
verify with `openssl pkeyutl -verify -rawin -pubin -inkey steaan-release.pub -in <file> -sigfile <file>.sig` and publish
with `gh release edit vX.Y.Z --draft=false`.

Then run `install.sh --check` on a test VPS, update a test instance, and only then update the customer instances.

## Agent binaries

The Windows agent binary is built inside the web image with the release public keys and the version compiled in, so
its integrity on the way to an instance is covered by the image digest in the signed manifest. Signing the agent
binary itself for self-update (a `.sig` next to the binary) is not part of this pipeline yet; see the known gaps in the
deployment report.
