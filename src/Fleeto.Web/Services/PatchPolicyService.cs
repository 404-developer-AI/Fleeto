using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <param name="Links">Clients, sites and endpoints that link the patch policy.</param>
/// <param name="Automations">Automations the workers keep for it in the patch management product.</param>
/// <param name="AutomationProblem">Why the last change to one of those automations failed; null when all are in step.</param>
public sealed record PatchPolicyListItem(Guid Id, string Name, string? Description, Guid? ClientId, string? ClientCode, bool Enabled,
    string Schedule, PatchUpdateScope Scope, bool AutoReboot, int Links, int ClientTemplateUses, int Automations, string? AutomationProblem,
    CheckAppliesTo AppliesTo = CheckAppliesTo.All, bool AutoInstallAgent = false, Action1AgentCounts? Agents = null);

/// <summary>
/// The Action1 agent on the managed Windows endpoints a patch policy applies to (0.6.0), as the workers last judged it:
/// installed, being installed, and failed (a failure waiting for its next attempt or one Fleeto gave up on).
/// </summary>
public sealed record Action1AgentCounts(int Installed, int Installing, int Failed);

/// <summary>Everything a patch policy holds (0.6.0); see <see cref="PatchPolicy"/> for what each option means in the product.</summary>
public sealed record PatchPolicyInput(
    string? Name,
    string? Description,
    bool Enabled,
    PatchScheduleKind ScheduleKind,
    WeekDays WeekDays,
    int MonthDay,
    int MonthWeek,
    DayOfWeek MonthWeekday,
    int StartMinute,
    bool EndpointLocalTime,
    PatchUpdateScope Scope,
    IReadOnlyList<string> UpdateSources,
    IReadOnlyList<string> UpdateTypes,
    IReadOnlyList<string> Severities,
    IReadOnlyList<string> ExcludedNames,
    IReadOnlyList<string> ExcludedVendors,
    bool RequireApproval,
    int InstallDelayDays,
    bool AutoReboot,
    bool RebootMessage,
    string? RebootMessageText,
    int RebootTimeoutMinutes,
    int RetryHours,
    CheckAppliesTo AppliesTo = CheckAppliesTo.All,
    bool AutoInstallAgent = false,
    bool TurnOffWindowsUpdate = false)
{
    public static PatchPolicyInput From(PatchPolicy p) => new(p.Name, p.Description, p.Enabled, p.ScheduleKind, p.WeekDays, p.MonthDay, p.MonthWeek,
        p.MonthWeekday, p.StartMinute, p.EndpointLocalTime, p.Scope, p.UpdateSources, p.UpdateTypes, p.Severities, p.ExcludedNames,
        p.ExcludedVendors, p.RequireApproval, p.InstallDelayDays, p.AutoReboot, p.RebootMessage, p.RebootMessageText, p.RebootTimeoutMinutes,
        p.RetryHours, p.AppliesTo, p.AutoInstallAgent, p.TurnOffWindowsUpdate);
}

/// <summary>
/// Patch policies (0.6.0), global or per client. Linked, not copied: the workers bring every automation made from a
/// policy in step with it after a change. Only the options the patch management product offers exist
/// (<see cref="PatchPolicyRules"/>).
/// </summary>
public sealed class PatchPolicyService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<PatchPolicyService> _logger;

    public PatchPolicyService(IFleetoDbContextFactory dbFactory, INotificationBus bus, TimeProvider time, ILogger<PatchPolicyService> logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PatchPolicyListItem>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.PatchPoliciesView);
        await using var db = _dbFactory.Create(caller.Scope);
        var policies = await db.PatchPolicies.AsNoTracking()
            .OrderBy(p => p.ClientId != null).ThenBy(p => p.Name)
            .Select(p => new
            {
                Policy = p,
                ClientCode = db.Clients.Where(c => c.Id == p.ClientId).Select(c => c.Code).FirstOrDefault(),
                Links = db.ClientPatchPolicies.Count(l => l.PatchPolicyId == p.Id) + db.SitePatchPolicies.Count(l => l.PatchPolicyId == p.Id) +
                        db.EndpointPatchPolicies.Count(l => l.PatchPolicyId == p.Id),
                ClientTemplateUses = db.ClientTemplates.Count(t => t.PatchPolicyId == p.Id) + db.ClientTemplateSites.Count(s => s.PatchPolicyId == p.Id),
                Automations = db.IntegrationAutomations.Count(a => a.PatchPolicyId == p.Id && a.ExternalAutomationId != null),
                Problem = db.IntegrationAutomations.Where(a => a.PatchPolicyId == p.Id && a.LastError != null).Select(a => a.LastError).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);
        // Where the agent stands on the endpoints of the policies that install it (0.6.0), one query for all of them.
        var automatic = policies.Where(r => r.Policy.AutoInstallAgent).Select(r => r.Policy.Id).ToList();
        var counts = automatic.Count == 0
            ? []
            : (await (from p in EffectivePolicies.Query(db)
                      join a in db.Action1AgentInstallStates on p.EndpointId equals a.EndpointId
                      where p.PatchPolicyId != null && automatic.Contains(p.PatchPolicyId.Value)
                      group a by new { PolicyId = p.PatchPolicyId!.Value, a.Status } into g
                      select new { g.Key.PolicyId, g.Key.Status, Count = g.Count() })
                    .ToListAsync(cancellationToken))
                .GroupBy(c => c.PolicyId)
                .ToDictionary(g => g.Key, g => new Action1AgentCounts(
                    g.Where(c => c.Status == Action1AgentStatus.Installed).Sum(c => c.Count),
                    g.Where(c => c.Status is Action1AgentStatus.Installing or Action1AgentStatus.Missing).Sum(c => c.Count),
                    g.Where(c => c.Status is Action1AgentStatus.Failed or Action1AgentStatus.Stopped or Action1AgentStatus.NotReported).Sum(c => c.Count)));
        return policies.Select(r => new PatchPolicyListItem(r.Policy.Id, r.Policy.Name, r.Policy.Description, r.Policy.ClientId, r.ClientCode,
                r.Policy.Enabled, PatchPolicyRules.DescribeSchedule(r.Policy), r.Policy.Scope, r.Policy.AutoReboot, r.Links, r.ClientTemplateUses,
                r.Automations, r.Problem, r.Policy.AppliesTo, r.Policy.AutoInstallAgent,
                r.Policy.AutoInstallAgent ? counts.GetValueOrDefault(r.Policy.Id, new Action1AgentCounts(0, 0, 0)) : null))
            .ToList();
    }

    public async Task<PatchPolicyInput?> GetAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.PatchPoliciesView);
        await using var db = _dbFactory.Create(caller.Scope);
        var policy = await db.PatchPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return policy is null ? null : PatchPolicyInput.From(policy);
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, Guid? clientId, PatchPolicyInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PatchPoliciesEdit))
        {
            return ServiceResult<Guid>.Forbidden();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var policy = new PatchPolicy { Id = Guid.NewGuid(), ClientId = clientId, CreatedAt = now };
        Apply(policy, input, now);
        if (PatchPolicyRules.Validate(policy) is { } problem)
        {
            return ServiceResult<Guid>.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        if (clientId is { } id && !await db.Clients.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ServiceResult<Guid>.NotFound("client");
        }

        if (TemplateAccess.CreateProblem(db, caller, clientId) is { } accessProblem)
        {
            return ServiceResult<Guid>.Fail(accessProblem);
        }

        if (await NameTakenAsync(db, clientId, policy.Name, null, cancellationToken))
        {
            return ServiceResult<Guid>.Fail($"A patch policy named {policy.Name} already exists here. Choose another name.");
        }

        db.PatchPolicies.Add(policy);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchPolicyCreated, "PatchPolicy", policy.Id.ToString(), clientId,
            Describe(policy)), now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return ServiceResult<Guid>.Fail($"A patch policy named {policy.Name} already exists here. Choose another name.");
        }

        return ServiceResult<Guid>.Ok(policy.Id);
    }

    public async Task<ServiceResult> UpdateAsync(Caller caller, Guid id, PatchPolicyInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PatchPoliciesEdit))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var policy = await db.PatchPolicies.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null)
        {
            return ServiceResult.NotFound("patch policy");
        }

        if (await TemplateAccess.ProblemAsync(db, caller, TemplateKind.PatchPolicy, policy.Id, policy.ClientId, false, cancellationToken) is { } accessProblem)
        {
            return ServiceResult.Fail(accessProblem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var before = (TurnsOffWindowsUpdate(policy), policy.AppliesTo);
        Apply(policy, input, now);
        if (PatchPolicyRules.Validate(policy) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        if (await NameTakenAsync(db, policy.ClientId, policy.Name, policy.Id, cancellationToken))
        {
            return ServiceResult.Fail($"A patch policy named {policy.Name} already exists here. Choose another name.");
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchPolicyUpdated, "PatchPolicy", policy.Id.ToString(), policy.ClientId,
            Describe(policy)), now));
        if (before != (TurnsOffWindowsUpdate(policy), policy.AppliesTo) && (before.Item1 || TurnsOffWindowsUpdate(policy)))
        {
            AddWindowsUpdateChange(db, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await IntegrationFollow.NotifyAsync(_bus, _logger, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Copies a patch policy, globally or for one client. The copy is independent from then on.</summary>
    public async Task<ServiceResult<Guid>> CopyAsync(Caller caller, Guid id, string? name, Guid? targetClientId, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PatchPoliciesEdit))
        {
            return ServiceResult<Guid>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var source = await db.PatchPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (source is null)
        {
            return ServiceResult<Guid>.NotFound("patch policy");
        }

        var created = await CreateAsync(caller, targetClientId, PatchPolicyInput.From(source) with { Name = name }, cancellationToken);
        if (created.Success)
        {
            await db.PatchPolicies.Where(p => p.Id == created.Value).ExecuteUpdateAsync(s => s.SetProperty(p => p.CopiedFromId, source.Id), cancellationToken);
        }

        return created;
    }

    /// <summary>
    /// Deletes a patch policy. Its links go with it, so the clients, sites and endpoints that used it follow the next wider
    /// level; client templates that named it name none. The workers remove its automations from the product.
    /// </summary>
    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PatchPoliciesEdit))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var policy = await db.PatchPolicies.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null)
        {
            return ServiceResult.NotFound("patch policy");
        }

        if (await TemplateAccess.ProblemAsync(db, caller, TemplateKind.PatchPolicy, policy.Id, policy.ClientId, false, cancellationToken) is { } accessProblem)
        {
            return ServiceResult.Fail(accessProblem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var links = await db.ClientPatchPolicies.CountAsync(l => l.PatchPolicyId == id, cancellationToken) +
                    await db.SitePatchPolicies.CountAsync(l => l.PatchPolicyId == id, cancellationToken) +
                    await db.EndpointPatchPolicies.CountAsync(l => l.PatchPolicyId == id, cancellationToken);
        db.PatchPolicies.Remove(policy);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchPolicyDeleted, "PatchPolicy", policy.Id.ToString(), policy.ClientId,
            new { policy.Name, Links = links }), now));
        if (TurnsOffWindowsUpdate(policy))
        {
            AddWindowsUpdateChange(db, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await IntegrationFollow.NotifyAsync(_bus, _logger, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>True when the policy asks the agents of its endpoints to turn automatic Windows Update off (0.7.0).</summary>
    private static bool TurnsOffWindowsUpdate(PatchPolicy policy) => policy is { Enabled: true, TurnOffWindowsUpdate: true };

    /// <summary>
    /// The agent configurations carry the Windows Update switch (0.7.0), so a change that turns it on or off for the endpoints of a
    /// policy re-signs them. The endpoints of a policy follow from links on three levels and its servers or workstations, so every
    /// agent is asked: such a change is rare, and the signer skips every configuration that did not change.
    /// </summary>
    private static void AddWindowsUpdateChange(FleetoDbContext db, DateTime now) =>
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Instance, CreatedAt = now });

    private static Task<bool> NameTakenAsync(FleetoDbContext db, Guid? clientId, string name, Guid? exceptId, CancellationToken cancellationToken) =>
        db.PatchPolicies.IgnoreQueryFilters().AnyAsync(p => p.ClientId == clientId && p.Name.ToLower() == name.ToLower() && p.Id != exceptId,
            cancellationToken);

    private static void Apply(PatchPolicy policy, PatchPolicyInput input, DateTime now)
    {
        policy.Name = input.Name?.Trim() ?? string.Empty;
        policy.Description = ServiceSupport.Clean(input.Description);
        policy.Enabled = input.Enabled;
        policy.AppliesTo = input.AppliesTo;
        policy.ScheduleKind = input.ScheduleKind;
        policy.WeekDays = input.WeekDays & WeekDays.All;
        policy.MonthDay = input.MonthDay;
        policy.MonthWeek = input.MonthWeek;
        policy.MonthWeekday = input.MonthWeekday;
        policy.StartMinute = input.StartMinute;
        policy.EndpointLocalTime = input.EndpointLocalTime;
        policy.Scope = input.Scope;
        // Filters only mean something for filtered updates; all updates keeps none, so switching back starts clean.
        var filtered = input.Scope == PatchUpdateScope.Filtered;
        policy.UpdateSources = filtered ? Distinct(input.UpdateSources) : [];
        policy.UpdateTypes = filtered ? Distinct(input.UpdateTypes) : [];
        policy.Severities = filtered ? Distinct(input.Severities) : [];
        policy.ExcludedNames = filtered ? Distinct(input.ExcludedNames) : [];
        policy.ExcludedVendors = filtered ? Distinct(input.ExcludedVendors) : [];
        policy.RequireApproval = input.RequireApproval;
        // The delay only applies to updates that need no approval in the product.
        policy.InstallDelayDays = input.RequireApproval ? 0 : input.InstallDelayDays;
        policy.AutoReboot = input.AutoReboot;
        policy.RebootMessage = input.RebootMessage;
        policy.RebootMessageText = ServiceSupport.Clean(input.RebootMessageText);
        policy.RebootTimeoutMinutes = input.RebootTimeoutMinutes;
        policy.RetryHours = input.RetryHours;
        policy.AutoInstallAgent = input.AutoInstallAgent;
        policy.TurnOffWindowsUpdate = input.TurnOffWindowsUpdate;
        policy.UpdatedAt = now;
    }

    private static List<string> Distinct(IEnumerable<string> values) =>
        values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static object Describe(PatchPolicy policy) => new
    {
        policy.Name,
        policy.Enabled,
        AppliesTo = policy.AppliesTo.ToString(),
        Schedule = PatchPolicyRules.DescribeSchedule(policy),
        Scope = policy.Scope.ToString(),
        policy.UpdateSources,
        policy.UpdateTypes,
        policy.Severities,
        policy.ExcludedNames,
        policy.ExcludedVendors,
        policy.RequireApproval,
        policy.InstallDelayDays,
        policy.AutoReboot,
        policy.RebootMessage,
        policy.RebootTimeoutMinutes,
        policy.RetryHours,
        policy.AutoInstallAgent,
        policy.TurnOffWindowsUpdate
    };
}
