using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>One client mapped to a tenant of the integration, for the mapping list.</summary>
public sealed record IntegrationMappingView(Guid Id, Guid ClientId, string ClientCode, string ClientName, string TenantId, string TenantName);

/// <summary>A client of this instance, for the mapping choice.</summary>
public sealed record IntegrationClient(Guid Id, string Code, string Name);

/// <summary>
/// What Settings shows about the Action1 integration. The credentials themselves are never part of it: only which client
/// id is in use and whether a secret is stored.
/// </summary>
public sealed record IntegrationView(Guid Id, IntegrationType Type, bool Enabled, Action1Region Region, string CredentialName,
    IntegrationStatus Status, string? StatusMessage, DateTime? LastAttemptAt, DateTime? LastSuccessAt,
    IReadOnlyList<IntegrationMappingView> Mappings, DateTime UpdatedAt);

/// <param name="ClientSecret">Blank when editing keeps the stored secret.</param>
public sealed record Action1Input(string? ClientId, string? ClientSecret, Action1Region Region, bool Enabled);

/// <summary>
/// The Action1 integration for admins (0.4.0): one enterprise per instance, its credentials write-only (stored encrypted
/// and bound to the row, never returned), a connection test, and the mapping of Action1 organizations to clients. An
/// organization maps to exactly one client and a client to at most one organization, so patch state can never land under
/// another client.
/// </summary>
public sealed class IntegrationService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly ISecretProtector _protector;
    private readonly Action1ClientFactory _clients;
    private readonly TimeProvider _time;

    public IntegrationService(IFleetoDbContextFactory dbFactory, ISecretProtector protector, Action1ClientFactory clients, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _clients = clients;
        _time = time;
    }

    /// <summary>The Action1 integration, or null while none is configured.</summary>
    public async Task<IntegrationView?> GetAction1Async(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        var integration = await db.Integrations.AsNoTracking().Include(i => i.Mappings)
            .SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        if (integration is null)
        {
            return null;
        }

        var clientIds = integration.Mappings.Select(m => m.ClientId).ToList();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id))
            .Select(c => new IntegrationClient(c.Id, c.Code, c.Name)).ToDictionaryAsync(c => c.Id, cancellationToken);

        return ToView(integration, clients);
    }

    /// <summary>Every client of the instance, for the mapping choice.</summary>
    public async Task<IReadOnlyList<IntegrationClient>> ListClientsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        return await db.Clients.AsNoTracking().OrderBy(c => c.Code)
            .Select(c => new IntegrationClient(c.Id, c.Code, c.Name)).ToListAsync(cancellationToken);
    }

    /// <summary>Stores the Action1 credentials. A blank secret keeps the one that is stored.</summary>
    public async Task<ServiceResult> SaveAction1Async(Caller caller, Action1Input input, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        var clientId = (input.ClientId ?? string.Empty).Trim();
        if (clientId.Length is 0 or > 200)
        {
            return ServiceResult.Fail("Enter the Action1 client id of the API credentials. It is shown in the Action1 console under Configuration, Users & API Credentials.");
        }

        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow().UtcDateTime;
        var integration = await db.Integrations.Include(i => i.Mappings)
            .SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        var creating = integration is null;
        if (integration is null)
        {
            integration = new Integration { Id = Guid.NewGuid(), Type = IntegrationType.Action1, CreatedAt = now };
            db.Integrations.Add(integration);
        }

        var secret = (input.ClientSecret ?? string.Empty).Trim();
        var secretChanged = secret.Length > 0;
        if (secret.Length == 0)
        {
            if (creating || string.IsNullOrEmpty(integration.EncryptedCredentials))
            {
                return ServiceResult.Fail("Enter the Action1 client secret. It is shown once when the API credentials are created in the Action1 console.");
            }

            // Keep the stored secret, but let the client id and the region change.
            secret = IntegrationCredentials.Unprotect(_protector, integration.Id, integration.EncryptedCredentials).ClientSecret;
        }
        else if (secret.Length > 500)
        {
            return ServiceResult.Fail("That client secret is longer than Action1 issues. Copy it again from the Action1 console.");
        }

        var regionChanged = integration.Region != input.Region;
        integration.Region = input.Region;
        integration.CredentialName = clientId;
        integration.EncryptedCredentials = IntegrationCredentials.Protect(_protector, integration.Id, new Action1Credentials(clientId, secret));
        integration.Enabled = input.Enabled;
        integration.UpdatedAt = now;
        if (secretChanged || regionChanged || creating)
        {
            // The connection has to prove itself again before anything says it works.
            integration.Status = IntegrationStatus.Unknown;
            integration.StatusMessage = null;
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(secretChanged ? AuditActions.CredentialChanged : AuditActions.IntegrationChanged,
            "Integration", integration.Id.ToString(), null, new
            {
                Change = creating ? "created" : "updated",
                Type = IntegrationType.Action1.ToString(),
                Region = input.Region.ToString(),
                CredentialName = clientId,
                SecretChanged = secretChanged,
                integration.Enabled
            }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Removes the integration with its mappings. The patch state Fleeto stored stays until its own cleanup.</summary>
    public async Task<ServiceResult> RemoveAction1Async(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var integration = await db.Integrations.SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        if (integration is null)
        {
            return ServiceResult.NotFound("Action1 integration");
        }

        db.Integrations.Remove(integration);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IntegrationRemoved, "Integration", integration.Id.ToString(), null, new
        {
            Type = IntegrationType.Action1.ToString(),
            integration.CredentialName
        }), _time.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Contacts Action1 with the stored credentials and records what came back.</summary>
    public async Task<ServiceResult> TestAction1Async(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var integration = await db.Integrations.SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        if (integration is null)
        {
            return ServiceResult.NotFound("Action1 integration");
        }

        using var client = _clients.TryCreate(integration);
        if (client is null)
        {
            return ServiceResult.Fail("The stored Action1 credentials could not be read. Enter the client id and client secret again.");
        }

        var result = await client.TestConnectionAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        integration.LastAttemptAt = now;
        integration.Status = result.Ok ? IntegrationStatus.Ok : IntegrationStatus.Failing;
        integration.StatusMessage = result.Ok ? null : Trim(result.Message);
        if (result.Ok)
        {
            integration.LastSuccessAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return result.Ok ? ServiceResult.Ok() : ServiceResult.Fail(result.Message);
    }

    /// <summary>The organizations of the Action1 enterprise, for the mapping choice.</summary>
    public async Task<ServiceResult<IReadOnlyList<ExternalTenant>>> ListAction1OrganizationsAsync(Caller caller,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<IReadOnlyList<ExternalTenant>>.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var integration = await db.Integrations.SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        if (integration is null)
        {
            return ServiceResult<IReadOnlyList<ExternalTenant>>.NotFound("Action1 integration");
        }

        using var client = _clients.TryCreate(integration);
        if (client is null)
        {
            return ServiceResult<IReadOnlyList<ExternalTenant>>.Fail(
                "The stored Action1 credentials could not be read. Enter the client id and client secret again.");
        }

        var result = await client.ListTenantsAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        integration.LastAttemptAt = now;
        integration.Status = result.Ok ? IntegrationStatus.Ok : IntegrationStatus.Failing;
        integration.StatusMessage = result.Ok ? null : Trim(result.Message);
        if (result.Ok)
        {
            integration.LastSuccessAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return result.Ok
            ? ServiceResult<IReadOnlyList<ExternalTenant>>.Ok(result.Value ?? [])
            : ServiceResult<IReadOnlyList<ExternalTenant>>.Fail(result.Message);
    }

    /// <summary>Maps one Action1 organization to one client, replacing what that client was mapped to.</summary>
    public async Task<ServiceResult> SaveMappingAsync(Caller caller, Guid clientId, string tenantId, string tenantName,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        tenantId = (tenantId ?? string.Empty).Trim();
        if (tenantId.Length is 0 or > 200)
        {
            return ServiceResult.Fail("Choose the Action1 organization to map to this client.");
        }

        await using var db = _dbFactory.CreateSystem();
        var integration = await db.Integrations.Include(i => i.Mappings)
            .SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        if (integration is null)
        {
            return ServiceResult.NotFound("Action1 integration");
        }

        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken);
        if (client is null)
        {
            return ServiceResult.NotFound("client");
        }

        if (integration.Mappings.FirstOrDefault(m => m.ExternalTenantId == tenantId && m.ClientId != clientId) is { } taken)
        {
            var other = await db.Clients.AsNoTracking().Where(c => c.Id == taken.ClientId).Select(c => c.Code).FirstOrDefaultAsync(cancellationToken);
            return ServiceResult.Fail($"That Action1 organization is already mapped to client {other}. Remove that mapping first.");
        }

        var mapping = integration.Mappings.FirstOrDefault(m => m.ClientId == clientId);
        if (mapping is null)
        {
            mapping = new IntegrationMapping
            {
                Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = clientId, CreatedAt = _time.GetUtcNow().UtcDateTime
            };
            // Added to the set, not only to the collection: the id is set here, and EF reads a set key on a row it meets
            // through a navigation as a row that already exists.
            db.IntegrationMappings.Add(mapping);
            integration.Mappings.Add(mapping);
        }

        mapping.ExternalTenantId = tenantId;
        mapping.ExternalTenantName = Trim(tenantName ?? string.Empty, 200) ?? string.Empty;
        integration.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IntegrationMappingChanged, "Integration", integration.Id.ToString(),
            clientId, new { Type = IntegrationType.Action1.ToString(), client.Code, TenantId = tenantId, TenantName = mapping.ExternalTenantName }),
            integration.UpdatedAt));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Removes one mapping. The client keeps its endpoints; they simply have no patch state any more.</summary>
    public async Task<ServiceResult> RemoveMappingAsync(Caller caller, Guid mappingId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var mapping = await db.IntegrationMappings.SingleOrDefaultAsync(m => m.Id == mappingId, cancellationToken);
        if (mapping is null)
        {
            return ServiceResult.NotFound("mapping");
        }

        db.IntegrationMappings.Remove(mapping);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IntegrationMappingChanged, "Integration", mapping.IntegrationId.ToString(),
            mapping.ClientId, new { Change = "removed", TenantId = mapping.ExternalTenantId, mapping.ExternalTenantName }),
            _time.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private static IntegrationView ToView(Integration integration, IReadOnlyDictionary<Guid, IntegrationClient> clients) =>
        new(integration.Id, integration.Type, integration.Enabled, integration.Region ?? Action1Region.Europe, integration.CredentialName,
            integration.Status, integration.StatusMessage, integration.LastAttemptAt, integration.LastSuccessAt,
            integration.Mappings
                .Select(m => new IntegrationMappingView(m.Id, m.ClientId,
                    clients.TryGetValue(m.ClientId, out var c) ? c.Code : string.Empty,
                    clients.TryGetValue(m.ClientId, out var name) ? name.Name : string.Empty,
                    m.ExternalTenantId, m.ExternalTenantName))
                .OrderBy(m => m.ClientCode).ToList(),
            integration.UpdatedAt);

    private static string? Trim(string? value, int max = 1000) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
