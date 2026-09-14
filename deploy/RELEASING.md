# Releasing Fleeto

A release is a tagged commit `vX.Y.Z` that passes CI. Server images and the agent share the version number. CI builds
and pushes the images and prepares the unsigned release files; a Steaan release manager signs them offline with the
release key on the hardware token. No private key is ever available to CI, a runner or a VPS.

## 1. Prepare the release commit

1. Update `<Version>` in `Directory.Build.props`, `MD-Files/CHANGELOG.md` and, if needed, `MD-Files/ROADMAP.md`.
2. Set `deploy/release-rollback`:
   - `images` when the previous release runs on the new schema (expand/contract followed; the default);
   - `restore` when it does not. install.sh then asks for confirmation and restores the pre-update dump on failure.
3. Make sure the repository variables `FLEETIFY_LICENSE_PUBLIC_KEYS` and `FLEETIFY_RELEASE_PUBLIC_KEYS` hold the
   production public keys (semicolon-separated base64, current key first, then standby keys).
4. Merge to `main`, wait for CI, then tag: `git tag vX.Y.Z && git push origin vX.Y.Z`.

## 2. What the release workflow produces

`.github/workflows/release.yml` builds the six images for linux/amd64 and pushes them to
`ghcr.io/404-developer-ai/fleetify-{web,gateway,signer,workers,tool,caddy}:X.Y.Z` with the production public keys
compiled in. It captures the image digests and uploads the artifact `fleeto-X.Y.Z-unsigned`:

```
releases/latest                    plain text: X.Y.Z
releases/X.Y.Z/install.sh          bundled: version, release public keys (PEM), embedded templates
releases/X.Y.Z/manifest.json       fleetify-tool release manifest: version, image digests, install.sh SHA-256, rollback
SHA256SUMS
```

The ghcr.io packages must be public (they contain public keys only); install.sh pulls them without credentials.

## 3. Sign offline

On the offline signing machine with the hardware token:

1. Download the artifact (on an online machine) and carry it over; check `sha256sum -c SHA256SUMS`.
2. Review `manifest.json`: version, six digests, rollback mode. Compare the digests with the ones shown in the
   workflow log (`docker buildx imagetools inspect ghcr.io/404-developer-ai/fleetify-web:X.Y.Z`).
3. Check that `install.sh` embeds the expected key: `grep -A3 'BEGIN PUBLIC KEY' releases/X.Y.Z/install.sh`.
4. Sign each file; the signature is the raw 64-byte ed25519 signature in `<file>.sig`:
   - with the token through OpenSSL (PKCS#11 provider, key label as configured for the token):
     `openssl pkeyutl -sign -rawin -provider pkcs11 -inkey "pkcs11:object=steaan-release" -in <file> -out <file>.sig`
   - or, for a development key only: `fleetify-tool release sign --key release-signing.key --file <file>`

   Files to sign: `releases/X.Y.Z/manifest.json`, `releases/X.Y.Z/install.sh`, `releases/latest`, and a copy of
   `releases/X.Y.Z/install.sh` published as `/install.sh` (same bytes, same signature).
5. Verify every signature with the public key before publishing:
   `openssl pkeyutl -verify -rawin -pubin -inkey steaan-release.pub -in <file> -sigfile <file>.sig`

## 4. Publish to get.fleeto.app

Upload in this order, so a VPS never sees a `latest` that points to a missing manifest:

1. `releases/X.Y.Z/manifest.json(.sig)` and `releases/X.Y.Z/install.sh(.sig)`
2. `install.sh(.sig)` at the root (the file customers download for a first install)
3. `releases/latest(.sig)`

Then run `install.sh --check` on a test VPS, update a staging instance, and only then announce the release.

## Agent binaries

The Windows agent binary is built inside the web image with the release public keys and the version compiled in, so
its integrity on the way to an instance is covered by the image digest in the signed manifest. Signing the agent
binary itself for self-update (a `.sig` next to the binary) is not part of this pipeline yet; see the known gaps in the
deployment report.
