#!/usr/bin/env bash
# Branding check (MD-Files/branding-fleeto.md sections 6 and 7), run in CI and before every release.
#
# 1. The old internal name. Fleeto is the only name, internal and user-visible (decided 2026-09-15). The old name may appear only
#    in the files that move data from before the rename (MD-Files/ARCHITECTURE.md section 7, Rename to Fleeto), listed in
#    LEGACY_FILES below, and in the history documents. Checked in every tracked and untracked (not ignored) file.
# 2. The vocabulary of the web UI: user-visible text never says "device" or "machine" (the vocabulary is "endpoint").
#    Scanned: Razor components, Razor pages and static HTML under src/Fleeto.Web (or the paths given as arguments).
#    Not counted as user-visible:
#      - Razor directives (@using, @inject, @namespace, @inherits, @implements, @attribute, @typeparam, @layout, @rendermode)
#      - Razor, HTML, C# block and C# line comments
#      - identifiers: the word glued to identifier characters or to . _ - / (DeviceId, device-width, Environment.MachineName)
#    A line can opt out with a trailing comment containing "branding-check: ignore" plus the reason.
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
violations=0

# --- 1. Old internal name ------------------------------------------------------------------------------------------------
old_name="fleet""ify"
LEGACY_FILES=(
    # Migration code and its tests: they read what was written before the rename.
    "src/Fleeto.Infrastructure/Security/LegacyNames.cs"
    "src/Fleeto.Infrastructure/Migrations/*_RenameToFleeto.cs"
    "tests/Fleeto.Infrastructure.Tests/LegacyRenameTests.cs"
    "agent/internal/service/legacy.go"
    "agent/internal/service/legacy_windows.go"
    "agent/internal/service/legacy_test.go"
    "deploy/install.sh"
    "deploy/ci/test-deploy.sh"
    "tools/dev/setup-dev.ps1"
    # Documents that describe the rename or record history.
    "CLAUDE.md"
    "README.md"
    "deploy/README.md"
    "deploy/RELEASING.md"
    "MD-Files/ARCHITECTURE.md"
    "MD-Files/branding-fleeto.md"
    "MD-Files/ROADMAP.md"
    "MD-Files/CHANGELOG.md"
    "MD-Files/CHANGELOG-ARCHIVE.md"
    "MD-Files/NOTFORCLAUDE.MD"
)

is_legacy_file() {
    local file="$1" pattern
    for pattern in "${LEGACY_FILES[@]}"; do
        # shellcheck disable=SC2053 # the pattern is a glob on purpose
        [[ "$file" == $pattern ]] && return 0
    done
    return 1
}

cd "$repo_root"
if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    while IFS= read -r -d '' file; do
        [[ -f "$file" && "$file" != "deploy/ci/branding-check.sh" ]] || continue
        is_legacy_file "$file" && continue
        if [[ "$file" == *"$old_name"* || "$file" == *"Fleet""ify"* ]]; then
            echo "$file: the file name uses the old internal name (use Fleeto)"
            violations=$((violations + 1))
        fi
        if matches="$(grep -Ini "$old_name" -- "$file" 2>/dev/null)"; then
            while IFS= read -r line; do
                echo "$file:$line"
                violations=$((violations + 1))
            done <<<"$matches"
        fi
    done < <(git ls-files -z --cached --others --exclude-standard)
    if [[ "$violations" -gt 0 ]]; then
        echo "branding-check: the old internal name appears above; the name is Fleeto. A file that moves data from before the rename belongs in LEGACY_FILES."
    fi
fi

# --- 2. UI vocabulary ------------------------------------------------------------------------------------------------------
if [[ $# -gt 0 ]]; then
    paths=("$@")
else
    paths=("$repo_root/src/Fleeto.Web")
fi

mapfile -d '' files < <(find "${paths[@]}" -type f \( -name '*.razor' -o -name '*.cshtml' -o -name '*.html' \) \
    -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/wwwroot/lib/*' -print0 2>/dev/null)

if [[ ${#files[@]} -gt 0 ]]; then
    perl -e '
        use strict;
        use warnings;
        my $violations = 0;
        for my $file (@ARGV) {
            open(my $fh, "<", $file) or die "cannot read $file: $!";
            local $/;
            my $text = <$fh>;
            close($fh);
            # Blank out comments but keep newlines, so reported line numbers stay correct.
            my $blank = sub { my $s = shift; $s =~ s/[^\n]/ /g; return $s; };
            $text =~ s/(\@\*.*?\*\@)/$blank->($1)/gse;
            $text =~ s/(<!--.*?-->)/$blank->($1)/gse;
            $text =~ s/(\/\*.*?\*\/)/$blank->($1)/gse;
            my $number = 0;
            for my $line (split(/\n/, $text, -1)) {
                $number++;
                next if $line =~ /branding-check: ignore/;
                next if $line =~ /^\s*\@(using|inject|namespace|inherits|implements|attribute|typeparam|layout|rendermode)\b/;
                # C# line comments (not "://" in URLs).
                $line =~ s{(?<![:"\x27])//.*$}{};
                while ($line =~ /(?<![A-Za-z0-9_.\-\/\@])((?i:devices?|machines?))(?![A-Za-z0-9_\-\/])/g) {
                    my $shown = $line;
                    $shown =~ s/^\s+//;
                    print "$file:$number: \"$1\" in user-visible text (use endpoint)\n    $shown\n";
                    $violations++;
                }
            }
        }
        print STDERR "UI_VIOLATIONS=$violations\n";
        print "branding-check: " . scalar(@ARGV) . " UI file(s) checked.\n";
    ' "${files[@]}" 2>"${TMPDIR:-/tmp}/branding-check-ui.$$" || true
    ui="$(sed -n 's/^UI_VIOLATIONS=//p' "${TMPDIR:-/tmp}/branding-check-ui.$$")"
    rm -f "${TMPDIR:-/tmp}/branding-check-ui.$$"
    violations=$((violations + ${ui:-1}))
fi

if [[ "$violations" -gt 0 ]]; then
    echo ""
    echo "branding-check: $violations violation(s). See MD-Files/branding-fleeto.md sections 6 and 7."
    exit 1
fi
echo "branding-check: no violations."
