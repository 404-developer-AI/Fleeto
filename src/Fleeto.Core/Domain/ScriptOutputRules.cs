using System.Globalization;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// Rules of the output of a script check (0.9.0, ARCHITECTURE.md §4, Check result): the agent reports what the script wrote, up to
/// the output limit of the check, with every result; output that did not change since the last result it sent with output is left
/// out (at most a day), so a result without output shows the output of the latest earlier result that has it. The output can hold
/// personal data and secrets the script prints: it is shown only to users who see the checks of the endpoint, never logged, never
/// put in an alert, and never loaded with the evaluation of check results.
/// </summary>
public static class ScriptOutputRules
{
    /// <summary>Check parameter of a script check: how much output is reported, in KB.</summary>
    public const string LimitParameter = "output_limit_kb";

    public const int DefaultLimitKb = 8;
    public const int MinLimitKb = 1;
    public const int MaxLimitKb = 256;

    /// <summary>
    /// How far back the output of a result without output is looked for. The agent sends unchanged output again after a day, so
    /// two days cover a late batch; a result whose output is older (a buffer that evicted results) shows that it is not available.
    /// </summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(2);

    /// <summary>The output limit of a script check in KB, from its parameters, within the bounds.</summary>
    public static int LimitKb(IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue(LimitParameter, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) && kb > 0
            ? Math.Min(kb, MaxLimitKb)
            : DefaultLimitKb;

    /// <summary>True when the script wrote more than the agent kept: it keeps everything up to the limit of the run.</summary>
    public static bool IsCut(long? totalBytes, int? limitBytes) => totalBytes is { } total && limitBytes is { } limit && total > limit;

    /// <summary>
    /// Cause and next step when the output of a run was cut off. <paramref name="where"/> names where the check is edited, such as
    /// "monitoring template Servers" or "this endpoint".
    /// </summary>
    public static string CutMessage(long totalBytes, int? limitBytes, string where)
    {
        var limitKb = limitBytes is > 0 ? (int)Math.Ceiling(limitBytes.Value / 1024d) : DefaultLimitKb;
        var totalKb = (long)Math.Ceiling(totalBytes / 1024d);
        if (limitKb >= MaxLimitKb)
        {
            return $"The script wrote {totalKb} KB of output and only the first {limitKb} KB is kept, the most a script check can keep. " +
                   "Let the script write less, or write the details to a file on the endpoint.";
        }

        var suggested = (int)Math.Min(MaxLimitKb, Math.Max(limitKb * 2L, totalKb));
        return $"The script wrote {totalKb} KB of output and only the first {limitKb} KB is kept. " +
               $"To keep more, edit the check in {where} and raise Output limit (KB), for example to {suggested} (at most {MaxLimitKb}).";
    }

    /// <summary>True for a check type whose results carry script output.</summary>
    public static bool Applies(CheckType type) => type == CheckType.Script;
}
