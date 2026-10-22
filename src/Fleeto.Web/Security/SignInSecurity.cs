using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Security;

/// <summary>
/// What the security monitor needs from a sign-in (0.7.0): a failed attempt counted for its address, and the network of a
/// successful one remembered for its user, so the monitor can tell a sign-in from a new network and never blocks a network
/// a user signed in from. Recording a network never stops a sign-in: a failure is logged and the sign-in goes on.
/// </summary>
public sealed class SignInSecurity
{
    /// <summary>Longest User-Agent kept in the audit entry of a sign-in.</summary>
    public const int MaxUserAgentLength = 256;

    private readonly SecuritySignalRecorder _signals;
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<SignInSecurity> _logger;

    public SignInSecurity(SecuritySignalRecorder signals, IFleetoDbContextFactory dbFactory, TimeProvider time, ILogger<SignInSecurity> logger)
    {
        _signals = signals;
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
    }

    /// <summary>The User-Agent of the request, cut to <see cref="MaxUserAgentLength"/>, for the audit entry of a sign-in.</summary>
    public static string? UserAgent(HttpContext context)
    {
        var value = context.Request.Headers.UserAgent.ToString();
        return value.Length == 0 ? null : value.Length > MaxUserAgentLength ? value[..MaxUserAgentLength] : value;
    }

    public void Failed(HttpContext context, SecuritySignalKind kind = SecuritySignalKind.SignInFailed) =>
        _signals.Record(kind, context.Connection.RemoteIpAddress);

    /// <summary>Remembers the network of a successful sign-in: added with its first time, or its last time moved forward.</summary>
    public async Task SucceededAsync(Guid userId, HttpContext context, CancellationToken cancellationToken = default)
    {
        if (context.Connection.RemoteIpAddress is not { } address)
        {
            return;
        }

        var network = IpNetworks.Format(IpNetworks.SignInNetwork(address));
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            await using var db = _dbFactory.CreateSystem();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "UserSignInNetworks" ("UserId", "Network", "FirstSeen", "LastSeen") VALUES ({userId}, {network}, {now}, {now})
                ON CONFLICT ("UserId", "Network") DO UPDATE SET "LastSeen" = GREATEST("UserSignInNetworks"."LastSeen", EXCLUDED."LastSeen")
                """, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "The network of a sign-in could not be recorded for user {UserId}", userId);
        }
    }
}
