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

    /// <summary>Public PEM bundle of the instance CA certificates, fetched by the agent before enrollment.</summary>
    public const string CaPath = "/v1/ca";
    public const string ProtobufContentType = "application/x-protobuf";
}
