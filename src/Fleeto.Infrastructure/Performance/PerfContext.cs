using System.Diagnostics;

namespace Fleeto.Infrastructure.Performance;

/// <summary>
/// One measured unit of work: a page interaction, a request, a worker run, an agent message or a signing request (PERFORMANCE.md
/// §5). It flows with the async context, so every database command and external call made inside it, on any thread, adds its time
/// here. A scope inside another adds to both, and stops collecting once it ended: work that outlives it (a render that continues
/// after the event) is not counted against it.
/// </summary>
public sealed class PerfScope : IDisposable
{
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _dbCount;
    private long _dbMicros;
    private long _externalMicros;
    private long _waitMicros;
    private int _ended;

    internal PerfScope(string source, PerfScope? parent)
    {
        Source = source;
        Parent = parent;
    }

    /// <summary>The source database work is counted under: <c>interaction:/clients</c>, <c>worker:patch-sync</c>.</summary>
    public string Source { get; }

    public PerfScope? Parent { get; }

    public bool Ended => Volatile.Read(ref _ended) == 1;

    public int DbCount => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _dbCount));
    public double DbMs => Interlocked.Read(ref _dbMicros) / 1000d;
    public double ExternalMs => Interlocked.Read(ref _externalMicros) / 1000d;

    /// <summary>Time since the scope began, minus the time it waited for the user (an open dialog).</summary>
    public double ElapsedMs => Math.Max(0, Stopwatch.GetElapsedTime(_start).TotalMilliseconds - Interlocked.Read(ref _waitMicros) / 1000d);

    internal void AddDb(double milliseconds)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (!scope.Ended)
            {
                Interlocked.Increment(ref scope._dbCount);
                Interlocked.Add(ref scope._dbMicros, (long)(milliseconds * 1000));
            }
        }
    }

    internal void AddExternal(double milliseconds)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (!scope.Ended)
            {
                Interlocked.Add(ref scope._externalMicros, (long)(milliseconds * 1000));
            }
        }
    }

    /// <summary>
    /// Time the work waited for the user, such as a confirmation dialog awaited inside a click handler. It is not time Fleeto was
    /// slow, so it is left out of <see cref="ElapsedMs"/>, here and in every enclosing scope.
    /// </summary>
    public void AddUserWait(TimeSpan wait)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (!scope.Ended)
            {
                Interlocked.Add(ref scope._waitMicros, (long)(wait.TotalMilliseconds * 1000));
            }
        }
    }

    /// <summary>Ends the scope: later work no longer counts against it, and the enclosing scope becomes current again.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0 && ReferenceEquals(PerfContext.Current, this))
        {
            PerfContext.Restore(Parent);
        }
    }
}

/// <summary>The current <see cref="PerfScope"/> of the async context.</summary>
public static class PerfContext
{
    private static readonly AsyncLocal<PerfScope?> CurrentScope = new();

    public static PerfScope? Current => CurrentScope.Value;

    /// <summary>The source of database work right now: the innermost open scope, or <see cref="Core.Performance.PerfKeys.OtherSource"/>.</summary>
    public static string Source
    {
        get
        {
            for (var scope = CurrentScope.Value; scope is not null; scope = scope.Parent)
            {
                if (!scope.Ended)
                {
                    return scope.Source;
                }
            }

            return Core.Performance.PerfKeys.OtherSource;
        }
    }

    /// <summary>Begins a scope inside the current one. Dispose it at the end of the work, in the same async method.</summary>
    public static PerfScope Begin(string source)
    {
        var scope = new PerfScope(source, CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    internal static void Restore(PerfScope? scope) => CurrentScope.Value = scope;
}
