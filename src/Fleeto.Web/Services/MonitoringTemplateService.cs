using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record MonitoringTemplateListItem(Guid Id, string Name, string? Description, Guid? ClientId, string? ClientCode, int CheckCount,
    int SiteCount, int ClientTemplateSiteCount, int ClientCount = 0, CheckAppliesTo AppliesTo = CheckAppliesTo.All);

public sealed record CheckDefinitionView(Guid Id, string Name, CheckType Type, int IntervalSeconds, IReadOnlyDictionary<string, string> Parameters,
    double? WarningThreshold, double? CriticalThreshold, int FailuresBeforeAlert, CheckAppliesTo AppliesTo, bool Enabled);

public sealed record MonitoringTemplateDetail(Guid Id, string Name, string? Description, Guid? ClientId, string? ClientCode, int SiteCount,
    IReadOnlyList<CheckDefinitionView> Checks, CheckAppliesTo AppliesTo = CheckAppliesTo.All);

public sealed record CheckDefinitionInput(string? Name, CheckType Type, int IntervalSeconds, IReadOnlyDictionary<string, string> Parameters,
    double? WarningThreshold, double? CriticalThreshold, int FailuresBeforeAlert, CheckAppliesTo AppliesTo, bool Enabled);

/// <summary>
/// Monitoring templates (global or per client) and their checks. Linked, not copied: a change applies at once to every
/// site that links the template, through a configuration change event.
/// </summary>
public sealed class MonitoringTemplateService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public MonitoringTemplateService(IFleetoDbContextFactory dbFactory, TimeProvider time)
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
                db.ClientTemplateSiteMonitoringTemplates.Count(l => l.MonitoringTemplateId == t.Id) +
                db.ClientTemplateMonitoringTemplates.Count(l => l.MonitoringTemplateId == t.Id),
                db.ClientMonitoringTemplates.Count(l => l.MonitoringTemplateId == t.Id), t.AppliesTo))
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
                CheckParameters.Parse(c.ParametersJson), c.WarningThreshold, c.CriticalThreshold, c.FailuresBeforeAlert, c.AppliesTo, c.Enabled)).ToList(),
            template.AppliesTo);
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, string? name, string? description, Guid? clientId,
        CheckAppliesTo appliesTo = CheckAppliesTo.All, CancellationToken cancellationToken = default)
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
            AppliesTo = Enum.IsDefined(appliesTo) ? appliesTo : CheckAppliesTo.All,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MonitoringTemplates.Add(template);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MonitoringTemplateCreated, "MonitoringTemplate", template.Id.ToString(), clientId,
            new { template.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(template.Id);
    }

    /// <param name="appliesTo">The endpoints the template is for (0.6.0); null keeps it.</param>
    public async Task<ServiceResult> UpdateAsync(Caller caller, Guid templateId, string? name, string? description,
        CheckAppliesTo? appliesTo = null, CancellationToken cancellationToken = default)
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
        if (appliesTo is { } classes && Enum.IsDefined(classes) && classes != template.AppliesTo)
        {
            // Its checks start or stop running on a class of endpoints: every endpoint that links it gets a new configuration.
            template.AppliesTo = classes;
            db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.MonitoringTemplate, ScopeId = template.Id, CreatedAt = now });
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MonitoringTemplateUpdated, "MonitoringTemplate", template.Id.ToString(),
            template.ClientId, new { template.Name, AppliesTo = template.AppliesTo.ToString() }), now));
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

            if (existing.Type != input.Type)
            {
                return ServiceResult<Guid>.Fail("The type of a check cannot change: its results and history belong to that type. Add a new check instead.");
            }

            check = existing;
        }
        else
        {
            check = new CheckDefinition { Id = Guid.NewGuid(), MonitoringTemplateId = templateId, ClientId = template.ClientId, CreatedAt = now };
        }

        if (!Enum.IsDefined(input.Type))
        {
            return ServiceResult<Guid>.Fail("Choose a check type.");
        }

        // Only the parameters that belong to the type (and apply) are kept, trimmed.
        var parameters = CheckCatalog.CleanParameters(input.Type, input.Parameters);
        if (input.Type == CheckType.Script && await ScriptChecks.BindAsync(db, template.ClientId, parameters, cancellationToken) is { } scriptProblem)
        {
            return ServiceResult<Guid>.Fail(scriptProblem);
        }

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

        // A script check may only use a global script or one of the template's client.
        var scriptIds = source.Checks.Where(c => c.Type == CheckType.Script)
            .Select(c => CheckParameters.Parse(c.ParametersJson).TryGetValue(CheckCatalog.ScriptParameter, out var id) && Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .ToList();
        if (scriptIds.Count > 0)
        {
            var foreign = await db.Scripts.IgnoreQueryFilters().AsNoTracking()
                .Where(s => scriptIds.Contains(s.Id) && s.ClientId != null && s.ClientId != targetClientId)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (foreign is not null)
            {
                return ServiceResult<Guid>.Fail(
                    $"The template has a script check with script {foreign}, which belongs to one client. Choose a global script for that check first, or copy the template for that client.");
            }
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var copy = new MonitoringTemplate
        {
            Id = Guid.NewGuid(),
            ClientId = targetClientId,
            Name = cleanName,
            Description = source.Description,
            AppliesTo = source.AppliesTo,
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
        // Links cascade away with the template, so the affected sites and endpoints are recorded before the delete.
        var clientIds = await db.ClientMonitoringTemplates.IgnoreQueryFilters().Where(l => l.MonitoringTemplateId == templateId)
            .Select(l => l.ClientId).ToListAsync(cancellationToken);
        var siteIds = await db.SiteMonitoringTemplates.IgnoreQueryFilters().Where(l => l.MonitoringTemplateId == templateId)
            .Select(l => l.SiteId).ToListAsync(cancellationToken);
        var endpointIds = await db.EndpointMonitoringTemplates.IgnoreQueryFilters().Where(l => l.MonitoringTemplateId == templateId)
            .Select(l => l.EndpointId).ToListAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
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

        db.MonitoringTemplates.Remove(template);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.MonitoringTemplateDeleted, "MonitoringTemplate", template.Id.ToString(),
            template.ClientId, new { template.Name, Clients = clientIds.Count, Sites = siteIds.Count, Endpoints = endpointIds.Count }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Inventories read at most to suggest services for a template: the most recent ones of its endpoints.</summary>
    internal const int ServiceSuggestionInventories = 1000;

    // Distinct services over the most recent inventories of the endpoints that run this template (through their site or directly),
    // limited to the caller's clients. Raw SQL skips the EF query filters, so the scope is a parameter here.
    private const string TemplateServicesSql = """
        WITH recent AS (
          SELECT i."ServicesJson"
          FROM "InventorySnapshots" i
          JOIN "Endpoints" e ON e."Id" = i."EndpointId"
          WHERE (@allClients OR i."ClientId" = ANY(@clientIds))
            AND (EXISTS (SELECT 1 FROM "SiteMonitoringTemplates" l WHERE l."SiteId" = e."SiteId" AND l."MonitoringTemplateId" = @templateId)
                 OR EXISTS (SELECT 1 FROM "ClientMonitoringTemplates" cl WHERE cl."ClientId" = e."ClientId" AND cl."MonitoringTemplateId" = @templateId)
                 OR EXISTS (SELECT 1 FROM "EndpointMonitoringTemplates" el WHERE el."EndpointId" = e."Id" AND el."MonitoringTemplateId" = @templateId))
          ORDER BY i."ReceivedAt" DESC
          LIMIT @inventories)
        SELECT s->>'name' AS "Name", max(s->>'displayName') AS "DisplayName", '' AS "StartType", '' AS "State"
        FROM recent, jsonb_array_elements(recent."ServicesJson") s
        WHERE jsonb_typeof(s) = 'object' AND coalesce(s->>'name', '') <> ''
        GROUP BY s->>'name'
        ORDER BY max(s->>'displayName'), s->>'name'
        LIMIT 2000
        """;

    /// <summary>
    /// Services seen on the endpoints that run this template, for picking the service of a service check. Empty while no linked
    /// endpoint has reported its services; typing a name stays possible.
    /// </summary>
    public async Task<IReadOnlyList<ServiceOption>> GetServicesAsync(Caller caller, Guid templateId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        if (!await db.MonitoringTemplates.AnyAsync(t => t.Id == templateId, cancellationToken))
        {
            return [];
        }

        return await db.Database.SqlQueryRaw<ServiceOption>(TemplateServicesSql,
                new Npgsql.NpgsqlParameter("allClients", caller.Scope.AllClients),
                new Npgsql.NpgsqlParameter("clientIds", caller.Scope.ClientIds.ToArray()),
                new Npgsql.NpgsqlParameter("templateId", templateId),
                new Npgsql.NpgsqlParameter("inventories", ServiceSuggestionInventories))
            .ToListAsync(cancellationToken);
    }

    private static Task<bool> NameTakenAsync(FleetoDbContext db, Guid? clientId, string name, Guid? exceptId, CancellationToken cancellationToken) =>
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
