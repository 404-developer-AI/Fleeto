#!/bin/bash
# Builds the Ubuntu package of Fleeto Connect: build-deb.sh <linux-amd64 binary> <Fleeto version> <output .deb>.
# Reproducible: every file has mtime 0, owner root, and dpkg-deb clamps its own timestamps to SOURCE_DATE_EPOCH.
set -euo pipefail
binary="$1"
version="$2"
out="$3"
here="$(cd "$(dirname "$0")" && pwd)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || { echo "build-deb.sh: '$version' is not a version" >&2; exit 2; }
root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT
chmod 0755 "$root"
install -d -m 0755 "$root/DEBIAN" "$root/usr/bin" "$root/usr/share/applications"
install -m 0755 "$binary" "$root/usr/bin/fleeto-connect"
install -m 0644 "$here/fleeto-connect.desktop" "$root/usr/share/applications/fleeto-connect.desktop"
install -m 0755 "$here/postinst" "$root/DEBIAN/postinst"
install -m 0755 "$here/postrm" "$root/DEBIAN/postrm"
sed "s/@VERSION@/${version//-/\~}/" "$here/control" > "$root/DEBIAN/control"
chmod 0644 "$root/DEBIAN/control"
find "$root" -exec touch -h -d @0 {} +
SOURCE_DATE_EPOCH=0 dpkg-deb --root-owner-group -Zxz --build "$root" "$out" >/dev/null
