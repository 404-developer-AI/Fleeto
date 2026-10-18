using System.Diagnostics.Metrics;
using Fleeto.Core.Performance;

namespace Fleeto.Infrastructure.Performance;

/// <summary>
/// Measures without touching the code that does the work (PERFORMANCE.md §4.2, §4.5, §4.6): the metrics Npgsql publishes for every
/// command and the connection pool of the one data source of the process, the metrics of every outbound HttpClient, and every
/// exception thrown in the process by its type name. A command's time is added to the current <see cref="PerfScope"/>, so a page,
/// request or loop knows how much of its time was database work. Never a value: no statement text, no host path, no message.
/// </summary>
public sealed class PerfMeterListener : IDisposable
{
    private const string CommandDuration = "db.client.operation.duration";
    private const string ConnectionCount = "db.client.connection.count";
    private const string PoolTimeouts = "db.client.connection.npgsql.timeouts";
    private const string HttpDuration = "http.client.request.duration";

    [ThreadStatic] private static bool _inException;

    private readonly PerfRecorder _recorder;
    private readonly MeterListener _listener = new();
    private int _used;
    private int _idle;
    private long _timeouts;
    private long _exceptions;
    private bool _started;

    public PerfMeterListener(PerfRecorder recorder)
    {
        _recorder = recorder;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name is CommandDuration or ConnectionCount or PoolTimeouts ||
                (instrument.Name == HttpDuration && instrument.Meter.Name == "System.Net.Http"))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>(OnDouble);
        _listener.SetMeasurementEventCallback<long>(OnLong);
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => OnLong(instrument, value, tags, null));
    }

    /// <summary>Connections of the pool in use and idle, as last reported.</summary>
    internal (int Used, int Idle) Pool => (Volatile.Read(ref _used), Volatile.Read(ref _idle));

    internal long PoolTimeoutsTotal => Interlocked.Read(ref _timeouts);

    internal long ExceptionsTotal => Interlocked.Read(ref _exceptions);

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _listener.Start();
        AppDomain.CurrentDomain.FirstChanceException += OnException;
    }

    /// <summary>Asks observable instruments for their current value; called by the runtime sampler.</summary>
    internal void Observe() => _listener.RecordObservableInstruments();

    private void OnDouble(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name == CommandDuration)
        {
            var ms = value * 1000;
            PerfContext.Current?.AddDb(ms);
            _recorder.Record(PerfCollector.Database, PerfKind.Db, PerfContext.Source, string.Empty, ms, error: HasTag(tags, "error.type"));
        }
        else if (instrument.Name == HttpDuration)
        {
            var ms = value * 1000;
            PerfContext.Current?.AddExternal(ms);
            var host = Tag(tags, "server.address") ?? "unknown";
            var status = Tag(tags, "http.response.status_code") is { } code && int.TryParse(code, out var parsed) ? parsed : 0;
            var failed = HasTag(tags, "error.type") || status >= 500;
            var subKey = status == 0 ? "error" : PerfKeys.StatusClass(status);
            _recorder.Record(PerfCollector.External, PerfKind.External, host.ToLowerInvariant(), subKey, ms, failed);
        }
    }

    private void OnLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name == ConnectionCount)
        {
            var idle = string.Equals(Tag(tags, "db.client.connection.state"), "idle", StringComparison.OrdinalIgnoreCase);
            ref var target = ref idle ? ref _idle : ref _used;
            if (instrument.IsObservable)
            {
                Volatile.Write(ref target, (int)value);
            }
            else
            {
                Interlocked.Add(ref target, (int)value);
            }
        }
        else if (instrument.Name == PoolTimeouts)
        {
            if (instrument.IsObservable)
            {
                Interlocked.Exchange(ref _timeouts, value);
            }
            else
            {
                Interlocked.Add(ref _timeouts, value);
            }
        }
    }

    private void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        // Recording must never throw into the code that threw, nor recurse when it does. Cancellation is how .NET ends a wait (every
        // loop of the workers cancels its timer on each pass), not a failure: counting it would report thousands of "exceptions"
        // that cost nothing worth fixing (found on the dev server on 2026-10-18).
        if (_inException || e.Exception is OperationCanceledException)
        {
            return;
        }

        _inException = true;
        try
        {
            Interlocked.Increment(ref _exceptions);
            // Diagnose adds the class that threw and the kind of work it happened in; Basic only the type (spec §5, Runtime).
            var subKey = _recorder.Diagnosing ? $"{Thrower(e.Exception)} in {SourceKind(PerfContext.Source)}" : string.Empty;
            _recorder.Count(PerfCollector.Runtime, PerfKind.Exception, e.Exception.GetType().FullName ?? "Exception", subKey);
        }
        catch (Exception)
        {
            // Nothing to do: measuring is never worth failing for.
        }
        finally
        {
            _inException = false;
        }
    }

    /// <summary>The class whose method threw, with compiler-generated classes (async methods, lambdas) turned back into their own.</summary>
    internal static string Thrower(Exception exception)
    {
        var type = exception.TargetSite?.DeclaringType;
        while (type is { DeclaringType: not null } && type.Name.StartsWith('<'))
        {
            type = type.DeclaringType;
        }

        if (type is null)
        {
            return "unknown";
        }

        var name = type.FullName ?? type.Name;
        var generic = name.IndexOf('`', StringComparison.Ordinal);
        return (generic > 0 ? name[..generic] : name).Replace('+', '.');
    }

    /// <summary>interaction, api, worker, gateway, signer or other: never the page or loop itself, which would multiply the keys.</summary>
    internal static string SourceKind(string source) => source.IndexOf(':') is > 0 and var colon ? source[..colon] : source;

    private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == name)
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private static bool HasTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == name && tag.Value is not null)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.FirstChanceException -= OnException;
        _listener.Dispose();
    }
}
