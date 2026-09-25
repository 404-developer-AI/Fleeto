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
    UpdateRing UpdateRing = UpdateRing.Standard, long MaxOutputBytes = ScriptRules.DefaultMaxOutputBytes,
    int RemoteIdleTimeoutMinutes = RemoteSessionRules.DefaultIdleTimeoutMinutes, bool RemoteConsentRequired = false,
    int RemoteConsentTimeoutSeconds = RemoteSessionRules.DefaultConsentTimeoutSeconds, bool RemoteBannerVisible = true, bool RemoteClipboardEnabled = true,
    long RemoteMaxFileBytes = RemoteSessionRules.DefaultMaxFileBytes, int ClientCount = 0, int EndpointCount = 0);

/// <param name="MaintenanceWindows">Recurring maintenance windows (0.2.0). Null keeps the current windows when updating, none when creating.</param>
/// <param name="RemoteIdleTimeoutMinutes">Minutes a remote session may go without input (0.3.0). Null keeps the current value.</param>
/// <param name="RemoteConsentRequired">Remote control on workstations asks the signed-in user first (0.3.0). Null keeps the current value.</param>
/// <param name="RemoteConsentTimeoutSeconds">Seconds the consent prompt waits before access is granted. Null keeps the current value.</param>
/// <param name="RemoteBannerVisible">Remote control on workstations shows a banner naming the technicians. Null keeps the current value.</param>
/// <param name="RemoteClipboardEnabled">Remote control synchronises the clipboard. Null keeps the current value.</param>
/// <param name="RemoteMaxFileBytes">The largest file one remote session transfer may carry. Null keeps the current value.</param>
public sealed record PolicyInput(string? Name, string? Description, int HeartbeatIntervalSeconds, int InventoryIntervalSeconds,
    int OfflineAlertAfterMinutes, AlertSeverity OfflineAlertSeverity, IReadOnlyList<MaintenanceWindow>? MaintenanceWindows = null,
    bool? ScriptApprovalRequired = null, UpdateRing? UpdateRing = null, long? MaxOutputBytes = null, int? RemoteIdleTimeoutMinutes = null,
    bool? RemoteConsentRequired = null, int? RemoteConsentTimeoutSeconds = null, bool? RemoteBannerVisible = null, bool? RemoteClipboardEnabled = null,
    long? RemoteMaxFileBytes = null);

/// <summary>Policies (global or per client). Linked, not copied: a change applies at once to every site that uses the policy.</summary>
public sealed class PolicyService
{
    public const int MinHeartbeatSeconds = 10;
    public const int MaxHeartbeatSeconds = 300;
    public const int MinInventorySeconds = 900;
    public const int MaxInventorySeconds = 7 * 86400;
    public const int MaxOfflineAlertMinutes = 7 * 24 * 60;

    /// <summary>The job output cap is chosen in whole mebibytes (0.2.1); the signer holds the same bounds.</summary>
    public const int MinOutputMegabytes = (int)(ScriptRules.MinOutputBytes / ScriptRules.Mebibyte);
    public const int MaxOutputMegabytes = (int)(ScriptRules.MaxOutputBytes / ScriptRules.Mebibyte);

    /// <summary>The remote session file size cap is chosen in whole mebibytes (0.3.0); the signer holds the same bounds.</summary>
    public const int MinRemoteFileMegabytes = (int)(RemoteSessionRules.MinMaxFileBytes / ScriptRules.Mebibyte);
    public const int MaxRemoteFileMegabytes = (int)(RemoteSessionRules.MaxMaxFileBytes / ScriptRules.Mebibyte);

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
                    db.ClientTemplateSites.Count(s => s.PolicyId == p.Id) + db.ClientTemplates.Count(t => t.PolicyId == p.Id),
                    Array.Empty<MaintenanceWindow>(), p.ScriptApprovalRequired, p.UpdateRing,
                    p.MaxOutputBytes, p.RemoteIdleTimeoutMinutes, p.RemoteConsentRequired, p.RemoteConsentTimeoutSeconds, p.RemoteBannerVisible,
                    p.RemoteClipboardEnabled, p.RemoteMaxFileBytes,
                    db.ClientPolicies.Count(l => l.PolicyId == p.Id), db.EndpointPolicies.Count(l => l.PolicyId == p.Id)),
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
            source.ScriptApprovalRequired, source.UpdateRing, source.MaxOutputBytes, source.RemoteIdleTimeoutMinutes, source.RemoteConsentRequired,
            source.RemoteConsentTimeoutSeconds, source.RemoteBannerVisible, source.RemoteClipboardEnabled, source.RemoteMaxFileBytes);
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
            return ServiceResult.Fail("The default policy applies to every endpoint without a linked policy and cannot be deleted. Edit it instead.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var clientIds = await db.ClientPolicies.IgnoreQueryFilters().Where(l => l.PolicyId == policyId).Select(l => l.ClientId).ToListAsync(cancellationToken);
        var siteIds = await db.SitePolicies.IgnoreQueryFilters().Where(l => l.PolicyId == policyId).Select(l => l.SiteId).ToListAsync(cancellationToken);
        var endpointIds = await db.EndpointPolicies.IgnoreQueryFilters().Where(l => l.PolicyId == policyId).Select(l => l.EndpointId)
            .ToListAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // What used the policy falls back to the next wider level (links cascade); it needs a new configuration.
        foreach (var clientId in clientIds)
        {
            db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Client, ScopeId = clientId, CreatedAt = now });
        }

        foreach (var siteId in siteIds)
        {
            db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Site, ScopeId = siteId, CreatedAt = now });
        }

        foreach (var endpointId in endpointIds)
        {
            db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Endpoint, ScopeId = endpointId, CreatedAt = now });
        }

        db.Policies.Remove(policy);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PolicyDeleted, "Policy", policy.Id.ToString(), policy.ClientId,
            new { policy.Name, Clients = clientIds.Count, Sites = siteIds.Count, Endpoints = endpointIds.Count }), now));
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

        if (input.MaxOutputBytes is { } output)
        {
            policy.MaxOutputBytes = ScriptRules.OutputCap(output);
        }

        if (input.RemoteIdleTimeoutMinutes is { } idle)
        {
            policy.RemoteIdleTimeoutMinutes = RemoteSessionRules.IdleTimeoutMinutes(idle);
        }

        if (input.RemoteConsentRequired is { } consent)
        {
            policy.RemoteConsentRequired = consent;
        }

        if (input.RemoteConsentTimeoutSeconds is { } consentTimeout)
        {
            policy.RemoteConsentTimeoutSeconds = RemoteSessionRules.ConsentTimeoutSeconds(consentTimeout);
        }

        if (input.RemoteBannerVisible is { } banner)
        {
            policy.RemoteBannerVisible = banner;
        }

        if (input.RemoteClipboardEnabled is { } clipboard)
        {
            policy.RemoteClipboardEnabled = clipboard;
        }

        if (input.RemoteMaxFileBytes is { } fileBytes)
        {
            policy.RemoteMaxFileBytes = RemoteSessionRules.MaxFileBytes(fileBytes);
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
        policy.MaxOutputBytes,
        policy.RemoteIdleTimeoutMinutes,
        policy.RemoteConsentRequired,
        policy.RemoteConsentTimeoutSeconds,
        policy.RemoteBannerVisible,
        policy.RemoteClipboardEnabled,
        policy.RemoteMaxFileBytes,
        MaintenanceWindows =MaintenanceWindowSchedule.Parse(policy.MaintenanceWindowsJson).Select(MaintenanceWindows.Describe).ToList()
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

        if (input.MaxOutputBytes is { } output && (output < ScriptRules.MinOutputBytes || output > ScriptRules.MaxOutputBytes))
        {
            return $"The job output cap must be between {MinOutputMegabytes} and {MaxOutputMegabytes} MiB.";
        }

        if (input.RemoteIdleTimeoutMinutes is { } idle && (idle < RemoteSessionRules.MinIdleTimeoutMinutes || idle > RemoteSessionRules.MaxIdleTimeoutMinutes))
        {
            return $"The remote session idle timeout must be between {RemoteSessionRules.MinIdleTimeoutMinutes} and {RemoteSessionRules.MaxIdleTimeoutMinutes} minutes.";
        }

        if (input.RemoteConsentTimeoutSeconds is { } consent &&
            (consent < RemoteSessionRules.MinConsentTimeoutSeconds || consent > RemoteSessionRules.MaxConsentTimeoutSeconds))
        {
            return $"The consent timeout must be between {RemoteSessionRules.MinConsentTimeoutSeconds} and {RemoteSessionRules.MaxConsentTimeoutSeconds} seconds.";
        }

        if (input.RemoteMaxFileBytes is { } fileBytes && (fileBytes < RemoteSessionRules.MinMaxFileBytes || fileBytes > RemoteSessionRules.MaxMaxFileBytes))
        {
            return $"The remote session file size cap must be between {MinRemoteFileMegabytes} and {MaxRemoteFileMegabytes} MiB.";
        }

        return input.MaintenanceWindows is { } windows ? MaintenanceWindows.Validate(windows) : null;
    }
}
