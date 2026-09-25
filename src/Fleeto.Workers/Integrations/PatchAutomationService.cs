using System.Security.Cryptography;
using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Common;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Keeps the patch policies of Fleeto as scheduled automations in Action1 (0.6.0).
/// <list type="bullet">
/// <item>Per mapped client and per patch policy that applies to at least one of its managed endpoints Action1 knows, one
/// automation in the client's organization, whose targets are exactly those endpoints (<see cref="EffectivePolicyRules"/>).</item>
/// <item>A change to the policy or its targets rewrites the automation; one that was removed in the console is created again.</item>
/// <item>An automation nobody needs any more (policy deleted, no targets left, client unmapped or deleted) is removed.</item>
/// <item>The automations of an organization that Fleeto did not make are recorded on the mapping, for the warning in the UI.</item>
/// </list>
/// Fleeto only touches automations it made: it knows them by the id it stored, never by their name. Every call counts
/// against the request budget of the whole instance, so a pass makes at most <see cref="MaxWorkPerPass"/> calls.
/// </summary>
public sealed class PatchAutomationService : WorkerLoop
{
    public const int MaxWorkPerPass = 10;

    /// <summary>How often the automations of each organization are read, to find removed ones and the ones Fleeto does not manage.</summary>
    public static readonly TimeSpan ReadInterval = TimeSpan.FromHours(1);

    /// <summary>After Action1 refused an automation, it is tried again after this long unless the policy or its targets change.</summary>
    public static readonly TimeSpan RefusedRetry = TimeSpan.FromMinutes(30);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly Action1ClientFactory _clients;
    private readonly LicenseService _licenses;
    private readonly CircuitBreaker _breaker;
    private IDisposable? _subscription;

    public PatchAutomationService(IFleetoDbContextFactory dbFactory, INotificationBus bus, Action1ClientFactory clients, LicenseService licenses,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<PatchAutomationService> logger)
        : base("patch-automations", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _clients = clients;
        _licenses = licenses;
        _breaker = new CircuitBreaker(failureThreshold: 3, pause: TimeSpan.FromMinutes(15), time);
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override TimeSpan MaxRunDuration => TimeSpan.FromMinutes(15);

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

    /// <summary>One pass: removals first, so a policy that moved does not briefly run twice, then creations and changes, then reads.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var integration = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        if (integration is null || !integration.Enabled || _breaker.IsOpen)
        {
            return;
        }

        using var client = _clients.TryCreate(integration);
        if (client is null)
        {
            return;
        }

        var now = Time.GetUtcNow().UtcDateTime;
        var budget = new Budget();
        try
        {
            var wanted = await WantedAsync(db, integration.Id, cancellationToken);
            var rows = await db.IntegrationAutomations.Where(a => a.IntegrationId == integration.Id).ToListAsync(cancellationToken);

            var unwanted = rows.Where(r => !wanted.TryGetValue((r.ClientId, r.PatchPolicyId), out var target) || target.TenantId != r.ExternalTenantId)
                .ToList();
            foreach (var row in unwanted)
            {
                if (row.ExternalAutomationId is { } automationId)
                {
                    if (!budget.Spend())
                    {
                        break;
                    }

                    var deleted = await client.DeleteAutomationAsync(row.ExternalTenantId, automationId, cancellationToken);
                    if (!Handle(row, deleted, now))
                    {
                        continue;
                    }
                }

                db.IntegrationAutomations.Remove(row);
                rows.Remove(row);
            }

            foreach (var ((clientId, policyId), target) in wanted.OrderBy(w => w.Key.ClientId).ThenBy(w => w.Key.PatchPolicyId))
            {
                var row = rows.SingleOrDefault(r => r.ClientId == clientId && r.PatchPolicyId == policyId);
                var automation = new Action1Automation(target.Policy, target.EndpointIds);
                var hash = Hash(automation);
                if (row is not null && row.ExternalAutomationId is not null && row.SyncedHash == hash)
                {
                    continue;
                }

                if (row is { LastError: not null, SyncedHash: var tried } && tried == hash && now - row.UpdatedAt < RefusedRetry)
                {
                    continue;
                }

                if (!budget.Spend())
                {
                    break;
                }

                if (row is null)
                {
                    row = new IntegrationAutomation
                    {
                        Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = clientId, PatchPolicyId = policyId,
                        ExternalTenantId = target.TenantId, CreatedAt = now
                    };
                    db.IntegrationAutomations.Add(row);
                    rows.Add(row);
                }

                row.SyncedHash = hash;
                if (row.ExternalAutomationId is { } existing)
                {
                    var updated = await client.UpdateAutomationAsync(row.ExternalTenantId, existing, automation, cancellationToken);
                    if (!Handle(row, updated, now))
                    {
                        continue;
                    }

                    if (updated.Value)
                    {
                        row.SyncedAt = now;
                        continue;
                    }

                    // Removed in the Action1 console: create it again, within the same piece of work.
                    row.ExternalAutomationId = null;
                }

                var created = await client.CreateAutomationAsync(row.ExternalTenantId, automation, cancellationToken);
                if (Handle(row, created, now))
                {
                    row.ExternalAutomationId = created.Value;
                    row.SyncedAt = now;
                }
            }

            await ReadOrganizationsAsync(db, integration.Id, client, rows, budget, now, cancellationToken);
            _breaker.RecordSuccess();
        }
        catch (TransientException)
        {
            // Recorded on the breaker; what is done so far is saved below.
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// What Action1 should have: per client and patch policy the organization and the Action1 ids of the managed endpoints
    /// the policy applies to. Only endpoints Action1 reported in the client's own organization count; nothing at all while
    /// the license lets no endpoint be managed.
    /// </summary>
    private async Task<Dictionary<(Guid ClientId, Guid PatchPolicyId), Target>> WantedAsync(FleetoDbContext db, Guid integrationId,
        CancellationToken cancellationToken)
    {
        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        if (TierRules.EffectiveTier(EndpointTier.Managed, license) != EndpointTier.Managed)
        {
            return [];
        }

        var effective = EffectivePolicies.Query(db);
        var rows = await (from e in db.Endpoints
                          join ep in effective on e.Id equals ep.EndpointId
                          join ps in db.EndpointPatchStates on e.Id equals ps.EndpointId
                          join m in db.IntegrationMappings on e.ClientId equals m.ClientId
                          where m.IntegrationId == integrationId && e.Tier == EndpointTier.Managed && ep.PatchPolicyId != null &&
                                ps.ExternalEndpointId != "" &&
                                ps.ExternalTenantId == m.ExternalTenantId
                          select new { e.ClientId, PatchPolicyId = ep.PatchPolicyId!.Value, m.ExternalTenantId, ps.ExternalEndpointId })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var policyIds = rows.Select(r => r.PatchPolicyId).Distinct().ToList();
        var policies = await db.PatchPolicies.AsNoTracking().Where(p => policyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        return rows.Where(r => policies.ContainsKey(r.PatchPolicyId))
            .GroupBy(r => (r.ClientId, r.PatchPolicyId))
            .ToDictionary(g => g.Key, g => new Target(g.First().ExternalTenantId, policies[g.Key.PatchPolicyId],
                g.Select(r => r.ExternalEndpointId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()));
    }

    /// <summary>
    /// Reads the automations of each mapped organization once per <see cref="ReadInterval"/>: an automation of Fleeto that
    /// is gone is created again on the next pass, and the others are recorded for the warning in the UI.
    /// </summary>
    private async Task ReadOrganizationsAsync(FleetoDbContext db, Guid integrationId, Action1Client client, List<IntegrationAutomation> rows,
        Budget budget, DateTime now, CancellationToken cancellationToken)
    {
        var due = await db.IntegrationMappings
            .Where(m => m.IntegrationId == integrationId && (m.OtherAutomationsReadAt == null || m.OtherAutomationsReadAt < now - ReadInterval))
            .OrderBy(m => m.OtherAutomationsReadAt)
            .ToListAsync(cancellationToken);
        foreach (var mapping in due)
        {
            if (!budget.Spend())
            {
                return;
            }

            var listed = await client.ListAutomationsAsync(mapping.ExternalTenantId, cancellationToken);
            if (!listed.Ok)
            {
                if (!listed.Permanent)
                {
                    Transient();
                }

                Logger.LogWarning("Could not read the automations of Action1 organization {Organization}: {Message}", mapping.ExternalTenantId,
                    listed.Message);
                mapping.OtherAutomationsReadAt = now;
                continue;
            }

            var ours = rows.Where(r => r.ExternalTenantId == mapping.ExternalTenantId && r.ExternalAutomationId is not null).ToList();
            var present = listed.Value!.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in ours.Where(r => !present.Contains(r.ExternalAutomationId!)))
            {
                gone.ExternalAutomationId = null;
                gone.UpdatedAt = now;
            }

            var ourIds = ours.Select(r => r.ExternalAutomationId).ToHashSet(StringComparer.Ordinal);
            var others = listed.Value!.Where(a => !ourIds.Contains(a.Id))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => new OtherAutomation(a.Name, a.Settings))
                .ToList();
            mapping.OtherAutomationsJson = OtherAutomation.Serialize(others);
            mapping.OtherAutomationsReadAt = now;
        }
    }

    /// <summary>
    /// Records the outcome of a call on the row. Returns true on success. A refusal is kept on the row and shown on the
    /// patch policy; a passing failure counts on the breaker and ends the pass.
    /// </summary>
    private bool Handle(IntegrationAutomation row, IntegrationResult result, DateTime now)
    {
        row.UpdatedAt = now;
        if (result.Ok)
        {
            row.LastError = null;
            return true;
        }

        row.LastError = Cut(result.Permanent
            ? $"{result.Message} Check that the role of the Action1 API credentials may manage automations."
            : result.Message ?? "Action1 did not answer.", 1000);
        Logger.LogWarning("Action1 refused automation of patch policy {PatchPolicyId} for client {ClientId}: {Message}", row.PatchPolicyId,
            row.ClientId, result.Message);
        if (!result.Permanent)
        {
            Transient();
            throw new TransientException();
        }

        return false;
    }

    private bool Handle<T>(IntegrationAutomation row, IntegrationResult<T> result, DateTime now) =>
        Handle(row, result.Ok ? IntegrationResult.Success() : IntegrationResult.Fail(result.Message ?? string.Empty, result.Permanent), now);

    private void Transient()
    {
        if (_breaker.RecordFailure())
        {
            Logger.LogWarning("Keeping patch policies in Action1 failed repeatedly; pausing until {OpenUntil}", _breaker.OpenUntil);
        }
    }

    /// <summary>What Fleeto sends, hashed: a new hash means the automation in Action1 is out of step.</summary>
    internal static string Hash(Action1Automation automation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(automation.ToJson().ToJsonString())));

    private static string Cut(string value, int length) => value.Length <= length ? value : value[..length];

    private sealed record Target(string TenantId, PatchPolicy Policy, IReadOnlyList<string> EndpointIds);

    private sealed class Budget
    {
        private int _work;

        public bool Spend() => ++_work <= MaxWorkPerPass;
    }

    private sealed class TransientException : Exception;
}
