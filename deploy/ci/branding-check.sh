#!/usr/bin/env bash
# Branding check (MD-Files/branding-fleeto.md section 6 and 7), run in CI and before every release.
#
# Fails when user-visible text in the web UI
#   1. contains "Fleetify" (user-visible text says Fleeto; Fleetify is for namespaces, images and identifiers), or
#   2. uses "device" or "machine" (the vocabulary is "endpoint").
#
# Scanned: Razor components, Razor pages and static HTML under src/Fleetify.Web (or the paths given as arguments).
# Not counted as user-visible:
#   - Razor directives (@using, @inject, @namespace, @inherits, @implements, @attribute, @typeparam, @layout, @rendermode)
#   - Razor, HTML, C# block and C# line comments
#   - identifiers: the word glued to identifier characters or to . _ - / (Fleetify.Core, FleetifyRoles, DeviceId,
#     device-width, Environment.MachineName, fleetify-web)
# A line can opt out with a trailing comment containing "branding-check: ignore" plus the reason.
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ $# -gt 0 ]]; then
    paths=("$@")
else
    paths=("$repo_root/src/Fleetify.Web")
fi

mapfile -d '' files < <(find "${paths[@]}" -type f \( -name '*.razor' -o -name '*.cshtml' -o -name '*.html' \) \
    -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/wwwroot/lib/*' -print0 2>/dev/null)

if [[ ${#files[@]} -eq 0 ]]; then
    echo "branding-check: no Razor or HTML files found in ${paths[*]}; nothing to check."
    exit 0
fi

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
            my @found;
            while ($line =~ /(?<![A-Za-z0-9_.\-\/])(Fleetify)(?![A-Za-z0-9_.\-\/])/g) {
                push @found, "\"Fleetify\" in user-visible text (use Fleeto)";
            }
            while ($line =~ /(?<![A-Za-z0-9_.\-\/\@])((?i:devices?|machines?))(?![A-Za-z0-9_\-\/])/g) {
                push @found, "\"$1\" in user-visible text (use endpoint)";
            }
            for my $message (@found) {
                my $shown = $line;
                $shown =~ s/^\s+//;
                print "$file:$number: $message\n    $shown\n";
                $violations++;
            }
        }
    }
    if ($violations > 0) {
        print "\nbranding-check: $violations violation(s). See MD-Files/branding-fleeto.md sections 6 and 7.\n";
        exit 1;
    }
    print "branding-check: " . scalar(@ARGV) . " file(s) checked, no violations.\n";
' "${files[@]}"
