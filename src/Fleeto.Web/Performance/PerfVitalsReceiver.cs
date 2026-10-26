using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;

namespace Fleeto.Web.Performance;

/// <summary>
/// Takes what the browser of a signed-in user measured (0.7.0 step 3, PERFORMANCE.md §4.1): Web Vitals per page, long tasks and the
/// round trip of the live connection, for a share of page loads (every page in Diagnose). The browser sends its report over the
/// circuit, so only a signed-in page can send one and there is no endpoint to call from outside; what it sends passes the whitelist
/// of <see cref="PerfVitals"/>, its path only picks a route template web knows, and the minute is the server's.
/// </summary>
public sealed class PerfVitalsReceiver
{
    private readonly PerfRecorder _recorder;
    private readonly PageRoutes _routes;
    private readonly TimeProvider _time;

    public PerfVitalsReceiver(PerfRecorder recorder, PageRoutes routes, TimeProvider time)
    {
        _recorder = recorder;
        _routes = routes;
        _time = time;
    }

    /// <summary>Whether a page that loads now reports: decided once per page load.</summary>
    public bool Samples() => _recorder.Settings.Current.SamplesFrontend(_time.GetUtcNow().UtcDateTime, Random.Shared.NextDouble() * 100);

    /// <summary>Whether a page that does not report should start: only while Diagnose runs, which wants every page.</summary>
    public bool Wanted() => _recorder.Collects(PerfCollector.Frontend) && _recorder.Settings.Current.IsDiagnosing(_time.GetUtcNow().UtcDateTime);

    /// <summary>Records the samples of one report that pass the whitelist. Returns how many were recorded.</summary>
    public int Accept(IReadOnlyList<PerfVitalSample>? samples)
    {
        if (!_recorder.Collects(PerfCollector.Frontend))
        {
            return 0;
        }

        var records = PerfVitals.Accept(samples, RouteOf);
        foreach (var record in records)
        {
            if (record.SubKey == PerfVitals.View)
            {
                _recorder.Count(PerfCollector.Frontend, PerfKind.Vital, record.Key, PerfVitals.View);
            }
            else
            {
                _recorder.Record(PerfCollector.Frontend, record.Kind, record.Key, record.SubKey, record.Value);
            }
        }

        return records.Count;
    }

    private string? RouteOf(string path) => _routes.Match(path) is var route && route != RouteTemplates.Unmatched ? route : null;
}

/// <summary>Reports of one page per minute, so a page that sends too often is cut off (<see cref="PerfVitals.MaxReportsPerMinute"/>).</summary>
public sealed class PerfVitalsBudget
{
    private long _minute = -1;
    private int _reports;

    public bool Take(DateTime utcNow)
    {
        var minute = utcNow.Ticks / TimeSpan.TicksPerMinute;
        if (minute != _minute)
        {
            _minute = minute;
            _reports = 0;
        }

        return ++_reports <= PerfVitals.MaxReportsPerMinute;
    }
}
