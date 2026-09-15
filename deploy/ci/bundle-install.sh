#!/usr/bin/env bash
# Builds the release install.sh from deploy/install.sh:
#   - sets INSTALLER_VERSION,
#   - replaces the STEAAN_RELEASE_PUBLIC_KEY_PEM placeholder line with the release public keys as PEM,
#   - embeds the templates install.sh needs on a VPS (Compose files, PostgreSQL scripts) as quoted heredocs, so one
#     file, covered by one hash in the signed release manifest, carries everything.
#
# Usage:
#   deploy/ci/bundle-install.sh --version 0.1.0 --release-public-keys "<base64>;<base64>" --out dist/install.sh
#
# --release-public-keys takes the same value as the FLEETIFY_RELEASE_PUBLIC_KEYS build property: semicolon-separated
# base64 raw ed25519 public keys, current key first. Public keys only; no private key is ever an input.
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source_script="$repo_root/deploy/install.sh"
version=""
keys=""
output=""

fail() { echo "bundle-install: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) version="${2:-}"; shift 2 ;;
        --release-public-keys) keys="${2:-}"; shift 2 ;;
        --out) output="${2:-}"; shift 2 ;;
        *) fail "unknown argument $1" ;;
    esac
done

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]{1,10}(\.[0-9A-Za-z]{1,10}){0,4})?$ ]] ||
    fail "--version must be MAJOR.MINOR.PATCH with an optional pre-release such as -alpha.1"
[[ -n "$keys" ]] || fail "--release-public-keys is required (an install.sh without keys cannot verify releases)"
[[ -n "$output" ]] || fail "--out is required"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# --- Release public keys as PEM --------------------------------------------------------------------------------------
pem_file="$work/keys.pem"
: >"$pem_file"
IFS=';' read -r -a key_list <<<"$keys"
key_count=0
for key in "${key_list[@]}"; do
    key="$(tr -d '[:space:]' <<<"$key")"
    [[ -n "$key" ]] || continue
    raw="$work/key-$key_count.raw"
    printf '%s' "$key" | base64 -d >"$raw" 2>/dev/null || fail "release public key $((key_count + 1)) is not valid base64"
    [[ "$(stat -c %s "$raw")" -eq 32 ]] || fail "release public key $((key_count + 1)) is not 32 bytes"
    # SubjectPublicKeyInfo for ed25519: SEQUENCE { SEQUENCE { OID 1.3.101.112 } BIT STRING key } (as Ed25519.PublicKeyToPem)
    {
        echo "-----BEGIN PUBLIC KEY-----"
        { printf '\x30\x2a\x30\x05\x06\x03\x2b\x65\x70\x03\x21\x00'; cat "$raw"; } | base64 -w 0
        echo
        echo "-----END PUBLIC KEY-----"
    } >"$work/key-$key_count.pem"
    openssl pkey -pubin -in "$work/key-$key_count.pem" -noout 2>/dev/null || fail "release public key $((key_count + 1)) is not a valid ed25519 key"
    cat "$work/key-$key_count.pem" >>"$pem_file"
    key_count=$((key_count + 1))
done
[[ "$key_count" -gt 0 ]] || fail "no release public keys given"

# --- Embedded templates ----------------------------------------------------------------------------------------------
templates_file="$work/templates.sh"
mapfile -t template_names < <(awk '/^readonly TEMPLATE_FILES=\(/ { inside = 1; next } inside && /^\)/ { exit } inside { gsub(/[" ]/, ""); if ($0 != "") print }' "$source_script")
[[ ${#template_names[@]} -gt 0 ]] || fail "TEMPLATE_FILES not found in install.sh"
{
    for name in "${template_names[@]}"; do
        file="$repo_root/deploy/$name"
        [[ -f "$file" ]] || fail "template deploy/$name does not exist"
        delimiter="FLEETIFY_TEMPLATE_END_$(sha256sum "$file" | cut -c1-16)"
        if grep -qx "$delimiter" "$file"; then
            fail "deploy/$name contains its heredoc delimiter"
        fi
        if [[ -n "$(tail -c 1 "$file")" ]]; then
            fail "deploy/$name must end with a newline"
        fi
        function_name="bundled_template_${name//[^a-zA-Z0-9]/_}"
        printf '%s() {\n' "$function_name"
        printf "    cat <<'%s'\n" "$delimiter"
        cat "$file"
        printf '%s\n' "$delimiter"
        printf '}\n'
    done
} >"$templates_file"

# --- Assemble --------------------------------------------------------------------------------------------------------
start_marker='# >>> fleetify-bundle: templates'
end_marker='# <<< fleetify-bundle: templates'
[[ "$(grep -cxF "$start_marker" "$source_script")" -eq 1 && "$(grep -cxF "$end_marker" "$source_script")" -eq 1 ]] \
    || fail "template markers not found exactly once in install.sh"
[[ "$(grep -cx 'STEAAN_RELEASE_PUBLIC_KEY_PEM' "$source_script")" -eq 1 ]] || fail "key placeholder line not found exactly once"
[[ "$(grep -cx 'readonly INSTALLER_VERSION="0.0.0-dev"' "$source_script")" -eq 1 ]] || fail "INSTALLER_VERSION line not found"

awk -v version="$version" -v pem="$pem_file" -v templates="$templates_file" \
    -v start="$start_marker" -v end="$end_marker" '
    $0 == "readonly INSTALLER_VERSION=\"0.0.0-dev\"" { print "readonly INSTALLER_VERSION=\"" version "\""; next }
    $0 == "STEAAN_RELEASE_PUBLIC_KEY_PEM" { while ((getline line < pem) > 0) print line; close(pem); next }
    $0 == start { print; while ((getline line < templates) > 0) print line; close(templates); skipping = 1; next }
    $0 == end { skipping = 0; print; next }
    !skipping { print }
' "$source_script" >"$work/install.sh"

# --- Checks ----------------------------------------------------------------------------------------------------------
bash -n "$work/install.sh" || fail "the bundled install.sh has a syntax error"
if grep -qx 'STEAAN_RELEASE_PUBLIC_KEY_PEM' "$work/install.sh"; then
    fail "the key placeholder is still present"
fi
for name in "${template_names[@]}"; do
    function_name="bundled_template_${name//[^a-zA-Z0-9]/_}"
    # The embedded copy must be byte-identical to the source file.
    diff -q <(bash -c "source '$work/install.sh'; $function_name") "$repo_root/deploy/$name" >/dev/null \
        || fail "embedded template $name differs from deploy/$name"
done

mkdir -p "$(dirname "$output")"
install -m 0755 "$work/install.sh" "$output"
echo "bundle-install: wrote $output (version $version, $key_count release key(s), ${#template_names[@]} templates, sha256 $(sha256sum "$output" | cut -c1-64))"
