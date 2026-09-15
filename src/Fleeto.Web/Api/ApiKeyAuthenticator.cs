using System.Security.Cryptography;
using System.Text;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Security;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Api;

/// <summary>The result of checking an API key: the caller to run the request as, or the problem to answer with.</summary>
public sealed record ApiAuthentication(Caller? Caller, string? Problem)
{
    public static ApiAuthentication Fail(string problem) => new(null, problem);
}

/// <summary>
/// Checks the <c>Authorization: Bearer flt_&lt;id&gt;_&lt;secret&gt;</c> header of a public API call (ARCHITECTURE.md §4, API call). The key is
/// read from the database on every call, so a revoked key stops working on its next call. Only an <c>Authorization</c> header counts:
/// a session cookie never authenticates an API call, so a signed-in browser cannot be used for cross-site requests.
/// </summary>
public sealed class ApiKeyAuthenticator
{
    public const string MissingKeyProblem = "The request has no API key.";
    public const string InvalidKeyProblem = "The API key is not valid.";
    public const string NotUsableKeyProblem = "The API key was revoked or has expired.";

    /// <summary>How often the last use of a key is written at most; every call is in the audit log anyway.</summary>
    private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(1);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<ApiKeyAuthenticator> _logger;

    public ApiKeyAuthenticator(IFleetoDbContextFactory dbFactory, IAuditLog audit, TimeProvider time, ILogger<ApiKeyAuthenticator> logger)
    {
        _dbFactory = dbFactory;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    public async Task<ApiAuthentication> AuthenticateAsync(string? authorization, string? ipAddress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return ApiAuthentication.Fail(MissingKeyProblem);
        }

        const string scheme = "Bearer ";
        if (!authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ||
            !OpaqueTokens.TryParse(authorization[scheme.Length..], OpaqueTokens.ApiKeyPrefix, out var keyId, out var secretHash))
        {
            return ApiAuthentication.Fail(InvalidKeyProblem);
        }

        await using var db = _dbFactory.CreateSystem();
        var key = await db.ApiKeys.AsNoTracking().Include(k => k.Clients).SingleOrDefaultAsync(k => k.Id == keyId, cancellationToken);
        if (key is null)
        {
            return ApiAuthentication.Fail(InvalidKeyProblem);
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key.SecretHash), Encoding.ASCII.GetBytes(secretHash)))
        {
            // Someone knows the id of a real key but not its secret: worth an audit entry. The address rate limit caps how many.
            _logger.LogWarning("Refused a public API call with a wrong secret for API key {ApiKeyId} from {IpAddress}", key.Id, ipAddress);
            await _audit.WriteAsync(new AuditRecord(AuditActions.ApiKeyAuthenticationFailed, "ApiKey", key.Id.ToString(), null, AuditActorType.ApiKey,
                key.Id.ToString(), key.Name, new { Reason = "wrong_secret" }, ipAddress), cancellationToken);
            return ApiAuthentication.Fail(InvalidKeyProblem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (!key.IsUsable(now))
        {
            return ApiAuthentication.Fail(NotUsableKeyProblem);
        }

        var threshold = now - LastUsedPrecision;
        await db.ApiKeys.Where(k => k.Id == key.Id && (k.LastUsedAt == null || k.LastUsedAt < threshold))
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);

        IClientScope scope = key.AllClients ? SystemClientScope.Instance : new RestrictedClientScope(key.Clients.Select(c => c.ClientId));
        // An API key reads like the read-only role: every service check that holds for a read-only user holds for a key.
        return new ApiAuthentication(new Caller(key.Id, key.Name, string.Empty, [FleetoRoles.ReadOnly], scope, ipAddress, AuditActorType.ApiKey), null);
    }
}
