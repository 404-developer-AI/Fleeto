namespace Fleeto.Core.Entities;

/// <summary>The two kinds of remote session (0.3.0). Stored by name.</summary>
public enum RemoteSessionKind
{
    /// <summary>Take over the screen, served by the agent.</summary>
    RemoteControl,

    /// <summary>Terminal, files, services and processes without touching the screen, served by the watchdog.</summary>
    RemoteBackground
}

/// <summary>Where one technician's connection to a remote session stands. Stored by name.</summary>
public enum RemoteParticipantState
{
    /// <summary>Written by web; fleeto-signer has not signed the session token yet.</summary>
    Requested,
    /// <summary>The token is signed; the browser has 60 seconds to open the relay with it.</summary>
    Signed,
    /// <summary>The gateway accepted the token (single use) and asked the endpoint to connect.</summary>
    Connecting,
    /// <summary>Browser and endpoint are paired through the relay.</summary>
    Connected,
    /// <summary>The connection ended after it was connected, or never connected in time; <see cref="RemoteSessionParticipant.EndReason"/> says why.</summary>
    Ended,
    /// <summary>fleeto-signer, the gateway or the endpoint refused it.</summary>
    Refused,
    /// <summary>The signer did not answer or the request failed.</summary>
    Failed
}

/// <summary>
/// One remote session on one endpoint (ARCHITECTURE.md §4, Remote session). Several technicians can take part; each is a
/// <see cref="RemoteSessionParticipant"/> with its own signed token and key exchange. Nothing of what happens inside the session
/// is stored here: the gateway relays ciphertext only.
/// </summary>
public class RemoteSession
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public RemoteSessionKind Kind { get; set; }

    /// <summary>The service on the endpoint that serves the session: the agent for remote control, the watchdog for remote background.</summary>
    public AgentComponent Component { get; set; }

    /// <summary>
    /// Remote control on Windows (0.3.0 step 3): the Windows session shown, 0 for the console (sign-in screen included), otherwise the
    /// id of a signed-in session the agent reported. Null for remote background.
    /// </summary>
    public int? WindowsSessionId { get; set; }

    public Guid StartedByUserId { get; set; }
    public string StartedByName { get; set; } = string.Empty;

    /// <summary>Optional reason given when the session was opened.</summary>
    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When the first participant connected; null when nobody ever did.</summary>
    public DateTime? StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
}

/// <summary>One technician's connection to a remote session, authorised by its own single-use session token.</summary>
public class RemoteSessionParticipant
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }

    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;

    /// <summary>Optional reason given when joining.</summary>
    public string? Reason { get; set; }

    public RemoteParticipantState State { get; set; } = RemoteParticipantState.Requested;

    /// <summary>The browser's ephemeral X25519 public key (32 bytes), bound into the token by the signer.</summary>
    public byte[] BrowserPublicKey { get; set; } = [];

    /// <summary>Serialized RemoteSessionToken protobuf, set by the signer.</summary>
    public byte[]? TokenPayload { get; set; }

    public byte[]? TokenSignature { get; set; }
    public string? SigningKeyId { get; set; }
    public DateTime? SignedAt { get; set; }

    /// <summary>The token opens the relay only until this time (60 seconds after signing).</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>Address of the browser as the gateway saw it.</summary>
    public string? IpAddress { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ConnectingAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
}

/// <summary>
/// An action taken inside a remote background session (a file, service or process action with its target), as the endpoint
/// reported it. Terminal content is never stored.
/// </summary>
public class RemoteSessionAction
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid? ParticipantId { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public DateTime Time { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string? Detail { get; set; }
}
