using System.Globalization;
using System.Text.RegularExpressions;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>The exit code lists of a script (0.6.0); a code is in at most one of them.</summary>
public sealed record ExitCodeRules(
    IReadOnlyCollection<int> Success,
    IReadOnlyCollection<int> Info,
    IReadOnlyCollection<int> Warning,
    IReadOnlyCollection<int> Error)
{
    public static readonly ExitCodeRules None = new([], [], [], []);

    public bool IsEmpty => Success.Count + Info.Count + Warning.Count + Error.Count == 0;

    /// <summary>The status a list gives the code, or null when no list names it.</summary>
    public ExitStatus? Listed(int code) =>
        Success.Contains(code) ? ExitStatus.Success :
        Info.Contains(code) ? ExitStatus.Info :
        Warning.Contains(code) ? ExitStatus.Warning :
        Error.Contains(code) ? ExitStatus.Error : null;
}

/// <summary>
/// What an exit code of a library script means (0.6.0, decided 2026-10-15). The script lists codes as success, info, warning or
/// error; a code it does not list keeps the default of where the script runs: for a job or a task step 0 succeeds and every other
/// code is an error, for a script check 0 is OK, 1 a warning and every other code critical (as since 0.2.0).
/// </summary>
public static partial class ScriptExitCodes
{
    /// <summary>At most this many codes over the four lists together.</summary>
    public const int MaxCodes = 100;

    /// <summary>The status of an exit code of a job or task step.</summary>
    public static ExitStatus StatusForJob(ExitCodeRules rules, int code) =>
        rules.Listed(code) ?? (code == 0 ? ExitStatus.Success : ExitStatus.Error);

    /// <summary>True when a job with this status ends as succeeded: success, info and warning do, error does not.</summary>
    public static bool Succeeds(ExitStatus status) => status != ExitStatus.Error;

    /// <summary>The status of a script check that ended with this exit code: info counts as OK, error as critical.</summary>
    public static CheckStatus StatusForCheck(ExitCodeRules rules, int code) =>
        rules.Listed(code) switch
        {
            ExitStatus.Success or ExitStatus.Info => CheckStatus.Ok,
            ExitStatus.Warning => CheckStatus.Warning,
            ExitStatus.Error => CheckStatus.Critical,
            _ => code == 0 ? CheckStatus.Ok : code == 1 ? CheckStatus.Warning : CheckStatus.Critical
        };

    /// <summary>
    /// Reads a list as typed by a user: codes and ranges separated by commas, semicolons or spaces ("1, 2, 5-8", "-1",
    /// "0xC0000005"). Returns the codes sorted without duplicates, or the reason it cannot.
    /// </summary>
    public static (IReadOnlyList<int> Codes, string? Error) Parse(string? text)
    {
        var codes = new SortedSet<int>();
        foreach (var token in (text ?? string.Empty).Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = RangePattern().Match(token);
            if (range.Success && TryCode(range.Groups[1].Value, out var from) && TryCode(range.Groups[2].Value, out var to))
            {
                if (to < from)
                {
                    return ([], $"The range {token} runs backwards. Write the lowest code first.");
                }

                if ((long)to - from >= MaxCodes)
                {
                    return ([], $"The range {token} holds more than {MaxCodes} codes.");
                }

                for (var code = from; ; code++)
                {
                    codes.Add(code);
                    if (code == to)
                    {
                        break;
                    }
                }
            }
            else if (TryCode(token, out var code))
            {
                codes.Add(code);
            }
            else
            {
                return ([], $"\"{token}\" is not an exit code. Type whole numbers or ranges such as 5-8, separated by commas.");
            }

            if (codes.Count > MaxCodes)
            {
                return ([], $"A script can list at most {MaxCodes} exit codes.");
            }
        }

        return (codes.ToList(), null);
    }

    /// <summary>Writes a list back as Parse reads it, with consecutive codes as a range ("1-3, 7").</summary>
    public static string Format(IEnumerable<int> codes)
    {
        var sorted = codes.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }

            parts.Add(j - i >= 2
                ? $"{sorted[i].ToString(CultureInfo.InvariantCulture)}-{sorted[j].ToString(CultureInfo.InvariantCulture)}"
                : string.Join(", ", sorted.GetRange(i, j - i + 1).Select(c => c.ToString(CultureInfo.InvariantCulture))));
            i = j + 1;
        }

        return string.Join(", ", parts);
    }

    /// <summary>The problems of a set of lists: a code in two lists, or too many codes.</summary>
    public static IReadOnlyList<string> Validate(ExitCodeRules rules)
    {
        var problems = new List<string>();
        var lists = new (string Name, IReadOnlyCollection<int> Codes)[]
        {
            ("Success", rules.Success), ("Info", rules.Info), ("Warning", rules.Warning), ("Error", rules.Error)
        };
        for (var i = 0; i < lists.Length; i++)
        {
            for (var j = i + 1; j < lists.Length; j++)
            {
                var both = lists[i].Codes.Intersect(lists[j].Codes).Order().ToList();
                if (both.Count > 0)
                {
                    problems.Add($"Exit code {Format(both)} is listed as {lists[i].Name} and as {lists[j].Name}. Keep each code in one list.");
                }
            }
        }

        if (lists.Sum(l => l.Codes.Distinct().Count()) > MaxCodes)
        {
            problems.Add($"A script can list at most {MaxCodes} exit codes.");
        }

        return problems;
    }

    /// <summary>The label of a status in the UI.</summary>
    public static string Label(ExitStatus status) => status switch
    {
        ExitStatus.Success => "Success",
        ExitStatus.Info => "Info",
        ExitStatus.Warning => "Warning",
        _ => "Error"
    };

    private static bool TryCode(string token, out int code)
    {
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(token[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
        {
            // Windows reports an NTSTATUS such as 0xC0000005 as a negative 32-bit exit code.
            code = unchecked((int)hex);
            return true;
        }

        return int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out code);
    }

    [GeneratedRegex(@"^(-?\d+)-(-?\d+)$")]
    private static partial Regex RangePattern();
}
