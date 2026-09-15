namespace Fleetify.Protocol;

/// <summary>Limits and constants of agent protocol v1. The Go agent mirrors these values.</summary>
public static class ProtocolLimits
{
    /// <summary>Largest WebSocket message either side accepts.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>Largest enrollment request body.</summary>
    public const int MaxEnrollRequestBytes = 64 * 1024;

    public const int MaxResultsPerBatch = 500;

    /// <summary>Default heartbeat interval until a signed configuration sets one.</summary>
    public const int DefaultHeartbeatSeconds = 30;

    /// <summary>An agent silent for this many heartbeat intervals is treated as offline.</summary>
    public const int MissedHeartbeatsBeforeOffline = 3;

    /// <summary>Domain separation prefix for configuration signatures.</summary>
    public const string ConfigSignatureContext = "fleetify-agent-config-v1";

    public const string EnrollPath = "/v1/enroll";
    public const string ConnectPath = "/v1/connect";

    /// <summary>Renewal of an expired, never revoked agent certificate (0.2.0).</summary>
    public const string RecoverPath = "/v1/recover";

    /// <summary>Header on a 401 from <see cref="ConnectPath"/> when the certificate expired but can still be recovered.</summary>
    public const string CertificateStateHeader = "Fleeto-Certificate";

    public const string CertificateExpiredValue = "expired";

    /// <summary>How long after expiry a never revoked agent certificate can still renew itself.</summary>
    public static readonly TimeSpan RecoveryGrace = TimeSpan.FromDays(365);

    /// <summary>Public PEM bundle of the instance CA certificates, fetched by the agent before enrollment.</summary>
    public const string CaPath = "/v1/ca";
    public const string ProtobufContentType = "application/x-protobuf";

    /// <summary>Agent binaries of the current release, fetched with an agent or watchdog certificate (0.2.1).</summary>
    public const string ReleasesPath = "/v1/releases";

    /// <summary>Largest agent binary a release manifest may list; the agent refuses anything larger.</summary>
    public const long MaxAgentBinaryBytes = 128L * 1024 * 1024;
}
