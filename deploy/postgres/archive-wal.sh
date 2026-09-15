#!/bin/sh
# Fleeto: PostgreSQL archive_command. Usage: fleeto-archive-wal %p %f
#
# Copies one completed WAL segment into the spool directory shared with fleeto-workers, which encrypts it with the
# backup public key, uploads it off the VPS and then deletes it from the spool. Exit 0 only when the segment is safely
# in the spool; any other exit makes PostgreSQL keep the segment and retry.
#
# The spool is /opt/fleeto/<instance>/wal-spool on the host: owner 70 (postgres), group 10001 (workers), mode 2770.
set -eu

if [ "$#" -ne 2 ]; then
    echo "fleeto-archive-wal: expected 2 arguments (%p %f), got $#" >&2
    exit 2
fi

source_path="$1"
name="$2"
spool="/var/lib/fleeto/wal-spool"

case "$name" in
    */* | .* | "")
        echo "fleeto-archive-wal: refusing unexpected file name '$name'" >&2
        exit 2
        ;;
esac

target="$spool/$name"
if [ -f "$target" ]; then
    # Already archived, e.g. PostgreSQL retried after a crash between copy and acknowledgement. Identical content is
    # success; different content must never be overwritten silently.
    if cmp -s "$source_path" "$target"; then
        exit 0
    fi
    echo "fleeto-archive-wal: $target exists with different content; not overwriting" >&2
    exit 1
fi

partial="$spool/.$name.partial"
rm -f "$partial"
cp "$source_path" "$partial"
# Group-readable for the workers; the setgid spool directory gives the file group 10001.
chmod 0640 "$partial"
sync "$partial" 2>/dev/null || sync
# Rename is atomic: the workers only pick up names without the leading dot.
mv "$partial" "$target"
