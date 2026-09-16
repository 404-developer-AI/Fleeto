using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Gateway.Data;
using Fleeto.Gateway.Signing;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Npgsql;

namespace Fleeto.Gateway.Sessions;

/// <summary>Watchdog sessions, service and update reports, watchdog certificates and update offers (0.2.1).</summary>
public sealed partial class AgentSessionManager
{
    /// <summary>
    /// Accepts a watchdog session: resolves a duplicate identity like an agent session, marks the watchdog online and offers the current
    /// release. A watchdog never gets configurations, jobs, check run or inventory requests.
    /// </summary>
    private async Task<bool> OpenWatchdogAsync(AgentSession session, Hello hello, CancellationToken cancellationToken)
    {
        if (hello.Component != Component.Watchdog)
        {
            session.Close(DisconnectCode.ProtocolError, "A watchdog certificate may only open a watchdog session.");
            return false;
        }

        var endpointId = session.EndpointId;
        var probeTimeout = TimeSpan.FromSeconds(_options.DuplicateProbeSeconds);
        while (true)
        {
            _watchdogs.TryGetValue(endpointId, out var existing);
            if (existing is not null)
            {
                if (await existing.ProbeAsync(probeTimeout, cancellationToken))
                {
                    await RefuseDuplicateAsync(session, existing, cancellationToken);
                    return false;
                }

                existing.Abort("Replaced by a new watchdog connection with the same identity.");
            }

            var stripe = Stripe(endpointId);
            await stripe.WaitAsync(cancellationToken);
            try
            {
                var registered = existing is null
                    ? _watchdogs.TryAdd(endpointId, session)
                    : _watchdogs.TryUpdate(endpointId, session, existing) || _watchdogs.TryAdd(endpointId, session);
                if (!registered)
                {
                    continue;
                }

                var now = _time.GetUtcNow().UtcDateTime;
                WatchdogSessionStart? start;
                try
                {
                    start = await _store.OpenWatchdogSessionAsync(endpointId, hello.AgentVersion, now, cancellationToken);
                }
                catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
                {
                    _watchdogs.TryRemove(KeyValuePair.Create(endpointId, session));
                    _logger.LogWarning(ex, "Endpoint {EndpointId}: could not open the watchdog session because the database is unavailable", endpointId);
                    session.Close(DisconnectCode.ServerShutdown, "The gateway cannot reach its database. Reconnect later.");
                    return false;
                }

                if (start is null)
                {
                    _watchdogs.TryRemove(KeyValuePair.Create(endpointId, session));
                    session.Close(DisconnectCode.Revoked, RevokedReason);
                    return false;
                }

                session.ClientId = start.ClientId;
                session.Tier = start.Tier;
                session.Ring = start.Ring;
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
                SendOffer(session, force: true);
                _logger.LogDebug("Endpoint {EndpointId}: watchdog {Version} connected from {RemoteAddress}", endpointId, hello.AgentVersion, session.RemoteAddress);
                break;
            }
            finally
            {
                stripe.Release();
            }
        }

        await PublishAsync(NotificationChannels.EndpointStatus, endpointId);
        return true;
    }

    private async Task CloseWatchdogAsync(AgentSession session)
    {
        var endpointId = session.EndpointId;
        var stripe = Stripe(endpointId);
        await stripe.WaitAsync();
        var removed = false;
        try
        {
            if (!_watchdogs.TryRemove(KeyValuePair.Create(endpointId, session)))
            {
                return;
            }

            removed = true;
            try
            {
                await _store.MarkWatchdogOfflineAsync(endpointId, session.LastSeen, _time.GetUtcNow().UtcDateTime, CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                _logger.LogWarning(ex, "Endpoint {EndpointId}: could not record that the watchdog disconnected", endpointId);
            }
        }
        finally
        {
            stripe.Release();
        }

        if (removed)
        {
            _logger.LogDebug("Endpoint {EndpointId}: watchdog disconnected: {Reason}", endpointId, session.CloseReason);
            await PublishAsync(NotificationChannels.EndpointStatus, endpointId);
        }
    }

    /// <summary>
    /// A watchdog session handles heartbeats (with the state of the agent service), Pong, its own certificate renewal and update reports.
    /// Everything else is ignored: a watchdog certificate can never deliver check results, inventory or job output.
    /// </summary>
    private async Task HandleWatchdogAsync(AgentSession session, AgentMessage message, CancellationToken cancellationToken)
    {
        switch (message.BodyCase)
        {
            case AgentMessage.BodyOneofCase.Heartbeat:
                await SavePeerStatusAsync(session, message.Heartbeat, cancellationToken);
                break;
            case AgentMessage.BodyOneofCase.Pong:
                session.OnPong(message.Pong.Nonce);
                break;
            case AgentMessage.BodyOneofCase.RenewCertificate:
                StartRenewal(session, message.RenewCertificate);
                break;
            case AgentMessage.BodyOneofCase.UpdateStatus:
                await SaveUpdateStatusAsync(session, message.UpdateStatus, cancellationToken);
                break;
            case AgentMessage.BodyOneofCase.Hello:
                session.Close(DisconnectCode.ProtocolError, "Hello may only be sent once per connection.");
                break;
            default:
                _logger.LogWarning("Endpoint {EndpointId}: the watchdog sent a {Message} message, which only the agent may send; ignored",
                    session.EndpointId, message.BodyCase);
                break;
        }
    }

    /// <summary>Stores the state of the other service as this one reports it, only when it changed on this connection.</summary>
    private async Task SavePeerStatusAsync(AgentSession session, Heartbeat heartbeat, CancellationToken cancellationToken)
    {
        if (heartbeat.Peer is not { } peer || peer.State == ServiceState.Unspecified)
        {
            return;
        }

        var key = $"{peer.Version}\n{peer.State}\n{peer.Detail}";
        if (key == session.LastPeerStatus)
        {
            return;
        }

        var component = session.IsWatchdog ? AgentComponent.Agent : AgentComponent.Watchdog;
        try
        {
            await _store.SavePeerStatusAsync(session.EndpointId, session.ClientId, component, peer, _time.GetUtcNow().UtcDateTime, cancellationToken);
            session.LastPeerStatus = key;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // Sent again with the next heartbeat that differs, or stored on the next connection.
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not store the reported state of the {Component} service", session.EndpointId, component);
            return;
        }

        _logger.LogInformation("Endpoint {EndpointId}: the {Reporter} reports the {Component} service as {State}", session.EndpointId, session.Component,
            component, peer.State);
        await PublishAsync(NotificationChannels.EndpointStatus, session.EndpointId);
    }

    /// <summary>Stores the signed-in users an agent reports (0.2.2). The agent sends the list only when it changed.</summary>
    private async Task SaveSignedInUsersAsync(AgentSession session, Heartbeat heartbeat, CancellationToken cancellationToken)
    {
        if (heartbeat.SignedInUsers is not { } users)
        {
            return;
        }

        try
        {
            await _store.SaveSignedInUsersAsync(session.EndpointId, users, _time.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // The list is personal data and only a convenience for the run window: a lost report is replaced by the next change.
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not store the signed-in users", session.EndpointId);
            return;
        }

        _logger.LogDebug("Endpoint {EndpointId}: {Count} signed-in users", session.EndpointId, users.Users.Count);
    }

    private async Task SaveUpdateStatusAsync(AgentSession session, UpdateStatus status, CancellationToken cancellationToken)
    {
        var component = status.Component switch
        {
            Component.Agent => (AgentComponent?)AgentComponent.Agent,
            Component.Watchdog => AgentComponent.Watchdog,
            _ => null
        };
        if (status.State == UpdateState.Waiting && component is not null)
        {
            await SaveUpdateWaitAsync(session, component.Value, status, cancellationToken);
            return;
        }

        var state = GatewayStore.MapUpdateState(status.State);
        if (state is null || component is null)
        {
            return;
        }

        try
        {
            await _store.SaveUpdateStatusAsync(session.EndpointId, session.ClientId, component.Value, status.Version, state.Value, status.Detail,
                _time.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not store an update report", session.EndpointId);
            return;
        }

        var level = state is ComponentUpdateState.Failed or ComponentUpdateState.RolledBack ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(level, "Endpoint {EndpointId}: {Component} {Version}: {State} {Detail}", session.EndpointId, component, DbText.Clean(status.Version, 50),
            state, DbText.Clean(status.Detail, 500));
        await PublishAsync(NotificationChannels.EndpointStatus, session.EndpointId);
    }

    private async Task SaveUpdateWaitAsync(AgentSession session, AgentComponent component, UpdateStatus status, CancellationToken cancellationToken)
    {
        if (GatewayStore.MapUpdateWait(status.WaitReason) is not { } reason)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        // At most 30 days, so a wrong duration cannot overflow or promise a time far away.
        DateTime? until = status.WaitSeconds > 0 ? now.AddSeconds(Math.Min(status.WaitSeconds, (uint)TimeSpan.FromDays(30).TotalSeconds)) : null;
        try
        {
            await _store.SaveUpdateWaitAsync(session.EndpointId, session.ClientId, component, status.Version, reason, until, now, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Endpoint {EndpointId}: could not store an update wait", session.EndpointId);
            return;
        }

        _logger.LogInformation("Endpoint {EndpointId}: {Component} {Version}: waiting ({Reason}) until {Until}", session.EndpointId, component,
            DbText.Clean(status.Version, 50), reason, until);
        await PublishAsync(NotificationChannels.EndpointStatus, session.EndpointId);
    }

    private void StartWatchdogCertificate(AgentSession session, WatchdogCertificateRequest request)
    {
        if (!session.TryBeginWatchdogCertificate())
        {
            session.Send(WatchdogCertificateError("A watchdog certificate request for this endpoint is already in progress.", temporary: true));
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                session.Send(await IssueWatchdogCertificateAsync(session, request.CsrDer.ToByteArray(), session.Closed));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Endpoint {EndpointId}: watchdog certificate request failed", session.EndpointId);
                session.Send(WatchdogCertificateError("The watchdog certificate could not be issued right now. The agent retries later.", temporary: true));
            }
            finally
            {
                session.EndWatchdogCertificate();
            }
        });
    }

    /// <summary>
    /// Issues a certificate for the watchdog of the session's endpoint. Only an agent session may ask, and the watchdog key must differ from
    /// the key of the agent certificate this connection authenticated with; the signer checks the rest again.
    /// </summary>
    internal async Task<ServerMessage> IssueWatchdogCertificateAsync(AgentSession session, byte[] csrDer, CancellationToken cancellationToken)
    {
        if (session.IsWatchdog)
        {
            return WatchdogCertificateError("Only the agent can request a watchdog certificate.");
        }

        string csrKey;
        try
        {
            csrKey = InternalCertificateAuthority.CsrPublicKeyFingerprint(csrDer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return WatchdogCertificateError("The certificate signing request is invalid. It must be an ECDSA P-256 request signed with the watchdog key.");
        }

        if (SecureCompare.HexEquals(csrKey, session.Identity.PublicKeyFingerprint))
        {
            return WatchdogCertificateError("The watchdog needs its own key, not the key of the agent.");
        }

        var outcome = await _signing.RequestAsync(SigningRequestKind.WatchdogCertificate, session.ClientId, session.EndpointId, csrDer, "gateway",
            cancellationToken);
        switch (outcome.State)
        {
            case SigningOutcomeState.Completed when outcome.Result is not null:
                _allowList.AddIssued(outcome.Result, session.EndpointId, AgentComponent.Watchdog);
                _logger.LogInformation("Endpoint {EndpointId}: watchdog certificate issued", session.EndpointId);
                return new ServerMessage { WatchdogCertificate = new WatchdogCertificateResponse { CertificateDer = ByteString.CopyFrom(outcome.Result) } };
            case SigningOutcomeState.Refused:
                _logger.LogWarning("Endpoint {EndpointId}: watchdog certificate refused: {Reason}", session.EndpointId, outcome.RefusalReason);
                return WatchdogCertificateError(outcome.RefusalReason ?? "The watchdog certificate was refused.");
            default:
                return WatchdogCertificateError("The watchdog certificate could not be issued right now. The agent retries later.", temporary: true);
        }
    }

    /// <summary>A refusal, or with <paramref name="temporary"/> a failure that passes on its own, which the agent retries within minutes.</summary>
    private static ServerMessage WatchdogCertificateError(string error, bool temporary = false) =>
        new() { WatchdogCertificate = new WatchdogCertificateResponse { Error = error, Temporary = temporary } };

    /// <summary>
    /// Sends the current release when it was not sent on this connection yet, or when whether the endpoint's ring may install it changed.
    /// The receiver verifies the release signature and decides; this only follows the update rings and the controls of web.
    /// </summary>
    internal void SendOffer(AgentSession session, bool force)
    {
        var release = _releases?.Current;
        if (release is null || session.IsClosing)
        {
            return;
        }

        var allowed = release.IsAllowed(session.Ring, _time.GetUtcNow().UtcDateTime);
        if (!force && session.LastOffer is { } last && last.Version == release.Version && last.Allowed == allowed)
        {
            return;
        }

        session.LastOffer = (release.Version, allowed);
        session.Send(new ServerMessage
        {
            UpdateOffer = new UpdateOffer
            {
                Manifest = ByteString.CopyFrom(release.Manifest),
                Signature = ByteString.CopyFrom(release.Signature),
                UpdateAllowed = allowed
            }
        });
    }

    /// <summary>Re-evaluates the offer of every live session: after a release loads, a control changes or a ring delay passes.</summary>
    internal void EvaluateOffers()
    {
        foreach (var session in _sessions.Values.Concat(_watchdogs.Values))
        {
            SendOffer(session, force: false);
        }
    }
}
