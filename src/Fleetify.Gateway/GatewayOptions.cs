using Fleetify.Protocol;

namespace Fleetify.Gateway;

/// <summary>
/// Gateway settings from the <c>Gateway</c> configuration section. Kestrel endpoints are built from these values in
/// code, so <c>ASPNETCORE_URLS</c> has no effect on where the gateway listens.
/// </summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Address both listeners bind to. <c>0.0.0.0</c> in the container, <c>127.0.0.1</c> locally.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>HTTPS port for enrollment and the mTLS WebSocket (8443 in the container, 7200 locally).</summary>
    public int AgentPort { get; set; } = 8443;

    /// <summary>Plain HTTP port for <c>/health</c> (8081 in the container, 5201 locally).</summary>
    public int HealthPort { get; set; } = 8081;

    /// <summary>How long a requester waits for fleetify-signer.</summary>
    public int SigningTimeoutSeconds { get; set; } = 30;

    /// <summary>Enrollment requests allowed per remote address per minute.</summary>
    public int EnrollmentsPerMinutePerAddress { get; set; } = 20;

    /// <summary>Heartbeat interval announced in HelloAck until a signed configuration says otherwise.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = ProtocolLimits.DefaultHeartbeatSeconds;

    /// <summary>Time the agent has to send Hello after the WebSocket opens.</summary>
    public int HelloTimeoutSeconds { get; set; } = 10;

    /// <summary>Time a live session has to answer a Ping when a second connection claims the same identity.</summary>
    public int DuplicateProbeSeconds { get; set; } = 5;

    /// <summary>Messages queued for one agent before the gateway gives up on it (the agent is not reading).</summary>
    public int SendQueueCapacity { get; set; } = 256;

    public TimeSpan SigningTimeout => TimeSpan.FromSeconds(SigningTimeoutSeconds);
}
