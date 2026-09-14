#!/usr/bin/env bash
# Deployment self-test, run in CI (and locally where possible):
#   1. bundles install.sh with a throwaway release key and checks the embedded templates,
#   2. signs a fake "latest" pointer and manifest and checks that install.sh accepts them and rejects tampering,
#   3. checks that an unbundled install.sh refuses to verify anything,
#   4. generates the host Caddyfile for two fake instances (and for none),
#   5. with Docker: validates both Caddyfiles with the fleetify-caddy image and renders the instance Compose file.
#
# Usage: deploy/ci/test-deploy.sh [--caddy-image <image>]   (without --caddy-image the Docker steps are skipped)
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
caddy_image=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --caddy-image) caddy_image="$2"; shift 2 ;;
        *) echo "unknown argument $1" >&2; exit 2 ;;
    esac
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
pass() { echo "PASS: $*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

# --- 1. Bundle with a throwaway key ----------------------------------------------------------------------------------
openssl genpkey -algorithm ed25519 -out "$work/release.key" 2>/dev/null
public_key="$(openssl pkey -in "$work/release.key" -pubout -outform DER | tail -c 32 | base64 | tr -d '\n')"
bash "$repo_root/deploy/ci/bundle-install.sh" --version 9.9.9 --release-public-keys "$public_key" --out "$work/site/install.sh" >/dev/null
grep -qx 'readonly INSTALLER_VERSION="9.9.9"' "$work/site/install.sh" || fail "version not set in bundle"
pass "bundle-install.sh produced a bundle with embedded key and templates"

# --- 2. Signed pointer and manifest ----------------------------------------------------------------------------------
sign() { openssl pkeyutl -sign -rawin -inkey "$work/release.key" -in "$1" -out "$1.sig"; }
mkdir -p "$work/site/releases/9.9.9"
printf '9.9.9\n' >"$work/site/releases/latest"
digest="sha256:$(printf '%064d' 7)"
jq -n --arg d "$digest" --arg h "$(sha256sum "$work/site/install.sh" | cut -c1-64)" \
    '{formatVersion: 1, version: "9.9.9", images: {caddy: $d, gateway: $d, signer: $d, tool: $d, web: $d, workers: $d}, installShSha256: $h, rollback: "images"}' \
    >"$work/site/releases/9.9.9/manifest.json"
sign "$work/site/releases/latest"
sign "$work/site/releases/9.9.9/manifest.json"

cat >"$work/verify.sh" <<'EOF'
source "$SITE/install.sh"
set +e
trap - ERR
download() { cp "$SITE/${1#https://get.fleeto.app/}" "$2"; }
resolve_latest_version || exit 10
[[ "$LATEST_VERSION" == "9.9.9" ]] || exit 11
( load_manifest 9.9.9 >/dev/null ) || exit 12
load_manifest 9.9.9 >/dev/null
[[ "${MANIFEST_IMAGES[web]}" == "ghcr.io/404-developer-ai/fleetify-web@$DIGEST" ]] || exit 13
[[ "$(sha256_of "$SITE/install.sh")" == "$MANIFEST_INSTALL_SH_SHA256" ]] || exit 14
exit 0
EOF
SITE="$work/site" DIGEST="$digest" bash "$work/verify.sh" >/dev/null 2>&1 || fail "install.sh did not accept a correctly signed release (exit $?)"
pass "install.sh accepts a correctly signed pointer and manifest"

cp "$work/site/releases/9.9.9/manifest.json" "$work/manifest.orig"
sed -i 's/"images"/"rollback": "restore", "images"/' "$work/site/releases/9.9.9/manifest.json"
if SITE="$work/site" DIGEST="$digest" bash "$work/verify.sh" >/dev/null 2>&1; then
    fail "install.sh accepted a tampered manifest"
fi
cp "$work/manifest.orig" "$work/site/releases/9.9.9/manifest.json"
pass "install.sh rejects a tampered manifest"

# --- 3. Unbundled script refuses ------------------------------------------------------------------------------------
if bash -c "source '$repo_root/deploy/install.sh'; set +e; trap - ERR; (load_release_keys) >/dev/null 2>&1"; then
    fail "the unbundled install.sh (placeholder key) did not refuse"
fi
pass "the unbundled install.sh refuses to verify releases"

# --- 4. Caddyfile generation -----------------------------------------------------------------------------------------
mkdir -p "$work/root/rmm-a-example" "$work/root/rmm-b-example" "$work/empty" "$work/caddy-two" "$work/caddy-empty"
printf 'FLEETIFY_INSTANCE=rmm-a-example\nFLEETIFY_FQDN=rmm.a.example\nWEB_PORT=20000\nAGENT_PORT=20001\n' >"$work/root/rmm-a-example/instance.conf"
printf 'FLEETIFY_INSTANCE=rmm-b-example\nFLEETIFY_FQDN=rmm.b.example\nWEB_PORT=20002\nAGENT_PORT=20003\n' >"$work/root/rmm-b-example/instance.conf"
FLEETIFY_ROOT="$work/root" bash -c "source '$repo_root/deploy/install.sh'; generate_caddyfile" >"$work/caddy-two/Caddyfile"
FLEETIFY_ROOT="$work/empty" bash -c "source '$repo_root/deploy/install.sh'; generate_caddyfile" >"$work/caddy-empty/Caddyfile"
grep -q 'tls sni agents.rmm.b.example' "$work/caddy-two/Caddyfile" || fail "SNI route missing"
grep -q 'proxy 127.0.0.1:20003' "$work/caddy-two/Caddyfile" || fail "gateway upstream missing"
grep -q 'proxy_protocol v2' "$work/caddy-two/Caddyfile" || fail "PROXY protocol towards the gateway missing"
grep -q 'reverse_proxy 127.0.0.1:20000' "$work/caddy-two/Caddyfile" || fail "web upstream missing"
pass "Caddyfile generated for two instances and for none"

# --- 5. Docker-based checks ------------------------------------------------------------------------------------------
if [[ -z "$caddy_image" ]]; then
    echo "SKIP: Docker checks (no --caddy-image)"
    exit 0
fi

for config in caddy-two caddy-empty; do
    docker run --rm --network none -v "$work/$config:/candidate:ro" "$caddy_image" \
        adapt --config /candidate/Caddyfile --adapter caddyfile >/dev/null || fail "caddy adapt rejected $config"
done
pass "fleetify-caddy image adapts both generated Caddyfiles"

instance="$work/root/rmm-a-example"
mkdir -p "$instance/secrets"
for file in root.key signer.key db-postgres.password db-migrator.password db-web.password db-gateway.password db-signer.password db-workers.password db-backup.password; do
    printf 'not-a-real-secret' >"$instance/secrets/$file"
done
cp "$repo_root/deploy/compose/compose.yml" "$instance/compose.yml"
cat >>"$instance/instance.conf" <<CONF
FLEETIFY_VERSION=9.9.9
POSTGRES_IMAGE=timescale/timescaledb:2.30.0-pg17@sha256:3113d12b78392c064aa7475caf7a52b447b29ddd4f9bfd23526733fcb03e3459
TOOL_IMAGE=ghcr.io/404-developer-ai/fleetify-tool@$digest
SIGNER_IMAGE=ghcr.io/404-developer-ai/fleetify-signer@$digest
GATEWAY_IMAGE=ghcr.io/404-developer-ai/fleetify-gateway@$digest
WORKERS_IMAGE=ghcr.io/404-developer-ai/fleetify-workers@$digest
WEB_IMAGE=ghcr.io/404-developer-ai/fleetify-web@$digest
CONF
docker compose --project-name fleetify-rmm-a-example --project-directory "$instance" --env-file "$instance/instance.conf" \
    -f "$instance/compose.yml" config >"$work/rendered.yml" || fail "docker compose rejected compose.yml"
grep -q '127.0.0.1' "$work/rendered.yml" || fail "published ports are not bound to loopback"
if grep -q 'docker.sock' "$work/rendered.yml"; then fail "Docker socket mounted"; fi
pass "instance compose.yml renders with docker compose"

cp "$repo_root/deploy/caddy/compose.yml" "$work/caddy-compose.yml"
printf 'CADDY_IMAGE=%s\n' "$caddy_image" >"$work/caddy.conf"
docker compose --project-name fleetify-caddy --env-file "$work/caddy.conf" -f "$work/caddy-compose.yml" config >/dev/null \
    || fail "docker compose rejected caddy/compose.yml"
pass "host proxy compose.yml renders with docker compose"
