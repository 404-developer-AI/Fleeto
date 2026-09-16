namespace Fleeto.Workers.Options;

/// <summary>Email delivery settings that are not secrets. SMTP itself is configured in Settings (encrypted).</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// Development only: when no SMTP settings exist, emails are written as .eml files to this directory.
    /// Environment variables such as <c>%LOCALAPPDATA%</c> are expanded. Empty disables the pickup directory.
    /// </summary>
    public string PickupDirectory { get; set; } = string.Empty;

    /// <summary>Sender address used for pickup directory emails.</summary>
    public string PickupFromAddress { get; set; } = "fleeto@localhost";

    /// <summary>SMTP connect, command and data timeout.</summary>
    public int SmtpTimeoutSeconds { get; set; } = 30;

    /// <summary>Consecutive delivery failures that open the circuit breaker.</summary>
    public int CircuitBreakerFailures { get; set; } = 5;

    /// <summary>How long delivery pauses once the circuit breaker is open.</summary>
    public int CircuitBreakerPauseMinutes { get; set; } = 5;

    /// <summary>Emails handled per pass.</summary>
    public int BatchSize { get; set; } = 50;
}

/// <summary>Backup job settings that are not secrets. Destination and public key live in Settings (encrypted).</summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>
    /// Path of pg_dump. "pg_dump" uses the PATH (containers). "auto" searches
    /// <c>C:\Program Files\PostgreSQL\*\bin\pg_dump.exe</c> on Windows, newest version first, then falls back to the PATH.
    /// </summary>
    public string PgDumpPath { get; set; } = "pg_dump";

    /// <summary>Maximum run time of pg_dump before it is killed and the backup fails.</summary>
    public int PgDumpTimeoutMinutes { get; set; } = 180;

    /// <summary>Directory for the temporary dump and its encrypted copy. Empty means the system temp directory.</summary>
    public string TempDirectory { get; set; } = string.Empty;

    /// <summary>Upload attempts per file before the upload counts as failed.</summary>
    public int UploadAttempts { get; set; } = 3;

    /// <summary>Timeout of one upload request.</summary>
    public int UploadTimeoutMinutes { get; set; } = 60;
}

/// <summary>Tuning of check evaluation.</summary>
public sealed class CheckEvaluationOptions
{
    public const string SectionName = "CheckEvaluation";

    /// <summary>Endpoints evaluated in parallel.</summary>
    public int Parallelism { get; set; } = 8;

    /// <summary>Results evaluated per endpoint per transaction.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Time window of the regular sweep for endpoints with unevaluated results (every 30 seconds).</summary>
    public int SweepWindowMinutes { get; set; } = 10;

    /// <summary>
    /// Time window of the wide sweep at start and every hour, and the oldest result evaluated for an endpoint that
    /// has no cursor yet.
    /// </summary>
    public int WideSweepWindowHours { get; set; } = 24;
}

/// <summary>Webhook delivery settings that are not secrets. Channels and URLs are configured in Settings (encrypted).</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>Connect and request timeout of one delivery.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Consecutive delivery failures of one channel that pause that channel.</summary>
    public int CircuitBreakerFailures { get; set; } = 5;

    /// <summary>How long a channel pauses once its circuit breaker is open.</summary>
    public int CircuitBreakerPauseMinutes { get; set; } = 5;

    /// <summary>Deliveries handled per pass.</summary>
    public int BatchSize { get; set; } = 50;
}
