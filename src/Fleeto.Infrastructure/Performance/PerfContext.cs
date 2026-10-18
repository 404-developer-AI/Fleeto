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
    private Dictionary<string, (int Count, double Ms)>? _statements;

    /// <summary>Distinct statements counted per scope in Diagnose; more are not counted for N+1.</summary>
    public const int MaxStatements = 200;

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

    /// <summary>
    /// Diagnose: one more run of the statement <paramref name="hash"/> in this scope, for N+1 detection and the heaviest statements
    /// of a slow event. Counted in the innermost scope only, so a page load inside an event is not counted twice. At most
    /// <see cref="MaxStatements"/> distinct statements are kept per scope.
    /// </summary>
    internal void AddStatement(string hash, double milliseconds)
    {
        if (Ended)
        {
            return;
        }

        var statements = LazyInitializer.EnsureInitialized(ref _statements, () => new Dictionary<string, (int Count, double Ms)>(StringComparer.Ordinal));
        lock (statements)
        {
            if (statements.TryGetValue(hash, out var current))
            {
                statements[hash] = (current.Count + 1, current.Ms + milliseconds);
            }
            else if (statements.Count < MaxStatements)
            {
                statements[hash] = (1, milliseconds);
            }
        }
    }

    /// <summary>The statements counted in this scope (Diagnose), or empty.</summary>
    internal IReadOnlyList<(string Hash, int Count, double Ms)> Statements()
    {
        var statements = Volatile.Read(ref _statements);
        if (statements is null)
        {
            return [];
        }

        lock (statements)
        {
            return statements.Select(pair => (pair.Key, pair.Value.Count, pair.Value.Ms)).ToList();
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
    public static string Source => Innermost?.Source ?? Core.Performance.PerfKeys.OtherSource;

    /// <summary>The innermost scope that has not ended, or null outside any measured work.</summary>
    public static PerfScope? Innermost
    {
        get
        {
            for (var scope = CurrentScope.Value; scope is not null; scope = scope.Parent)
            {
                if (!scope.Ended)
                {
                    return scope;
                }
            }

            return null;
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
