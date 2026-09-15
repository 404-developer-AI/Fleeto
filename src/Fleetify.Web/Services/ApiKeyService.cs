using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Security;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Web.Services;

public enum ApiKeyState
{
    Active,
    Expired,
    Revoked
}

public sealed record ApiKeyView(Guid Id, string Name, string DisplayPrefix, bool AllClients, IReadOnlyList<ClientOption> Clients, string CreatedByName,
    DateTime CreatedAt, DateTime? ExpiresAt, DateTime? LastUsedAt, DateTime? RevokedAt, string? RevokedByName, ApiKeyState State);

/// <param name="Validity">Null: the key does not expire.</param>
/// <param name="ClientIds">The clients the key may read when <paramref name="AllClients"/> is false.</param>
public sealed record ApiKeyInput(string? Name, bool AllClients, IReadOnlyCollection<Guid> ClientIds, TimeSpan? Validity);

/// <param name="Token">The whole key. Shown once and never stored.</param>
public sealed record ApiKeyCreated(Guid Id, string Token);

/// <summary>
/// API keys for the public REST API (0.2.1), for admins: create, list and revoke. A key is read-only, optionally limited to clients
/// and optionally expiring. The token is returned once at creation; only the SHA-256 of its secret is stored, and neither the token
/// nor the hash reaches the audit log.
/// </summary>
public sealed class ApiKeyService
{
    public const int MaxClients = 1000;

    public static readonly IReadOnlyList<(TimeSpan? Validity, string Label)> Validities =
    [
        (TimeSpan.FromDays(30), "30 days"),
        (TimeSpan.FromDays(90), "90 days"),
        (TimeSpan.FromDays(365), "1 year"),
        (null, "No expiry")
    ];

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public ApiKeyService(IFleetifyDbContextFactory dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    /// <summary>What an admin sees of a key id: enough to tell keys apart and to match a key in a log, never the secret.</summary>
    public static string DisplayPrefix(Guid id) => $"{OpaqueTokens.ApiKeyPrefix}_{id:N}"[..12] + "…";

    public async Task<IReadOnlyList<ApiKeyView>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        var keys = await db.ApiKeys.AsNoTracking().Include(k => k.Clients)
            .OrderBy(k => k.RevokedAt != null).ThenByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);
        var clientIds = keys.SelectMany(k => k.Clients).Select(c => c.ClientId).Distinct().ToList();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id))
            .Select(c => new ClientOption(c.Id, c.Code, c.Name)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;

        return keys.Select(k => new ApiKeyView(k.Id, k.Name, DisplayPrefix(k.Id), k.AllClients,
                k.Clients.Where(c => clients.ContainsKey(c.ClientId)).Select(c => clients[c.ClientId]).OrderBy(c => c.Code).ToList(),
                k.CreatedByName, k.CreatedAt, k.ExpiresAt, k.LastUsedAt, k.RevokedAt, k.RevokedByName,
                k.RevokedAt is not null ? ApiKeyState.Revoked : k.IsUsable(now) ? ApiKeyState.Active : ApiKeyState.Expired))
            .ToList();
    }

    public async Task<ServiceResult<ApiKeyCreated>> CreateAsync(Caller caller, ApiKeyInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<ApiKeyCreated>.Forbidden();
        }

        var name = ServiceSupport.Clean(input.Name);
        if (name is null)
        {
            return ServiceResult<ApiKeyCreated>.Fail("Enter a name that says where the key is used, for example \"PSA sync\".");
        }

        if (name.Length > ApiKey.MaxNameLength)
        {
            return ServiceResult<ApiKeyCreated>.Fail($"The name can be at most {ApiKey.MaxNameLength} characters.");
        }

        if (Validities.All(v => v.Validity != input.Validity))
        {
            return ServiceResult<ApiKeyCreated>.Fail("Choose an expiry of 30 days, 90 days, 1 year or no expiry.");
        }

        var clientIds = input.AllClients ? new List<Guid>() : input.ClientIds.Distinct().ToList();
        if (!input.AllClients && clientIds.Count == 0)
        {
            return ServiceResult<ApiKeyCreated>.Fail("Choose at least one client, or give the key access to all clients.");
        }

        if (clientIds.Count > MaxClients)
        {
            return ServiceResult<ApiKeyCreated>.Fail($"A key can be limited to at most {MaxClients} clients. Give it access to all clients instead.");
        }

        await using var db = _dbFactory.CreateSystem();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).Select(c => new { c.Id, c.Code }).ToListAsync(cancellationToken);
        if (clients.Count != clientIds.Count)
        {
            return ServiceResult<ApiKeyCreated>.Fail("One of the chosen clients no longer exists. Close the dialog and try again.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var (token, id, secretHash) = OpaqueTokens.Create(OpaqueTokens.ApiKeyPrefix);
        var key = new ApiKey
        {
            Id = id,
            Name = name,
            SecretHash = secretHash,
            AllClients = input.AllClients,
            Clients = clientIds.Select(c => new ApiKeyClient { ApiKeyId = id, ClientId = c }).ToList(),
            CreatedByUserId = caller.UserId,
            CreatedByName = caller.Name.Length <= 200 ? caller.Name : caller.Name[..200],
            CreatedAt = now,
            ExpiresAt = input.Validity is { } validity ? now + validity : null
        };
        db.ApiKeys.Add(key);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ApiKeyCreated, "ApiKey", id.ToString(), null, new
        {
            key.Name,
            key.AllClients,
            Clients = clients.Select(c => c.Code).OrderBy(c => c).ToList(),
            key.ExpiresAt
        }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ApiKeyCreated>.Ok(new ApiKeyCreated(id, token));
    }

    /// <summary>Revokes a key. It stops working on the next call; a revoked key cannot be restored.</summary>
    public async Task<ServiceResult> RevokeAsync(Caller caller, Guid keyId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == keyId, cancellationToken);
        if (key is null)
        {
            return ServiceResult.NotFound("API key");
        }

        if (key.RevokedAt is not null)
        {
            return ServiceResult.Fail("This API key is already revoked.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        key.RevokedAt = now;
        key.RevokedByUserId = caller.UserId;
        key.RevokedByName = caller.Name.Length <= 200 ? caller.Name : caller.Name[..200];
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ApiKeyRevoked, "ApiKey", key.Id.ToString(), null, new { key.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }
}
