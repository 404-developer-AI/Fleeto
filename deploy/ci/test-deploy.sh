#!/usr/bin/env bash
# Deployment self-test, run in CI (and locally where possible):
#   1. bundles install.sh with a throwaway release key and checks the embedded templates,
#   2. serves a signed manifest as a fake GitHub release and checks that install.sh picks the right release, accepts a
#      correctly signed manifest and rejects tampering,
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

# --- 2. Fake GitHub release with a signed manifest ------------------------------------------------------------------
sign() { openssl pkeyutl -sign -rawin -inkey "$work/release.key" -in "$1" -out "$1.sig"; }
mkdir -p "$work/site/assets" "$work/site/api" "$work/no-instances" "$work/pre-instance/test-example"
digest="sha256:$(printf '%064d' 7)"
jq -n --arg d "$digest" --arg h "$(sha256sum "$work/site/install.sh" | cut -c1-64)" \
    '{formatVersion: 1, version: "9.9.9", images: {caddy: $d, gateway: $d, signer: $d, tool: $d, web: $d, workers: $d}, installShSha256: $h, rollback: "images"}' \
    >"$work/site/assets/manifest.json"
sign "$work/site/assets/manifest.json"
asset() { jq -n --arg name "$1" '{name: $name, url: ("https://api.github.com/repos/404-developer-AI/Fleeto/releases/assets/" + $name)}'; }
jq -n --argjson a "$(asset manifest.json)" --argjson b "$(asset manifest.json.sig)" '{tag_name: "v9.9.9", assets: [$a, $b]}' \
    >"$work/site/api/release-9.9.9.json"
# Drafts, tags that are not versions and older releases never win; a pre-release only counts on a VPS that runs one.
jq -n '[{tag_name: "v9.9.8", prerelease: false, draft: false}, {tag_name: "v9.9.9", prerelease: false, draft: false},
        {tag_name: "v9.10.0-alpha.1", prerelease: true, draft: false}, {tag_name: "v10.0.0", prerelease: false, draft: true},
        {tag_name: "nightly", prerelease: false, draft: false}]' >"$work/site/api/releases.json"
jq -n '[{tag_name: "v9.9.9-alpha.2", prerelease: true, draft: false}, {tag_name: "v9.9.9-alpha.10", prerelease: true, draft: false}]' \
    >"$work/site/api/pre-releases.json"
printf 'FLEETIFY_FQDN=test.example\nFLEETIFY_VERSION=9.9.9-alpha.1\n' >"$work/pre-instance/test-example/instance.conf"

cat >"$work/github.sh" <<'EOF'
source "$SITE/install.sh"
set +e
trap - ERR
# GitHub stand-in: API paths map to files in $SITE; the token is never needed.
ensure_github_credentials() { :; }
github_request() {
    local url="$2" output="$3"
    : >"$output.headers"
    case "$url" in
        *"/releases?per_page=100") cp "$SITE/api/${RELEASE_LIST:-releases.json}" "$output" ;;
        *"/releases/tags/v9.9.9") cp "$SITE/api/release-9.9.9.json" "$output" ;;
        *"/releases/assets/"*) cp "$SITE/assets/${url##*/}" "$output" ;;
        *) printf 404; return 0 ;;
    esac
    printf 200
}
EOF

cat >"$work/verify.sh" <<'EOF'
source "$WORK/github.sh"
resolve_latest_version || exit 10
[[ "$LATEST_VERSION" == "9.9.9" && "$NEWER_PRE_RELEASE" == "9.10.0-alpha.1" ]] || exit 11
( load_manifest 9.9.9 >/dev/null ) || exit 12
load_manifest 9.9.9 >/dev/null
[[ "${MANIFEST_IMAGES[web]}" == "ghcr.io/404-developer-ai/fleetify-web@$DIGEST" ]] || exit 13
[[ "$(sha256_of "$SITE/install.sh")" == "$MANIFEST_INSTALL_SH_SHA256" ]] || exit 14
( load_manifest 9.9.8 >/dev/null 2>&1 ) && exit 15
exit 0
EOF
FLEETIFY_ROOT="$work/no-instances" WORK="$work" SITE="$work/site" DIGEST="$digest" bash "$work/verify.sh" >/dev/null 2>&1 \
    || fail "install.sh did not accept a correctly signed release (exit $?)"
pass "install.sh picks the newest release from GitHub and accepts its signed manifest"

cat >"$work/pre-release.sh" <<'EOF'
source "$WORK/github.sh"
resolve_latest_version || exit 10
[[ "$LATEST_VERSION" == "$EXPECTED" ]] || { echo "latest $LATEST_VERSION, expected $EXPECTED" >&2; exit 11; }
exit 0
EOF
FLEETIFY_ROOT="$work/pre-instance" WORK="$work" SITE="$work/site" EXPECTED=9.10.0-alpha.1 bash "$work/pre-release.sh" \
    || fail "a VPS that runs a pre-release does not follow newer pre-releases"
FLEETIFY_ROOT="$work/no-instances" WORK="$work" SITE="$work/site" RELEASE_LIST=pre-releases.json EXPECTED=9.9.9-alpha.10 bash "$work/pre-release.sh" \
    || fail "without any release install.sh does not offer the newest pre-release"
pass "pre-releases count only on a VPS that runs one or while no release exists"

cat >"$work/versions.sh" <<'EOF'
source "$SITE/install.sh"
set +e
trap - ERR
expect() { if version_lt "$1" "$2"; then r=lower; else r=not-lower; fi; [[ "$r" == "$3" ]] || { echo "$1 vs $2: $r" >&2; exit 1; }; }
expect 0.1.0 0.2.0-alpha.1 lower
expect 0.2.0-alpha.1 0.2.0 lower
expect 0.2.0 0.2.0-alpha.1 not-lower
expect 0.2.0-alpha.1 0.2.0-alpha.2 lower
expect 0.2.0-alpha.2 0.2.0-alpha.10 lower
expect 0.2.0-alpha.9 0.2.0-beta.1 lower
expect 0.2.0-alpha 0.2.0-alpha.1 lower
expect 0.2.0-1 0.2.0-alpha lower
expect 0.2.0-alpha.1 0.2.0-alpha.1 not-lower
expect 0.10.0 0.9.0 not-lower
is_version 0.2.0-alpha.1 || exit 2
! is_version 0.2.0- || exit 3
! is_version '0.2.0-alpha;rm' || exit 4
! is_version 01.2.0 || exit 5
exit 0
EOF
SITE="$work/site" bash "$work/versions.sh" || fail "install.sh compares or validates versions wrongly (exit $?)"
pass "install.sh orders releases and pre-releases by semantic versioning"

cp "$work/site/assets/manifest.json" "$work/manifest.orig"
sed -i 's/"images"/"rollback": "restore", "images"/' "$work/site/assets/manifest.json"
if FLEETIFY_ROOT="$work/no-instances" WORK="$work" SITE="$work/site" DIGEST="$digest" bash "$work/verify.sh" >/dev/null 2>&1; then
    fail "install.sh accepted a tampered manifest"
fi
cp "$work/manifest.orig" "$work/site/assets/manifest.json"
pass "install.sh rejects a tampered manifest"

# --- 3. Unbundled script refuses ------------------------------------------------------------------------------------
if bash -c "source '$repo_root/deploy/install.sh'; set +e; trap - ERR; (load_release_keys) >/dev/null 2>&1"; then
    fail "the unbundled install.sh (placeholder key) did not refuse"
fi
pass "the unbundled install.sh refuses to verify releases"

# --- 3b. DNS check behind a firewall or NAT -------------------------------------------------------------------------
cat >"$work/dns.sh" <<'EOF'
source "$REPO/deploy/install.sh"
set +e
trap - ERR
detected_host_addresses() { printf '172.16.10.81\n172.32.0.178\n'; }
resolve_addresses() {
    case "$1" in
        rmm.nat.example | agents.rmm.nat.example) printf '172.32.0.189\n' ;;
        rmm.half.example) printf '172.32.0.189\n' ;;
        rmm.own.example | agents.rmm.own.example) printf '172.32.0.178\n' ;;
    esac
}
case "$CASE" in
    own) ( check_dns rmm.own.example ) >/dev/null 2>&1 || exit 20 ;;
    unconfirmed) ( check_dns rmm.nat.example </dev/null ) >/dev/null 2>&1 && exit 21 ;;
    answered)
        # The operator answers the question on stdin: once for the address both names use, then it is stored.
        is_interactive() { true; }
        ensure_fleetify_root() { :; }
        ( check_dns rmm.nat.example <<<"y" ) >/dev/null 2>&1 || exit 26
        grep -qx '172.32.0.189' "$FLEETIFY_ROOT/public-addresses" || exit 27
        ( check_dns rmm.nat.example </dev/null ) >/dev/null 2>&1 || exit 28
        ;;
    stored)
        printf '172.32.0.189\n' >"$FLEETIFY_ROOT/public-addresses"
        ( check_dns rmm.nat.example ) >/dev/null 2>&1 || exit 22
        output="$( ( check_dns rmm.half.example ) 2>&1 )" && exit 23
        grep -q 'agents.rmm.half.example has no A or AAAA record' <<<"$output" || exit 24
        grep -qE 'agents.rmm.half.example +A +172.32.0.189' <<<"$output" || exit 25
        ;;
esac
exit 0
EOF
for case in own unconfirmed answered stored; do
    mkdir -p "$work/dns-$case"
    FLEETIFY_ROOT="$work/dns-$case" REPO="$repo_root" CASE="$case" bash "$work/dns.sh" </dev/null \
        || fail "DNS check behind NAT, case $case (exit $?)"
done
pass "the DNS check accepts a forwarded public address only once it is confirmed, and suggests it for missing records"

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
