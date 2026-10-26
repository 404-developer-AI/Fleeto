namespace Fleeto.Core.Domain;

/// <summary>
/// The version comparison of the two package managers Fleeto reads (0.8.0, VULNERABILITIES.md §7). The only place a package version
/// is compared: a wrong comparison is a wrong vulnerability finding. Ported from the reference implementations, dpkg's
/// <c>verrevcmp</c> (lib/dpkg/version.c) and rpm's <c>rpmvercmp</c> (rpmio/rpmvercmp.c), and tested against their test sets.
/// Both return a negative number, zero or a positive number, like <see cref="string.CompareOrdinal(string, string)"/>.
/// </summary>
public static class PackageVersions
{
    /// <summary>
    /// Compares two Debian versions <c>[epoch:]upstream[-revision]</c>: the epoch as a number, then the upstream version and the
    /// revision with <see cref="DpkgPart"/>. A missing epoch is 0, a missing revision is empty (which equals "0").
    /// </summary>
    public static int CompareDpkg(string a, string b)
    {
        var (epochA, upstreamA, revisionA) = SplitDpkg(a);
        var (epochB, upstreamB, revisionB) = SplitDpkg(b);
        if (epochA != epochB)
        {
            return epochA < epochB ? -1 : 1;
        }

        var upstream = DpkgPart(upstreamA, upstreamB);
        return upstream != 0 ? Math.Sign(upstream) : Math.Sign(DpkgPart(revisionA, revisionB));
    }

    /// <summary>The epoch, upstream version and revision of a Debian version. The epoch must be a number; anything else counts as 0.</summary>
    public static (long Epoch, string Upstream, string Revision) SplitDpkg(string version)
    {
        var text = version.Trim();
        long epoch = 0;
        var colon = text.IndexOf(':');
        if (colon > 0 && long.TryParse(text.AsSpan(0, colon), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            epoch = parsed;
            text = text[(colon + 1)..];
        }

        var dash = text.LastIndexOf('-');
        return dash >= 0 ? (epoch, text[..dash], text[(dash + 1)..]) : (epoch, text, string.Empty);
    }

    /// <summary>dpkg's <c>verrevcmp</c>: non-digit runs compared by <see cref="DpkgOrder"/> (where <c>~</c> sorts before everything, even the end), digit runs as numbers.</summary>
    public static int DpkgPart(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            var firstDiff = 0;
            while ((i < a.Length && !char.IsAsciiDigit(a[i])) || (j < b.Length && !char.IsAsciiDigit(b[j])))
            {
                var ac = DpkgOrder(i < a.Length ? a[i] : '\0');
                var bc = DpkgOrder(j < b.Length ? b[j] : '\0');
                if (ac != bc)
                {
                    return ac - bc;
                }

                i++;
                j++;
            }

            while (i < a.Length && a[i] == '0')
            {
                i++;
            }

            while (j < b.Length && b[j] == '0')
            {
                j++;
            }

            while (i < a.Length && j < b.Length && char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                if (firstDiff == 0)
                {
                    firstDiff = a[i] - b[j];
                }

                i++;
                j++;
            }

            if (i < a.Length && char.IsAsciiDigit(a[i]))
            {
                return 1;
            }

            if (j < b.Length && char.IsAsciiDigit(b[j]))
            {
                return -1;
            }

            if (firstDiff != 0)
            {
                return firstDiff;
            }
        }

        return 0;
    }

    private static int DpkgOrder(char c) => c switch
    {
        _ when char.IsAsciiDigit(c) => 0,
        _ when char.IsAsciiLetter(c) => c,
        '~' => -1,
        '\0' => 0,
        _ => c + 256
    };

    /// <summary>
    /// Compares two rpm versions <c>[epoch:]version[-release]</c>: the epoch as a number (missing is 0), then the version and the
    /// release with <see cref="RpmPart"/>. A release is compared only when both have one, as rpm does for a dependency without one.
    /// </summary>
    public static int CompareRpm(string a, string b)
    {
        var (epochA, versionA, releaseA) = SplitRpm(a);
        var (epochB, versionB, releaseB) = SplitRpm(b);
        if (epochA != epochB)
        {
            return epochA < epochB ? -1 : 1;
        }

        var version = RpmPart(versionA, versionB);
        return version != 0 || releaseA is null || releaseB is null ? version : RpmPart(releaseA, releaseB);
    }

    /// <summary>The epoch, version and release of an rpm version; the release is null when there is none.</summary>
    public static (long Epoch, string Version, string? Release) SplitRpm(string evr)
    {
        var text = evr.Trim();
        long epoch = 0;
        var colon = text.IndexOf(':');
        if (colon > 0 && long.TryParse(text.AsSpan(0, colon), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            epoch = parsed;
            text = text[(colon + 1)..];
        }

        var dash = text.LastIndexOf('-');
        return dash >= 0 ? (epoch, text[..dash], text[(dash + 1)..]) : (epoch, text, null);
    }

    /// <summary>
    /// rpm's <c>rpmvercmp</c>: segments of digits or of letters, separators ignored; a numeric segment is newer than an alphabetic
    /// one; <c>~</c> sorts before everything, even the end; <c>^</c> sorts after the end but before anything else. Returns -1, 0 or 1.
    /// </summary>
    public static int RpmPart(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 0;
        }

        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            while (i < a.Length && !char.IsAsciiLetterOrDigit(a[i]) && a[i] is not ('~' or '^'))
            {
                i++;
            }

            while (j < b.Length && !char.IsAsciiLetterOrDigit(b[j]) && b[j] is not ('~' or '^'))
            {
                j++;
            }

            var ca = i < a.Length ? a[i] : '\0';
            var cb = j < b.Length ? b[j] : '\0';
            if (ca == '~' || cb == '~')
            {
                if (ca != '~')
                {
                    return 1;
                }

                if (cb != '~')
                {
                    return -1;
                }

                i++;
                j++;
                continue;
            }

            if (ca == '^' || cb == '^')
            {
                if (i >= a.Length)
                {
                    return -1;
                }

                if (j >= b.Length)
                {
                    return 1;
                }

                if (ca != '^')
                {
                    return 1;
                }

                if (cb != '^')
                {
                    return -1;
                }

                i++;
                j++;
                continue;
            }

            if (i >= a.Length || j >= b.Length)
            {
                break;
            }

            int endA = i, endB = j;
            bool isNumber;
            if (char.IsAsciiDigit(a[i]))
            {
                while (endA < a.Length && char.IsAsciiDigit(a[endA]))
                {
                    endA++;
                }

                while (endB < b.Length && char.IsAsciiDigit(b[endB]))
                {
                    endB++;
                }

                isNumber = true;
            }
            else
            {
                while (endA < a.Length && char.IsAsciiLetter(a[endA]))
                {
                    endA++;
                }

                while (endB < b.Length && char.IsAsciiLetter(b[endB]))
                {
                    endB++;
                }

                isNumber = false;
            }

            // A segment of the other kind: a number is newer than letters.
            if (endB == j)
            {
                return isNumber ? 1 : -1;
            }

            var segA = a.AsSpan(i, endA - i);
            var segB = b.AsSpan(j, endB - j);
            if (isNumber)
            {
                segA = segA.TrimStart('0');
                segB = segB.TrimStart('0');
                if (segA.Length != segB.Length)
                {
                    return segA.Length > segB.Length ? 1 : -1;
                }
            }

            var rc = segA.SequenceCompareTo(segB);
            if (rc != 0)
            {
                return rc < 0 ? -1 : 1;
            }

            i = endA;
            j = endB;
        }

        if (i >= a.Length && j >= b.Length)
        {
            return 0;
        }

        return i < a.Length ? 1 : -1;
    }
}
