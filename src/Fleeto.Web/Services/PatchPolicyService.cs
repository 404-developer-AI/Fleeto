using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <param name="Links">Clients, sites and endpoints that link the patch policy.</param>
/// <param name="Automations">Automations the workers keep for it in the patch management product.</param>
/// <param name="AutomationProblem">Why the last change to one of those automations failed; null when all are in step.</param>
public sealed record PatchPolicyListItem(Guid Id, string Name, string? Description, Guid? ClientId, string? ClientCode, bool Enabled,
    string Schedule, PatchUpdateScope Scope, bool AutoReboot, int Links, int ClientTemplateUses, int Automations, string? AutomationProblem);

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
    int RetryHours)
{
    public static PatchPolicyInput From(PatchPolicy p) => new(p.Name, p.Description, p.Enabled, p.ScheduleKind, p.WeekDays, p.MonthDay, p.MonthWeek,
        p.MonthWeekday, p.StartMinute, p.EndpointLocalTime, p.Scope, p.UpdateSources, p.UpdateTypes, p.Severities, p.ExcludedNames,
        p.ExcludedVendors, p.RequireApproval, p.InstallDelayDays, p.AutoReboot, p.RebootMessage, p.RebootMessageText, p.RebootTimeoutMinutes,
        p.RetryHours);
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
        caller.EnsureView();
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
        return policies.Select(r => new PatchPolicyListItem(r.Policy.Id, r.Policy.Name, r.Policy.Description, r.Policy.ClientId, r.ClientCode,
                r.Policy.Enabled, PatchPolicyRules.DescribeSchedule(r.Policy), r.Policy.Scope, r.Policy.AutoReboot, r.Links, r.ClientTemplateUses,
                r.Automations, r.Problem))
            .ToList();
    }

    public async Task<PatchPolicyInput?> GetAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var policy = await db.PatchPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return policy is null ? null : PatchPolicyInput.From(policy);
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, Guid? clientId, PatchPolicyInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
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
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var policy = await db.PatchPolicies.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null)
        {
            return ServiceResult.NotFound("patch policy");
        }

        var now = _time.GetUtcNow().UtcDateTime;
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
        await db.SaveChangesAsync(cancellationToken);
        await IntegrationFollow.NotifyAsync(_bus, _logger, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Copies a patch policy, globally or for one client. The copy is independent from then on.</summary>
    public async Task<ServiceResult<Guid>> CopyAsync(Caller caller, Guid id, string? name, Guid? targetClientId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
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
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var policy = await db.PatchPolicies.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null)
        {
            return ServiceResult.NotFound("patch policy");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var links = await db.ClientPatchPolicies.CountAsync(l => l.PatchPolicyId == id, cancellationToken) +
                    await db.SitePatchPolicies.CountAsync(l => l.PatchPolicyId == id, cancellationToken) +
                    await db.EndpointPatchPolicies.CountAsync(l => l.PatchPolicyId == id, cancellationToken);
        db.PatchPolicies.Remove(policy);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PatchPolicyDeleted, "PatchPolicy", policy.Id.ToString(), policy.ClientId,
            new { policy.Name, Links = links }), now));
        await db.SaveChangesAsync(cancellationToken);
        await IntegrationFollow.NotifyAsync(_bus, _logger, cancellationToken);
        return ServiceResult.Ok();
    }

    private static Task<bool> NameTakenAsync(FleetoDbContext db, Guid? clientId, string name, Guid? exceptId, CancellationToken cancellationToken) =>
        db.PatchPolicies.IgnoreQueryFilters().AnyAsync(p => p.ClientId == clientId && p.Name.ToLower() == name.ToLower() && p.Id != exceptId,
            cancellationToken);

    private static void Apply(PatchPolicy policy, PatchPolicyInput input, DateTime now)
    {
        policy.Name = input.Name?.Trim() ?? string.Empty;
        policy.Description = ServiceSupport.Clean(input.Description);
        policy.Enabled = input.Enabled;
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
        policy.UpdatedAt = now;
    }

    private static List<string> Distinct(IEnumerable<string> values) =>
        values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static object Describe(PatchPolicy policy) => new
    {
        policy.Name,
        policy.Enabled,
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
        policy.RetryHours
    };
}
