namespace Fleetify.Gateway.Diagnostics;

/// <summary>Lock-free counters for the periodic summary line. Cumulative; the summary logger computes deltas.</summary>
public sealed class GatewayMetrics
{
    private long _batchesStored;
    private long _batchesDuplicate;
    private long _batchesDiscarded;
    private long _resultsStored;
    private long _ingestFailures;
    private long _enrollmentsCompleted;
    private long _enrollmentsRefused;
    private long _enrollmentsFailed;
    private long _connectionsAccepted;
    private long _connectionsRefused;
    private long _duplicateIdentities;

    public long BatchesStored => Interlocked.Read(ref _batchesStored);
    public long BatchesDuplicate => Interlocked.Read(ref _batchesDuplicate);
    public long BatchesDiscarded => Interlocked.Read(ref _batchesDiscarded);
    public long ResultsStored => Interlocked.Read(ref _resultsStored);
    public long IngestFailures => Interlocked.Read(ref _ingestFailures);
    public long EnrollmentsCompleted => Interlocked.Read(ref _enrollmentsCompleted);
    public long EnrollmentsRefused => Interlocked.Read(ref _enrollmentsRefused);
    public long EnrollmentsFailed => Interlocked.Read(ref _enrollmentsFailed);
    public long ConnectionsAccepted => Interlocked.Read(ref _connectionsAccepted);
    public long ConnectionsRefused => Interlocked.Read(ref _connectionsRefused);
    public long DuplicateIdentities => Interlocked.Read(ref _duplicateIdentities);

    public void BatchStored(int results)
    {
        Interlocked.Increment(ref _batchesStored);
        Interlocked.Add(ref _resultsStored, results);
    }

    public void BatchDuplicate() => Interlocked.Increment(ref _batchesDuplicate);
    public void BatchDiscarded() => Interlocked.Increment(ref _batchesDiscarded);
    public void IngestFailed() => Interlocked.Increment(ref _ingestFailures);
    public void EnrollmentCompleted() => Interlocked.Increment(ref _enrollmentsCompleted);
    public void EnrollmentRefused() => Interlocked.Increment(ref _enrollmentsRefused);
    public void EnrollmentFailed() => Interlocked.Increment(ref _enrollmentsFailed);
    public void ConnectionAccepted() => Interlocked.Increment(ref _connectionsAccepted);
    public void ConnectionRefused() => Interlocked.Increment(ref _connectionsRefused);
    public void DuplicateIdentity() => Interlocked.Increment(ref _duplicateIdentities);
}
