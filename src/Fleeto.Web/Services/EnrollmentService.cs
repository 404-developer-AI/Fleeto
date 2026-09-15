using System.Text.RegularExpressions;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Security;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;
using AuditActions = Fleeto.Core.Interfaces.AuditActions;

namespace Fleeto.Web.Services;

public sealed record EnrollmentTokenListItem(Guid Id, string Name, DateTime CreatedAt, DateTime ExpiresAt, int? MaxUses, int UseCount,
    DateTime? RevokedAt, string Status, string? EndpointHostname = null);

/// <summary>A token that was just created. The plaintext exists only in this object and on the screen, once.</summary>
public sealed record CreatedEnrollmentToken(Guid Id, string Token, string? InstallCommand, string? Problem);

/// <summary>Enrollment tokens for a site and the one-line install command that carries one.</summary>
public sealed class EnrollmentService
{
    /// <summary>Lifetimes offered in the UI. Anything else is refused.</summary>
    public static readonly IReadOnlyList<(TimeSpan Lifetime, string Label)> Lifetimes =
    [
        (TimeSpan.FromHours(1), "1 hour"),
        (TimeSpan.FromDays(1), "1 day"),
        (TimeSpan.FromDays(7), "7 days"),
        (TimeSpan.FromDays(30), "30 days")
    ];

    public const int MaxUsesLimit = 10000;

    /// <summary>Lifetime of an "enroll again" token: single use, for one reinstall.</summary>
    public static readonly TimeSpan EnrollAgainLifetime = TimeSpan.FromDays(1);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<EnrollmentService> _logger;

    public EnrollmentService(IFleetoDbContextFactory dbFactory, TimeProvider time, ILogger<EnrollmentService> logger)
    {
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EnrollmentTokenListItem>> ListAsync(Caller caller, Guid siteId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime;
        var tokens = await db.EnrollmentTokens.AsNoTracking()
            .Where(t => t.SiteId == siteId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(200)
            .Select(t => new { Token = t, Hostname = db.Endpoints.Where(e => e.Id == t.EndpointId).Select(e => e.Hostname).FirstOrDefault() })
            .ToListAsync(cancellationToken);
        return tokens.Select(x => new EnrollmentTokenListItem(x.Token.Id, x.Token.Name, x.Token.CreatedAt, x.Token.ExpiresAt, x.Token.MaxUses,
            x.Token.UseCount, x.Token.RevokedAt,
            x.Token.RevokedAt is not null ? "Revoked"
            : x.Token.ExpiresAt <= now ? "Expired"
            : x.Token.MaxUses is not null && x.Token.UseCount >= x.Token.MaxUses ? "Used"
            : "Active", x.Hostname)).ToList();
    }

    /// <summary>
    /// Creates a token and returns its plaintext with the install command. Only the SHA-256 of the secret is stored; the
    /// token is never logged or written to the audit trail.
    /// </summary>
    /// <param name="maxUses">1 for single use, null for unlimited until expiry, or a number.</param>
    public async Task<ServiceResult<CreatedEnrollmentToken>> CreateAsync(Caller caller, Guid siteId, string? name, TimeSpan lifetime, int? maxUses,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<CreatedEnrollmentToken>.Forbidden();
        }

        var cleanName = ServiceSupport.Clean(name);
        if (cleanName is null || cleanName.Length > 100)
        {
            return ServiceResult<CreatedEnrollmentToken>.Fail("Enter a token name of at most 100 characters, for example the person or batch it is for.");
        }

        if (Lifetimes.All(l => l.Lifetime != lifetime))
        {
            return ServiceResult<CreatedEnrollmentToken>.Fail("Choose an expiry of 1 hour, 1 day, 7 days or 30 days.");
        }

        if (maxUses is < 1 or > MaxUsesLimit)
        {
            return ServiceResult<CreatedEnrollmentToken>.Fail($"The number of uses must be between 1 and {MaxUsesLimit}.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var site = await db.Sites.AsNoTracking().Where(s => s.Id == siteId).Select(s => new { s.Id, s.ClientId, s.Name }).SingleOrDefaultAsync(cancellationToken);
        if (site is null)
        {
            return ServiceResult<CreatedEnrollmentToken>.NotFound("site");
        }

        return await CreateTokenAsync(db, caller, site.ClientId, site.Id, site.Name, null, cleanName, lifetime, maxUses, cancellationToken);
    }

    /// <summary>
    /// Creates a single-use "enroll again" token for an existing endpoint (0.2.0): the agent installed with it takes over this
    /// endpoint, with its checks, alerts, notes and history, and the endpoint's earlier certificates are revoked when it enrolls.
    /// For an agent that was offline longer than certificate recovery allows, or a reinstalled machine.
    /// </summary>
    public async Task<ServiceResult<CreatedEnrollmentToken>> CreateForEndpointAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<CreatedEnrollmentToken>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new { e.Id, e.ClientId, e.SiteId, e.Hostname, e.Source, SiteName = e.Site!.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult<CreatedEnrollmentToken>.NotFound("endpoint");
        }

        if (endpoint.Source != EndpointSource.Agent)
        {
            return ServiceResult<CreatedEnrollmentToken>.Fail("This endpoint has no agent. Only endpoints with an agent can be enrolled again.");
        }

        return await CreateTokenAsync(db, caller, endpoint.ClientId, endpoint.SiteId, endpoint.SiteName, endpoint.Id,
            $"Enroll {endpoint.Hostname} again", EnrollAgainLifetime, 1, cancellationToken);
    }

    private async Task<ServiceResult<CreatedEnrollmentToken>> CreateTokenAsync(FleetoDbContext db, Caller caller, Guid clientId, Guid siteId,
        string siteName, Guid? endpointId, string name, TimeSpan lifetime, int? maxUses, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var (token, id, hash) = OpaqueTokens.Create(OpaqueTokens.EnrollmentPrefix);
        var row = new EnrollmentToken
        {
            Id = id,
            ClientId = clientId,
            SiteId = siteId,
            EndpointId = endpointId,
            Name = name.Length > 100 ? name[..100] : name,
            TokenHash = hash,
            ExpiresAt = now + lifetime,
            MaxUses = maxUses,
            CreatedByUserId = caller.UserId,
            CreatedAt = now
        };
        db.EnrollmentTokens.Add(row);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.EnrollmentTokenCreated, "EnrollmentToken", id.ToString(), clientId,
            new { SiteId = siteId, Site = siteName, row.Name, row.ExpiresAt, row.MaxUses, EndpointId = endpointId }), now));
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Enrollment token {TokenId} created for site {SiteId} (endpoint {EndpointId}) by {UserId}", id, siteId, endpointId, caller.UserId);

        var instance = await db.InstanceSettings.AsNoTracking()
            .Select(i => new { i.WebBaseUrl, i.AgentHostName, i.AgentPort })
            .SingleOrDefaultAsync(cancellationToken);
        // Project public columns only: the web role cannot read the encrypted CA key.
        var fingerprint = await db.CertificateAuthorities.AsNoTracking()
            .Where(c => c.RetiredAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.Fingerprint)
            .FirstOrDefaultAsync(cancellationToken);

        if (instance is null)
        {
            return ServiceResult<CreatedEnrollmentToken>.Ok(new CreatedEnrollmentToken(id, token, null,
                "The instance settings are missing, so no install command can be built. Run fleeto-tool migrate for this instance."));
        }

        if (fingerprint is null)
        {
            return ServiceResult<CreatedEnrollmentToken>.Ok(new CreatedEnrollmentToken(id, token, null,
                "The instance certificate authority does not exist yet because the signer has not started. Start fleeto-signer, then create a new token."));
        }

        return ServiceResult<CreatedEnrollmentToken>.Ok(new CreatedEnrollmentToken(id, token,
            InstallCommand.Build(instance.WebBaseUrl, instance.AgentHostName, instance.AgentPort, token, fingerprint), null));
    }

    public async Task<ServiceResult> RevokeAsync(Caller caller, Guid tokenId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var token = await db.EnrollmentTokens.SingleOrDefaultAsync(t => t.Id == tokenId, cancellationToken);
        if (token is null)
        {
            return ServiceResult.NotFound("enrollment token");
        }

        if (token.RevokedAt is not null)
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        token.RevokedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.EnrollmentTokenRevoked, "EnrollmentToken", token.Id.ToString(), token.ClientId,
            new { token.SiteId, token.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }
}

/// <summary>
/// Builds the one-line PowerShell install command shown once after a token is created. It downloads the agent from this
/// instance and installs it with the gateway address, the token and the SHA-256 fingerprint of the instance CA.
/// <para>
/// The -Command argument is single-quoted (inner quotes doubled) so the elevated PowerShell the command is pasted into
/// does not expand <c>$ErrorActionPreference</c> or <c>$p</c> before the child process sees them; a double-quoted
/// argument would be broken in both Windows PowerShell 5.1 and PowerShell 7.
/// </para>
/// </summary>
public static partial class InstallCommand
{
    public static string Build(string webBaseUrl, string agentHostName, int agentPort, string token, string caFingerprint)
    {
        if (!Uri.TryCreate(webBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("The web base URL is not an absolute http(s) URL.", nameof(webBaseUrl));
        }

        if (!HostNamePattern().IsMatch(agentHostName))
        {
            throw new ArgumentException("The agent host name contains characters that are not allowed.", nameof(agentHostName));
        }

        if (agentPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(agentPort));
        }

        if (!TokenPattern().IsMatch(token))
        {
            throw new ArgumentException("The token has an unexpected format.", nameof(token));
        }

        if (!FingerprintPattern().IsMatch(caFingerprint))
        {
            throw new ArgumentException("The CA fingerprint must be 64 hexadecimal characters.", nameof(caFingerprint));
        }

        var downloadUrl = webBaseUrl.TrimEnd('/') + "/agent/download/windows-amd64";
        var inner =
            "$ErrorActionPreference=" + Quote("Stop") + "; " +
            "$p=Join-Path $env:TEMP " + Quote("fleeto-agent.exe") + "; " +
            "Invoke-WebRequest -UseBasicParsing " + Quote(downloadUrl) + " -OutFile $p; " +
            "& $p install --server " + Quote($"{agentHostName}:{agentPort}") +
            " --token " + Quote(token) +
            " --ca-fingerprint " + Quote(caFingerprint.ToLowerInvariant());

        return "powershell -NoProfile -ExecutionPolicy Bypass -Command '" + inner.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>A PowerShell single-quoted string literal.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^[A-Za-z0-9.-]{1,255}$")]
    private static partial Regex HostNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{10,200}$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex FingerprintPattern();
}
