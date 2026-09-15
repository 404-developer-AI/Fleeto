#!/usr/bin/env bash
# =====================================================================================================================
# Fleeto install.sh: installs and updates Fleeto instances on an Ubuntu VPS (22.04 or 24.04, amd64). Run as root.
#
# Steaan runs every instance (SaaS). Releases are GitHub Releases of the private Fleeto repository. Never pipe this script
# from curl into a shell. Download it from the release, verify its signature with the Steaan release public key and only
# then run it (deploy/README.md, First install):
#
#   gh release download v<version> --repo 404-developer-AI/Fleeto --pattern 'install.sh*'     (on a Steaan workstation)
#   scp install.sh install.sh.sig steaan-release.pub root@<vps>:                               (to the VPS)
#   openssl pkeyutl -verify -rawin -pubin -inkey steaan-release.pub -in install.sh -sigfile install.sh.sig
#   sudo bash install.sh
#
# After that first manual check the script verifies everything itself: every release manifest and every newer install.sh
# are checked against the release public keys embedded below before they are used, and container images are pulled by
# digest only (MD-Files/ARCHITECTURE.md section 7).
#
# Usage: install.sh --help
# =====================================================================================================================
set -Eeuo pipefail
umask 077

# ---------------------------------------------------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------------------------------------------------
# Replaced with the release version by deploy/ci/bundle-install.sh.
readonly INSTALLER_VERSION="0.0.0-dev"
# Releases are the published GitHub Releases v<version> of this repository, each with the assets install.sh(.sig) and
# manifest.json(.sig). The repository and its images are private, so install.sh asks once for two read-only tokens.
readonly RELEASE_REPOSITORY="${FLEETIFY_RELEASE_REPOSITORY:-404-developer-AI/Fleeto}"
readonly GITHUB_API_URL="https://api.github.com"
readonly REGISTRY="ghcr.io/404-developer-ai"
readonly FLEETIFY_ROOT="${FLEETIFY_ROOT:-/opt/fleetify}"
# GitHub tokens, readable by root only: a fine-grained token with Contents: read-only on RELEASE_REPOSITORY (release
# files) and a classic token with only read:packages (images on ghcr.io; GitHub Packages accepts no fine-grained tokens).
readonly CREDENTIALS_DIR="$FLEETIFY_ROOT/credentials"
readonly RELEASES_TOKEN_FILE="$CREDENTIALS_DIR/github-releases.token"
readonly PACKAGES_TOKEN_FILE="$CREDENTIALS_DIR/github-packages.token"
readonly PACKAGES_USER_FILE="$CREDENTIALS_DIR/github-packages.user"
# Public addresses a firewall or NAT forwards to this VPS (TCP 80 and 443), confirmed once by the operator.
readonly PUBLIC_ADDRESSES_FILE="$FLEETIFY_ROOT/public-addresses"
readonly CADDY_DIR="$FLEETIFY_ROOT/caddy"
readonly INSTALLED_SCRIPT="$FLEETIFY_ROOT/bin/install.sh"
# PostgreSQL 17 with TimescaleDB, pinned by digest. This script is covered by the signed manifest, so this pin is too.
readonly POSTGRES_IMAGE="timescale/timescaledb:2.30.0-pg17@sha256:3113d12b78392c064aa7475caf7a52b447b29ddd4f9bfd23526733fcb03e3459"
# Images every release manifest must list (ghcr.io/404-developer-ai/fleetify-<name>@<digest>).
readonly RELEASE_IMAGES=(web gateway signer workers tool caddy)
# Long-running services of an instance, in health-check order.
readonly INSTANCE_SERVICES=(postgres signer gateway workers web)
# Container identities (Dockerfiles and deploy/compose/compose.yml).
readonly APP_UID=10001
readonly APP_GID=10001
readonly POSTGRES_UID=70
# Docker's apt repository signing key (https://download.docker.com/linux/ubuntu/gpg).
readonly DOCKER_APT_KEY_FINGERPRINT="9DC858229FC7DD38854AE2D88D81803C0EBFCD88"
# Loopback port range for instance web and gateway ports.
readonly PORT_RANGE_START=20000
readonly PORT_RANGE_END=29998
readonly HEALTH_TIMEOUT_SECONDS=300
readonly PRE_UPDATE_BACKUPS_KEPT=3

SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
SCRIPT_DIR="$(dirname "$SCRIPT_PATH")"
readonly SCRIPT_PATH SCRIPT_DIR

# ---------------------------------------------------------------------------------------------------------------------
# Steaan release public keys (PEM, current key first, then standby keys). Release builds replace the placeholder
# line with the production keys (deploy/ci/bundle-install.sh). A script that still contains the placeholder refuses
# to install or update anything, because it cannot verify a release.
# ---------------------------------------------------------------------------------------------------------------------
release_public_keys_pem() {
    cat <<'FLEETIFY_RELEASE_PUBLIC_KEYS'
STEAAN_RELEASE_PUBLIC_KEY_PEM
FLEETIFY_RELEASE_PUBLIC_KEYS
}

# ---------------------------------------------------------------------------------------------------------------------
# Embedded templates. In a release build the block between the markers holds one function per file below; in a
# repository checkout the files are read from the deploy/ directory next to this script.
# ---------------------------------------------------------------------------------------------------------------------
# >>> fleetify-bundle: templates
# <<< fleetify-bundle: templates

# shellcheck disable=SC2034 # read by deploy/ci/bundle-install.sh, which embeds these files
readonly TEMPLATE_FILES=(
    "compose/compose.yml"
    "postgres/init/10-fleetify-roles.sh"
    "postgres/archive-wal.sh"
    "caddy/compose.yml"
)

template() {
    local name="$1" function_name
    function_name="bundled_template_${name//[^a-zA-Z0-9]/_}"
    if declare -F "$function_name" >/dev/null; then
        "$function_name"
    elif [[ -f "$SCRIPT_DIR/$name" ]]; then
        cat "$SCRIPT_DIR/$name"
    else
        die "Template $name is missing from this install.sh." "Download install.sh from a Fleeto release on GitHub (deploy/README.md, First install)."
    fi
}

# ---------------------------------------------------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------------------------------------------------
if [[ -t 1 ]]; then
    C_RESET=$'\033[0m' C_BOLD=$'\033[1m' C_BLUE=$'\033[34m' C_GREEN=$'\033[32m' C_YELLOW=$'\033[33m' C_RED=$'\033[31m'
else
    C_RESET="" C_BOLD="" C_BLUE="" C_GREEN="" C_YELLOW="" C_RED=""
fi

step() { printf '\n%s==>%s %s%s%s\n' "$C_BLUE" "$C_RESET" "$C_BOLD" "$*" "$C_RESET"; }
info() { printf '    %s\n' "$*"; }
ok() { printf '    %s%s%s\n' "$C_GREEN" "$*" "$C_RESET"; }
warn() { printf '    %sWarning:%s %s\n' "$C_YELLOW" "$C_RESET" "$*" >&2; }
error() { printf '%sError:%s %s\n' "$C_RED" "$C_RESET" "$*" >&2; }

# die <cause> [next step]: errors state the cause and the next step.
die() {
    error "$1"
    if [[ $# -gt 1 && -n "$2" ]]; then
        printf '       %s\n' "$2" >&2
    fi
    exit 1
}

on_unexpected_error() {
    local status="$1" line="$2" command="$3"
    error "install.sh stopped: '$command' failed with exit code $status (line $line)."
    printf '       %s\n' "Fix the cause shown above and run install.sh again; it is safe to re-run. 'install.sh --list' shows the state of every instance." >&2
}
trap 'on_unexpected_error $? $LINENO "$BASH_COMMAND"' ERR

WORK_DIR=""
cleanup() {
    if [[ -n "$WORK_DIR" && -d "$WORK_DIR" ]]; then
        rm -rf -- "$WORK_DIR"
    fi
}
trap cleanup EXIT

# ---------------------------------------------------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------------------------------------------------
ARG_FQDN=""
ARG_VERSION=""
ARG_CHECK=false
ARG_LIST=false
ARG_ALL=false
ARG_YES=false
ARG_GITHUB_TOKENS=false
ORIGINAL_ARGS=("$@")

usage() {
    cat <<USAGE
Fleeto install.sh ${INSTALLER_VERSION}: install and update Fleeto instances on this VPS.

Usage:
  install.sh --fqdn <name>                    Install a new instance for <name>, or update the existing one
  install.sh --fqdn <name> --version <x.y.z>  Install or update to a specific version
  install.sh --fqdn <name> --check            Show installed and latest version; change nothing
  install.sh --check                          Same, for every instance on this VPS
  install.sh --list                           List the instances on this VPS
  install.sh --all [--version <x.y.z>]        Update every instance on this VPS
  install.sh --github-tokens                  Enter or replace the GitHub tokens used to download releases
  install.sh --help                           Show this help

Options:
  --yes     Do not ask for confirmation (needed for releases that can only roll back by restoring a backup)

Without --fqdn and without a command, install.sh asks for the FQDN.

Releases: the newest published release, and pre-releases (x.y.z-alpha.n) only when no release exists yet or an instance
on this VPS already runs a pre-release. Use --version for any other published version.

DNS records needed before a new install (both pointing to this VPS):
  <name>          A/AAAA   web UI and API
  agents.<name>   A/AAAA   agent connections (TLS passed through to the instance gateway)
USAGE
}

parse_args() {
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --fqdn) [[ $# -ge 2 ]] || die "--fqdn needs a value." "Example: --fqdn rmm.customer.example"; ARG_FQDN="$2"; shift 2 ;;
            --fqdn=*) ARG_FQDN="${1#*=}"; shift ;;
            --version) [[ $# -ge 2 ]] || die "--version needs a value." "Example: --version 0.1.0"; ARG_VERSION="$2"; shift 2 ;;
            --version=*) ARG_VERSION="${1#*=}"; shift ;;
            --check) ARG_CHECK=true; shift ;;
            --list) ARG_LIST=true; shift ;;
            --all) ARG_ALL=true; shift ;;
            --yes | -y) ARG_YES=true; shift ;;
            --github-tokens) ARG_GITHUB_TOKENS=true; shift ;;
            --help | -h) usage; exit 0 ;;
            *) die "Unknown argument '$1'." "Run install.sh --help to see the supported commands." ;;
        esac
    done

    if [[ -n "$ARG_VERSION" ]] && ! is_version "$ARG_VERSION"; then
        die "'$ARG_VERSION' is not a valid version." "Use the form MAJOR.MINOR.PATCH with an optional pre-release, for example 0.2.0 or 0.2.0-alpha.1."
    fi
    if $ARG_GITHUB_TOKENS && { $ARG_LIST || $ARG_CHECK || $ARG_ALL || [[ -n "$ARG_FQDN" || -n "$ARG_VERSION" ]]; }; then
        die "--github-tokens cannot be combined with other commands." "Run install.sh --github-tokens on its own."
    fi
    if $ARG_LIST && { $ARG_CHECK || $ARG_ALL || [[ -n "$ARG_FQDN" ]]; }; then
        die "--list cannot be combined with other commands." "Run install.sh --list on its own."
    fi
    if $ARG_ALL && { $ARG_CHECK || [[ -n "$ARG_FQDN" ]]; }; then
        die "--all updates every instance and cannot be combined with --fqdn or --check." "Run install.sh --all on its own, optionally with --version."
    fi
}

# ---------------------------------------------------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------------------------------------------------
# A version is MAJOR.MINOR.PATCH with an optional pre-release of at most five short identifiers (0.2.0-alpha.1).
is_version() {
    [[ "$1" =~ ^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})(-[0-9A-Za-z]{1,10}(\.[0-9A-Za-z]{1,10}){0,4})?$ ]]
}

# version_lt a b: true when version a is lower than version b, with semantic versioning precedence for pre-releases:
# 0.2.0-alpha.1 < 0.2.0-alpha.2 < 0.2.0-beta < 0.2.0. Both arguments are valid versions (is_version).
version_lt() {
    local core_a="${1%%-*}" core_b="${2%%-*}" pre_a="" pre_b=""
    [[ "$1" == *-* ]] && pre_a="${1#*-}"
    [[ "$2" == *-* ]] && pre_b="${2#*-}"
    local -a a b
    IFS=. read -r -a a <<<"$core_a"
    IFS=. read -r -a b <<<"$core_b"
    local i
    for i in 0 1 2; do
        if ((10#${a[i]} < 10#${b[i]})); then return 0; fi
        if ((10#${a[i]} > 10#${b[i]})); then return 1; fi
    done
    # Same MAJOR.MINOR.PATCH: a pre-release is lower than the release itself.
    if [[ -z "$pre_a" ]]; then return 1; fi
    if [[ -z "$pre_b" ]]; then return 0; fi
    local -a pa pb
    IFS=. read -r -a pa <<<"$pre_a"
    IFS=. read -r -a pb <<<"$pre_b"
    local x y
    for ((i = 0; i < ${#pa[@]} && i < ${#pb[@]}; i++)); do
        x="${pa[i]}"
        y="${pb[i]}"
        if [[ "$x" == "$y" ]]; then continue; fi
        if [[ "$x" =~ ^[0-9]+$ && "$y" =~ ^[0-9]+$ ]]; then
            if ((10#$x < 10#$y)); then return 0; fi
            return 1
        fi
        # A numeric identifier is lower than an alphanumeric one; two alphanumeric identifiers compare in ASCII order.
        if [[ "$x" =~ ^[0-9]+$ ]]; then return 0; fi
        if [[ "$y" =~ ^[0-9]+$ ]]; then return 1; fi
        if [[ "$(LC_ALL=C sort <<<"$x"$'\n'"$y" | head -n 1)" == "$x" ]]; then return 0; fi
        return 1
    done
    # Equal so far: the one with fewer identifiers is lower.
    if ((${#pa[@]} < ${#pb[@]})); then return 0; fi
    return 1
}

confirm() {
    local question="$1" answer
    if $ARG_YES; then
        return 0
    fi
    if [[ ! -t 0 ]]; then
        die "Confirmation needed: $question" "Run install.sh again with --yes to confirm non-interactively."
    fi
    read -r -p "    $question [y/N] " answer
    [[ "$answer" =~ ^[Yy]([Ee][Ss])?$ ]]
}

require_root() {
    [[ "$(id -u)" -eq 0 ]] || die "install.sh must run as root." "Run it again with sudo: sudo bash install.sh $*"
}

require_supported_os() {
    [[ -r /etc/os-release ]] || die "Cannot identify the operating system (/etc/os-release is missing)." "Use Ubuntu 22.04 or 24.04."
    local id version_id
    # shellcheck source=/dev/null
    id="$(. /etc/os-release && printf '%s' "${ID:-}")"
    # shellcheck source=/dev/null
    version_id="$(. /etc/os-release && printf '%s' "${VERSION_ID:-}")"
    if [[ "$id" != "ubuntu" || ! "$version_id" =~ ^(22\.04|24\.04)$ ]]; then
        die "This VPS runs ${id:-unknown} ${version_id:-}; Fleeto supports Ubuntu 22.04 and 24.04." "Install Fleeto on a supported Ubuntu version."
    fi
    local arch
    arch="$(dpkg --print-architecture)"
    [[ "$arch" == "amd64" ]] || die "This VPS is $arch; Fleeto images are built for amd64 only." "Use an amd64 VPS."
}

make_work_dir() {
    if [[ -z "$WORK_DIR" ]]; then
        WORK_DIR="$(mktemp -d /tmp/fleetify-install.XXXXXXXX)"
    fi
}

download() {
    local url="$1" output="$2"
    curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --tlsv1.2         --connect-timeout 15 --max-time 300 --retry 3 --retry-delay 2         --output "$output" "$url"
}

sha256_of() { sha256sum "$1" | awk '{ print $1 }'; }

# ---------------------------------------------------------------------------------------------------------------------
# GitHub access
# ---------------------------------------------------------------------------------------------------------------------
# github_request <token file> <url> <output> [accept]: GET with the token and print the HTTP status ("000" when GitHub
# could not be reached). The response headers are written to <output>.headers. The token goes to curl in a header file,
# never on a command line, and curl does not send it on to the storage host a download redirects to.
github_request() {
    local token_file="$1" url="$2" output="$3" accept="${4:-application/vnd.github+json}" header_file status
    make_work_dir
    header_file="$(mktemp "$WORK_DIR/auth.XXXXXXXX")"
    printf 'Authorization: Bearer %s\n' "$(<"$token_file")" >"$header_file"
    status="$(curl --silent --location --proto '=https' --proto-redir '=https' --tlsv1.2 \
        --connect-timeout 15 --max-time 300 --retry 3 --retry-delay 2 \
        --header @"$header_file" --header "Accept: $accept" --header "X-GitHub-Api-Version: 2022-11-28" \
        --dump-header "$output.headers" --output "$output" --write-out '%{http_code}' "$url")" || status="000"
    rm -f -- "$header_file"
    printf '%s' "${status: -3}"
}

# header_value <headers file> <name>: the value of the last header with that name (case-insensitive).
header_value() {
    tr -d '\r' <"$1" | awk -v name="$(tr '[:upper:]' '[:lower:]' <<<"$2")" '
        { split($0, parts, ":"); if (tolower(parts[1]) == name) { value = substr($0, length(parts[1]) + 2); sub(/^ +/, "", value) } }
        END { print value }'
}

# github_failure <status> <what>: stops with the cause and the next step for a failed GitHub request.
github_failure() {
    local status="$1" what="$2"
    case "$status" in
        000) die "Could not reach GitHub to download $what." "Check the outbound HTTPS connection of this VPS to api.github.com and try again." ;;
        401) die "GitHub refused the release token while downloading $what: it expired or was revoked." "Create a new token and run install.sh --github-tokens." ;;
        403 | 404) die "GitHub answered $status for $what: it does not exist, is not published yet, or the release token has no access to $RELEASE_REPOSITORY." \
            "Check that the release is published (deploy/RELEASING.md) and that the token may read the repository; replace it with install.sh --github-tokens." ;;
        *) die "GitHub answered $status for $what." "Try again later; if it persists, check https://www.githubstatus.com." ;;
    esac
}

# warn_token_expiry <headers file> <label>: warns when a token expires within 30 days.
warn_token_expiry() {
    local expires expires_at
    expires="$(header_value "$1" github-authentication-token-expiration)"
    [[ -n "$expires" ]] || return 0
    expires_at="$(date -d "$expires" +%s 2>/dev/null)" || return 0
    if ((expires_at - $(date +%s) < 30 * 86400)); then
        warn "The GitHub $2 expires on $expires. Create a new one before then and run install.sh --github-tokens."
    fi
}

# ensure_github_credentials [replace]: asks for the two read-only GitHub tokens when they are missing (or when replacing),
# checks what each one can do, and stores them readable by root only.
ensure_github_credentials() {
    local replace="${1:-false}" releases packages status scopes extra login
    if ! $replace && [[ -s "$RELEASES_TOKEN_FILE" && -s "$PACKAGES_TOKEN_FILE" && -s "$PACKAGES_USER_FILE" ]]; then
        return 0
    fi
    [[ -t 0 ]] || die "install.sh has no GitHub tokens to download Fleeto releases." "Run install.sh --github-tokens once in an interactive session."
    make_work_dir
    step "GitHub access for Fleeto releases"
    info "The Fleeto repository and its images are private. install.sh needs two read-only tokens (deploy/README.md, GitHub tokens):"
    info "  1. a fine-grained token for $RELEASE_REPOSITORY with only Contents: read-only (release files);"
    info "  2. a classic token with only the read:packages scope (container images on ghcr.io)."
    info "Input is hidden."

    read -r -s -p "    Release token: " releases
    printf '\n'
    [[ "$releases" =~ ^[A-Za-z0-9_]{20,255}$ ]] || die "That is not a GitHub token." "Copy the whole token (a fine-grained token starts with github_pat_) and run install.sh --github-tokens."
    printf '%s' "$releases" >"$WORK_DIR/candidate-releases.token"
    status="$(github_request "$WORK_DIR/candidate-releases.token" "$GITHUB_API_URL/repos/$RELEASE_REPOSITORY/releases?per_page=1" "$WORK_DIR/check-releases.json")"
    [[ "$status" == 200 ]] || die "The release token cannot read the releases of $RELEASE_REPOSITORY (GitHub answered $status)." \
        "Give the token access to that repository with Contents: read-only, then run install.sh --github-tokens."
    warn_token_expiry "$WORK_DIR/check-releases.json.headers" "release token"

    read -r -s -p "    Packages token: " packages
    printf '\n'
    [[ "$packages" =~ ^[A-Za-z0-9_]{20,255}$ ]] || die "That is not a GitHub token." "Copy the whole classic token (it starts with ghp_) and run install.sh --github-tokens."
    printf '%s' "$packages" >"$WORK_DIR/candidate-packages.token"
    status="$(github_request "$WORK_DIR/candidate-packages.token" "$GITHUB_API_URL/user" "$WORK_DIR/check-user.json")"
    [[ "$status" == 200 ]] || die "GitHub refused the packages token (it answered $status)." "Create a classic token with only read:packages and run install.sh --github-tokens."
    scopes="$(header_value "$WORK_DIR/check-user.json.headers" x-oauth-scopes)"
    scopes="${scopes// /}"
    [[ ",$scopes," == *",read:packages,"* ]] || die "The packages token has no read:packages scope (scopes: ${scopes:-none}); fine-grained tokens cannot read ghcr.io." \
        "Create a classic token with only read:packages and run install.sh --github-tokens."
    extra="$(tr ',' '\n' <<<"$scopes" | grep -vx 'read:packages' | paste -sd, - || true)"
    if [[ -n "$extra" ]]; then
        warn "The packages token also has the scopes $extra. install.sh only needs read:packages; fewer scopes limit the damage if this VPS is ever compromised."
    fi
    warn_token_expiry "$WORK_DIR/check-user.json.headers" "packages token"
    login="$(jq -r '.login // empty' "$WORK_DIR/check-user.json")"
    [[ "$login" =~ ^[A-Za-z0-9-]{1,39}$ ]] || die "GitHub did not return the user of the packages token." "Try again; if it persists, create a new classic token."

    install -d -m 0700 -o root -g root "$CREDENTIALS_DIR"
    printf '%s' "$releases" | write_file_atomic "$RELEASES_TOKEN_FILE" 0600
    printf '%s' "$packages" | write_file_atomic "$PACKAGES_TOKEN_FILE" 0600
    printf '%s' "$login" | write_file_atomic "$PACKAGES_USER_FILE" 0600
    rm -f -- "$WORK_DIR"/candidate-*.token
    ok "GitHub tokens checked and stored in $CREDENTIALS_DIR (root only)"
}

# github_api <path> <output>: GET api.github.com<path> with the release token; prints the HTTP status. Callers run
# ensure_github_credentials first: it may ask for input, which cannot happen inside a command substitution.
github_api() {
    github_request "$RELEASES_TOKEN_FILE" "$GITHUB_API_URL$1" "$2"
}

# release_asset <version> <asset name> <output>: downloads one asset of the published release v<version>.
release_asset() {
    local version="$1" name="$2" output="$3" release status url
    ensure_github_credentials
    make_work_dir
    release="$WORK_DIR/release-$version.json"
    if [[ ! -s "$release" ]]; then
        status="$(github_api "/repos/$RELEASE_REPOSITORY/releases/tags/v$version" "$release")"
        if [[ "$status" != 200 ]]; then
            rm -f -- "$release"
            github_failure "$status" "release v$version"
        fi
    fi
    url="$(jq -r --arg name "$name" '[.assets[]? | select(.name == $name) | .url][0] // ""' "$release")"
    [[ "$url" == "$GITHUB_API_URL/repos/$RELEASE_REPOSITORY/releases/assets/"* ]] \
        || die "Release v$version has no file $name." "Sign and publish the release as described in deploy/RELEASING.md."
    status="$(github_request "$RELEASES_TOKEN_FILE" "$url" "$output" application/octet-stream)"
    [[ "$status" == 200 ]] || github_failure "$status" "$name of release v$version"
}

# The registry login lives in this run's work directory only, so no registry credential stays on disk after install.sh.
REGISTRY_LOGGED_IN=false
registry_login() {
    $REGISTRY_LOGGED_IN && return 0
    ensure_github_credentials
    make_work_dir
    export DOCKER_CONFIG="$WORK_DIR/docker"
    install -d -m 0700 "$DOCKER_CONFIG"
    docker login ghcr.io --username "$(<"$PACKAGES_USER_FILE")" --password-stdin <"$PACKAGES_TOKEN_FILE" >/dev/null 2>&1 \
        || die "ghcr.io refused the packages token: it expired, was revoked or lacks read:packages." "Create a classic token with only read:packages and run install.sh --github-tokens."
    REGISTRY_LOGGED_IN=true
}

# ---------------------------------------------------------------------------------------------------------------------
# Release verification
# ---------------------------------------------------------------------------------------------------------------------
KEYS_DIR=""

load_release_keys() {
    [[ -n "$KEYS_DIR" ]] && return 0
    make_work_dir
    local placeholder="STEAAN_RELEASE_""PUBLIC_KEY_PEM"
    if release_public_keys_pem | grep -qx "$placeholder"; then
        die "This install.sh has no Steaan release public key embedded, so it cannot verify releases and will not install or update anything." \
            "Download the signed install.sh from a Fleeto release on GitHub and verify it as described at the top of this file."
    fi
    KEYS_DIR="$WORK_DIR/keys"
    mkdir -p "$KEYS_DIR"
    release_public_keys_pem | awk -v dir="$KEYS_DIR" '
        /-----BEGIN PUBLIC KEY-----/ { n++; file = sprintf("%s/release-%02d.pem", dir, n); inside = 1 }
        inside { print > file }
        /-----END PUBLIC KEY-----/ { inside = 0; close(file) }'
    compgen -G "$KEYS_DIR/release-*.pem" >/dev/null || die "The embedded release public key block contains no PEM key." "Download a release build of install.sh."
}

# verify_signature <file> <signature file>: succeeds when any embedded release key verifies the ed25519 signature.
verify_signature() {
    local file="$1" signature="$2" key
    load_release_keys
    [[ -s "$signature" && "$(stat -c %s "$signature")" -eq 64 ]] || return 1
    for key in "$KEYS_DIR"/release-*.pem; do
        if openssl pkeyutl -verify -rawin -pubin -inkey "$key" -in "$file" -sigfile "$signature" >/dev/null 2>&1; then
            return 0
        fi
    done
    return 1
}

# fetch_verified_asset <version> <asset name> <output>: downloads an asset and its .sig and verifies the signature.
fetch_verified_asset() {
    local version="$1" name="$2" output="$3"
    release_asset "$version" "$name" "$output"
    release_asset "$version" "$name.sig" "$output.sig"
    verify_signature "$output" "$output.sig" \
        || die "The signature of $name in release v$version does not verify against the Steaan release public keys. The download was not used." \
            "Do not continue: it can mean the release was tampered with. Check the release on GitHub with the Steaan release manager."
}

# running_pre_release: true when an instance on this VPS runs a pre-release.
running_pre_release() {
    local instance installed
    while read -r instance; do
        installed="$(conf_get "$(instance_dir "$instance")/instance.conf" FLEETIFY_VERSION)"
        [[ "$installed" == *-* ]] && return 0
    done < <(list_instances)
    return 1
}

# The version to install or update to: the newest published release. A pre-release counts only when no release exists yet
# or an instance on this VPS runs a pre-release, so a test VPS follows pre-releases and a production VPS never does. The
# list comes from the GitHub API; what it names is only used after its manifest verifies.
LATEST_VERSION=""
NEWER_PRE_RELEASE=""
resolve_latest_version() {
    [[ -n "$LATEST_VERSION" ]] && return 0
    ensure_github_credentials
    make_work_dir
    local file="$WORK_DIR/releases.json" status tag prerelease version stable="" pre=""
    status="$(github_api "/repos/$RELEASE_REPOSITORY/releases?per_page=100" "$file")"
    [[ "$status" == 200 ]] || github_failure "$status" "the list of Fleeto releases"
    warn_token_expiry "$file.headers" "release token"
    while IFS=$'\t' read -r tag prerelease; do
        version="${tag#v}"
        if [[ "$tag" != v* ]] || ! is_version "$version"; then
            continue
        fi
        if [[ "$prerelease" == "true" || "$version" == *-* ]]; then
            if [[ -z "$pre" ]] || version_lt "$pre" "$version"; then pre="$version"; fi
        elif [[ -z "$stable" ]] || version_lt "$stable" "$version"; then
            stable="$version"
        fi
    done < <(jq -r '.[] | select(.draft == false) | [.tag_name, (.prerelease | tostring)] | @tsv' "$file")

    LATEST_VERSION="$stable"
    if [[ -n "$pre" ]] && { [[ -z "$stable" ]] || version_lt "$stable" "$pre"; }; then
        if [[ -z "$stable" ]] || running_pre_release; then
            LATEST_VERSION="$pre"
        else
            NEWER_PRE_RELEASE="$pre"
        fi
    fi
    [[ -n "$LATEST_VERSION" ]] || die "$RELEASE_REPOSITORY has no published Fleeto release." "Publish a signed release first (deploy/RELEASING.md)."
}

# Manifest of the target release, loaded by load_manifest.
MANIFEST_VERSION=""
MANIFEST_ROLLBACK=""
MANIFEST_INSTALL_SH_SHA256=""
declare -A MANIFEST_IMAGES=()

load_manifest() {
    local version="$1" file
    [[ "$MANIFEST_VERSION" == "$version" ]] && return 0
    make_work_dir
    file="$WORK_DIR/manifest-$version.json"
    step "Verifying the release manifest for Fleeto $version"
    fetch_verified_asset "$version" manifest.json "$file"

    jq -e 'type == "object" and .formatVersion == 1' "$file" >/dev/null \
        || die "The manifest for $version has an unsupported format." "Update install.sh to the latest release first."
    [[ "$(jq -r '.version' "$file")" == "$version" ]] \
        || die "The manifest downloaded for $version describes another version." "Report this to Steaan support; the download was not used."
    MANIFEST_ROLLBACK="$(jq -r '.rollback' "$file")"
    [[ "$MANIFEST_ROLLBACK" == "images" || "$MANIFEST_ROLLBACK" == "restore" ]] \
        || die "The manifest for $version has an invalid rollback mode." "Report this to Steaan support."
    MANIFEST_INSTALL_SH_SHA256="$(jq -r '.installShSha256' "$file")"
    [[ "$MANIFEST_INSTALL_SH_SHA256" =~ ^[0-9a-f]{64}$ ]] \
        || die "The manifest for $version has an invalid install.sh hash." "Report this to Steaan support."

    MANIFEST_IMAGES=()
    local name digest
    for name in "${RELEASE_IMAGES[@]}"; do
        digest="$(jq -r --arg name "$name" '.images[$name] // ""' "$file")"
        [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] \
            || die "The manifest for $version has no valid image digest for '$name'." "Report this to Steaan support."
        MANIFEST_IMAGES[$name]="$REGISTRY/fleetify-$name@$digest"
    done
    MANIFEST_VERSION="$version"
    ok "Manifest for $version verified (rollback mode: $MANIFEST_ROLLBACK)"
}

# The install.sh that applies a release must be the one listed in its verified manifest: templates and pins in this
# script belong to that release. When this script differs, download the listed one, check its hash, install it in
# /opt/fleetify/bin and hand over to it with the same arguments.
ensure_installer_for_release() {
    local version="$1" own_hash candidate
    own_hash="$(sha256_of "$SCRIPT_PATH")"
    mkdir -p "$FLEETIFY_ROOT/bin"
    chmod 0700 "$FLEETIFY_ROOT/bin"

    if [[ "$own_hash" == "$MANIFEST_INSTALL_SH_SHA256" ]]; then
        if [[ "$SCRIPT_PATH" != "$INSTALLED_SCRIPT" ]] && { [[ ! -f "$INSTALLED_SCRIPT" ]] || [[ "$(sha256_of "$INSTALLED_SCRIPT")" != "$own_hash" ]]; }; then
            install -m 0700 -o root -g root "$SCRIPT_PATH" "$INSTALLED_SCRIPT"
        fi
        return 0
    fi

    if [[ "${FLEETIFY_HANDOFF:-}" == "1" ]]; then
        die "The install.sh handed over to does not match the manifest for $version." "Report this to Steaan support."
    fi

    step "Switching to the install.sh of Fleeto $version"
    candidate="$WORK_DIR/install-$version.sh"
    release_asset "$version" install.sh "$candidate"
    [[ "$(sha256_of "$candidate")" == "$MANIFEST_INSTALL_SH_SHA256" ]] \
        || die "The downloaded install.sh for $version does not match the hash in its signed manifest. It was not used." \
            "Report this to Steaan support."
    install -m 0700 -o root -g root "$candidate" "$INSTALLED_SCRIPT"
    ok "install.sh $version verified and installed as $INSTALLED_SCRIPT"
    cleanup
    exec env FLEETIFY_HANDOFF=1 bash "$INSTALLED_SCRIPT" "${ORIGINAL_ARGS[@]}" --version "$version"
}

pull_release_images() {
    local name ref
    step "Pulling Fleeto $MANIFEST_VERSION images by digest"
    for name in "${RELEASE_IMAGES[@]}"; do
        ref="${MANIFEST_IMAGES[$name]}"
        pull_image "$ref"
    done
    pull_image "$POSTGRES_IMAGE"
}

pull_image() {
    local ref="$1" attempt
    if docker image inspect "$ref" >/dev/null 2>&1; then
        info "present: $ref"
        return 0
    fi
    if [[ "$ref" == "$REGISTRY/"* ]]; then
        registry_login
    fi
    for attempt in 1 2 3; do
        if docker pull --quiet "$ref" >/dev/null; then
            ok "pulled: $ref"
            return 0
        fi
        warn "Pulling $ref failed (attempt $attempt of 3)."
        sleep $((attempt * 5))
    done
    die "Could not pull $ref." "Check that this VPS can reach ${ref%%/*} and try again. Nothing was changed."
}

# ---------------------------------------------------------------------------------------------------------------------
# Host bootstrap: packages, Docker, host proxy
# ---------------------------------------------------------------------------------------------------------------------
ensure_packages() {
    local missing=() command package
    local -A packages=([curl]=curl [gpg]=gnupg [openssl]=openssl [jq]=jq [dig]=bind9-dnsutils [ss]=iproute2 [flock]=util-linux)
    for command in "${!packages[@]}"; do
        command -v "$command" >/dev/null 2>&1 || missing+=("${packages[$command]}")
    done
    package_installed ca-certificates || missing+=(ca-certificates)
    if [[ ${#missing[@]} -gt 0 ]]; then
        step "Installing required packages: ${missing[*]}"
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq
        apt-get install -y -qq --no-install-recommends "${missing[@]}" >/dev/null
        for package in "${missing[@]}"; do ok "installed $package"; done
    fi
}

package_installed() {
    # shellcheck disable=SC2016 # ${Status} is a dpkg-query format field, not a shell variable
    [[ "$(dpkg-query -W -f='${Status}' "$1" 2>/dev/null || true)" == "install ok installed" ]]
}

ensure_docker() {
    if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
        systemctl is-active --quiet docker || systemctl enable --now docker
        return 0
    fi

    step "Installing Docker Engine from Docker's apt repository"
    export DEBIAN_FRONTEND=noninteractive
    make_work_dir
    local key="$WORK_DIR/docker.asc" fingerprint codename
    install -d -m 0700 "$WORK_DIR/gnupg"
    download "https://download.docker.com/linux/ubuntu/gpg" "$key" \
        || die "Could not download Docker's repository key." "Check the outbound HTTPS connection of this VPS and try again."
    fingerprint="$(GNUPGHOME="$WORK_DIR/gnupg" gpg --batch --show-keys --with-colons "$key" 2>/dev/null | awk -F: '$1 == "fpr" { print $10; exit }')"
    [[ "$fingerprint" == "$DOCKER_APT_KEY_FINGERPRINT" ]] \
        || die "Docker's repository key has fingerprint '${fingerprint:-none}', expected $DOCKER_APT_KEY_FINGERPRINT. It was not trusted." \
            "Check https://docs.docker.com/engine/install/ubuntu/ for a key change and report it to Steaan support."
    install -d -m 0755 /etc/apt/keyrings
    GNUPGHOME="$WORK_DIR/gnupg" gpg --batch --yes --dearmor --output /etc/apt/keyrings/docker.gpg "$key"
    chmod 0644 /etc/apt/keyrings/docker.gpg
    # shellcheck source=/dev/null
    codename="$(. /etc/os-release && printf '%s' "$VERSION_CODENAME")"
    printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu %s stable\n' \
        "$(dpkg --print-architecture)" "$codename" >/etc/apt/sources.list.d/docker.list
    chmod 0644 /etc/apt/sources.list.d/docker.list

    ensure_docker_daemon_config
    apt-get update -qq
    apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-compose-plugin docker-buildx-plugin >/dev/null
    systemctl enable --now docker
    docker compose version >/dev/null || die "Docker Compose is not available after installing Docker." "Check the apt output above and run install.sh again."
    ok "$(docker --version)"
}

# Only written when no daemon.json exists: an existing configuration belongs to the VPS owner.
ensure_docker_daemon_config() {
    if [[ -f /etc/docker/daemon.json ]]; then
        if ! jq -e '."default-address-pools"' /etc/docker/daemon.json >/dev/null 2>&1; then
            warn "/etc/docker/daemon.json has no default-address-pools. Docker's default pools allow roughly 10 Fleeto instances (3 networks each) on this VPS."
        fi
        return 0
    fi
    install -d -m 0755 /etc/docker
    cat >/etc/docker/daemon.json <<'JSON'
{
  "default-address-pools": [{ "base": "10.210.0.0/16", "size": 24 }],
  "log-driver": "local",
  "log-opts": { "max-size": "20m", "max-file": "5" },
  "live-restore": true,
  "no-new-privileges": true
}
JSON
    chmod 0644 /etc/docker/daemon.json
    ok "wrote /etc/docker/daemon.json (address pool 10.210.0.0/16 in /24 networks, log rotation, live restore)"
}

ensure_fleetify_root() {
    install -d -m 0700 -o root -g root "$FLEETIFY_ROOT" "$FLEETIFY_ROOT/bin"
}

acquire_lock() {
    ensure_fleetify_root
    # fd 9 stays open (and locked) across the exec into a newer install.sh.
    exec 9>"$FLEETIFY_ROOT/.install.lock"
    flock -n 9 || die "Another install.sh run is in progress on this VPS." "Wait for it to finish and run install.sh again."
}

dc_caddy() {
    docker compose --project-name fleetify-caddy --project-directory "$CADDY_DIR" \
        --env-file "$CADDY_DIR/caddy.conf" -f "$CADDY_DIR/compose.yml" "$@"
}

# ensure_caddy <release version>: installs or updates the host proxy. The proxy is shared, so it only moves forward:
# a release older than the running proxy never replaces it.
ensure_caddy() {
    local version="$1" image="${MANIFEST_IMAGES[caddy]}" current_release="" changed=false
    install -d -m 0700 -o root -g root "$CADDY_DIR" "$CADDY_DIR/config"
    if [[ -f "$CADDY_DIR/caddy.conf" ]]; then
        current_release="$(conf_get "$CADDY_DIR/caddy.conf" CADDY_RELEASE)"
    fi
    if [[ -z "$current_release" ]] || version_lt "$current_release" "$version"; then
        step "Setting up the host proxy (Caddy with SNI passthrough) from release $version"
        write_file_atomic "$CADDY_DIR/caddy.conf" 0600 <<CONF
# Fleeto host proxy configuration, written by install.sh. Contains no secrets.
CADDY_RELEASE=$version
CADDY_IMAGE=$image
CONF
        changed=true
    fi
    template "caddy/compose.yml" | write_file_atomic "$CADDY_DIR/compose.yml" 0600
    if [[ ! -f "$CADDY_DIR/config/Caddyfile" ]]; then
        generate_caddyfile | write_file_atomic "$CADDY_DIR/config/Caddyfile" 0600
    fi
    image="$(conf_get "$CADDY_DIR/caddy.conf" CADDY_IMAGE)"
    pull_image "$image"
    if $changed || ! caddy_running; then
        dc_caddy up -d --remove-orphans >/dev/null
        wait_for_container_health fleetify-caddy 90 || die "The host proxy did not become healthy." "Check 'docker logs fleetify-caddy' and run install.sh again."
        ok "host proxy running"
    fi
}

caddy_running() {
    [[ "$(docker inspect -f '{{.State.Running}}' fleetify-caddy 2>/dev/null || true)" == "true" ]]
}

# ---------------------------------------------------------------------------------------------------------------------
# Instance files
# ---------------------------------------------------------------------------------------------------------------------
# write_file_atomic <path> <mode>: writes stdin to a temporary file next to <path>, then renames it into place.
write_file_atomic() {
    local path="$1" mode="$2" temporary
    temporary="$(mktemp "$path.XXXXXX")"
    cat >"$temporary"
    chmod "$mode" "$temporary"
    mv -f "$temporary" "$path"
}

# conf_get <file> <key>: reads KEY=value without executing the file.
conf_get() {
    awk -v key="$2" 'index($0, key "=") == 1 { print substr($0, length(key) + 2); exit }' "$1"
}

validate_fqdn() {
    local fqdn="$1"
    [[ ${#fqdn} -le 240 ]] || die "The FQDN '$fqdn' is too long." "Use a name of at most 240 characters."
    [[ "$fqdn" =~ ^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]([a-z0-9-]{0,61}[a-z0-9])?$ ]] \
        || die "'$fqdn' is not a valid FQDN." "Use a fully qualified lowercase name such as rmm.customer.example."
}

instance_name_for() { printf '%s' "${1//./-}"; }
instance_dir() { printf '%s/%s' "$FLEETIFY_ROOT" "$1"; }

# All instance directories (those with an instance.conf), sorted.
list_instances() {
    local conf
    for conf in "$FLEETIFY_ROOT"/*/instance.conf; do
        [[ -f "$conf" ]] || continue
        basename "$(dirname "$conf")"
    done | sort
}

dc_instance() {
    local instance="$1" dir
    shift
    dir="$(instance_dir "$instance")"
    docker compose --project-name "fleetify-$instance" --project-directory "$dir" \
        --env-file "$dir/instance.conf" -f "$dir/compose.yml" "$@"
}

port_in_use() {
    local port="$1" conf
    for conf in "$FLEETIFY_ROOT"/*/instance.conf; do
        [[ -f "$conf" ]] || continue
        if [[ "$(conf_get "$conf" WEB_PORT)" == "$port" || "$(conf_get "$conf" AGENT_PORT)" == "$port" ]]; then
            return 0
        fi
    done
    [[ -n "$(ss -Htan "sport = :$port" 2>/dev/null)" ]]
}

# Sets WEB_PORT and AGENT_PORT to the first free pair of loopback ports.
allocate_ports() {
    local port
    for ((port = PORT_RANGE_START; port <= PORT_RANGE_END; port += 2)); do
        if ! port_in_use "$port" && ! port_in_use "$((port + 1))"; then
            WEB_PORT="$port"
            AGENT_PORT="$((port + 1))"
            return 0
        fi
    done
    die "No free loopback ports left between $PORT_RANGE_START and $PORT_RANGE_END." "Remove unused instances from this VPS."
}

# write_instance_conf <instance> <fqdn> <version> <state> <web port> <agent port>; images from the loaded manifest.
# host_network_mtu: the MTU of the interface the default route leaves through (a 1400 link, a tunnel), between 1280 and
# 1500. Containers on a Docker network with a larger MTU than the uplink send packets the uplink cannot carry; that only
# works while every hop returns "packet too big", so the instance networks use this MTU instead.
host_network_mtu() {
    local device mtu
    device="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{ for (i = 1; i < NF; i++) if ($i == "dev") { print $(i + 1); exit } }')"
    mtu="$(cat "/sys/class/net/${device:-none}/mtu" 2>/dev/null || true)"
    if [[ ! "$mtu" =~ ^[0-9]{3,5}$ ]]; then
        printf '1500'
        return 0
    fi
    if ((mtu > 1500)); then mtu=1500; fi
    if ((mtu < 1280)); then mtu=1280; fi
    printf '%s' "$mtu"
}

# instance_networks_diverge <instance>: true when an existing network of the instance has another MTU than instance.conf.
instance_networks_diverge() {
    local instance="$1" expected network actual
    expected="$(conf_get "$(instance_dir "$instance")/instance.conf" NETWORK_MTU)"
    expected="${expected:-1500}"
    for network in internal edge egress; do
        actual="$(docker network inspect --format '{{ index .Options "com.docker.network.driver.mtu" }}' "fleetify-${instance}_$network" 2>/dev/null)" \
            || continue
        if [[ -z "$actual" || "$actual" == "<no value>" ]]; then
            actual=1500
        fi
        if [[ "$actual" != "$expected" ]]; then
            return 0
        fi
    done
    return 1
}

# recreate_networks_if_diverged <instance>: Docker cannot change the MTU of an existing network, so when it differs from
# instance.conf the instance is stopped and its networks removed (volumes stay); the next start creates them anew.
recreate_networks_if_diverged() {
    local instance="$1"
    instance_networks_diverge "$instance" || return 0
    step "Recreating the networks of $instance with MTU $(conf_get "$(instance_dir "$instance")/instance.conf" NETWORK_MTU)"
    dc_instance "$instance" down --timeout 30 --remove-orphans >/dev/null
}

write_instance_conf() {
    local instance="$1" fqdn="$2" version="$3" state="$4" web_port="$5" agent_port="$6" dir edge_egress="false" memory="1GB" cpus="2"
    dir="$(instance_dir "$instance")"
    # Keep settings the VPS owner may have tuned.
    if [[ -f "$dir/instance.conf" ]]; then
        edge_egress="$(conf_get "$dir/instance.conf" EDGE_EGRESS)"; edge_egress="${edge_egress:-false}"
        memory="$(conf_get "$dir/instance.conf" POSTGRES_MEMORY)"; memory="${memory:-1GB}"
        cpus="$(conf_get "$dir/instance.conf" POSTGRES_CPUS)"; cpus="${cpus:-2}"
    fi
    write_file_atomic "$dir/instance.conf" 0600 <<CONF
# Fleeto instance configuration, written by install.sh. It contains NO secrets: secrets are files in ./secrets/.
# Used as the Compose --env-file for interpolation only. Edit only EDGE_EGRESS, POSTGRES_MEMORY and POSTGRES_CPUS.
FLEETIFY_INSTANCE=$instance
FLEETIFY_FQDN=$fqdn
FLEETIFY_VERSION=$version
FLEETIFY_STATE=$state
FLEETIFY_ROLLBACK=$MANIFEST_ROLLBACK
WEB_PORT=$web_port
AGENT_PORT=$agent_port
EDGE_EGRESS=$edge_egress
POSTGRES_MEMORY=$memory
POSTGRES_CPUS=$cpus
# MTU of the instance networks, detected from the uplink of this VPS on every install and update.
NETWORK_MTU=$(host_network_mtu)
POSTGRES_IMAGE=$POSTGRES_IMAGE
TOOL_IMAGE=${MANIFEST_IMAGES[tool]}
SIGNER_IMAGE=${MANIFEST_IMAGES[signer]}
GATEWAY_IMAGE=${MANIFEST_IMAGES[gateway]}
WORKERS_IMAGE=${MANIFEST_IMAGES[workers]}
WEB_IMAGE=${MANIFEST_IMAGES[web]}
CONF
}

set_instance_state() {
    local instance="$1" state="$2" conf
    conf="$(instance_dir "$instance")/instance.conf"
    sed "s/^FLEETIFY_STATE=.*/FLEETIFY_STATE=$state/" "$conf" | write_file_atomic "$conf" 0600
}

set_instance_version() {
    local instance="$1" version="$2" conf
    conf="$(instance_dir "$instance")/instance.conf"
    sed "s/^FLEETIFY_VERSION=.*/FLEETIFY_VERSION=$version/" "$conf" | write_file_atomic "$conf" 0600
}

# write_release_manifest <instance dir>: the manifest of the loaded release and its signature, both verified by load_manifest, for the
# gateway to offer agent updates. Agents verify the signature again against the release keys compiled into them.
write_release_manifest() {
    local dir="$1" source="$WORK_DIR/manifest-$MANIFEST_VERSION.json"
    [[ -n "$MANIFEST_VERSION" && -f "$source" && -f "$source.sig" ]] \
        || die "The verified release manifest is not available to hand to the gateway." "Run install.sh again."
    install -d -m 0755 -o root -g root "$dir/release"
    install -m 0644 -o root -g root "$source" "$dir/release/manifest.json.new"
    install -m 0644 -o root -g root "$source.sig" "$dir/release/manifest.json.sig.new"
    mv -f "$dir/release/manifest.json.sig.new" "$dir/release/manifest.json.sig"
    mv -f "$dir/release/manifest.json.new" "$dir/release/manifest.json"
}

write_instance_templates() {
    local dir="$1"
    install -d -m 0700 -o root -g root "$dir/postgres"
    # Bind-mounted as a directory: the postgres user (uid 70) in the container must be able to list it.
    install -d -m 0755 -o root -g root "$dir/postgres/init"
    template "compose/compose.yml" | write_file_atomic "$dir/compose.yml" 0600
    # Read by the postgres user inside the container.
    template "postgres/init/10-fleetify-roles.sh" | write_file_atomic "$dir/postgres/init/10-fleetify-roles.sh" 0755
    template "postgres/archive-wal.sh" | write_file_atomic "$dir/postgres/archive-wal.sh" 0755
}

create_instance_directories() {
    local dir="$1"
    install -d -m 0700 -o root -g root "$dir" "$dir/secrets" "$dir/backups" "$dir/state"
    # Read by the gateway (uid 10001) through a read-only bind mount; nothing in it is secret.
    install -d -m 0755 -o root -g root "$dir/release"
    # WAL spool shared by postgres (owner) and the workers (group); setgid keeps the group on new files.
    install -d -m 2770 -o "$POSTGRES_UID" -g "$APP_GID" "$dir/wal-spool"
    install -d -m 0700 -o "$APP_UID" -g "$APP_GID" "$dir/work"
}

# Secret files are generated once and never overwritten: replacing root.key or signer.key makes the instance's
# encrypted data unreadable. Mode 0440 root:10001, because Compose mounts file secrets as bind mounts that keep host
# ownership and the containers run as uid 10001 (postgres joins group 10001). The directory is 0700 root.
generate_secret_file() {
    local path="$1" kind="$2" temporary
    if [[ -e "$path" ]]; then
        chown root:"$APP_GID" "$path"
        chmod 0440 "$path"
        return 0
    fi
    temporary="$(mktemp "$path.XXXXXX")"
    case "$kind" in
        key) openssl rand -base64 32 | tr -d '\n' >"$temporary" ;;
        password) openssl rand -hex 32 | tr -d '\n' >"$temporary" ;;
    esac
    [[ -s "$temporary" ]] || { rm -f "$temporary"; die "Generating $(basename "$path") failed." "Check that openssl works and run install.sh again."; }
    chown root:"$APP_GID" "$temporary"
    chmod 0440 "$temporary"
    mv -n "$temporary" "$path"
    rm -f "$temporary"
    ok "generated $(basename "$path")"
}

generate_instance_secrets() {
    local secrets="$1/secrets" role
    generate_secret_file "$secrets/root.key" key
    generate_secret_file "$secrets/signer.key" key
    for role in postgres migrator web gateway signer workers backup; do
        generate_secret_file "$secrets/db-$role.password" password
    done
}

append_history() {
    local instance="$1" line="$2"
    printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$line" >>"$(instance_dir "$instance")/state/history.log"
}

# ---------------------------------------------------------------------------------------------------------------------
# DNS
# ---------------------------------------------------------------------------------------------------------------------
resolve_addresses() {
    local name="$1"
    {
        dig +short +time=3 +tries=2 A "$name" 2>/dev/null || true
        dig +short +time=3 +tries=2 AAAA "$name" 2>/dev/null || true
    } | { grep -E '^([0-9]{1,3}\.){3}[0-9]{1,3}$|^[0-9a-fA-F:]+:[0-9a-fA-F:.]*$' || true; } | tr 'A-F' 'a-f' | sort -u
}

# detected_host_addresses: the addresses on this VPS's interfaces and the public addresses it is seen with outbound.
detected_host_addresses() {
    ip -o addr show scope global 2>/dev/null | awk '{ split($4, a, "/"); print a[1] }'
    # A VPS behind 1:1 NAT has no public address on its interfaces; ask for the public one as well.
    if curl -4 --silent --fail --max-time 5 https://api.ipify.org 2>/dev/null; then echo; fi
    if curl -6 --silent --fail --max-time 5 https://api6.ipify.org 2>/dev/null; then echo; fi
}

# stored_public_addresses: inbound addresses of a firewall or NAT in front of this VPS, confirmed earlier.
stored_public_addresses() {
    if [[ -f "$PUBLIC_ADDRESSES_FILE" ]]; then
        grep -E '^([0-9]{1,3}\.){3}[0-9]{1,3}$|^[0-9a-f:]+:[0-9a-f:.]*$' "$PUBLIC_ADDRESSES_FILE" || true
    fi
}

host_addresses() {
    { detected_host_addresses; stored_public_addresses; } | { grep -v '^$' || true; } | tr 'A-F' 'a-f' | sort -u
}

# is_interactive: stdin is a terminal. Callers never redirect stdin around a question (a loop over a here-string would
# silently answer it).
is_interactive() { [[ -t 0 ]]; }

# confirm_forwarded_address <address> <name>: asks whether a firewall or NAT forwards TCP 80 and 443 on an address that
# is not on this VPS (inbound and outbound addresses often differ behind a firewall). A confirmed address is stored, so
# later runs do not ask again. Never assumed without an interactive answer: a wrong record means no certificates.
confirm_forwarded_address() {
    local address="$1" name="$2" answer
    is_interactive || return 1
    info "$name resolves to $address, which is not an address of this VPS."
    info "Behind a firewall or NAT that is expected when it forwards TCP 80 and 443 on $address to this VPS"
    info "(a plain port forward that keeps the client address, so agents are shown with their own address)."
    read -r -p "    Does a firewall or NAT forward TCP 80 and 443 on $address to this VPS? [y/N] " answer
    [[ "$answer" =~ ^[Yy]([Ee][Ss])?$ ]] || return 1
    ensure_fleetify_root
    { stored_public_addresses; printf '%s\n' "$address"; } | sort -u | write_file_atomic "$PUBLIC_ADDRESSES_FILE" 0600
    ok "$address stored as a forwarded public address of this VPS ($PUBLIC_ADDRESSES_FILE)"
}

check_dns() {
    local fqdn="$1" name address problems=() host_ips resolved name_ok
    local -a addresses
    step "Checking DNS for $fqdn and agents.$fqdn"
    host_ips="$(host_addresses)"
    for name in "$fqdn" "agents.$fqdn"; do
        resolved="$(resolve_addresses "$name")"
        if [[ -z "$resolved" ]]; then
            problems+=("$name has no A or AAAA record")
            continue
        fi
        name_ok=true
        # An array, not a loop reading a here-string: confirm_forwarded_address reads the answer from stdin.
        mapfile -t addresses <<<"$resolved"
        for address in "${addresses[@]}"; do
            if grep -qxF "$address" <<<"$host_ips"; then
                continue
            fi
            if confirm_forwarded_address "$address" "$name"; then
                host_ips="$(printf '%s\n%s\n' "$host_ips" "$address" | sort -u)"
            else
                problems+=("$name resolves to $address, which is not an address of this VPS and not confirmed as forwarded to it")
                name_ok=false
            fi
        done
        if $name_ok; then
            ok "$name -> $(tr '\n' ' ' <<<"$resolved")"
        fi
    done
    if [[ ${#problems[@]} -gt 0 ]]; then
        local problem
        for problem in "${problems[@]}"; do error "$problem"; done
        local ipv4 ipv6
        # Suggest the forwarded public address when there is one, otherwise this VPS's own public address.
        ipv4="$( { stored_public_addresses; printf '%s\n' "$host_ips"; } | grep -E '^([0-9]{1,3}\.){3}[0-9]{1,3}$' | grep -Ev '^(10\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.)' | head -n 1 || true)"
        ipv6="$(grep ':' <<<"$host_ips" | grep -Ev '^(fe80|fc|fd)' | head -n 1 || true)"
        printf '\n       Create these DNS records, wait until they resolve, and run install.sh again:\n' >&2
        printf '         %-40s A     %s\n' "$fqdn" "${ipv4:-<public IPv4 of this VPS>}" "agents.$fqdn" "${ipv4:-<public IPv4 of this VPS>}" >&2
        if [[ -n "$ipv6" ]]; then
            printf '         %-40s AAAA  %s\n' "$fqdn" "$ipv6" "agents.$fqdn" "$ipv6" >&2
        fi
        printf '       Remove any record that points elsewhere. Behind a firewall or NAT, use the address it forwards TCP 80 and 443 on\n' >&2
        printf '       and run install.sh interactively to confirm that address once.\n' >&2
        exit 1
    fi
}

# ---------------------------------------------------------------------------------------------------------------------
# Host proxy configuration
# ---------------------------------------------------------------------------------------------------------------------
# The Caddyfile is generated from every instance.conf on this VPS. Agent traffic for agents.<fqdn> is matched by SNI in
# a layer4 listener wrapper on :443 and proxied to the gateway untouched (mTLS stays end to end), preceded by a PROXY
# protocol v2 header so the gateway learns the agent's address (it trusts the header only from the Docker networks the
# published port is reached through). Every other
# connection falls through to Caddy's own TLS, which terminates HTTPS per FQDN with automatic certificates.
generate_caddyfile() {
    local instance conf fqdn web_port agent_port matcher
    local -a instances=()
    mapfile -t instances < <(list_instances)

    printf '# Generated by install.sh from %s/*/instance.conf. Do not edit: install.sh overwrites this file.\n' "$FLEETIFY_ROOT"
    printf '{\n'
    printf '\t# Admin API on a unix socket inside the container only, never on TCP.\n'
    printf '\tadmin unix//run/caddy/admin.sock\n'
    if [[ ${#instances[@]} -gt 0 ]]; then
        printf '\n\tservers :443 {\n'
        printf '\t\tlistener_wrappers {\n'
        printf '\t\t\tlayer4 {\n'
        for instance in "${instances[@]}"; do
            conf="$(instance_dir "$instance")/instance.conf"
            fqdn="$(conf_get "$conf" FLEETIFY_FQDN)"
            agent_port="$(conf_get "$conf" AGENT_PORT)"
            validate_generated_values "$instance" "$fqdn" "$agent_port"
            matcher="agents_${instance//-/_}"
            printf '\t\t\t\t@%s tls sni agents.%s\n' "$matcher" "$fqdn"
            printf '\t\t\t\troute @%s {\n' "$matcher"
            printf '\t\t\t\t\tproxy 127.0.0.1:%s {\n' "$agent_port"
            printf '\t\t\t\t\t\tproxy_protocol v2\n'
            printf '\t\t\t\t\t}\n'
            printf '\t\t\t\t}\n'
        done
        printf '\t\t\t}\n'
        printf '\t\t\ttls\n'
        printf '\t\t}\n'
        printf '\t}\n'
    fi
    printf '}\n\n'

    printf '# Container health check (loopback only).\n'
    printf 'http://127.0.0.1:2020 {\n\tbind 127.0.0.1\n\trespond "ok" 200\n}\n'

    for instance in "${instances[@]}"; do
        conf="$(instance_dir "$instance")/instance.conf"
        fqdn="$(conf_get "$conf" FLEETIFY_FQDN)"
        web_port="$(conf_get "$conf" WEB_PORT)"
        validate_generated_values "$instance" "$fqdn" "$web_port"
        printf '\n# Instance %s\n' "$instance"
        printf '%s {\n' "$fqdn"
        printf '\theader Strict-Transport-Security "max-age=31536000; includeSubDomains"\n'
        printf '\treverse_proxy 127.0.0.1:%s\n' "$web_port"
        printf '}\n'
    done
}

# Values from instance.conf end up in the proxy configuration; never let a damaged file inject directives.
validate_generated_values() {
    local instance="$1" fqdn="$2" port="$3"
    [[ "$fqdn" =~ ^[a-z0-9.-]+$ && "$port" =~ ^[0-9]{4,5}$ ]] \
        || die "instance.conf of $instance contains an invalid FQDN or port." "Check $(instance_dir "$instance")/instance.conf."
}

# Regenerates the Caddyfile, validates it with the Caddy image and reloads the running proxy gracefully (agent
# connections of other instances survive a reload). On failure the previous configuration stays active.
# caddy_adapt <image> <directory with a Caddyfile>: validates the Caddyfile with the proxy image, isolated (no network,
# read-only) but with the same capabilities as the running proxy. The Caddy binary carries the file capability to bind
# ports below 1024, and the kernel refuses to start it when that capability is not in the container's bounding set.
caddy_adapt() {
    local image="$1" directory="$2"
    docker run --rm --network none --read-only --user 0:0 --cap-drop ALL --cap-add NET_BIND_SERVICE \
        --security-opt no-new-privileges:true -v "$directory:/candidate:ro" "$image" \
        adapt --config /candidate/Caddyfile --adapter caddyfile >/dev/null
}

update_caddy_routes() {
    local image candidate_dir previous
    step "Updating host proxy routes"
    image="$(conf_get "$CADDY_DIR/caddy.conf" CADDY_IMAGE)"
    make_work_dir
    candidate_dir="$WORK_DIR/caddy-candidate"
    mkdir -p "$candidate_dir"
    generate_caddyfile >"$candidate_dir/Caddyfile"
    if ! caddy_adapt "$image" "$candidate_dir" 2>"$WORK_DIR/caddy-adapt.log"; then
        cat "$WORK_DIR/caddy-adapt.log" >&2
        die "The generated proxy configuration is invalid; the running proxy was not changed." "Report this output to Steaan support."
    fi

    previous="$WORK_DIR/Caddyfile.previous"
    cp -p "$CADDY_DIR/config/Caddyfile" "$previous" 2>/dev/null || true
    write_file_atomic "$CADDY_DIR/config/Caddyfile" 0600 <"$candidate_dir/Caddyfile"
    if ! caddy_running; then
        dc_caddy up -d >/dev/null
    fi
    if docker exec fleetify-caddy caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile --address unix//run/caddy/admin.sock >/dev/null 2>"$WORK_DIR/caddy-reload.log"; then
        ok "proxy routes reloaded"
        return 0
    fi
    cat "$WORK_DIR/caddy-reload.log" >&2
    if [[ -f "$previous" ]]; then
        write_file_atomic "$CADDY_DIR/config/Caddyfile" 0600 <"$previous"
        docker exec fleetify-caddy caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile --address unix//run/caddy/admin.sock >/dev/null 2>&1 || true
    fi
    die "The host proxy refused the new routes; the previous routes were restored." "Check 'docker logs fleetify-caddy' and run install.sh again."
}

# ---------------------------------------------------------------------------------------------------------------------
# Health
# ---------------------------------------------------------------------------------------------------------------------
wait_for_container_health() {
    local container="$1" timeout="$2" deadline status
    deadline=$((SECONDS + timeout))
    while ((SECONDS < deadline)); do
        status="$(docker inspect -f '{{.State.Status}}/{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$container" 2>/dev/null || true)"
        case "$status" in
            running/healthy | running/none) return 0 ;;
            exited/* | dead/*) return 1 ;;
        esac
        sleep 3
    done
    return 1
}

# instance_health_problems <instance>: prints one line per problem; prints nothing when healthy.
instance_health_problems() {
    local instance="$1" dir service container_id status fqdn web_port agent_port
    dir="$(instance_dir "$instance")"
    fqdn="$(conf_get "$dir/instance.conf" FLEETIFY_FQDN)"
    web_port="$(conf_get "$dir/instance.conf" WEB_PORT)"
    agent_port="$(conf_get "$dir/instance.conf" AGENT_PORT)"
    for service in "${INSTANCE_SERVICES[@]}"; do
        container_id="$(dc_instance "$instance" ps -q "$service" 2>/dev/null || true)"
        if [[ -z "$container_id" ]]; then
            echo "$service is not running"
            continue
        fi
        status="$(docker inspect -f '{{.State.Status}}/{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$container_id" 2>/dev/null || true)"
        [[ "$status" == "running/healthy" ]] || echo "$service is ${status:-unknown}"
    done
    curl --fail --silent --max-time 5 --output /dev/null "http://127.0.0.1:$web_port/health" \
        || echo "web does not answer /health on 127.0.0.1:$web_port"
    # A completed TLS handshake proves the gateway obtained its server certificate from the signer.
    timeout 10 openssl s_client -connect "127.0.0.1:$agent_port" -servername "agents.$fqdn" </dev/null >/dev/null 2>&1 \
        || echo "gateway does not complete a TLS handshake on 127.0.0.1:$agent_port"
}

wait_for_instance_health() {
    local instance="$1" timeout="${2:-$HEALTH_TIMEOUT_SECONDS}" deadline health_report="" line
    deadline=$((SECONDS + timeout))
    info "waiting up to ${timeout}s for all services to become healthy"
    while ((SECONDS < deadline)); do
        health_report="$(instance_health_problems "$instance")"
        if [[ -z "$health_report" ]]; then
            ok "all services healthy"
            return 0
        fi
        sleep 5
    done
    while read -r line; do error "$line"; done <<<"$health_report"
    return 1
}

show_unhealthy_logs() {
    local instance="$1" service
    for service in signer gateway workers web; do
        printf '\n--- last log lines of %s ---\n' "$service" >&2
        dc_instance "$instance" logs --no-color --tail 25 "$service" >&2 2>/dev/null || true
    done
}

wait_for_postgres() {
    local instance="$1" container_id
    dc_instance "$instance" up -d --no-deps postgres >/dev/null
    container_id="$(dc_instance "$instance" ps -q postgres)"
    wait_for_container_health "$container_id" "$HEALTH_TIMEOUT_SECONDS" \
        || die "PostgreSQL of $instance did not become healthy." "Check '$(dc_hint "$instance") logs postgres' and run install.sh again."
    ok "postgres healthy"
}

# run_migrator <instance>: runs the one-shot migrator and returns its exit code.
run_migrator() {
    local instance="$1" container_id code
    dc_instance "$instance" up -d --no-deps --force-recreate migrator >/dev/null
    container_id="$(dc_instance "$instance" ps -a -q migrator)"
    code="$(docker wait "$container_id")"
    if [[ "$code" != "0" ]]; then
        # The migrator prints no secrets on failure (the setup link only appears after success).
        dc_instance "$instance" logs --no-color --tail 40 migrator >&2 || true
        return 1
    fi
    return 0
}

dc_hint() { printf 'docker compose -p fleetify-%s --env-file %s/instance.conf -f %s/compose.yml' "$1" "$(instance_dir "$1")" "$(instance_dir "$1")"; }

setup_link_of() {
    dc_instance "$1" logs --no-color --no-log-prefix migrator 2>/dev/null \
        | grep -Eo 'https://[^[:space:]]+/setup\?token=[^[:space:]]+' | tail -n 1 || true
}

# ---------------------------------------------------------------------------------------------------------------------
# Database backup and restore (pre-update safety copy; the nightly off-VPS backup is done by fleetify-workers)
# ---------------------------------------------------------------------------------------------------------------------
backup_database() {
    local instance="$1" label="$2" dir file
    dir="$(instance_dir "$instance")/backups"
    file="$dir/$label-$(date -u +%Y%m%dT%H%M%SZ).dump"
    step "Backing up the database of $instance"
    if ! dc_instance "$instance" exec -T postgres pg_dump --username=postgres --dbname=fleetify --format=custom >"$file.partial"; then
        rm -f "$file.partial"
        die "The pre-update backup of $instance failed; nothing was changed." "Check '$(dc_hint "$instance") logs postgres' and free disk space, then run install.sh again."
    fi
    chmod 0600 "$file.partial"
    mv "$file.partial" "$file"
    ok "backup written: $file ($(du -h "$file" | awk '{ print $1 }'))"
    # Keep the most recent pre-update backups only; they exist for rollback, not as the backup strategy.
    find "$dir" -maxdepth 1 -type f -name 'pre-update-*.dump' -printf '%T@ %p\n' | sort -rn \
        | awk -v keep="$PRE_UPDATE_BACKUPS_KEPT" 'NR > keep { sub(/^[^ ]+ /, ""); print }' \
        | while read -r old; do rm -f -- "$old"; done
    LAST_BACKUP_FILE="$file"
}

restore_database() {
    local instance="$1" file="$2" log
    log="$(instance_dir "$instance")/backups/restore-$(date -u +%Y%m%dT%H%M%SZ).log"
    step "Restoring the database of $instance from $(basename "$file")"
    dc_instance "$instance" exec -T postgres psql --no-psqlrc --set ON_ERROR_STOP=1 --username=postgres --dbname=postgres >/dev/null <<'SQL'
DROP DATABASE IF EXISTS fleetify WITH (FORCE);
CREATE DATABASE fleetify OWNER fleetify_migrator ENCODING 'UTF8';
REVOKE ALL ON DATABASE fleetify FROM PUBLIC;
GRANT CONNECT ON DATABASE fleetify TO fleetify_migrator, fleetify_web, fleetify_gateway, fleetify_signer, fleetify_workers, fleetify_backup;
SQL
    dc_instance "$instance" exec -T postgres psql --no-psqlrc --set ON_ERROR_STOP=1 --username=postgres --dbname=fleetify >/dev/null <<'SQL'
ALTER SCHEMA public OWNER TO fleetify_migrator;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
CREATE EXTENSION IF NOT EXISTS timescaledb;
SELECT timescaledb_pre_restore();
SQL
    local restore_status=0
    dc_instance "$instance" exec -T postgres pg_restore --username=postgres --dbname=fleetify --no-password <"$file" >"$log" 2>&1 || restore_status=$?
    chmod 0600 "$log"
    dc_instance "$instance" exec -T postgres psql --no-psqlrc --set ON_ERROR_STOP=1 --username=postgres --dbname=fleetify \
        -c 'SELECT timescaledb_post_restore();' >/dev/null
    if [[ "$restore_status" -ne 0 ]]; then
        warn "pg_restore reported errors (exit code $restore_status); details in $log."
        return 1
    fi
    ok "database restored"
}

# ---------------------------------------------------------------------------------------------------------------------
# Install a new instance
# ---------------------------------------------------------------------------------------------------------------------
install_instance() {
    local fqdn="$1" version="$2" instance dir existing_fqdn
    instance="$(instance_name_for "$fqdn")"
    dir="$(instance_dir "$instance")"

    if [[ -f "$dir/instance.conf" ]]; then
        existing_fqdn="$(conf_get "$dir/instance.conf" FLEETIFY_FQDN)"
        [[ "$existing_fqdn" == "$fqdn" ]] \
            || die "Instance name $instance is already used by $existing_fqdn." "Choose an FQDN that does not map to the same name (dots become dashes)."
    fi

    step "Installing Fleeto $version for $fqdn (instance $instance)"
    pull_release_images
    ensure_caddy "$version"

    step "Preparing $dir"
    create_instance_directories "$dir"
    generate_instance_secrets "$dir"
    local web_port agent_port
    if [[ -f "$dir/instance.conf" ]]; then
        # A previous attempt got this far: keep its ports.
        web_port="$(conf_get "$dir/instance.conf" WEB_PORT)"
        agent_port="$(conf_get "$dir/instance.conf" AGENT_PORT)"
    else
        allocate_ports
        web_port="$WEB_PORT"
        agent_port="$AGENT_PORT"
    fi
    write_instance_templates "$dir"
    write_release_manifest "$dir"
    write_instance_conf "$instance" "$fqdn" "$version" installing "$web_port" "$agent_port"
    ok "web on 127.0.0.1:$web_port, gateway on 127.0.0.1:$agent_port"

    recreate_networks_if_diverged "$instance"
    step "Starting PostgreSQL"
    wait_for_postgres "$instance"

    step "Running database migrations"
    run_migrator "$instance" || die "The migrations of $instance failed." "Read the log lines above, fix the cause and run install.sh again; it continues where it stopped."
    ok "migrations applied"

    step "Starting the instance"
    dc_instance "$instance" up -d --remove-orphans >/dev/null \
        || die "Starting $instance failed." "Check '$(dc_hint "$instance") ps' and the logs, then run install.sh again."
    if ! wait_for_instance_health "$instance"; then
        show_unhealthy_logs "$instance"
        die "$instance did not become healthy." "Read the log lines above, fix the cause and run install.sh again."
    fi

    set_instance_state "$instance" ok
    update_caddy_routes
    append_history "$instance" "installed $version"

    local link
    link="$(setup_link_of "$instance")"
    step "Fleeto $version is running for $fqdn"
    info "URL:          https://$fqdn"
    info "Agents:       agents.$fqdn:443"
    if [[ -n "$link" ]]; then
        info "First admin:  open this one-time link within 24 hours:"
        printf '\n    %s\n\n' "$link"
    fi
    print_key_ceremony_reminder "$dir"
}

print_key_ceremony_reminder() {
    local dir="$1"
    printf '    %sKey ceremony, do this today:%s\n' "$C_BOLD" "$C_RESET"
    info "1. Copy $dir/secrets/root.key and signer.key to offline storage (two copies, separate places)."
    info "   Without them no backup of this instance can ever be restored."
    info "2. On an offline machine, create the backup key pair: fleetify-tool backup keygen --out <folder>"
    info "   Keep backup.key offline; paste the public key into Settings, Backups during first-admin setup."
    info "3. Configure the backup destination in Settings, Backups. Until then backups exist on this VPS only."
    info "See deploy/README.md (Key ceremony) for the full procedure."
}

# ---------------------------------------------------------------------------------------------------------------------
# Update an instance
# ---------------------------------------------------------------------------------------------------------------------
LAST_BACKUP_FILE=""

update_instance() {
    local instance="$1" version="$2" dir fqdn installed_version rollback_mode
    dir="$(instance_dir "$instance")"
    fqdn="$(conf_get "$dir/instance.conf" FLEETIFY_FQDN)"
    installed_version="$(conf_get "$dir/instance.conf" FLEETIFY_VERSION)"

    if [[ "$(conf_get "$dir/instance.conf" FLEETIFY_STATE)" == "updating" && -d "$dir/state/previous" ]]; then
        # An earlier run stopped halfway through an update: start again from the configuration before it.
        warn "An earlier update of $fqdn was interrupted; restoring its previous configuration first."
        cp -a "$dir/state/previous/compose.yml" "$dir/state/previous/instance.conf" "$dir/"
        rm -rf -- "$dir/postgres"
        cp -a "$dir/state/previous/postgres" "$dir/"
        restore_previous_release_manifest "$dir"
        installed_version="$(conf_get "$dir/instance.conf" FLEETIFY_VERSION)"
    fi
    is_version "$installed_version" || die "instance.conf of $instance has no valid installed version." "Check $dir/instance.conf."
    if version_lt "$version" "$installed_version"; then
        die "$fqdn runs $installed_version; install.sh does not downgrade to $version (the database schema may be newer)." \
            "To go back, restore a backup made before the update into a fresh instance (deploy/README.md, Restore)."
    fi

    step "Updating $fqdn from $installed_version to $version"
    if [[ "$MANIFEST_ROLLBACK" == "restore" ]]; then
        warn "Release $version changes the database in a way the previous release cannot run on."
        warn "If the update fails, install.sh restores the pre-update backup: changes made during the update are lost."
        confirm "Update $fqdn to $version?" || die "Update of $fqdn cancelled; nothing was changed." ""
    fi

    pull_release_images
    ensure_caddy "$version"
    create_instance_directories "$dir"
    generate_instance_secrets "$dir"

    wait_for_postgres "$instance"
    backup_database "$instance" "pre-update-$installed_version-to-$version"
    local backup_file="$LAST_BACKUP_FILE"

    # Snapshot of the current configuration for rollback.
    rm -rf -- "$dir/state/previous"
    install -d -m 0700 "$dir/state/previous"
    cp -a "$dir/compose.yml" "$dir/instance.conf" "$dir/postgres" "$dir/state/previous/"
    [[ -d "$dir/release" ]] && cp -a "$dir/release" "$dir/state/previous/"
    rollback_mode="$MANIFEST_ROLLBACK"

    local web_port agent_port
    web_port="$(conf_get "$dir/instance.conf" WEB_PORT)"
    agent_port="$(conf_get "$dir/instance.conf" AGENT_PORT)"
    write_instance_templates "$dir"
    write_release_manifest "$dir"
    # The recorded version changes only after the health check passes.
    write_instance_conf "$instance" "$fqdn" "$installed_version" updating "$web_port" "$agent_port"

    if instance_networks_diverge "$instance"; then
        if ! recreate_networks_if_diverged "$instance"; then
            rollback_instance "$instance" "$installed_version" "$version" "$rollback_mode" "$backup_file" "its networks could not be recreated"
        fi
        wait_for_postgres "$instance"
    fi

    step "Running database migrations"
    if ! run_migrator "$instance"; then
        rollback_instance "$instance" "$installed_version" "$version" "$rollback_mode" "$backup_file" "the migrations failed"
    fi
    ok "migrations applied"

    step "Restarting the instance"
    if ! dc_instance "$instance" up -d --remove-orphans >/dev/null; then
        rollback_instance "$instance" "$installed_version" "$version" "$rollback_mode" "$backup_file" "the containers did not start"
    fi
    if ! wait_for_instance_health "$instance"; then
        show_unhealthy_logs "$instance"
        rollback_instance "$instance" "$installed_version" "$version" "$rollback_mode" "$backup_file" "the health check failed"
    fi

    set_instance_version "$instance" "$version"
    set_instance_state "$instance" ok
    update_caddy_routes
    append_history "$instance" "updated $installed_version -> $version"
    ok "$fqdn runs Fleeto $version"
}

# restore_previous_release_manifest <instance dir>: the release manifest of the previous version, so its gateway offers its own release.
restore_previous_release_manifest() {
    local dir="$1"
    if [[ -d "$dir/state/previous/release" ]]; then
        rm -rf -- "$dir/release"
        cp -a "$dir/state/previous/release" "$dir/"
    fi
}

# rollback_instance <instance> <previous version> <failed version> <rollback mode> <backup file> <reason>; never returns.
rollback_instance() {
    local instance="$1" previous_version="$2" failed_version="$3" mode="$4" backup_file="$5" reason="$6" dir
    dir="$(instance_dir "$instance")"
    error "Update of $instance to $failed_version failed: $reason. Rolling back to $previous_version."
    append_history "$instance" "update $previous_version -> $failed_version failed ($reason); rolling back"

    dc_instance "$instance" stop --timeout 30 web gateway workers signer >/dev/null 2>&1 || true

    if [[ "$mode" == "restore" ]]; then
        if ! restore_database "$instance" "$backup_file"; then
            set_instance_state "$instance" rollback-failed
            die "Restoring the pre-update backup of $instance failed. The instance is stopped." \
                "Contact Steaan support with $dir/state/history.log and the restore log in $dir/backups. The backup is $backup_file."
        fi
    fi

    cp -a "$dir/state/previous/compose.yml" "$dir/state/previous/instance.conf" "$dir/"
    rm -rf -- "$dir/postgres"
    cp -a "$dir/state/previous/postgres" "$dir/"
    restore_previous_release_manifest "$dir"
    set_instance_state "$instance" rolled-back
    recreate_networks_if_diverged "$instance" || true

    if dc_instance "$instance" up -d --remove-orphans >/dev/null 2>&1 && wait_for_instance_health "$instance"; then
        append_history "$instance" "rolled back to $previous_version"
        die "The update to $failed_version failed ($reason); $instance was rolled back to $previous_version and is running." \
            "Send the log lines above to Steaan support before trying again."
    fi
    set_instance_state "$instance" rollback-failed
    append_history "$instance" "rollback to $previous_version failed"
    die "Rolling $instance back to $previous_version did not bring it back to health." \
        "Contact Steaan support with $dir/state/history.log. The pre-update backup is $backup_file."
}

# ---------------------------------------------------------------------------------------------------------------------
# --list, --check, --all
# ---------------------------------------------------------------------------------------------------------------------
command_list() {
    local instance conf running total=${#INSTANCE_SERVICES[@]}
    local -a instances=()
    mapfile -t instances < <(list_instances)
    if [[ ${#instances[@]} -eq 0 ]]; then
        info "No Fleeto instances on this VPS. Install one with: install.sh --fqdn <name>"
        return 0
    fi
    printf '%-40s %-34s %-9s %-16s %s\n' "FQDN" "INSTANCE" "VERSION" "STATE" "SERVICES RUNNING"
    for instance in "${instances[@]}"; do
        conf="$(instance_dir "$instance")/instance.conf"
        running="?"
        if command -v docker >/dev/null 2>&1; then
            running="$(dc_instance "$instance" ps --status running --services 2>/dev/null | grep -cxE "$(IFS='|'; echo "${INSTANCE_SERVICES[*]}")" || true)"
        fi
        printf '%-40s %-34s %-9s %-16s %s/%s\n' "$(conf_get "$conf" FLEETIFY_FQDN)" "$instance" \
            "$(conf_get "$conf" FLEETIFY_VERSION)" "$(conf_get "$conf" FLEETIFY_STATE)" "$running" "$total"
    done
}

command_check() {
    local fqdn="$1" instance conf installed
    local -a instances=()
    resolve_latest_version
    if [[ -n "$fqdn" ]]; then
        validate_fqdn "$fqdn"
        instance="$(instance_name_for "$fqdn")"
        [[ -f "$(instance_dir "$instance")/instance.conf" ]] || die "There is no instance for $fqdn on this VPS." "Run install.sh --list to see the instances."
        instances=("$instance")
    else
        mapfile -t instances < <(list_instances)
    fi
    info "Latest release: $LATEST_VERSION (this install.sh: $INSTALLER_VERSION)"
    if [[ -n "$NEWER_PRE_RELEASE" ]]; then
        info "Pre-release $NEWER_PRE_RELEASE is available; install it on a test VPS with --version $NEWER_PRE_RELEASE."
    fi
    local check="$WORK_DIR/check-packages-user.json"
    if [[ "$(github_request "$PACKAGES_TOKEN_FILE" "$GITHUB_API_URL/user" "$check")" == 200 ]]; then
        warn_token_expiry "$check.headers" "packages token"
    else
        warn "GitHub refused the packages token; image pulls will fail. Replace it with install.sh --github-tokens."
    fi
    for instance in "${instances[@]}"; do
        conf="$(instance_dir "$instance")/instance.conf"
        installed="$(conf_get "$conf" FLEETIFY_VERSION)"
        if is_version "$installed" && version_lt "$installed" "$LATEST_VERSION"; then
            info "$(conf_get "$conf" FLEETIFY_FQDN): $installed installed, update available. Run: install.sh --fqdn $(conf_get "$conf" FLEETIFY_FQDN)"
        else
            info "$(conf_get "$conf" FLEETIFY_FQDN): $installed installed, up to date."
        fi
    done
}

command_all() {
    local version="$1" instance failures=()
    local -a instances=()
    mapfile -t instances < <(list_instances)
    [[ ${#instances[@]} -gt 0 ]] || { info "No Fleeto instances on this VPS."; return 0; }
    for instance in "${instances[@]}"; do
        # Each update runs in a subshell so one failed (and rolled back) instance does not stop the others. The
        # subshell is not used as a condition, so errexit stays active inside it.
        local status=0
        set +e
        (
            set -e
            update_instance "$instance" "$version"
        )
        status=$?
        set -e
        if [[ "$status" -ne 0 ]]; then
            failures+=("$instance")
        fi
    done
    if [[ ${#failures[@]} -gt 0 ]]; then
        die "The update failed for: ${failures[*]}." "Their state is shown above; the other instances were updated."
    fi
    ok "All instances run Fleeto $version"
}

# ---------------------------------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------------------------------
main() {
    parse_args "$@"

    if $ARG_LIST; then
        require_root
        command_list
        return 0
    fi

    require_root "$@"
    require_supported_os
    ensure_packages

    if $ARG_GITHUB_TOKENS; then
        ensure_github_credentials true
        return 0
    fi

    if $ARG_CHECK; then
        command_check "$ARG_FQDN"
        return 0
    fi

    if ! $ARG_ALL && [[ -z "$ARG_FQDN" ]]; then
        if [[ -t 0 ]]; then
            read -r -p "FQDN of the instance (for example rmm.customer.example): " ARG_FQDN
        else
            die "No FQDN given." "Run install.sh --fqdn <name>, or install.sh --help for all commands."
        fi
    fi
    if [[ -n "$ARG_FQDN" ]]; then
        ARG_FQDN="$(tr '[:upper:]' '[:lower:]' <<<"${ARG_FQDN%.}")"
        validate_fqdn "$ARG_FQDN"
    fi

    acquire_lock

    local version="$ARG_VERSION"
    if [[ -z "$version" ]]; then
        resolve_latest_version
        version="$LATEST_VERSION"
    fi
    load_release_keys
    load_manifest "$version"
    ensure_installer_for_release "$version"

    if $ARG_ALL; then
        ensure_docker
        command_all "$version"
        return 0
    fi

    local instance conf
    instance="$(instance_name_for "$ARG_FQDN")"
    conf="$(instance_dir "$instance")/instance.conf"
    if [[ -f "$conf" && "$(conf_get "$conf" FLEETIFY_STATE)" != "installing" ]]; then
        ensure_docker
        update_instance "$instance" "$version"
    else
        # DNS first: fail before anything is installed on the VPS.
        check_dns "$ARG_FQDN"
        ensure_docker
        install_instance "$ARG_FQDN" "$version"
    fi
}

# Sourcing the script (tests) defines the functions without running anything.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
