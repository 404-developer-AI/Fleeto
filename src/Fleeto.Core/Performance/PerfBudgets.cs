using System.Globalization;
using System.Text;

namespace Fleeto.Core.Performance;

/// <summary>
/// A budget: the 95th percentile one flow should stay under (0.7.0 step 3, spec §8, Budgets). <paramref name="SubKey"/> narrows it:
/// <c>load</c> or <c>event</c> for a page, a method such as <c>GET</c> for a route, empty for every flow of the key.
/// </summary>
public sealed record PerfBudget(PerfKind Kind, string Key, string SubKey, double P95Ms)
{
    /// <summary>The budget as a line of the settings: <c>page /clients load = 800</c>, <c>api GET /api/v1/endpoints = 300</c>.</summary>
    public string ToLine()
    {
        // An API budget names its method before the route, as a request line does: api GET /api/v1/endpoints.
        var line = new StringBuilder(PerfBudgets.WordOf(Kind));
        if (Kind == PerfKind.Http && SubKey.Length > 0)
        {
            line.Append(' ').Append(SubKey);
        }

        line.Append(' ').Append(Key);
        if (Kind != PerfKind.Http && SubKey.Length > 0)
        {
            line.Append(' ').Append(SubKey);
        }

        return line.Append(" = ").Append(P95Ms.ToString("0.##", CultureInfo.InvariantCulture)).ToString();
    }
}

/// <summary>A budget against the period: the 95th percentile of the flows it covers, or null when they did not run.</summary>
public sealed record PerfBudgetResult(PerfBudget Budget, long Count, double? P95, double? TimeAboveMs)
{
    public bool Measured => Count > 0 && P95 is not null;
    public bool Met => !Measured || P95 <= Budget.P95Ms;
}

/// <summary>
/// Budgets as admins write them, one per line (spec §8: <c>METHOD route=ms</c>, here with the kind of flow first, because a
/// Fleeto page is not a route): <c>page /clients load = 800</c>, <c>api GET /api/v1/endpoints = 300</c>, <c>loop patch-sync = 5000</c>,
/// <c>message CheckResults = 200</c>. The parser says what is wrong with a line, so nothing is stored that cannot be judged.
/// </summary>
public static class PerfBudgets
{
    public const int MaxBudgets = 50;

    private static readonly IReadOnlyDictionary<string, PerfKind> Words = new Dictionary<string, PerfKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["page"] = PerfKind.Interaction,
        ["api"] = PerfKind.Http,
        ["loop"] = PerfKind.WorkerRun,
        ["message"] = PerfKind.AgentMessage
    };

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    public static string WordOf(PerfKind kind) => kind switch
    {
        PerfKind.Interaction => "page",
        PerfKind.Http => "api",
        PerfKind.WorkerRun => "loop",
        PerfKind.AgentMessage => "message",
        _ => kind.ToString().ToLowerInvariant()
    };

    /// <summary>The budgets of the text, or the problem with the first line that cannot be read.</summary>
    public static (IReadOnlyList<PerfBudget> Budgets, string? Problem) Parse(string? text)
    {
        var budgets = new List<PerfBudget>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (budgets, null);
        }

        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var (budget, problem) = ParseLine(line);
            if (problem is not null)
            {
                return ([], $"Line {number}: {problem}");
            }

            budgets.Add(budget!);
        }

        if (budgets.Count > MaxBudgets)
        {
            return ([], $"At most {MaxBudgets} budgets.");
        }

        return Problem(budgets) is { } duplicate ? ([], duplicate) : (budgets, null);
    }

    public static string Format(IReadOnlyList<PerfBudget> budgets) => string.Join('\n', budgets.Select(b => b.ToLine()));

    /// <summary>Why stored budgets cannot be saved, or null. Checked by the server whatever the page sent.</summary>
    public static string? Problem(IReadOnlyList<PerfBudget> budgets)
    {
        if (budgets.Count > MaxBudgets)
        {
            return $"At most {MaxBudgets} budgets.";
        }

        foreach (var budget in budgets)
        {
            if (!Words.Values.Contains(budget.Kind))
            {
                return "A budget is for a page, an API route, a loop or an agent message.";
            }

            if (string.IsNullOrWhiteSpace(budget.Key) || budget.Key.Length > PerfKeys.MaxKeyLength || budget.Key.Any(char.IsWhiteSpace))
            {
                return "A budget names its page, route, loop or message type without spaces.";
            }

            if (budget.SubKey.Length > PerfKeys.MaxSubKeyLength || budget.SubKey.Any(char.IsWhiteSpace))
            {
                return $"The budget of {budget.Key} has a sub key that cannot be read.";
            }

            if (double.IsNaN(budget.P95Ms) || budget.P95Ms is < 1 or > 3_600_000)
            {
                return $"The budget of {budget.Key} is between 1 ms and 1 hour.";
            }
        }

        var duplicate = budgets.GroupBy(b => (b.Kind, b.Key, b.SubKey)).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? null : $"{duplicate.Key.Key} has more than one budget.";
    }

    private static (PerfBudget?, string?) ParseLine(string line)
    {
        var equals = line.LastIndexOf('=');
        if (equals <= 0)
        {
            return (null, "write it as \"page /clients load = 800\": the kind, what it is for, then = and the milliseconds.");
        }

        var target = line[(equals + 1)..].Trim();
        if (target.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            target = target[..^2].Trim();
        }

        if (!double.TryParse(target, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var ms) || ms is < 1 or > 3_600_000)
        {
            return (null, "the budget after = is a number of milliseconds between 1 and 3600000.");
        }

        var words = line[..equals].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < 2 || !Words.TryGetValue(words[0], out var kind))
        {
            return (null, "start with page, api, loop or message.");
        }

        string key;
        var subKey = string.Empty;
        switch (kind)
        {
            case PerfKind.Http:
                // api [METHOD] /route
                if (words.Length == 3 && Methods.Contains(words[1], StringComparer.OrdinalIgnoreCase))
                {
                    subKey = words[1].ToUpperInvariant();
                    key = words[2];
                }
                else if (words.Length == 2)
                {
                    key = words[1];
                }
                else
                {
                    return (null, "write an API budget as \"api GET /api/v1/endpoints = 300\"; the method is optional.");
                }

                break;
            case PerfKind.Interaction:
                // page /route [load|event]
                if (words.Length == 3 && words[2] is "load" or "event")
                {
                    subKey = words[2];
                }
                else if (words.Length != 2)
                {
                    return (null, "write a page budget as \"page /clients load = 800\"; load or event is optional.");
                }

                key = words[1];
                break;
            default:
                if (words.Length != 2)
                {
                    return (null, $"write it as \"{words[0].ToLowerInvariant()} <name> = <ms>\".");
                }

                key = words[1];
                break;
        }

        if (kind is PerfKind.Interaction or PerfKind.Http && !key.StartsWith('/'))
        {
            return (null, "a page or route starts with /, as its route template: /clients/{ClientId:guid}.");
        }

        if (key.Length > PerfKeys.MaxKeyLength)
        {
            return (null, "the name is too long.");
        }

        return (new PerfBudget(kind, key, subKey, ms), null);
    }

    /// <summary>Whether a flow of the period is covered by a budget.</summary>
    public static bool Covers(PerfBudget budget, PerfSeries flow)
    {
        if (flow.Kind != budget.Kind || !string.Equals(flow.Key, budget.Key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (budget.SubKey.Length == 0)
        {
            return true;
        }

        // An API flow's sub key is "GET 2xx": the budget names the method only.
        return budget.Kind == PerfKind.Http
            ? flow.SubKey.StartsWith(budget.SubKey + " ", StringComparison.OrdinalIgnoreCase)
            : string.Equals(flow.SubKey, budget.SubKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every budget against the flows of the period it covers, added up.</summary>
    public static IReadOnlyList<PerfBudgetResult> Evaluate(IReadOnlyList<PerfBudget> budgets, IReadOnlyList<PerfSeries> series)
    {
        var results = new List<PerfBudgetResult>();
        foreach (var budget in budgets)
        {
            var flows = series.Where(s => Covers(budget, s)).ToList();
            if (flows.Count == 0)
            {
                results.Add(new PerfBudgetResult(budget, 0, null, null));
                continue;
            }

            var histogram = flows.Aggregate((int[]?)null, (total, s) => PerfHistogram.Add(total, s.Histogram)) ?? [];
            var max = flows.Max(s => s.Max);
            var p95 = PerfHistogram.Percentile(histogram, 0.95, max);
            results.Add(new PerfBudgetResult(budget, flows.Sum(s => s.Count), p95, PerfHistogram.TimeAbove(histogram, budget.P95Ms, max)));
        }

        return results;
    }
}
