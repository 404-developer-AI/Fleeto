using Fleetify.Gateway.Sessions;

namespace Fleetify.Gateway.Diagnostics;

/// <summary>One summary line per minute, so an operator can see load and trouble at a glance in the container log.</summary>
public sealed class GatewaySummaryLogger : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly GatewayMetrics _metrics;
    private readonly AgentSessionManager _sessions;
    private readonly TimeProvider _time;
    private readonly ILogger<GatewaySummaryLogger> _logger;

    public GatewaySummaryLogger(GatewayMetrics metrics, AgentSessionManager sessions, TimeProvider time, ILogger<GatewaySummaryLogger> logger)
    {
        _metrics = metrics;
        _sessions = sessions;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var previous = Snapshot();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var current = Snapshot();
            _logger.LogInformation(
                "Summary: {Sessions} sessions; last minute: {Batches} batches stored, {Duplicates} duplicate, {Discarded} agent-only discarded, " +
                "{ResultsPerSecond:0.0} results/s, {IngestFailures} ingest failures, {Connections} sessions opened, {Refused} refused, " +
                "{Duplicate} duplicate identities, {Enrollments} enrollments, {EnrollRefused} enrollments refused, {EnrollFailed} enrollments failed",
                _sessions.Count,
                current.BatchesStored - previous.BatchesStored,
                current.BatchesDuplicate - previous.BatchesDuplicate,
                current.BatchesDiscarded - previous.BatchesDiscarded,
                (current.ResultsStored - previous.ResultsStored) / Interval.TotalSeconds,
                current.IngestFailures - previous.IngestFailures,
                current.ConnectionsAccepted - previous.ConnectionsAccepted,
                current.ConnectionsRefused - previous.ConnectionsRefused,
                current.DuplicateIdentities - previous.DuplicateIdentities,
                current.EnrollmentsCompleted - previous.EnrollmentsCompleted,
                current.EnrollmentsRefused - previous.EnrollmentsRefused,
                current.EnrollmentsFailed - previous.EnrollmentsFailed);
            previous = current;
        }
    }

    private Counters Snapshot() => new(_metrics.BatchesStored, _metrics.BatchesDuplicate, _metrics.BatchesDiscarded, _metrics.ResultsStored,
        _metrics.IngestFailures, _metrics.ConnectionsAccepted, _metrics.ConnectionsRefused, _metrics.DuplicateIdentities,
        _metrics.EnrollmentsCompleted, _metrics.EnrollmentsRefused, _metrics.EnrollmentsFailed);

    private readonly record struct Counters(long BatchesStored, long BatchesDuplicate, long BatchesDiscarded, long ResultsStored,
        long IngestFailures, long ConnectionsAccepted, long ConnectionsRefused, long DuplicateIdentities,
        long EnrollmentsCompleted, long EnrollmentsRefused, long EnrollmentsFailed);
}
