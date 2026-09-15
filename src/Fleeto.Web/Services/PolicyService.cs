using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record PolicyListItem(Guid Id, string Name, string? Description, Guid? ClientId, string? ClientCode, bool IsDefault,
    int HeartbeatIntervalSeconds, int InventoryIntervalSeconds, int OfflineAlertAfterMinutes, AlertSeverity OfflineAlertSeverity, int SiteCount,
    int ClientTemplateSiteCount, IReadOnlyList<MaintenanceWindow> MaintenanceWindows, bool ScriptApprovalRequired = false,
    UpdateRing UpdateRing = UpdateRing.Standard);

/// <param name="MaintenanceWindows">Recurring maintenance windows (0.2.0). Null keeps the current windows when updating, none when creating.</param>
public sealed record PolicyInput(string? Name, string? Description, int HeartbeatIntervalSeconds, int InventoryIntervalSeconds,
    int OfflineAlertAfterMinutes, AlertSeverity OfflineAlertSeverity, IReadOnlyList<MaintenanceWindow>? MaintenanceWindows = null,
    bool? ScriptApprovalRequired = null, UpdateRing? UpdateRing = null);

/// <summary>Policies (global or per client). Linked, not copied: a change applies at once to every site that uses the policy.</summary>
public sealed class PolicyService
{
    public const int MinHeartbeatSeconds = 10;
    public const int MaxHeartbeatSeconds = 300;
    public const int MinInventorySeconds = 900;
    public const int MaxInventorySeconds = 7 * 86400;
    public const int MaxOfflineAlertMinutes = 7 * 24 * 60;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public PolicyService(IFleetoDbContextFactory dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task<IReadOnlyList<PolicyListItem>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var rows = await db.Policies.AsNoTracking()
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.ClientId != null).ThenBy(p => p.Name)
            .Select(p => new
            {
                Item = new PolicyListItem(p.Id, p.Name, p.Description, p.ClientId,
                    db.Clients.Where(c => c.Id == p.ClientId).Select(c => c.Code).FirstOrDefault(),
                    p.IsDefault, p.HeartbeatIntervalSeconds, p.InventoryIntervalSeconds, p.OfflineAlertAfterMinutes, p.OfflineAlertSeverity,
                    db.SitePolicies.Count(l => l.PolicyId == p.Id),
                    db.ClientTemplateSites.Count(s => s.PolicyId == p.Id), Array.Empty<MaintenanceWindow>(), p.ScriptApprovalRequired, p.UpdateRing),
                p.MaintenanceWindowsJson
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => r.Item with { MaintenanceWindows = MaintenanceWindowSchedule.Parse(r.MaintenanceWindowsJson) }).ToList();
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, Guid? clientId, PolicyInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        if (Validate(input) is { } problem)
        {
            return ServiceResult<Guid>.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        if (clientId is { } id && !await db.Clients.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ServiceResult<Guid>.NotFound("client");
        }

        var name = input.Name!.Trim();
        if (await NameTakenAsync(db, clientId, name, null, cancellationToken))
        {
            return ServiceResult<Guid>.Fail($"A policy named {name} already exists here. Choose another name.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var policy = new Policy { Id = Guid.NewGuid(), ClientId = clientId, CreatedAt = now };
        Apply(policy, input with { MaintenanceWindows = input.MaintenanceWindows ?? [] }, now);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Policies.Add(policy);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PolicyCreated, "Policy", policy.Id.ToString(), clientId, Describe(policy)), now));
        await db.SaveChangesAsync(cancellationToken);
        await MaintenanceWindowSchedule.ReplaceAsync(db, policy.Id, MaintenanceWindowSchedule.Parse(policy.MaintenanceWindowsJson), now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(policy.Id);
    }

    public async Task<ServiceResult> UpdateAsync(Caller caller, Guid policyId, PolicyInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        if (Validate(input) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var policy = await db.Policies.SingleOrDefaultAsync(p => p.Id == policyId, cancellationToken);
        if (policy is null)
        {
            return ServiceResult.NotFound("policy");
        }

        var name = input.Name!.Trim();
        if (await NameTakenAsync(db, policy.ClientId, name, policy.Id, cancellationToken))
        {
            return ServiceResult.Fail($"A policy named {name} already exists here. Choose another name.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        Apply(policy, input, now);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Every site using the policy (and, for the default policy, every site without one) gets a new configuration.
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Policy, ScopeId = policy.Id, CreatedAt = now });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PolicyUpdated, "Policy", policy.Id.ToString(), policy.ClientId, Describe(policy)), now));
        await db.SaveChangesAsync(cancellationToken);
        await MaintenanceWindowSchedule.ReplaceAsync(db, policy.Id, MaintenanceWindowSchedule.Parse(policy.MaintenanceWindowsJson), now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Copies a policy, globally or for one client. The copy is independent from then on.</summary>
    public async Task<ServiceResult<Guid>> CopyAsync(Caller caller, Guid policyId, string? name, Guid? targetClientId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var source = await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == policyId, cancellationToken);
        if (source is null)
        {
            return ServiceResult<Guid>.NotFound("policy");
        }

        var input = new PolicyInput(name, source.Description, source.HeartbeatIntervalSeconds, source.InventoryIntervalSeconds,
            source.OfflineAlertAfterMinutes, source.OfflineAlertSeverity, MaintenanceWindowSchedule.Parse(source.MaintenanceWindowsJson),
            source.ScriptApprovalRequired, source.UpdateRing);
        var created = await CreateAsync(caller, targetClientId, input, cancellationToken);
        if (created.Success)
        {
            await db.Policies.Where(p => p.Id == created.Value).ExecuteUpdateAsync(s => s.SetProperty(p => p.CopiedFromId, source.Id), cancellationToken);
        }

        return created;
    }

    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid policyId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var policy = await db.Policies.SingleOrDefaultAsync(p => p.Id == policyId, cancellationToken);
        if (policy is null)
        {
            return ServiceResult.NotFound("policy");
        }

        if (policy.IsDefault)
        {
            return ServiceResult.Fail("The default policy applies to every site without a linked policy and cannot be deleted. Edit it instead.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var siteIds = await db.SitePolicies.Where(l => l.PolicyId == policyId).Select(l => l.SiteId).ToListAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Sites that used the policy fall back to the default policy (links cascade); they need a new configuration.
        foreach (var siteId in siteIds)
        {
            db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Site, ScopeId = siteId, CreatedAt = now });
        }

        db.Policies.Remove(policy);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PolicyDeleted, "Policy", policy.Id.ToString(), policy.ClientId,
            new { policy.Name, Sites = siteIds.Count }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private static Task<bool> NameTakenAsync(FleetoDbContext db, Guid? clientId, string name, Guid? exceptId, CancellationToken cancellationToken) =>
        db.Policies.IgnoreQueryFilters().AnyAsync(p => p.ClientId == clientId && p.Name.ToLower() == name.ToLower() && p.Id != exceptId, cancellationToken);

    private static void Apply(Policy policy, PolicyInput input, DateTime now)
    {
        policy.Name = input.Name!.Trim();
        policy.Description = ServiceSupport.Clean(input.Description);
        policy.HeartbeatIntervalSeconds = input.HeartbeatIntervalSeconds;
        policy.InventoryIntervalSeconds = input.InventoryIntervalSeconds;
        policy.OfflineAlertAfterMinutes = input.OfflineAlertAfterMinutes;
        policy.OfflineAlertSeverity = input.OfflineAlertSeverity;
        if (input.MaintenanceWindows is { } windows)
        {
            policy.MaintenanceWindowsJson = MaintenanceWindowSchedule.Serialize(windows
                .Select(w => w with { Name = ServiceSupport.Clean(w.Name), TimeZone = w.TimeZone.Trim() })
                .ToList());
        }

        if (input.ScriptApprovalRequired is { } approval)
        {
            policy.ScriptApprovalRequired = approval;
        }

        if (input.UpdateRing is { } ring)
        {
            policy.UpdateRing = ring;
        }

        policy.UpdatedAt = now;
    }

    private static object Describe(Policy policy) => new
    {
        policy.Name,
        policy.HeartbeatIntervalSeconds,
        policy.InventoryIntervalSeconds,
        policy.OfflineAlertAfterMinutes,
        OfflineAlertSeverity = policy.OfflineAlertSeverity.ToString(),
        policy.ScriptApprovalRequired,
        UpdateRing = policy.UpdateRing.ToString(),
        MaintenanceWindows = MaintenanceWindowSchedule.Parse(policy.MaintenanceWindowsJson).Select(MaintenanceWindows.Describe).ToList()
    };

    internal static string? Validate(PolicyInput input)
    {
        if (input.UpdateRing is { } ring && !Enum.IsDefined(ring))
        {
            return "Choose the Preview, Standard or Delayed update ring.";
        }

        var name = ServiceSupport.Clean(input.Name);
        if (name is null || name.Length > 100)
        {
            return "Enter a policy name of at most 100 characters.";
        }

        if (input.Description is { Length: > 1000 })
        {
            return "The description can be at most 1000 characters.";
        }

        if (input.HeartbeatIntervalSeconds is < MinHeartbeatSeconds or > MaxHeartbeatSeconds)
        {
            return $"The heartbeat interval must be between {MinHeartbeatSeconds} and {MaxHeartbeatSeconds} seconds.";
        }

        if (input.InventoryIntervalSeconds is < MinInventorySeconds or > MaxInventorySeconds)
        {
            return "The inventory interval must be between 15 minutes and 7 days.";
        }

        if (input.OfflineAlertAfterMinutes is < 0 or > MaxOfflineAlertMinutes)
        {
            return "The offline alert delay must be between 0 (no offline alerts) and 10080 minutes (7 days).";
        }

        return input.MaintenanceWindows is { } windows ? MaintenanceWindows.Validate(windows) : null;
    }
}
