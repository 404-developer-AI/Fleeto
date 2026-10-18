using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Fleeto.Core.Entities;
using Fleeto.Core.Performance;

namespace Fleeto.Infrastructure.Performance;

/// <summary>
/// The statements of one process in Diagnose (0.7.0 step 2, PERFORMANCE.md §4.2): the fingerprint of every command text, and the
/// methods that send each statement. A command text is normalized once and then found by its text; the fingerprints seen in a
/// minute go to the database with that minute. The calling method needs a walk of the stack, which is expensive, so it is taken
/// under a budget of <see cref="CallerCapturesPerSecond"/> a second, and only until a statement has its callers. Everything here is
/// bounded: a process that sends more distinct statements than the caps stops adding new ones, and never grows.
/// </summary>
public sealed class PerfStatements
{
    public const int MaxTexts = 5000;
    public const int MaxFingerprints = 2000;
    public const int CallerCapturesPerSecond = 50;

    /// <summary>Attempts to find a caller per statement; after these it keeps the callers it found.</summary>
    public const int MaxCallerAttempts = 20;

    /// <summary>Longer command texts are normalized every time instead of being kept as a key.</summary>
    private const int MaxCachedTextLength = 20_000;

    private readonly ConcurrentDictionary<string, Fingerprint> _byText = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Fingerprint> _byHash = new(StringComparer.Ordinal);
    private readonly Lock _budgetLock = new();
    private long _budgetSecond;
    private int _budgetUsed;

    /// <summary>A statement: its hash, its normalized text and the methods seen sending it.</summary>
    public sealed class Fingerprint
    {
        private readonly List<string> _callers = [];
        private int _attempts;
        private int _seen;

        internal Fingerprint(string hash, string sql)
        {
            Hash = hash;
            Sql = sql;
        }

        public string Hash { get; }
        public string Sql { get; }

        public IReadOnlyList<string> Callers
        {
            get
            {
                lock (_callers)
                {
                    return _callers.ToList();
                }
            }
        }

        /// <summary>Whether a walk of the stack could still find a caller worth keeping.</summary>
        internal bool WantsCaller
        {
            get
            {
                lock (_callers)
                {
                    return _callers.Count < PerfFingerprint.MaxCallers && _attempts < MaxCallerAttempts;
                }
            }
        }

        internal void AddCaller(string? caller)
        {
            lock (_callers)
            {
                _attempts++;
                if (caller is not null && _callers.Count < PerfFingerprint.MaxCallers && !_callers.Contains(caller, StringComparer.Ordinal))
                {
                    _callers.Add(caller);
                }
            }
        }

        internal void MarkSeen() => Volatile.Write(ref _seen, 1);

        internal bool TakeSeen() => Interlocked.Exchange(ref _seen, 0) == 1;
    }

    /// <summary>The fingerprint of a command text, or null when it is empty or the caps are reached.</summary>
    public Fingerprint? Of(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return null;
        }

        var cacheable = commandText.Length <= MaxCachedTextLength;
        if (cacheable && _byText.TryGetValue(commandText, out var known))
        {
            known.MarkSeen();
            return known;
        }

        var sql = SqlFingerprint.Normalize(commandText);
        if (sql.Length == 0)
        {
            return null;
        }

        var hash = SqlFingerprint.Hash(sql);
        if (!_byHash.TryGetValue(hash, out var fingerprint))
        {
            if (_byHash.Count >= MaxFingerprints)
            {
                return null;
            }

            fingerprint = _byHash.GetOrAdd(hash, _ => new Fingerprint(hash, sql));
        }

        if (cacheable)
        {
            if (_byText.Count >= MaxTexts)
            {
                // Texts that differ only in values (a list of literals) would fill the cache; start over rather than grow.
                _byText.Clear();
            }

            _byText.TryAdd(commandText, fingerprint);
        }

        fingerprint.MarkSeen();
        return fingerprint;
    }

    /// <summary>
    /// Finds the method that sent the command, when the statement still wants one and the budget of this second allows a walk of
    /// the stack. Called at the start of the command, while the method is still on the stack.
    /// </summary>
    public void CaptureCaller(Fingerprint fingerprint, long nowSeconds)
    {
        if (!fingerprint.WantsCaller || !TakeBudget(nowSeconds))
        {
            return;
        }

        fingerprint.AddCaller(CallerOf(new StackTrace(1, false)));
    }

    /// <summary>The fingerprints seen since the last call, for the flush of a minute.</summary>
    internal IReadOnlyList<Fingerprint> TakeSeen() => _byHash.Values.Where(f => f.TakeSeen()).ToList();

    private bool TakeBudget(long nowSeconds)
    {
        lock (_budgetLock)
        {
            if (nowSeconds != _budgetSecond)
            {
                _budgetSecond = nowSeconds;
                _budgetUsed = 0;
            }

            if (_budgetUsed >= CallerCapturesPerSecond)
            {
                return false;
            }

            _budgetUsed++;
            return true;
        }
    }

    /// <summary>
    /// The first method of Fleeto on the stack that is not plumbing: not the monitor, not the database context, not a framework.
    /// Async methods and lambdas are compiled into nested classes; their names are turned back into the method they came from.
    /// Null when no such frame is on the stack (a command that continued after an await, for example).
    /// </summary>
    public static string? CallerOf(StackTrace trace)
    {
        foreach (var frame in trace.GetFrames())
        {
            if (frame.GetMethod() is not { DeclaringType: { } type } method)
            {
                continue;
            }

            var name = MethodName(method.Name);
            while (type.DeclaringType is not null && IsCompilerGenerated(type))
            {
                name = MethodName(type.Name) ?? name;
                type = type.DeclaringType;
            }

            var space = type.Namespace ?? string.Empty;
            if (!space.StartsWith("Fleeto.", StringComparison.Ordinal) || space.StartsWith("Fleeto.Infrastructure.Performance", StringComparison.Ordinal) ||
                type.Name is "FleetoDbContext" or "FleetoDbContextFactory" || name is null)
            {
                continue;
            }

            var caller = $"{TypeName(type)}.{name}";
            return caller.Length <= 200 ? caller : caller[..200];
        }

        return null;
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.Name.StartsWith('<') || type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null;

    /// <summary><c>&lt;GetAsync&gt;d__5</c> and <c>&lt;GetAsync&gt;b__0</c> become <c>GetAsync</c>; <c>MoveNext</c> says nothing.</summary>
    private static string? MethodName(string name)
    {
        if (name.StartsWith('<'))
        {
            var end = name.IndexOf('>', StringComparison.Ordinal);
            return end > 1 ? name[1..end] : null;
        }

        return name is "MoveNext" or "Invoke" ? null : name;
    }

    private static string TypeName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick > 0)
        {
            name = name[..tick];
        }

        return type.DeclaringType is { } outer ? $"{TypeName(outer)}.{name}" : $"{type.Namespace}.{name}";
    }
}
