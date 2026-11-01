using System.Security.Cryptography;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>The endpoint a window of the Proxmox or the Synology web interface is for (0.8.0), with what stops a session from starting.</summary>
/// <param name="Problem">Why a session cannot start now, with the next step; null when it can.</param>
public sealed record WebInterfaceTarget(Guid EndpointId, string Hostname, string? ClientCode, string OsName, RemoteSessionKind Kind, string? Problem)
{
    /// <summary>"Proxmox web interface" or "Synology web interface".</summary>
    public string Name => RemoteSessionRules.WebInterfaceName(Kind);

    /// <summary>"Proxmox" or "DSM": whose account the technician signs in with.</summary>
    public string Product => RemoteSessionRules.WebInterfaceProduct(Kind);
}

/// <summary>A session of a web interface that waits for Fleeto Connect: the link the window opens.</summary>
public sealed record WebInterfaceStart(Guid ClaimId, string Link, DateTime ExpiresAt);

/// <summary>What Fleeto Connect gets for its claim: the ticket of the session, as the browser gets it for a remote session, and the relay.</summary>
public sealed record ConnectTicket(Guid ParticipantId, Guid SessionId, string Token, string Signature, string KeyId, IReadOnlyList<string> Fingerprints,
    string RelayUrl, string Endpoint, Guid EndpointId);

public enum WebInterfaceProgressState
{
    /// <summary>Nothing claimed the link yet.</summary>
    WaitingForConnect,
    /// <summary>The link expired unclaimed: Fleeto Connect is probably not installed.</summary>
    NotClaimed,
    Connecting,
    Connected,
    Ended,
    Failed
}

/// <summary>Where a session of a web interface stands, for its window.</summary>
public sealed record WebInterfaceProgress(WebInterfaceProgressState State, string? Message, string? ClaimedBy, DateTime? ConnectedAt);

/// <summary>
/// The Proxmox and the Synology web interface (0.8.0, PROXMOX-WEB.md §3 and §8): the window writes a claim of its kind and opens a
/// fleeto-connect: link; Fleeto Connect claims it
/// once with its own ephemeral key, and only then are the session, its participant and the signing request written, through the same path
/// as a remote session opened in the browser. Web checks the user, the endpoint and the tier again at the claim; the signer, the gateway
/// and the agent decide again after it.
/// </summary>
public sealed partial class RemoteSessionService
{
    public const string ClaimProblem =
        "This link of Fleeto Connect was used before or has expired. Open the web interface from Fleeto again.";

    /// <summary>
    /// The endpoint for the window of a web interface; null when the kind is no web interface, the caller may not open it (its own permission
    /// per kind) or the endpoint is not visible.
    /// </summary>
    public async Task<WebInterfaceTarget?> GetWebInterfaceTargetAsync(Caller caller, Guid endpointId, RemoteSessionKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!RemoteSessionRules.IsWebInterface(kind) || !caller.Has(RemoteSessionRules.Permission(kind)))
        {
            return null;
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new
            {
                e.Id, e.Hostname, e.Tier, e.Source, e.OsPlatform, e.OsName, e.IsOnline, e.AgentVersion,
                ClientCode = db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Code).FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var name = RemoteSessionRules.WebInterfaceName(kind);
        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var problem = TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed
            ? $"The {name} is only available on managed endpoints. Switch the endpoint to managed first."
            : endpoint.Source != EndpointSource.Agent || !RemoteSessionRules.IsWebInterfaceHost(kind, endpoint.OsPlatform, endpoint.OsName)
                ? $"The {name} is available on {RemoteSessionRules.WebInterfaceHost(kind)} with a Fleeto agent."
            : !RemoteSessionRules.AgentSupportsWebInterface(kind, endpoint.AgentVersion)
                ? $"The agent of this endpoint runs {(string.IsNullOrEmpty(endpoint.AgentVersion) ? "no known version" : "Fleeto " + endpoint.AgentVersion)}. The {name} needs Fleeto {RemoteSessionRules.MinimumWebInterfaceAgentVersion(kind)} or later; the agent updates with its update ring."
            : !endpoint.IsOnline
                ? $"The agent of this endpoint is offline. The {name} opens when the endpoint is online."
            : null;
        return new WebInterfaceTarget(endpoint.Id, endpoint.Hostname, endpoint.ClientCode, endpoint.OsName, kind, problem);
    }

    /// <summary>
    /// Opens a session of a web interface: writes the claim of its kind with a new secret and returns the link for Fleeto Connect. Audited as
    /// requested, like every remote session.
    /// </summary>
    public async Task<ServiceResult<WebInterfaceStart>> StartWebInterfaceAsync(Caller caller, Guid endpointId, RemoteSessionKind kind, string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!RemoteSessionRules.IsWebInterface(kind) || !caller.Has(RemoteSessionRules.Permission(kind)))
        {
            return ServiceResult<WebInterfaceStart>.Forbidden();
        }

        reason = ServiceSupport.Clean(reason);
        if (reason is { Length: > RemoteSessionRules.MaxReasonLength })
        {
            return ServiceResult<WebInterfaceStart>.Fail($"The reason can be at most {RemoteSessionRules.MaxReasonLength} characters.");
        }

        var target = await GetWebInterfaceTargetAsync(caller, endpointId, kind, cancellationToken);
        if (target is null)
        {
            return ServiceResult<WebInterfaceStart>.NotFound("endpoint");
        }

        if (target.Problem is not null)
        {
            return ServiceResult<WebInterfaceStart>.Fail(target.Problem);
        }

        if (!TryTakeRequest(caller.UserId))
        {
            return ServiceResult<WebInterfaceStart>.Fail("You opened many remote sessions in the last minute. Wait a minute and try again.");
        }

        var secret = RandomNumberGenerator.GetBytes(RemoteSessionRules.ClaimSecretBytes);
        var secretText = Base64Url(secret);
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.Create(caller.Scope);
        var clientId = await db.Endpoints.Where(e => e.Id == endpointId).Select(e => e.ClientId).SingleAsync(cancellationToken);
        var instance = await db.InstanceSettings.AsNoTracking().Select(i => i.WebBaseUrl).SingleAsync(cancellationToken);
        var claim = new RemoteSessionClaim
        {
            Id = Guid.NewGuid(), ClientId = clientId, EndpointId = endpointId, UserId = caller.UserId, Kind = kind,
            UserName = caller.Name.Length > 200 ? caller.Name[..200] : caller.Name, Reason = reason,
            SecretHash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(secretText)), CreatedAt = now, ExpiresAt = now + RemoteSessionRules.ClaimValidity
        };
        db.RemoteSessionClaims.Add(claim);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.RemoteSessionRequested, "RemoteSessionClaim", claim.Id.ToString(), clientId, new
        {
            target.Hostname, EndpointId = endpointId, ParticipantId = claim.Id, Kind = kind.ToString(), Reason = reason
        }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<WebInterfaceStart>.Ok(new WebInterfaceStart(claim.Id, ConnectLink(instance, claim.Id, secretText), claim.ExpiresAt));
    }

    /// <summary>fleeto-connect://open?instance=&lt;host&gt;&amp;claim=&lt;id&gt;.&lt;secret&gt;; the host is the instance's own web address.</summary>
    internal static string ConnectLink(string webBaseUrl, Guid claimId, string secret)
    {
        var host = Uri.TryCreate(webBaseUrl, UriKind.Absolute, out var uri) ? uri.Authority : webBaseUrl;
        return $"fleeto-connect://open?instance={Uri.EscapeDataString(host)}&claim={claimId:D}.{secret}";
    }

    /// <summary>Where the session of a claim stands; null when the caller may not see it.</summary>
    public async Task<WebInterfaceProgress?> GetWebInterfaceProgressAsync(Caller caller, Guid claimId, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.Create(caller.Scope);
        var claim = await db.RemoteSessionClaims.AsNoTracking().SingleOrDefaultAsync(c => c.Id == claimId && c.UserId == caller.UserId, cancellationToken);
        if (claim is null)
        {
            return null;
        }

        var participant = await db.RemoteSessionParticipants.AsNoTracking().SingleOrDefaultAsync(p => p.Id == claimId, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        if (participant is null)
        {
            return claim.ClaimedAt is not null
                ? new WebInterfaceProgress(claim.Problem is null ? WebInterfaceProgressState.Connecting : WebInterfaceProgressState.Failed, claim.Problem, claim.ClaimedBy, null)
                : claim.ExpiresAt <= now
                    ? new WebInterfaceProgress(WebInterfaceProgressState.NotClaimed, null, null, null)
                    : new WebInterfaceProgress(WebInterfaceProgressState.WaitingForConnect, null, null, null);
        }

        return participant.State switch
        {
            RemoteParticipantState.Connected => new WebInterfaceProgress(WebInterfaceProgressState.Connected, null, claim.ClaimedBy, participant.ConnectedAt),
            RemoteParticipantState.Ended when participant.ConnectedAt is not null =>
                new WebInterfaceProgress(WebInterfaceProgressState.Ended, participant.EndReason, claim.ClaimedBy, participant.ConnectedAt),
            RemoteParticipantState.Ended or RemoteParticipantState.Refused or RemoteParticipantState.Failed =>
                new WebInterfaceProgress(WebInterfaceProgressState.Failed, participant.EndReason ?? "The session did not start.", claim.ClaimedBy, null),
            _ => new WebInterfaceProgress(WebInterfaceProgressState.Connecting, null, claim.ClaimedBy, null)
        };
    }

    /// <summary>
    /// Ends a session of a web interface from its window: an unclaimed link stops working, and a connection is marked revoked,
    /// which the gateway ends at once (notification) or within a minute.
    /// </summary>
    public async Task EndWebInterfaceAsync(Caller caller, Guid claimId, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.Create(caller.Scope);
        var mine = await db.RemoteSessionClaims.AnyAsync(c => c.Id == claimId && c.UserId == caller.UserId, cancellationToken);
        if (!mine)
        {
            return;
        }

        await db.RemoteSessionClaims.Where(c => c.Id == claimId && c.ClaimedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, now), cancellationToken);
        var revoked = await db.RemoteSessionParticipants
            .Where(p => p.Id == claimId && p.UserId == caller.UserId && p.EndedAt == null && p.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RevokedAt, now), cancellationToken);
        if (revoked > 0)
        {
            try
            {
                await _bus.PublishAsync(NotificationChannels.UserAccess, caller.UserId.ToString(), cancellationToken);
            }
            catch (Exception ex)
            {
                // Marked revoked: the gateway ends it within a minute without the notification.
                _logger.LogWarning(ex, "Could not notify the gateway to end web interface session {ParticipantId}", claimId);
            }
        }
    }

    /// <summary>
    /// Takes a claim of Fleeto Connect: the link's secret, once, within <see cref="RemoteSessionRules.ClaimValidity"/>, from a Fleeto Connect
    /// that is new enough, with a usable key. The user of the claim is checked again as at the start, then the session goes to the signer.
    /// </summary>
    public async Task<ServiceResult<ConnectTicket>> ClaimWebInterfaceAsync(string claimText, byte[] publicKey, string? connectVersion, string? platform,
        string? address, CancellationToken cancellationToken = default)
    {
        var dot = claimText.IndexOf('.');
        if (dot <= 0 || !Guid.TryParseExact(claimText[..dot], "D", out var claimId) || claimText.Length - dot - 1 is < 40 or > 100)
        {
            return ServiceResult<ConnectTicket>.Fail(ClaimProblem);
        }

        var secretHash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(claimText[(dot + 1)..]));
        var now = _time.GetUtcNow().UtcDateTime;
        RemoteSessionClaim? claim;
        await using (var db = _dbFactory.CreateSystem())
        {
            claim = await db.RemoteSessionClaims.AsNoTracking().SingleOrDefaultAsync(c => c.Id == claimId, cancellationToken);
            if (claim is null || !CryptographicOperations.FixedTimeEquals(claim.SecretHash, secretHash) || claim.ClaimedAt is not null || claim.ExpiresAt <= now)
            {
                return ServiceResult<ConnectTicket>.Fail(ClaimProblem);
            }

            if (!RemoteSessionRules.ConnectVersionAccepted(connectVersion, FleetoVersion.Current, claim.Kind))
            {
                return ServiceResult<ConnectTicket>.Fail(
                    $"This Fleeto Connect ({DbText(connectVersion, 40) ?? "no version"}) is too old for the {RemoteSessionRules.WebInterfaceName(claim.Kind)}. Download the current one from the window of the web interface in Fleeto, install it and open the web interface again.");
            }

            if (!RemoteSessionRules.IsValidPublicKey(publicKey))
            {
                return ServiceResult<ConnectTicket>.Fail("Fleeto Connect sent an invalid session key. Open the web interface from Fleeto again.");
            }

            // Once: the claim that sets ClaimedAt wins, a second one at the same moment finds it set.
            var by = $"Fleeto Connect {DbText(connectVersion, 40)} ({DbText(platform, 30) ?? "unknown"})";
            var taken = await db.RemoteSessionClaims.Where(c => c.Id == claimId && c.ClaimedAt == null && c.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ClaimedAt, now).SetProperty(c => c.ClaimedFrom, DbText(address, 64))
                    .SetProperty(c => c.ClaimedBy, by.Length > 100 ? by[..100] : by), cancellationToken);
            if (taken == 0)
            {
                return ServiceResult<ConnectTicket>.Fail(ClaimProblem);
            }

            claim.ClaimedBy = by;
        }

        var result = await OpenClaimAsync(claim, publicKey, address, cancellationToken);
        if (!result.Success)
        {
            await using var db = _dbFactory.CreateSystem();
            var problem = result.Problem ?? ServiceSupport.GenericProblem;
            await db.RemoteSessionClaims.Where(c => c.Id == claimId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Problem, problem.Length > 500 ? problem[..500] : problem), cancellationToken);
        }

        return result;
    }

    private async Task<ServiceResult<ConnectTicket>> OpenClaimAsync(RemoteSessionClaim claim, byte[] publicKey, string? address, CancellationToken cancellationToken)
    {
        var caller = await CallerOfClaimAsync(claim, address, cancellationToken);
        if (caller is null)
        {
            return ServiceResult<ConnectTicket>.Fail(RemoteSessionTokenProblem);
        }

        var target = await GetWebInterfaceTargetAsync(caller, claim.EndpointId, claim.Kind, cancellationToken);
        if (target is null)
        {
            return ServiceResult<ConnectTicket>.Fail(RemoteSessionTokenProblem);
        }

        if (target.Problem is not null)
        {
            return ServiceResult<ConnectTicket>.Fail(target.Problem);
        }

        var opened = await OpenAsync(caller, claim.EndpointId, target.Hostname, claim.Kind, AgentComponent.Agent, null, publicKey, claim.Reason,
            cancellationToken, participantId: claim.Id, claimedAudit: new
            {
                target.Hostname, EndpointId = claim.EndpointId, ParticipantId = claim.Id, Kind = claim.Kind.ToString(), claim.ClaimedBy, Address = address
            });
        if (opened is not { Success: true, Value: { } ticket })
        {
            return ServiceResult<ConnectTicket>.Fail(opened.Problem ?? ServiceSupport.GenericProblem);
        }

        await using var db = _dbFactory.CreateSystem();
        var webBaseUrl = await db.InstanceSettings.AsNoTracking().Select(i => i.WebBaseUrl).SingleAsync(cancellationToken);
        return ServiceResult<ConnectTicket>.Ok(new ConnectTicket(ticket.ParticipantId, ticket.SessionId, ticket.Token, ticket.Signature, ticket.KeyId,
            ticket.CertificateFingerprints, RelayUrl(webBaseUrl, ticket.ParticipantId), target.Hostname, claim.EndpointId));
    }

    private const string RemoteSessionTokenProblem =
        "You no longer have access to this endpoint or to its web interface. Ask an admin for access.";

    /// <summary>The relay of the instance for Fleeto Connect: the configured relay (development) or wss://&lt;host&gt;/relay/v1/sessions/.</summary>
    internal string RelayUrl(string webBaseUrl, Guid participantId)
    {
        var configured = _configuration["Remote:RelayUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/') + "/" + participantId.ToString("D");
        }

        var host = Uri.TryCreate(webBaseUrl, UriKind.Absolute, out var uri) ? uri.Authority : webBaseUrl;
        return $"wss://{host}/relay/v1/sessions/{participantId:D}";
    }

    /// <summary>The user of a claim as a caller, from the database as for a signed-in user; null when it may no longer sign in or has no role.</summary>
    private async Task<Caller?> CallerOfClaimAsync(RemoteSessionClaim claim, string? address, CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow();
        var user = await db.Users.AsNoTracking().Where(u => u.Id == claim.UserId)
            .Select(u => new { u.Id, u.Email, u.LockoutEnd }).SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.LockoutEnd > now)
        {
            return null;
        }

        var (roles, granted) = await UserPermissions.LoadAsync(db, user.Id, cancellationToken);
        if (roles.Count == 0)
        {
            return null;
        }

        IClientScope scope = roles.Contains(FleetoRoles.Admin)
            ? SystemClientScope.Instance
            : await UserAccessResolver.ResolveAsync(db, user.Id, cancellationToken);
        return new Caller(user.Id, claim.UserName, user.Email ?? string.Empty, roles, scope, address, Granted: granted);
    }

    private static string? DbText(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
