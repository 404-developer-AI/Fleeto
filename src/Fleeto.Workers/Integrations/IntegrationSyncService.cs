using System.Text.Json;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Workers.Common;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Talks to the external products of the instance (0.4.0). Every call to Action1 is made here and nowhere else: only the
/// workers are on the egress network, web cannot reach the internet at all (see <c>deploy/compose/compose.yml</c>). Web
/// asks by setting <see cref="Integration.SyncRequestedAt"/> and notifying <c>fleeto_integrations</c>; this service runs
/// the call and writes the result and the tenants back, which web shows.
/// <para>
/// It also refreshes the tenants on its own every <see cref="RefreshInterval"/>, so the names an admin maps stay current
/// without anyone pressing a button. A product that keeps failing is left alone by the circuit breaker until its pause
/// has passed; a request an admin made is always attempted, so the button never silently does nothing.
/// </para>
/// </summary>
public sealed class IntegrationSyncService : WorkerLoop
{
    /// <summary>How often the tenants are read again without anybody asking.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(4);

    /// <summary>How often the link to the product's own agent installer is read again per tenant (0.4.0 step 3).</summary>
    public static readonly TimeSpan AgentInstallerInterval = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly Action1ClientFactory _clients;
    private readonly CircuitBreaker _breaker;
    private readonly ILogger<IntegrationSyncService> _logger;
    private IDisposable? _subscription;

    public IntegrationSyncService(IFleetoDbContextFactory dbFactory, INotificationBus bus, Action1ClientFactory clients,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<IntegrationSyncService> logger)
        : base("integrations", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _clients = clients;
        _logger = logger;
        _breaker = new CircuitBreaker(failureThreshold: 3, pause: TimeSpan.FromMinutes(5), time);
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.Integrations, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await SyncAsync(cancellationToken);
        return false;
    }

    /// <summary>Runs the work of one pass: every integration that was asked for, or whose tenants are stale.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var integrations = await db.Integrations.ToListAsync(cancellationToken);
        var now = Time.GetUtcNow().UtcDateTime;

        foreach (var integration in integrations)
        {
            var requested = integration.SyncRequestedAt is not null;
            var stale = integration.Enabled && (integration.TenantsUpdatedAt is null || now - integration.TenantsUpdatedAt >= RefreshInterval);
            if (!requested && !stale)
            {
                continue;
            }

            if (!integration.Enabled && !requested)
            {
                continue;
            }

            // A refresh nobody asked for waits while the breaker is open; a request from an admin is always attempted, so
            // the connection test in Settings answers even when the product has been failing.
            if (!requested && _breaker.IsOpen)
            {
                continue;
            }

            await SyncOneAsync(db, integration, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SyncOneAsync(FleetoDbContext db, Integration integration, CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        integration.SyncRequestedAt = null;
        integration.LastAttemptAt = now;

        using var client = _clients.TryCreate(integration);
        if (client is null)
        {
            integration.Status = IntegrationStatus.Failing;
            integration.StatusMessage = "The stored credentials could not be read. Enter the client id and client secret again.";
            return;
        }

        IntegrationResult<IReadOnlyList<ExternalTenant>> result;
        try
        {
            result = await client.ListTenantsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reading the tenants of {Type} failed", integration.Type);
            _breaker.RecordFailure();
            integration.Status = IntegrationStatus.Failing;
            integration.StatusMessage = "Fleeto could not reach the product. It tries again.";
            return;
        }

        if (!result.Ok)
        {
            if (_breaker.RecordFailure())
            {
                _logger.LogWarning("{Type} failed repeatedly; pausing until {OpenUntil}", integration.Type, _breaker.OpenUntil);
            }

            integration.Status = IntegrationStatus.Failing;
            integration.StatusMessage = Trim(result.Message);
            return;
        }

        _breaker.RecordSuccess();
        var tenants = result.Value ?? [];
        integration.Status = IntegrationStatus.Ok;
        integration.StatusMessage = null;
        integration.LastSuccessAt = now;
        integration.TenantsUpdatedAt = now;
        integration.TenantsJson = JsonSerializer.Serialize(tenants, Json);

        // Names change in the product; the mapping follows them so an admin sees what they picked.
        var byId = tenants.ToDictionary(t => t.Id, t => t.Name);
        foreach (var mapping in await db.IntegrationMappings.Where(m => m.IntegrationId == integration.Id).ToListAsync(cancellationToken))
        {
            if (byId.TryGetValue(mapping.ExternalTenantId, out var name) && name != mapping.ExternalTenantName)
            {
                mapping.ExternalTenantName = name;
            }

            await ReadAgentInstallerAsync(client, mapping, now, cancellationToken);
        }

        _logger.LogInformation("{Type}: {Count} tenants read", integration.Type, tenants.Count);
    }

    /// <summary>
    /// Keeps the link to the product's own agent installer current for one tenant (0.4.0 step 3), so an endpoint without
    /// that agent can be given one. It is read once a day at most: the link rarely changes and every call counts against
    /// the request budget of the whole instance. A link an admin pasted is only replaced by one the product itself gives,
    /// never cleared.
    /// </summary>
    private async Task ReadAgentInstallerAsync(Action1Client client, IntegrationMapping mapping, DateTime now,
        CancellationToken cancellationToken)
    {
        if (mapping.AgentInstallerReadAt is { } read && now - read < AgentInstallerInterval)
        {
            return;
        }

        var result = await client.GetAgentInstallerUrlAsync(mapping.ExternalTenantId, cancellationToken);
        mapping.AgentInstallerReadAt = now;
        if (result.Ok && result.Value is { Length: > 0 } url && url != mapping.AgentInstallerUrl)
        {
            mapping.AgentInstallerUrl = url.Length <= 500 ? url : string.Empty;
            _logger.LogInformation("The agent installer link of tenant {Tenant} was read from the product", mapping.ExternalTenantId);
        }
    }

    private static string? Trim(string? value, int max = 1000) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
