using System.Collections.Concurrent;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Gateway.Data;
using Fleetify.Gateway.Diagnostics;
using Fleetify.Gateway.Releases;
using Fleetify.Gateway.Signing;
using Fleetify.Gateway.Tls;
using Fleetify.Infrastructure.Security;
using Fleetify.Protocol;
using Fleetify.Protocol.Agent.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fleetify.Gateway.Sessions;

/// <summary>
/// Owns every live agent session: acceptance (Hello, duplicate identity), message handling, configuration delivery with
/// tier enforcement, revocation, idle and expiry checks, and the online state in the database.
/// <para>
/// One agent session and one watchdog session per endpoint (0.2.1), told apart by the role of the certificate. A second connection for
/// the same endpoint and role while a live session exists is a clone unless the live session fails to answer a Ping, in which case it
/// was a dropped connection and is replaced. A watchdog session only carries heartbeats, certificate renewal and updates.
/// </para>
/// </summary>
public sealed partial class AgentSessionManager : BackgroundService
{
    private const int StripeCount = 256;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CatchUpInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StuckSendLimit = TimeSpan.FromSeconds(30);

    private const string RevokedReason =
        "The agent certificate was revoked or the endpoint was deleted. Enroll the agent again with a new token.";

    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, AgentSession> _watchdogs = new();
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, StripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly GatewayStore _store;
    private readonly INotificationBus _bus;
    private readonly CertificateAllowList _allowList;
    private readonly SigningRequestClient _signing;
    private readonly GatewayMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly GatewayOptions _options;
    private readonly ILogger<AgentSessionManager> _logger;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly ReleaseCatalog? _releases;
    private readonly List<IDisposable> _subscriptions = [];
    private long _lastIngestWarningTicks;
    private volatile bool _ready;
    private volatile bool _stopping;

    public AgentSessionManager(GatewayStore store, INotificationBus bus, CertificateAllowList allowList, SigningRequestClient signing,
        GatewayMetrics metrics, TimeProvider time, IOptions<GatewayOptions> options, ILogger<AgentSessionManager> logger,
        IHostApplicationLifetime? lifetime = null, ReleaseCatalog? releases = null)
    {
        _store = store;
        _bus = bus;
        _allowList = allowList;
        _signing = signing;
        _metrics = metrics;
        _time = time;
        _options = options.Value;
        _logger = logger;
        _lifetime = lifetime;
        _releases = releases;

        _allowList.Reloaded += OnAllowListReloaded;
        if (_releases is not null)
        {
            _releases.Changed += EvaluateOffers;
        }

        _subscriptions.Add(_bus.Subscribe(NotificationChannels.EndpointConfig, OnEndpointConfigAsync));
        _subscriptions.Add(_bus.Subscribe(NotificationChannels.EndpointStatus, OnEndpointStatusAsync));
        _subscriptions.Add(_bus.Subscribe(NotificationChannels.CheckRunRequests, OnCheckRunRequestAsync));
        _subscriptions.Add(_bus.Subscribe(NotificationChannels.Jobs, OnJobsAsync));
    }

    /// <summary>True once the startup reset of online state has succeeded; sessions are refused before that.</summary>
    public bool IsReady => _ready;

    public int Count => _sessions.Count;

    public bool TryGetSession(Guid endpointId, out AgentSession? session) => _sessions.TryGetValue(endpointId, out session);

    internal ICollection<AgentSession> Sessions => _sessions.Values;

    internal ICollection<AgentSession> WatchdogSessions => _watchdogs.Values;

    public int WatchdogCount => _watchdogs.Count;

    public bool TryGetWatchdogSession(Guid endpointId, out AgentSession? session) => _watchdogs.TryGetValue(endpointId, out session);

    // ---------------------------------------------------------------------------------------------------------------
    // Lifecycle of one session
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Accepts a session after its Hello: resolves a duplicate identity, marks the endpoint online, sends HelloAck, a
    /// newer configuration and an inventory request when needed. Returns false when the session was refused (the
    /// session has been closed with the reason).
    /// </summary>
    public async Task<bool> OpenAsync(AgentSession session, Hello hello, CancellationToken cancellationToken)
    {
        if (_stopping)
        {
            session.Close(DisconnectCode.ServerShutdown, "The gateway is restarting. Reconnect in a moment.");
            return false;
        }

        if (session.IsWatchdog)
        {
            return await OpenWatchdogAsync(session, hello, cancellationToken);
        }

        if (hello.Component == Component.Watchdog)
        {
            session.Close(DisconnectCode.ProtocolError, "A watchdog must connect with its own watchdog certificate.");
            return false;
        }

        var endpointId = session.EndpointId;
        var probeTimeout = TimeSpan.FromSeconds(_options.DuplicateProbeSeconds);
        while (true)
        {
            _sessions.TryGetValue(endpointId, out var existing);
            if (existing is not null)
            {
                if (await existing.ProbeAsync(probeTimeout, cancellationToken))
                {
                    await RefuseDuplicateAsync(session, existing, cancellationToken);
                    return false;
                }

                _logger.LogInformation(
                    "Endpoint {EndpointId}: the previous connection from {OldAddress} did not answer; replacing it with the connection from {NewAddress}",
                    endpointId, existing.RemoteAddress, session.RemoteAddress);
                existing.Abort("Replaced by a new connection with the same identity.");
            }

            var stripe = Stripe(endpointId);
            await stripe.WaitAsync(cancellationToken);
            try
            {
                var registered = existing is null
                    ? _sessions.TryAdd(endpointId, session)
                    : _sessions.TryUpdate(endpointId, session, existing) || _sessions.TryAdd(endpointId, session);
                if (!registered)
                {
                    // Another connection registered in the meantime: probe that one.
                    continue;
                }

                var now = _time.GetUtcNow().UtcDateTime;
                SessionStart? start;
                try
                {
                    start = await _store.OpenSessionAsync(endpointId, hello, hello.ConfigVersion, session.RemoteAddress, now, cancellationToken,
                        session.PublicIpAddress);
                }
                catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
                {
                    _sessions.TryRemove(KeyValuePair.Create(endpointId, session));
                    _logger.LogWarning(ex, "Endpoint {EndpointId}: could not open the session because the database is unavailable", endpointId);
                    session.Close(DisconnectCode.ServerShutdown, "The gateway cannot reach its database. Reconnect later.");
                    return false;
                }

                if (start is null)
                {
                    _sessions.TryRemove(KeyValuePair.Create(endpointId, session));
                    session.Close(DisconnectCode.Revoked, RevokedReason);
                    return false;
                }

                session.ClientId = start.ClientId;
                session.Tier = start.Tier;
                session.Ring = start.Ring;
                session.TryAdvanceConfigVersion(DbText.ClampToLong(hello.ConfigVersion));
                session.MarkReceived(now);
                _metrics.ConnectionAccepted();

                session.Send(new ServerMessage
                {
                    HelloAck = new HelloAck
                    {
                        EndpointId = endpointId.ToString("D"),
                        ServerTime = Timestamp.FromDateTime(now),
                        HeartbeatIntervalSeconds = (uint)_options.HeartbeatIntervalSeconds
                    }
                });

                if (start.NewerConfig is not null)
                {
                    DeliverConfig(session, start.NewerConfig);
                }

                if (start.InventoryHash is null || !string.Equals(start.InventoryHash, hello.InventoryHash, StringComparison.Ordinal))
                {
                    session.Send(new ServerMessage { InventoryRequest = new InventoryRequest() });
                }

                SendOffer(session, force: true);

                _logger.LogDebug("Endpoint {EndpointId} connected from {RemoteAddress}", endpointId, session.RemoteAddress);
                break;
            }
            finally
            {
                stripe.Release();
            }
        }

        await PublishAsync(NotificationChannels.EndpointStatus, endpointId);
        try
        {
            await DeliverRunRequestsAsync([endpointId], cancellationToken);
            await DeliverJobsAsync([endpointId], cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // The periodic catch-up delivers them later.
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not read pending check run requests or jobs", endpointId);
        }

        return true;
    }

    /// <summary>
    /// Ends a session: when it is still the registered session of its endpoint, marks the endpoint offline and records a
    /// Disconnected event. A session that was replaced or refused changes nothing.
    /// </summary>
    public async Task CloseAsync(AgentSession session)
    {
        if (_stopping)
        {
            // Shutdown marks every session offline in one statement.
            return;
        }

        if (session.IsWatchdog)
        {
            await CloseWatchdogAsync(session);
            return;
        }

        var endpointId = session.EndpointId;
        var stripe = Stripe(endpointId);
        await stripe.WaitAsync();
        var removed = false;
        try
        {
            if (!_sessions.TryRemove(KeyValuePair.Create(endpointId, session)))
            {
                return;
            }

            removed = true;
            var now = _time.GetUtcNow().UtcDateTime;
            try
            {
                await _store.MarkOfflineAsync(endpointId, session.LastSeen, $"Disconnected from {session.RemoteAddress}: {session.CloseReason}", now,
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                // The startup reset and the workers' offline detection correct this later.
                _logger.LogWarning(ex, "Endpoint {EndpointId}: could not record the disconnect", endpointId);
            }
        }
        finally
        {
            stripe.Release();
        }

        if (removed)
        {
            _logger.LogDebug("Endpoint {EndpointId} disconnected: {Reason}", endpointId, session.CloseReason);
            await PublishAsync(NotificationChannels.EndpointStatus, endpointId);
        }
    }

    private async Task RefuseDuplicateAsync(AgentSession session, AgentSession existing, CancellationToken cancellationToken)
    {
        _metrics.DuplicateIdentity();
        _logger.LogWarning(
            "Endpoint {EndpointId}: refused a second connection from {NewAddress} while the connection from {OldAddress} is alive (duplicate agent identity)",
            session.EndpointId, session.RemoteAddress, existing.RemoteAddress);
        session.Close(DisconnectCode.DuplicateIdentity,
            "Another live connection uses this agent identity. Revoke the agent in Fleeto and enroll each copy with its own token.");
        try
        {
            await _store.InsertEventAsync(session.EndpointId, EndpointEventKind.DuplicateIdentity,
                $"A second connection from {session.RemoteAddress} used the identity of the live connection from {existing.RemoteAddress}. The second connection was refused.",
                _time.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not record the duplicate identity event", session.EndpointId);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Messages from the agent
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Handles one message of an accepted session.</summary>
    public async Task HandleAsync(AgentSession session, AgentMessage message, CancellationToken cancellationToken)
    {
        session.MarkReceived(_time.GetUtcNow().UtcDateTime);
        if (session.IsWatchdog)
        {
            await HandleWatchdogAsync(session, message, cancellationToken);
            return;
        }

        switch (message.BodyCase)
        {
            case AgentMessage.BodyOneofCase.Heartbeat:
                await SavePeerStatusAsync(session, message.Heartbeat, cancellationToken);
                break;
            case AgentMessage.BodyOneofCase.WatchdogCertificate:
                StartWatchdogCertificate(session, message.WatchdogCertificate);
                break;
            case AgentMessage.BodyOneofCase.UpdateStatus:
                await SaveUpdateStatusAsync(session, message.UpdateStatus, cancellationToken);
                break;
            case AgentMessage.BodyOneofCase.Pong:
                session.OnPong(message.Pong.Nonce);
                break;
            case AgentMessage.BodyOneofCase.CheckResults:
                await IngestAsync(session, message.CheckResults, cancellationToken);
                break;
            case AgentMessage.BodyOneofCase.Inventory:
                await SaveInventoryAsync(session, message.Inventory, cancellationToken);
                break;
            case AgentMessage.BodyOneofCase.RenewCertificate:
                StartRenewal(session, message.RenewCertificate);
                break;
            case AgentMessage.BodyOneofCase.ConfigApplied:
                await ConfigAppliedAsync(session, message.ConfigApplied, cancellationToken);
                break;
            case AgentMessage.BodyOneofCase.JobStarted:
                await JobStartedAsync(session, message.JobStarted, cancellationToken);
                break;
            case AgentMessage.BodyOneofCase.JobOutput:
                await JobOutputAsync(session, message.JobOutput, cancellationToken);
                break;
            case AgentMessage.BodyOneofCase.JobCompletion:
                await JobCompletionAsync(session, message.JobCompletion, cancellationToken);
                break;
            case AgentMessage.BodyOneofCase.Hello:
                session.Close(DisconnectCode.ProtocolError, "Hello may only be sent once per connection.");
                break;
            default:
                // A newer agent may send messages this gateway does not know; ignoring them keeps old gateways compatible.
                _logger.LogDebug("Endpoint {EndpointId} sent an unknown message; ignored", session.EndpointId);
                break;
        }
    }

    private async Task IngestAsync(AgentSession session, CheckResultBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Results.Count > ProtocolLimits.MaxResultsPerBatch || batch.Sequence == 0 || batch.Sequence > long.MaxValue)
        {
            session.Close(DisconnectCode.ProtocolError,
                $"A check result batch needs a sequence of at least 1 and at most {ProtocolLimits.MaxResultsPerBatch} results.");
            return;
        }

        if (session.Tier != EndpointTier.Managed)
        {
            // Tier enforcement, layer 3: an agent-only endpoint has no checks. Acknowledge so the agent drops the batch.
            _metrics.BatchDiscarded();
            if (session.FirstAgentOnlyDiscard())
            {
                _logger.LogWarning("Endpoint {EndpointId} is agent-only but sent check results; they are acknowledged and not stored",
                    session.EndpointId);
            }

            session.Send(new ServerMessage { BatchAck = new BatchAck { Sequence = batch.Sequence } });
            return;
        }

        (IngestOutcome Outcome, int Stored) result;
        try
        {
            result = await _store.IngestAsync(session.EndpointId, session.ClientId, batch, _time.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // No ack: the agent keeps the batch on disk and sends it again.
            _metrics.IngestFailed();
            var nowTicks = _time.GetUtcNow().UtcTicks;
            var last = Interlocked.Read(ref _lastIngestWarningTicks);
            if (nowTicks - last > TimeSpan.TicksPerSecond * 10 && Interlocked.CompareExchange(ref _lastIngestWarningTicks, nowTicks, last) == last)
            {
                _logger.LogWarning(ex, "Storing check results failed; batches are not acknowledged until the database accepts them");
            }

            return;
        }

        session.Send(new ServerMessage { BatchAck = new BatchAck { Sequence = batch.Sequence } });
        if (result.Outcome == IngestOutcome.Stored)
        {
            _metrics.BatchStored(result.Stored);
            if (result.Stored < batch.Results.Count)
            {
                _logger.LogWarning("Endpoint {EndpointId}: {Skipped} results in batch {Sequence} had an invalid check id and were skipped",
                    session.EndpointId, batch.Results.Count - result.Stored, batch.Sequence);
            }

            await PublishAsync(NotificationChannels.CheckResults, session.EndpointId);
        }
        else
        {
            _metrics.BatchDuplicate();
        }
    }

    private async Task SaveInventoryAsync(AgentSession session, InventoryReport report, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveInventoryAsync(session.EndpointId, session.ClientId, report, _time.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // The hash stays different, so the next session asks for the inventory again.
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not store the inventory", session.EndpointId);
            return;
        }

        await PublishAsync(NotificationChannels.EndpointStatus, session.EndpointId);
    }

    private async Task ConfigAppliedAsync(AgentSession session, ConfigApplied applied, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(applied.Error))
        {
            _logger.LogWarning("Endpoint {EndpointId} refused configuration version {Version}: {Error}",
                session.EndpointId, applied.ConfigVersion, DbText.Clean(applied.Error, 500));
            return;
        }

        try
        {
            await _store.UpdateAppliedConfigVersionAsync(session.EndpointId, DbText.ClampToLong(applied.ConfigVersion),
                _time.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not record the applied configuration version", session.EndpointId);
        }
    }

    private void StartRenewal(AgentSession session, RenewCertificateRequest request)
    {
        if (!session.TryBeginRenewal())
        {
            session.Send(RenewalError("A certificate renewal for this agent is already in progress."));
            return;
        }

        // In the background: the signer may take seconds, and the receive loop must keep answering Pings meanwhile.
        _ = Task.Run(async () =>
        {
            try
            {
                session.Send(await RenewAsync(session, request.CsrDer.ToByteArray(), session.Closed));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Endpoint {EndpointId}: certificate renewal failed", session.EndpointId);
                session.Send(RenewalError("The renewal could not be completed right now. The agent keeps its current certificate and retries later."));
            }
            finally
            {
                session.EndRenewal();
            }
        });
    }

    /// <summary>
    /// Renews the session's certificate. The CSR must carry the key of the certificate this connection authenticated
    /// with, so a stolen session cannot swap in a key of its own.
    /// </summary>
    internal async Task<ServerMessage> RenewAsync(AgentSession session, byte[] csrDer, CancellationToken cancellationToken)
    {
        string csrKey;
        try
        {
            csrKey = InternalCertificateAuthority.CsrPublicKeyFingerprint(csrDer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return RenewalError("The certificate signing request is invalid. It must be an ECDSA P-256 request signed with the agent key.");
        }

        if (!SecureCompare.HexEquals(csrKey, session.Identity.PublicKeyFingerprint))
        {
            _logger.LogWarning("Endpoint {EndpointId}: refused a renewal whose key differs from the connection certificate", session.EndpointId);
            return RenewalError("The certificate signing request must use the same key as the current agent certificate.");
        }

        var outcome = await _signing.RequestAsync(SigningRequestKind.AgentRenewal, session.ClientId, session.EndpointId, csrDer, "gateway",
            cancellationToken);
        switch (outcome.State)
        {
            case SigningOutcomeState.Completed when outcome.Result is not null:
                // The new certificate must be on the allow list before the agent reconnects with it.
                _allowList.AddIssued(outcome.Result, session.EndpointId, session.Component);
                _logger.LogInformation("Endpoint {EndpointId}: {Component} certificate renewed", session.EndpointId, session.Component);
                return new ServerMessage { RenewCertificate = new RenewCertificateResponse { CertificateDer = ByteString.CopyFrom(outcome.Result) } };
            case SigningOutcomeState.Refused:
                _logger.LogWarning("Endpoint {EndpointId}: certificate renewal refused: {Reason}", session.EndpointId, outcome.RefusalReason);
                return RenewalError(outcome.RefusalReason ?? "The certificate renewal was refused.");
            default:
                return RenewalError("The renewal could not be completed right now. The agent keeps its current certificate and retries later.");
        }
    }

    private static ServerMessage RenewalError(string error) =>
        new() { RenewCertificate = new RenewCertificateResponse { Error = error } };

    // ---------------------------------------------------------------------------------------------------------------
    // Configuration and tier
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends a signed configuration when it is newer than what the agent has. Tier enforcement, layer 3: a managed
    /// configuration is never delivered to an endpoint whose stored tier is agent-only.
    /// </summary>
    internal bool DeliverConfig(AgentSession session, StoredConfig config)
    {
        session.Tier = config.Tier;

        AgentConfig parsed;
        try
        {
            parsed = AgentConfig.Parser.ParseFrom(config.Payload);
        }
        catch (InvalidProtocolBufferException ex)
        {
            _logger.LogError(ex, "Endpoint {EndpointId}: stored configuration version {Version} cannot be parsed; not delivered",
                session.EndpointId, config.Version);
            return false;
        }

        if (!Guid.TryParse(parsed.EndpointId, out var configEndpoint) || configEndpoint != session.EndpointId ||
            (ulong)config.Version != parsed.Version)
        {
            _logger.LogError("Endpoint {EndpointId}: stored configuration version {Version} names another endpoint or version; not delivered",
                session.EndpointId, config.Version);
            return false;
        }

        if (parsed.Tier == Protocol.Agent.V1.Tier.Unspecified ||
            (parsed.Tier == Protocol.Agent.V1.Tier.Managed && config.Tier != EndpointTier.Managed))
        {
            _logger.LogWarning(
                "Endpoint {EndpointId} is {Tier}: refused to deliver configuration version {Version} with tier {ConfigTier}",
                session.EndpointId, config.Tier, config.Version, parsed.Tier);
            return false;
        }

        if (!session.TryAdvanceConfigVersion(config.Version))
        {
            return false;
        }

        if (parsed.HeartbeatIntervalSeconds > 0)
        {
            session.HeartbeatSeconds = (int)Math.Min(parsed.HeartbeatIntervalSeconds, 24 * 3600);
        }

        return session.Send(new ServerMessage
        {
            Config = new SignedConfig
            {
                Payload = ByteString.CopyFrom(config.Payload),
                Signature = ByteString.CopyFrom(config.Signature),
                KeyId = config.KeyId
            }
        });
    }

    /// <summary>Reads and delivers the configuration of one live endpoint.</summary>
    internal async Task DeliverConfigAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(endpointId, out var session) || session.IsClosing)
        {
            return;
        }

        var config = await _store.ReadConfigAsync(endpointId, cancellationToken);
        if (config is not null)
        {
            DeliverConfig(session, config);
        }
    }

    private async Task OnEndpointConfigAsync(string payload, CancellationToken cancellationToken)
    {
        if (payload == NotificationBusEvents.Resync)
        {
            await CatchUpConfigsAsync(cancellationToken);
        }
        else if (Guid.TryParse(payload, out var endpointId))
        {
            await DeliverConfigAsync(endpointId, cancellationToken);
        }
    }

    private async Task OnEndpointStatusAsync(string payload, CancellationToken cancellationToken)
    {
        if (payload == NotificationBusEvents.Resync)
        {
            await RefreshTiersAsync(cancellationToken);
        }
        else if (Guid.TryParse(payload, out var endpointId) && _sessions.TryGetValue(endpointId, out var session))
        {
            var tiers = await _store.ReadTiersAsync([endpointId], cancellationToken);
            if (tiers.TryGetValue(endpointId, out var tier))
            {
                session.Tier = tier;
            }
        }
    }

    /// <summary>Delivers every configuration a live agent is missing (lost notification, listener reconnect).</summary>
    internal async Task CatchUpConfigsAsync(CancellationToken cancellationToken)
    {
        var sessions = _sessions.Values.ToArray();
        if (sessions.Length == 0)
        {
            return;
        }

        var versions = await _store.ReadConfigVersionsAsync(sessions.Select(s => s.EndpointId).ToArray(), cancellationToken);
        foreach (var session in sessions)
        {
            if (versions.TryGetValue(session.EndpointId, out var version) && version > session.KnownConfigVersion)
            {
                await DeliverConfigAsync(session.EndpointId, cancellationToken);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Check run requests
    // ---------------------------------------------------------------------------------------------------------------

    private async Task OnCheckRunRequestAsync(string payload, CancellationToken cancellationToken)
    {
        if (payload == NotificationBusEvents.Resync)
        {
            await DeliverRunRequestsAsync(_sessions.Keys.ToArray(), cancellationToken);
        }
        else if (Guid.TryParse(payload, out var requestId) &&
                 await _store.ReadRunRequestEndpointAsync(requestId, cancellationToken) is { } endpointId &&
                 _sessions.ContainsKey(endpointId))
        {
            await DeliverRunRequestsAsync([endpointId], cancellationToken);
        }
    }

    /// <summary>
    /// Delivers pending check run requests to live agents, each request once. Tier enforcement, layer 3: never to an
    /// endpoint whose stored tier is agent-only (the request then expires). The agent itself only runs checks of its signed
    /// configuration and rate-limits the requests.
    /// </summary>
    internal async Task DeliverRunRequestsAsync(Guid[] endpointIds, CancellationToken cancellationToken)
    {
        var live = endpointIds.Where(id => _sessions.TryGetValue(id, out var s) && !s.IsClosing && s.Tier == EndpointTier.Managed).ToArray();
        if (live.Length == 0)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var requests = await _store.ReadDeliverableRunRequestsAsync(live, now, cancellationToken);
        foreach (var request in requests)
        {
            if (!_sessions.TryGetValue(request.EndpointId, out var session) || session.IsClosing || session.Tier != EndpointTier.Managed)
            {
                continue;
            }

            if (!await _store.MarkRunRequestDeliveredAsync(request.Id, now, cancellationToken))
            {
                continue;
            }

            var message = new RunChecksNow { RequestId = request.Id.ToString("D") };
            message.CheckIds.Add(request.CheckDefinitionId.ToString("D"));
            if (session.Send(new ServerMessage { RunChecksNow = message }))
            {
                _logger.LogInformation("Endpoint {EndpointId}: asked the agent to run check {CheckId} now (request {RequestId})",
                    request.EndpointId, request.CheckDefinitionId, request.Id);
            }
            else
            {
                // The check still runs on its schedule; the technician can ask again.
                _logger.LogWarning("Endpoint {EndpointId}: check run request {RequestId} could not be sent", request.EndpointId, request.Id);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Jobs (0.2.0)
    // ---------------------------------------------------------------------------------------------------------------

    private async Task OnJobsAsync(string payload, CancellationToken cancellationToken)
    {
        if (payload == NotificationBusEvents.Resync)
        {
            await DeliverJobsAsync(_sessions.Keys.ToArray(), cancellationToken);
        }
        else if (Guid.TryParse(payload, out var endpointId) && _sessions.ContainsKey(endpointId))
        {
            await DeliverJobsAsync([endpointId], cancellationToken);
        }
    }

    /// <summary>
    /// Delivers queued, valid jobs to live managed agents, again after every reconnect (the agent runs a job id once). Tier
    /// enforcement, layer 3: never to an endpoint whose stored tier is agent-only; such jobs expire.
    /// </summary>
    internal async Task DeliverJobsAsync(Guid[] endpointIds, CancellationToken cancellationToken)
    {
        var live = endpointIds.Where(id => _sessions.TryGetValue(id, out var s) && !s.IsClosing && s.Tier == EndpointTier.Managed).ToArray();
        if (live.Length == 0)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var job in await _store.ReadDeliverableJobsAsync(live, now, cancellationToken))
        {
            if (!_sessions.TryGetValue(job.EndpointId, out var session) || session.IsClosing || session.Tier != EndpointTier.Managed ||
                !session.TryMarkJobSent(job.Id))
            {
                continue;
            }

            if (!await _store.MarkJobDeliveredAsync(job.Id, now, cancellationToken))
            {
                continue;
            }

            if (session.Send(new ServerMessage
                {
                    Job = new SignedJob { Payload = ByteString.CopyFrom(job.Payload), Signature = ByteString.CopyFrom(job.Signature), KeyId = job.KeyId }
                }))
            {
                _logger.LogInformation("Endpoint {EndpointId}: delivered job {JobId}", job.EndpointId, job.Id);
            }
        }
    }

    private async Task JobStartedAsync(AgentSession session, JobStarted started, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(started.JobId, out var jobId))
        {
            return;
        }

        if (await StoreJobMessageAsync(session, () => _store.JobStartedAsync(jobId, session.EndpointId, _time.GetUtcNow().UtcDateTime, cancellationToken)) is { } update)
        {
            session.Send(new ServerMessage { JobAck = new JobAck { JobId = started.JobId, Kind = JobAckKind.Started } });
            if (update == JobUpdate.Changed)
            {
                await PublishAsync(NotificationChannels.Jobs, session.EndpointId);
            }
        }
    }

    private async Task JobOutputAsync(AgentSession session, JobOutput output, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(output.JobId, out var jobId))
        {
            return;
        }

        if (output.Data.Length > ScriptRules.MaxChunkBytes)
        {
            session.Close(DisconnectCode.ProtocolError, $"A job output chunk may be at most {ScriptRules.MaxChunkBytes / 1024} KiB.");
            return;
        }

        if (await StoreJobMessageAsync(session, () => _store.StoreJobOutputAsync(jobId, session.EndpointId, output.Stream, output.Sequence,
                output.Data.ToByteArray(), _time.GetUtcNow().UtcDateTime, cancellationToken)) is { } update)
        {
            session.Send(new ServerMessage
            {
                JobAck = new JobAck { JobId = output.JobId, Kind = JobAckKind.Output, Stream = output.Stream, Sequence = output.Sequence }
            });
            if (update == JobUpdate.Changed || output.Sequence % 16 == 0)
            {
                await PublishAsync(NotificationChannels.Jobs, session.EndpointId);
            }
        }
    }

    private async Task JobCompletionAsync(AgentSession session, JobCompletion completion, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(completion.JobId, out var jobId))
        {
            return;
        }

        if (await StoreJobMessageAsync(session, () => _store.CompleteJobAsync(jobId, session.EndpointId, completion, _time.GetUtcNow().UtcDateTime,
                cancellationToken)) is { } update)
        {
            session.Send(new ServerMessage { JobAck = new JobAck { JobId = completion.JobId, Kind = JobAckKind.Completion } });
            if (update == JobUpdate.Changed)
            {
                _logger.LogInformation("Endpoint {EndpointId}: job {JobId} ended ({Result}, exit code {ExitCode})", session.EndpointId, jobId,
                    completion.Result, completion.ExitCode);
                await PublishAsync(NotificationChannels.Jobs, session.EndpointId);
            }
        }
    }

    /// <summary>Runs a job write. Returns null when the database is unavailable: no acknowledgement, so the agent sends it again.</summary>
    private async Task<JobUpdate?> StoreJobMessageAsync(AgentSession session, Func<Task<JobUpdate>> write)
    {
        try
        {
            return await write();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not store a job message; the agent sends it again", session.EndpointId);
            return null;
        }
    }

    internal async Task RefreshTiersAsync(CancellationToken cancellationToken)
    {
        var sessions = _sessions.Values.Concat(_watchdogs.Values).ToArray();
        if (sessions.Length == 0)
        {
            return;
        }

        var facts = await _store.ReadSessionFactsAsync(sessions.Select(s => s.EndpointId).Distinct().ToArray(), cancellationToken);
        foreach (var session in sessions)
        {
            if (facts.TryGetValue(session.EndpointId, out var fact))
            {
                session.Tier = fact.Tier;
                session.Ring = fact.Ring;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Revocation, idle connections, expiry
    // ---------------------------------------------------------------------------------------------------------------

    private void OnAllowListReloaded(Guid? endpointHint)
    {
        if (endpointHint is { } endpointId)
        {
            if (_sessions.TryGetValue(endpointId, out var session))
            {
                CloseIfNoLongerAllowed(session);
            }

            if (_watchdogs.TryGetValue(endpointId, out var watchdog))
            {
                CloseIfNoLongerAllowed(watchdog);
            }

            return;
        }

        foreach (var session in _sessions.Values.Concat(_watchdogs.Values))
        {
            CloseIfNoLongerAllowed(session);
        }
    }

    private void CloseIfNoLongerAllowed(AgentSession session)
    {
        if (!session.IsClosing && !_allowList.IsStillAllowed(session.Identity.Fingerprint, session.EndpointId))
        {
            _logger.LogInformation("Endpoint {EndpointId}: certificate no longer allowed; closing the session", session.EndpointId);
            session.Close(DisconnectCode.Revoked, RevokedReason);
        }
    }

    /// <summary>Closes sessions whose certificate expired, whose agent went silent or who stopped reading.</summary>
    internal void Sweep()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var session in _sessions.Values.Concat(_watchdogs.Values))
        {
            if (session.IsClosing)
            {
                continue;
            }

            if (session.Identity.ExpiresAt <= now)
            {
                session.Close(DisconnectCode.Revoked, "The agent certificate has expired. Enroll the agent again with a new token.");
            }
            else if (session.SendStartedAt is { } started && now - started > StuckSendLimit)
            {
                session.Abort("The agent did not read its messages; the connection was dropped.");
            }
            else if (now - session.LastReceive > TimeSpan.FromSeconds(session.HeartbeatSeconds * ProtocolLimits.MissedHeartbeatsBeforeOffline + 30))
            {
                session.Abort("No message from the agent within three heartbeat intervals.");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Hosting
    // ---------------------------------------------------------------------------------------------------------------

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime?.ApplicationStopping.Register(BeginShutdown);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetOnlineStateAsync(stoppingToken);

        var lastCatchUp = _time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Sweep();

            if (_time.GetUtcNow() - lastCatchUp >= CatchUpInterval)
            {
                lastCatchUp = _time.GetUtcNow();
                try
                {
                    await RefreshTiersAsync(stoppingToken);
                    EvaluateOffers();
                    await CatchUpConfigsAsync(stoppingToken);
                    await DeliverRunRequestsAsync(_sessions.Keys.ToArray(), stoppingToken);
                    await DeliverJobsAsync(_sessions.Keys.ToArray(), stoppingToken);
                }
                catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
                {
                    _logger.LogWarning(ex, "Periodic configuration catch-up failed; retrying in {Interval}", CatchUpInterval);
                }
            }
        }
    }

    /// <summary>Marks every endpoint offline before the first session is accepted. Retries until the database answers.</summary>
    internal async Task ResetOnlineStateAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var count = await _store.MarkAllOfflineAsync(_time.GetUtcNow().UtcDateTime, cancellationToken);
                _ready = true;
                _logger.LogInformation("Gateway ready for agent sessions; {Count} endpoints marked offline at start", count);
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                _logger.LogWarning("Cannot reach the database to reset online state ({Error}); retrying in {Delay}", ex.Message, delay);
                try
                {
                    await Task.Delay(delay, _time, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private void BeginShutdown()
    {
        _stopping = true;
        foreach (var session in _sessions.Values.Concat(_watchdogs.Values))
        {
            session.Close(DisconnectCode.ServerShutdown, "The gateway is restarting. Reconnect in a moment.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginShutdown();
        var ids = _sessions.Keys.ToArray();
        var watchdogIds = _watchdogs.Keys.ToArray();
        _sessions.Clear();
        _watchdogs.Clear();
        if (watchdogIds.Length > 0)
        {
            try
            {
                await _store.MarkWatchdogsOfflineAsync(watchdogIds, _time.GetUtcNow().UtcDateTime, cancellationToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not mark {Count} watchdogs offline at shutdown; the next start does it", watchdogIds.Length);
            }
        }

        if (ids.Length > 0)
        {
            try
            {
                await _store.MarkOfflineAsync(ids, "The gateway shut down.", _time.GetUtcNow().UtcDateTime, cancellationToken);
                _logger.LogInformation("Gateway stopping: {Count} sessions closed and marked offline", ids.Length);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not mark {Count} endpoints offline at shutdown; the next start does it", ids.Length);
            }
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _allowList.Reloaded -= OnAllowListReloaded;
        if (_releases is not null)
        {
            _releases.Changed -= EvaluateOffers;
        }

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        base.Dispose();
    }

    private SemaphoreSlim Stripe(Guid endpointId) => _stripes[(endpointId.GetHashCode() & 0x7FFFFFFF) % StripeCount];

    private async Task PublishAsync(string channel, Guid endpointId)
    {
        try
        {
            await _bus.PublishAsync(channel, endpointId.ToString("D"));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // Notifications are hints; subscribers catch up from the database.
            _logger.LogDebug(ex, "Could not publish {Channel} for endpoint {EndpointId}", channel, endpointId);
        }
    }
}
