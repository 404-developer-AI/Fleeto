using System.Diagnostics;
using Npgsql;

namespace Fleeto.Infrastructure.Performance;

/// <summary>
/// Diagnose on the one data source of a process (0.7.0 step 2, PERFORMANCE.md §4.2): Npgsql's tracing gives every command at its
/// start, with its text, while the method that sent it is still on the stack, and ends it with its duration. Outside Diagnose the
/// filters answer false before Npgsql creates anything, so Basic pays one field read per command. The statement text is
/// normalized into a fingerprint at once and never kept as it was: no parameter value, no literal. COPY (the monitor's own
/// flush) and opening physical connections are not traced.
/// </summary>
public static class PerfStatementTracer
{
    /// <summary>The activity source of Npgsql.</summary>
    public const string SourceName = "Npgsql";

    private const string StatementProperty = "fleeto.perf.statement";

    private static int _listening;

    /// <summary>Configures tracing on a data source builder; <see cref="Listen"/> makes it record.</summary>
    public static void Configure(NpgsqlTracingOptionsBuilder options)
    {
        options.ConfigureCommandFilter(_ => Perf.Recorder.Diagnosing);
        options.ConfigureBatchFilter(_ => Perf.Recorder.Diagnosing);
        options.ConfigureCopyOperationFilter(_ => false);
        options.EnablePhysicalOpenTracing(false);
        options.EnableFirstResponseEvent(false);
        options.ConfigureCommandEnrichmentCallback((activity, command) => Attach(activity, command.CommandText));
        options.ConfigureBatchEnrichmentCallback((activity, batch) => Attach(activity, BatchText(batch)));
    }

    /// <summary>Listens to Npgsql's activities, once per process. Activities are only sampled while Diagnose runs.</summary>
    public static void Listen()
    {
        if (Interlocked.Exchange(ref _listening, 1) == 1)
        {
            return;
        }

        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                Perf.Recorder.Diagnosing ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStopped = Stopped
        });
    }

    private static void Attach(Activity activity, string? text)
    {
        try
        {
            if (Perf.Recorder.BeginStatement(text) is { } start)
            {
                activity.SetCustomProperty(StatementProperty, start);
            }
        }
        catch (Exception)
        {
            // Measuring is never worth failing a command for.
        }
    }

    private static void Stopped(Activity activity)
    {
        if (activity.GetCustomProperty(StatementProperty) is not PerfStatementStart start)
        {
            return;
        }

        try
        {
            Perf.Recorder.EndStatement(start, activity.Duration.TotalMilliseconds, activity.Status == ActivityStatusCode.Error);
        }
        catch (Exception)
        {
            // As above.
        }
    }

    /// <summary>A batch (EF Core's SaveChanges) as one statement: its distinct command texts in order.</summary>
    private static string BatchText(NpgsqlBatch batch) =>
        string.Join("; ", batch.BatchCommands.Cast<NpgsqlBatchCommand>().Select(c => c.CommandText).Distinct(StringComparer.Ordinal));
}

/// <summary>A statement that started in Diagnose: its fingerprint and the scope it counts in, kept on the activity until it ends.</summary>
public sealed record PerfStatementStart(PerfStatements.Fingerprint Fingerprint, PerfScope? Scope, string Source);
