using System.Security.Cryptography;
using System.Threading.Channels;
using Fleeto.Core.Entities;
using Fleeto.Gateway.Tls;
using Fleeto.Protocol;
using Fleeto.Protocol.Agent.V1;

namespace Fleeto.Gateway.Sessions;

/// <summary>
/// One live agent connection. Holds the session state and a bounded outbox; the transport (WebSocket) drains the outbox
/// in its own send loop, so nothing in the gateway ever waits for an agent to read. An agent that lets the outbox fill
/// up is dropped.
/// </summary>
public sealed class AgentSession : IDisposable
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    private readonly Channel<ServerMessage> _outbox;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _probeLock = new();
    private (ulong Nonce, TaskCompletionSource<bool> Completion)? _probe;
    private int _closing;
    private int _tier;
    private long _knownConfigVersion;
    private long _lastSeenTicks;
    private long _flushedTicks;
    private long _lastReceiveTicks;
    private long _sendStartedTicks;
    private int _renewalInFlight;
    private int _watchdogCertificateInFlight;
    private int _agentOnlyWarned;
    private int _ring = (int)UpdateRing.Standard;

    public AgentSession(AgentIdentity identity, string remoteAddress, int sendQueueCapacity, DateTime now)
    {
        Identity = identity;
        RemoteAddress = remoteAddress;
        ConnectedAt = now;
        _lastReceiveTicks = now.Ticks;
        _outbox = Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(sendQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public Guid Id { get; } = Guid.NewGuid();
    public AgentIdentity Identity { get; }
    public Guid EndpointId => Identity.EndpointId;

    /// <summary>True for the watchdog of the endpoint (0.2.1); its certificate role decides, never its Hello.</summary>
    public bool IsWatchdog => Identity.Role == AgentComponent.Watchdog;

    /// <summary>The service this session is: agent or watchdog.</summary>
    public AgentComponent Component => Identity.Role;
    public string RemoteAddress { get; }

    /// <summary>Address of the agent as stored on the endpoint (Public IP); null when unknown.</summary>
    public string? PublicIpAddress { get; init; }

    public DateTime ConnectedAt { get; }

    /// <summary>Set when the session is accepted.</summary>
    public Guid ClientId { get; internal set; }

    /// <summary>Cancelled when the session is closed or aborted.</summary>
    public CancellationToken Closed => _cts.Token;

    public bool IsClosing => Volatile.Read(ref _closing) != 0;

    /// <summary>Messages for the agent, in order. Completed when the session closes.</summary>
    public ChannelReader<ServerMessage> Outbox => _outbox.Reader;

    /// <summary>Why the session was closed, for the Disconnected event.</summary>
    public string CloseReason { get; private set; } = "Connection closed.";

    /// <summary>Stored tier as last read from the database (tier enforcement, layer 3).</summary>
    public EndpointTier Tier
    {
        get => (EndpointTier)Volatile.Read(ref _tier);
        internal set => Volatile.Write(ref _tier, (int)value);
    }

    /// <summary>Effective update ring of the endpoint, as last read from the database (0.2.1).</summary>
    public UpdateRing Ring
    {
        get => (UpdateRing)Volatile.Read(ref _ring);
        internal set => Volatile.Write(ref _ring, (int)value);
    }

    /// <summary>Release version and permission of the last UpdateOffer sent on this connection; null before the first.</summary>
    internal (string Version, bool Allowed)? LastOffer { get; set; }

    /// <summary>The peer status last stored for this connection, so an unchanged heartbeat writes nothing.</summary>
    internal string? LastPeerStatus { get; set; }

    /// <summary>Heartbeat interval from the last delivered configuration; used for idle detection.</summary>
    public int HeartbeatSeconds { get; internal set; } = ProtocolLimits.DefaultHeartbeatSeconds;

    /// <summary>The highest configuration version the agent has or was sent.</summary>
    public long KnownConfigVersion => Interlocked.Read(ref _knownConfigVersion);

    internal DateTime LastSeen => new(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc);

    /// <summary>Queues a message. Returns false (and aborts the session) when the agent is not reading.</summary>
    public bool Send(ServerMessage message)
    {
        if (_outbox.Writer.TryWrite(message))
        {
            return true;
        }

        if (!IsClosing)
        {
            Abort("The agent did not read its messages; the connection was dropped.");
        }

        return false;
    }

    /// <summary>
    /// Sends Disconnect as the last message and closes. The transport sends it, closes the WebSocket and cancels the
    /// session; if it cannot within a few seconds the session is aborted.
    /// </summary>
    public void Close(DisconnectCode code, string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        CloseReason = reason;
        var queued = _outbox.Writer.TryWrite(new ServerMessage { Disconnect = new Disconnect { Code = code, Reason = reason } });
        _outbox.Writer.TryComplete();
        CancelProbe();
        try
        {
            if (queued)
            {
                _cts.CancelAfter(CloseGrace);
            }
            else
            {
                _cts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Ends the session at once without a Disconnect message.</summary>
    public void Abort(string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0)
        {
            CloseReason = reason;
        }

        _outbox.Writer.TryComplete();
        CancelProbe();
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Sends a Ping and waits for the matching Pong. Used when a second connection claims this identity: an answer
    /// means this connection is alive (the newcomer is a clone), silence means it is a dead connection.
    /// </summary>
    public async Task<bool> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsClosing)
        {
            return false;
        }

        var nonce = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_probeLock)
        {
            _probe = (nonce, completion);
        }

        if (!Send(new ServerMessage { Ping = new Ping { Nonce = nonce } }))
        {
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Closed);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await completion.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            lock (_probeLock)
            {
                if (_probe?.Completion == completion)
                {
                    _probe = null;
                }
            }
        }
    }

    internal void OnPong(ulong nonce)
    {
        lock (_probeLock)
        {
            if (_probe is { } probe && probe.Nonce == nonce)
            {
                probe.Completion.TrySetResult(true);
            }
        }
    }

    /// <summary>Raises the known configuration version; false when <paramref name="version"/> is not newer.</summary>
    internal bool TryAdvanceConfigVersion(long version)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _knownConfigVersion);
            if (version <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _knownConfigVersion, version, current) == current)
            {
                return true;
            }
        }
    }

    internal void MarkReceived(DateTime now)
    {
        Interlocked.Exchange(ref _lastReceiveTicks, now.Ticks);
        Interlocked.Exchange(ref _lastSeenTicks, now.Ticks);
    }

    internal DateTime LastReceive => new(Interlocked.Read(ref _lastReceiveTicks), DateTimeKind.Utc);

    /// <summary>True when LastSeen changed since the last flush; returns the value to flush.</summary>
    internal bool TryGetUnflushedLastSeen(out long ticks)
    {
        ticks = Interlocked.Read(ref _lastSeenTicks);
        return ticks > Interlocked.Read(ref _flushedTicks);
    }

    internal void MarkFlushed(long ticks)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _flushedTicks);
            if (ticks <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _flushedTicks, ticks, current) != current);
    }

    internal void MarkSendStarted(DateTime now) => Interlocked.Exchange(ref _sendStartedTicks, now.Ticks);

    internal void MarkSendFinished() => Interlocked.Exchange(ref _sendStartedTicks, 0);

    /// <summary>When the send in progress started, or null when no send is in progress.</summary>
    internal DateTime? SendStartedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _sendStartedTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _sentJobs = new();

    internal bool TryBeginRenewal() => Interlocked.CompareExchange(ref _renewalInFlight, 1, 0) == 0;

    internal void EndRenewal() => Interlocked.Exchange(ref _renewalInFlight, 0);

    internal bool TryBeginWatchdogCertificate() => Interlocked.CompareExchange(ref _watchdogCertificateInFlight, 1, 0) == 0;

    internal void EndWatchdogCertificate() => Interlocked.Exchange(ref _watchdogCertificateInFlight, 0);

    /// <summary>
    /// True the first time a job is offered to this connection; a job is delivered once per connection and again after a reconnect.
    /// </summary>
    internal bool TryMarkJobSent(Guid jobId) => _sentJobs.TryAdd(jobId, 0);

    /// <summary>True only the first time; used to log agent-only discards once per session.</summary>
    internal bool FirstAgentOnlyDiscard() => Interlocked.Exchange(ref _agentOnlyWarned, 1) == 0;

    private void CancelProbe()
    {
        lock (_probeLock)
        {
            _probe?.Completion.TrySetResult(false);
            _probe = null;
        }
    }

    public void Dispose() => _cts.Dispose();
}
