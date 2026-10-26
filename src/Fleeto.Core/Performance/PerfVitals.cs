namespace Fleeto.Core.Performance;

/// <summary>One measurement the browser reported: the path it was taken on, the metric and its value.</summary>
/// <param name="Path">The path of the page as the browser saw it (<c>/clients/…</c>); the server turns it into a route template.</param>
public sealed record PerfVitalSample(string? Path, string? Metric, double Value);

/// <summary>A measurement that passed the whitelist: kind, metric as sub key and its value as stored.</summary>
public sealed record PerfVitalRecord(PerfKind Kind, string Key, string SubKey, double Value);

/// <summary>
/// What the browser of a signed-in user may report to the performance monitor (0.7.0 step 3, spec §5, Frontend RUM): a fixed
/// whitelist of metrics with their ranges, a cap on the items per report and on the reports per minute. Nothing in a report is
/// trusted beyond its own metric: the time is the server's, the path only chooses a route template the server knows, and a value
/// outside its range is dropped, never clamped.
/// </summary>
public static class PerfVitals
{
    /// <summary>A layout shift score is a small fraction; it is stored times this, so the millisecond histogram holds it.</summary>
    public const double ClsScale = 1000;

    /// <summary>Items of one report beyond this are dropped.</summary>
    public const int MaxItems = 60;

    /// <summary>Reports of one page per minute beyond this are dropped: the browser sends one every 30 seconds and when hidden.</summary>
    public const int MaxReportsPerMinute = 6;

    public const string Lcp = "lcp";
    public const string Fcp = "fcp";
    public const string Ttfb = "ttfb";
    public const string Inp = "inp";
    public const string Cls = "cls";
    public const string LongTask = "long-task";
    public const string View = "view";
    public const string CircuitRoundTrip = "circuit";
    public const string Connect = "connect";

    /// <summary>The metrics of a page, with the highest value that can be real.</summary>
    private static readonly IReadOnlyDictionary<string, double> PageMetrics = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        [Lcp] = 120_000,
        [Fcp] = 120_000,
        [Ttfb] = 120_000,
        [Inp] = 60_000,
        [Cls] = 10,
        [LongTask] = 60_000,
        [View] = 0
    };

    /// <summary>The network metrics, which belong to no page.</summary>
    private static readonly IReadOnlyDictionary<string, double> NetworkMetrics = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        [CircuitRoundTrip] = 60_000,
        [Connect] = 60_000
    };

    /// <summary>
    /// The samples of one report that may be stored, with the path of a page metric turned into a route template by
    /// <paramref name="routeOf"/>. A sample with an unknown metric, a value out of range or a path without a known page is dropped.
    /// </summary>
    public static IReadOnlyList<PerfVitalRecord> Accept(IReadOnlyList<PerfVitalSample>? samples, Func<string, string?> routeOf)
    {
        if (samples is null || samples.Count == 0)
        {
            return [];
        }

        var accepted = new List<PerfVitalRecord>();
        foreach (var sample in samples.Take(MaxItems))
        {
            if (sample is null || sample.Metric is not { } metric || double.IsNaN(sample.Value) || double.IsInfinity(sample.Value) || sample.Value < 0)
            {
                continue;
            }

            if (NetworkMetrics.TryGetValue(metric, out var networkMax))
            {
                if (sample.Value <= networkMax)
                {
                    accepted.Add(new PerfVitalRecord(PerfKind.Network, metric, string.Empty, sample.Value));
                }

                continue;
            }

            if (!PageMetrics.TryGetValue(metric, out var max) || sample.Value > max || Path(sample.Path) is not { } path || routeOf(path) is not { } route)
            {
                continue;
            }

            accepted.Add(new PerfVitalRecord(PerfKind.Vital, route, metric, metric == Cls ? sample.Value * ClsScale : sample.Value));
        }

        return accepted;
    }

    /// <summary>The path of a URL the browser sent: no scheme, host, query or fragment, at most 500 characters, or null.</summary>
    public static string? Path(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2000)
        {
            return null;
        }

        var path = value;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Length > 500)
        {
            return null;
        }

        return path;
    }
}
