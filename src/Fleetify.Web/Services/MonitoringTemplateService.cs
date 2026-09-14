using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Web.Services;

public sealed record MonitoringTemplateListItem(Guid Id, string Name, string? Description, Guid? ClientId, string? ClientCode, int CheckCount,
    int SiteCount, int ClientTemplateSiteCount);

public sealed record CheckDefinitionView(Guid Id, string Name, CheckType Type, int IntervalSeconds, IReadOnlyDictionary<string, string> Parameters,
    double? WarningThreshold, double? CriticalThreshold, int FailuresBeforeAlert, CheckAppliesTo AppliesTo, bool Enabled);

public sealed record MonitoringTemplateDetail(Guid Id, string Name, string? Description, Guid? ClientId, string? ClientCode, int SiteCount,
    IReadOnlyList<CheckDefinitionView> Checks);

public sealed record CheckDefinitionInput(string? Name, CheckType Type, int IntervalSeconds, IReadOnlyDictionary<string, string> Parameters,
    double? WarningThreshold, double? CriticalThreshold, int FailuresBeforeAlert, CheckAppliesTo AppliesTo, bool Enabled);

/// <summary>
/// Monitoring templates (global or per client) and their checks. Linked, not copied: a change applies at once to every
/// site that links the template, through a configuration change event.
/// </summary>
public sealed class MonitoringTemplateService
{
    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public MonitoringTemplateService(IFleetifyDbContextFactory dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task<IReadOnlyList<MonitoringTemplateListItem>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await db.MonitoringTemplates.AsNoTracking()
            .OrderBy(t => t.ClientId != null).ThenBy(t => t.Name)
            .Select(t => new MonitoringTemplateListItem(t.Id, t.Name, t.Description, t.ClientId,
                db.Clients.Where(c => c.Id == t.ClientId).Select(c => c.Code).FirstOrDefault(),
                t.Checks.Count,
                db.SiteMonitoringTemplates.Count(l => l.MonitoringTemplateId == t.Id),
                db.ClientTemplateSiteMonitoringTemplates.Count(l => l.MonitoringTemplateId == t.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<MonitoringTemplateDetail?> GetAsync(Caller caller, Guid templateId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var template = await db.MonitoringTemplates.AsNoTracking().Include(t => t.Checks)
            .SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null)
        {
            return null;
        }

        var clientCode = template.ClientId is { } clientId
            ? await db.Clients.Where(c => c.Id == clientId).Select(c => c.Code).FirstOrDefaultAsync(cancellationToken)
            : null;
        var siteCount = await db.SiteMonitoringTemplates.CountAsync(l => l.MonitoringTemplateId == templateId, cancellationToken);
        return new MonitoringTemplateDetail(template.Id, template.Name, template.Description, template.ClientId, clientCode, siteCount,
            template.Checks.OrderBy(c => c.Name).Select(c => new CheckDefinitionView(c.Id, c.Name, c.Type, c.IntervalSeconds,
                CheckParameters.Parse(c.ParametersJson), c.WarningThreshold, c.CriticalThreshold, c.FailuresBeforeAlert, c.AppliesTo, c.Enabled)).ToList());
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, string? name, string? description, Guid? clientId,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        if (ValidateTemplate(name, description) is { } problem)
        {
            return ServiceResult<Guid>.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        if (clientId is { } id && !await db.Clients.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ServiceResult<Guid>.NotFound("client");
        }

        var cleanName = name!.Trim();
        if (await NameTakenAsync(db, clientId, cleanName, null, cancellationToken))
        {
            return ServiceResult<Guid>.Fail($"A monitoring template named {cleanName} already exists here. Choose another name.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var template = new MonitoringTemplate
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = cleanName,
            Description = ServiceSupport.Clean(description),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MonitoringTemplates.Add(template);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MonitoringTemplateCreated, "MonitoringTemplate", template.Id.ToString(), clientId,
            new { template.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(template.Id);
    }

    public async Task<ServiceResult> UpdateAsync(Caller caller, Guid templateId, string? name, string? description,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        if (ValidateTemplate(name, description) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var template = await db.MonitoringTemplates.SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null)
        {
            return ServiceResult.NotFound("monitoring template");
        }

        var cleanName = name!.Trim();
        if (await NameTakenAsync(db, template.ClientId, cleanName, template.Id, cancellationToken))
        {
            return ServiceResult.Fail($"A monitoring template named {cleanName} already exists here. Choose another name.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        template.Name = cleanName;
        template.Description = ServiceSupport.Clean(description);
        template.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MonitoringTemplateUpdated, "MonitoringTemplate", template.Id.ToString(),
            template.ClientId, new { template.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Adds a check (checkId null) or changes one. Problems from <see cref="CheckParameters.Validate"/> are returned together.</summary>
    public async Task<ServiceResult<Guid>> SaveCheckAsync(Caller caller, Guid templateId, Guid? checkId, CheckDefinitionInput input,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var template = await db.MonitoringTemplates.SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null)
        {
            return ServiceResult<Guid>.NotFound("monitoring template");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        CheckDefinition check;
        if (checkId is { } id)
        {
            var existing = await db.CheckDefinitions.SingleOrDefaultAsync(c => c.Id == id && c.MonitoringTemplateId == templateId, cancellationToken);
            if (existing is null)
            {
                return ServiceResult<Guid>.NotFound("check");
            }

            check = existing;
        }
        else
        {
            check = new CheckDefinition { Id = Guid.NewGuid(), MonitoringTemplateId = templateId, ClientId = template.ClientId, CreatedAt = now };
        }

        // Only the parameters that belong to the type are kept, trimmed.
        var parameters = input.Parameters
            .Where(p => ParameterNames(input.Type).Contains(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value.Trim());

        check.Name = input.Name?.Trim() ?? string.Empty;
        check.Type = input.Type;
        check.IntervalSeconds = input.IntervalSeconds;
        check.ParametersJson = CheckParameters.Serialize(parameters);
        check.WarningThreshold = input.WarningThreshold;
        check.CriticalThreshold = input.CriticalThreshold;
        check.FailuresBeforeAlert = input.FailuresBeforeAlert;
        check.AppliesTo = input.AppliesTo;
        check.Enabled = input.Enabled;
        check.UpdatedAt = now;

        var problems = CheckParameters.Validate(check);
        if (check.Name.Length > 100)
        {
            problems = [.. problems, "The check name can be at most 100 characters."];
        }

        if (problems.Count > 0)
        {
            return ServiceResult<Guid>.Fail(string.Join(" ", problems));
        }

        if (checkId is null)
        {
            db.CheckDefinitions.Add(check);
        }

        template.UpdatedAt = now;
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.MonitoringTemplate, ScopeId = templateId, CreatedAt = now });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MonitoringTemplateUpdated, "MonitoringTemplate", templateId.ToString(),
            template.ClientId, new
            {
                Template = template.Name,
                Change = checkId is null ? "check added" : "check changed",
                Check = check.Name,
                Type = check.Type.ToString(),
                check.IntervalSeconds,
                check.WarningThreshold,
                check.CriticalThreshold,
                check.Enabled
            }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(check.Id);
    }

    public async Task<ServiceResult> SetCheckEnabledAsync(Caller caller, Guid templateId, Guid checkId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var check = await db.CheckDefinitions.Include(c => c.MonitoringTemplate)
            .SingleOrDefaultAsync(c => c.Id == checkId && c.MonitoringTemplateId == templateId, cancellationToken);
        if (check is null)
        {
            return ServiceResult.NotFound("check");
        }

        if (check.Enabled == enabled)
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        check.Enabled = enabled;
        check.UpdatedAt = now;
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.MonitoringTemplate, ScopeId = templateId, CreatedAt = now });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MonitoringTemplateUpdated, "MonitoringTemplate", templateId.ToString(),
            check.ClientId, new { Template = check.MonitoringTemplate?.Name, Change = enabled ? "check enabled" : "check disabled", Check = check.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteCheckAsync(Caller caller, Guid templateId, Guid checkId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var check = await db.CheckDefinitions.Include(c => c.MonitoringTemplate)
            .SingleOrDefaultAsync(c => c.Id == checkId && c.MonitoringTemplateId == templateId, cancellationToken);
        if (check is null)
        {
            return ServiceResult.NotFound("check");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.CheckDefinitions.Remove(check);
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.MonitoringTemplate, ScopeId = templateId, CreatedAt = now });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MonitoringTemplateUpdated, "MonitoringTemplate", templateId.ToString(),
            check.ClientId, new { Template = check.MonitoringTemplate?.Name, Change = "check deleted", Check = check.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Copies a template with its checks, globally or for one client. The copy is independent from then on.</summary>
    public async Task<ServiceResult<Guid>> CopyAsync(Caller caller, Guid templateId, string? name, Guid? targetClientId,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        if (ValidateTemplate(name, null) is { } problem)
        {
            return ServiceResult<Guid>.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var source = await db.MonitoringTemplates.AsNoTracking().Include(t => t.Checks).SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (source is null)
        {
            return ServiceResult<Guid>.NotFound("monitoring template");
        }

        if (targetClientId is { } clientId && !await db.Clients.AnyAsync(c => c.Id == clientId, cancellationToken))
        {
            return ServiceResult<Guid>.NotFound("client");
        }

        var cleanName = name!.Trim();
        if (await NameTakenAsync(db, targetClientId, cleanName, null, cancellationToken))
        {
            return ServiceResult<Guid>.Fail($"A monitoring template named {cleanName} already exists there. Choose another name.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var copy = new MonitoringTemplate
        {
            Id = Guid.NewGuid(),
            ClientId = targetClientId,
            Name = cleanName,
            Description = source.Description,
            CopiedFromId = source.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var check in source.Checks)
        {
            copy.Checks.Add(new CheckDefinition
            {
                Id = Guid.NewGuid(),
                ClientId = targetClientId,
                MonitoringTemplateId = copy.Id,
                Name = check.Name,
                Type = check.Type,
                IntervalSeconds = check.IntervalSeconds,
                ParametersJson = check.ParametersJson,
                WarningThreshold = check.WarningThreshold,
                CriticalThreshold = check.CriticalThreshold,
                FailuresBeforeAlert = check.FailuresBeforeAlert,
                AppliesTo = check.AppliesTo,
                Enabled = check.Enabled,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        db.MonitoringTemplates.Add(copy);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MonitoringTemplateCreated, "MonitoringTemplate", copy.Id.ToString(), targetClientId,
            new { copy.Name, CopiedFrom = source.Name, Checks = copy.Checks.Count }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(copy.Id);
    }

    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid templateId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var template = await db.MonitoringTemplates.SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null)
        {
            return ServiceResult.NotFound("monitoring template");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        // Links cascade away with the template, so the affected sites are recorded before the delete.
        var siteIds = await db.SiteMonitoringTemplates.IgnoreQueryFilters().Where(l => l.MonitoringTemplateId == templateId)
            .Select(l => l.SiteId).ToListAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var siteId in siteIds)
        {
            db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Site, ScopeId = siteId, CreatedAt = now });
        }

        db.MonitoringTemplates.Remove(template);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MonitoringTemplateDeleted, "MonitoringTemplate", template.Id.ToString(),
            template.ClientId, new { template.Name, Sites = siteIds.Count }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Parameter names each check type uses.</summary>
    public static IReadOnlyCollection<string> ParameterNames(CheckType type) => type switch
    {
        CheckType.DiskFree => ["drive"],
        CheckType.ServiceRunning => ["service"],
        _ => []
    };

    private static Task<bool> NameTakenAsync(FleetifyDbContext db, Guid? clientId, string name, Guid? exceptId, CancellationToken cancellationToken) =>
        db.MonitoringTemplates.IgnoreQueryFilters()
            .AnyAsync(t => t.ClientId == clientId && t.Name.ToLower() == name.ToLower() && t.Id != exceptId, cancellationToken);

    private static string? ValidateTemplate(string? name, string? description)
    {
        var cleanName = ServiceSupport.Clean(name);
        if (cleanName is null || cleanName.Length > 100)
        {
            return "Enter a monitoring template name of at most 100 characters.";
        }

        return description is { Length: > 1000 } ? "The description can be at most 1000 characters." : null;
    }
}
