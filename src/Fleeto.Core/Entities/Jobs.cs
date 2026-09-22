namespace Fleeto.Core.Entities;

public enum ScriptLanguage
{
    PowerShell,
    Batch,
    /// <summary>POSIX sh on Linux.</summary>
    Shell,
    Bash
}

/// <summary>A script in the library (0.2.0). ClientId null = global. Every change of the body creates a new version.</summary>
public class Script
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ScriptLanguage Language { get; set; }

    /// <summary>The newest version; jobs always run it.</summary>
    public Guid? CurrentVersionId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<ScriptVersion> Versions { get; set; } = [];
}

/// <summary>
/// One immutable version of a script. Approval by a second admin (with a fresh two-factor code) is bound to the body hash;
/// sites whose policy requires approval run only an approved current version.
/// </summary>
public class ScriptVersion
{
    public Guid Id { get; set; }
    public Guid ScriptId { get; set; }

    /// <summary>Always the ClientId of the script, kept equal by a trigger.</summary>
    public Guid? ClientId { get; set; }

    public int Number { get; set; }
    public string Body { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the UTF-8 body.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; }
    public Guid AuthorUserId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }

    /// <summary>The body hash the approver saw; an approval counts only while it equals <see cref="Sha256"/>.</summary>
    public string? ApprovedSha256 { get; set; }

    public bool IsApproved => ApprovedAt is not null && ApprovedByUserId is not null && ApprovedByUserId != AuthorUserId && ApprovedSha256 == Sha256;
}

public enum JobType
{
    /// <summary>A script from the library, chosen by a technician.</summary>
    Script,

    /// <summary>
    /// Installs the Action1 agent on a Windows endpoint (0.4.0 step 3). There is no script version: fleeto-signer
    /// composes the body from the installer link of the client's Action1 organization (see <c>Action1AgentInstall</c>).
    /// </summary>
    Action1Agent
}

public enum JobState
{
    /// <summary>Written by web; fleeto-signer has not signed it yet.</summary>
    PendingSignature,
    /// <summary>Signed; the gateway delivers it while the agent is online and it is valid.</summary>
    Queued,
    Running,
    Succeeded,
    Failed,
    /// <summary>Not started before its ValidUntil.</summary>
    Expired,
    /// <summary>Refused by fleeto-signer or by the agent, with a reason.</summary>
    Refused,
    /// <summary>Started, but no result arrived within its timeout and grace: the outcome is unknown.</summary>
    Lost,
    /// <summary>Cancelled by a technician before it was delivered.</summary>
    Cancelled
}

/// <summary>What the agent reported when the job ended.</summary>
public enum JobResult
{
    Exited,
    TimedOut,
    Refused,
    FailedToStart,
    /// <summary>The agent stopped while the job ran; whether it finished is unknown.</summary>
    Interrupted
}

/// <summary>The account a script runs under on the endpoint (0.2.1), chosen per run.</summary>
public enum JobRunAs
{
    /// <summary>SYSTEM on Windows, root on Linux: the account the agent service itself runs as.</summary>
    Service,

    /// <summary>The user of the active session. The job fails when nobody is signed in.</summary>
    LoggedOnUser
}

public enum JobOutputState
{
    None,
    Receiving,
    Complete,
    Incomplete
}

/// <summary>
/// One job on one endpoint (ARCHITECTURE.md §4, Job). Runs created together share a <see cref="BatchId"/>. The signed payload
/// carries the script body, so the job stays exactly what was signed even when the script changes or is deleted.
/// </summary>
public class Job
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public Guid BatchId { get; set; }
    public JobType Type { get; set; } = JobType.Script;

    public Guid? ScriptId { get; set; }
    public Guid? ScriptVersionId { get; set; }
    public string ScriptName { get; set; } = string.Empty;
    public int ScriptVersionNumber { get; set; }
    public ScriptLanguage Language { get; set; }
    public string ScriptSha256 { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; }
    public long MaxOutputBytes { get; set; }

    /// <summary>The account the script runs under on the endpoint (0.2.1). Signed with the job.</summary>
    public JobRunAs RunAs { get; set; } = JobRunAs.Service;

    /// <summary>The account the agent ran the script under when it ran as the signed-in user (0.2.1), as the agent reported it.</summary>
    public string? RunAsAccount { get; set; }

    /// <summary>
    /// The user the technician chose for a job that runs as the signed-in user (0.2.2): the SID or uid the agent reported. Signed with the
    /// job; null lets the agent pick the console session first.
    /// </summary>
    public string? RunAsUserId { get; set; }

    /// <summary>The account name of <see cref="RunAsUserId"/> when it was chosen, for the job history.</summary>
    public string? RunAsChosenAccount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ValidUntil { get; set; }
    public Guid InitiatedByUserId { get; set; }
    public string InitiatedByName { get; set; } = string.Empty;

    public JobState State { get; set; } = JobState.PendingSignature;
    public string? RefusalReason { get; set; }

    /// <summary>Serialized JobPayload protobuf, set by the signer.</summary>
    public byte[]? Payload { get; set; }

    public byte[]? Signature { get; set; }
    public string? SigningKeyId { get; set; }
    public DateTime? SignedAt { get; set; }

    public DateTime? DeliveredAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public JobResult? Result { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>Agent message when the job could not run or was refused on the endpoint.</summary>
    public string? Error { get; set; }

    public JobOutputState OutputState { get; set; } = JobOutputState.None;

    /// <summary>Output bytes stored so far, both streams; the gateway refuses chunks beyond <see cref="MaxOutputBytes"/>.</summary>
    public long ReceivedOutputBytes { get; set; }

    public bool OutputTruncated { get; set; }
    public long? StdoutChunks { get; set; }
    public long? StdoutBytes { get; set; }
    public string? StdoutSha256 { get; set; }
    public long? StderrChunks { get; set; }
    public long? StderrBytes { get; set; }
    public string? StderrSha256 { get; set; }
}

public enum JobStream
{
    Stdout,
    Stderr
}

/// <summary>A piece of job output, stored idempotently per job, stream and sequence (ARCHITECTURE.md §4, Job output).</summary>
public class JobOutputChunk
{
    public Guid JobId { get; set; }
    public JobStream Stream { get; set; }
    public long Sequence { get; set; }
    public Guid ClientId { get; set; }
    public byte[] Data { get; set; } = [];
    public DateTime ReceivedAt { get; set; }
}

/// <summary>Limits of scripts and jobs (0.2.0), shared by web, signer, gateway and workers.</summary>
public static class ScriptRules
{
    public const int MaxBodyLength = 256 * 1024;
    public const int MinTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 24 * 3600;
    public const int DefaultTimeoutSeconds = 10 * 60;

    public static readonly TimeSpan DefaultValidity = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(7);

    /// <summary>
    /// Output per job the agent sends at most; beyond it the output is marked truncated. The policy of the endpoint's site
    /// sets the cap (0.2.1) between <see cref="MinOutputBytes"/> and <see cref="MaxOutputBytes"/>; signer, gateway and agent
    /// hold that ceiling whatever a policy or a job payload says.
    /// </summary>
    public const long DefaultMaxOutputBytes = 50L * 1024 * 1024;

    public const long MinOutputBytes = 1L * 1024 * 1024;
    public const long MaxOutputBytes = 200L * 1024 * 1024;

    /// <summary>The cap a policy may set, rounded to whole mebibytes and held inside the bounds above.</summary>
    public static long OutputCap(long bytes) => Math.Clamp(bytes, MinOutputBytes, MaxOutputBytes) / Mebibyte * Mebibyte;

    public const long Mebibyte = 1024 * 1024;

    /// <summary>Largest output chunk.</summary>
    public const int MaxChunkBytes = 64 * 1024;

    /// <summary>Shortest interval of a script check: starting a script interpreter is not free.</summary>
    public const int MinCheckIntervalSeconds = 60;

    /// <summary>Longest a script check may run; the version's timeout applies when it is shorter, and never beyond the interval.</summary>
    public const int MaxCheckTimeoutSeconds = 5 * 60;

    /// <summary>
    /// Script bytes one agent configuration carries at most (the configuration travels in one protocol message of 4 MiB). Script
    /// checks beyond it are sent without their script and report why.
    /// </summary>
    public const int MaxCheckScriptBytesPerConfig = 1024 * 1024;

    /// <summary>Endpoints one run may target.</summary>
    public const int MaxEndpointsPerRun = 500;

    /// <summary>Jobs one run may create (0.2.2): a run for all signed-in users creates one job per user on each endpoint.</summary>
    public const int MaxJobsPerRun = 500;

    /// <summary>
    /// Endpoint count above which a run emails every admin, until an admin changes it in Settings, Scripts (0.2.1). A run on a handful
    /// of endpoints is daily work; a run on many is worth telling the other admins about.
    /// </summary>
    public const int DefaultAdminNoticeAbove = 10;

    /// <summary>Reads the stored notice threshold; anything missing or out of range falls back to the default.</summary>
    public static int AdminNoticeAbove(string? stored) =>
        int.TryParse(stored, out var value) && value >= 0 && value <= MaxEndpointsPerRun ? value : DefaultAdminNoticeAbove;

    /// <summary>A running job without a result becomes lost this long after its timeout.</summary>
    public static readonly TimeSpan LostGrace = TimeSpan.FromMinutes(15);

    /// <summary>Output that is still incomplete this long after the job ended stays incomplete.</summary>
    public static readonly TimeSpan OutputRecoveryPeriod = TimeSpan.FromDays(7);
}
