using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Infrastructure.Services;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;
using Endpoint = Fleetify.Core.Entities.Endpoint;

namespace Fleetify.Web.Services;

/// <summary>One row of the Checks tab: a check with one target's state, or a check that has not run yet (Status null).</summary>
public sealed record EndpointCheckRow(
    Guid DefinitionId, string Name, CheckType Type, string Target, CheckStatus? Status, bool RerunRequested, double? Value, string Detail,
    string Error, DateTime? LastResultAt, int IntervalSeconds, CheckSource Source, string? TemplateName, bool DisabledOnEndpoint,
    bool HasOverrides, AlertView? OpenAlert, IReadOnlyDictionary<string, string> Parameters);

/// <param name="Managed">False for an agent-only endpoint (or an expired license): no checks run and nothing can change.</param>
/// <param name="ConfigurationPending">The agent has not applied the latest signed configuration yet.</param>
public sealed record EndpointChecksView(bool Managed, bool ConfigurationPending, IReadOnlyList<EndpointCheckRow> Rows);

/// <summary>A template check with the template values and this endpoint's overrides, for the override dialog.</summary>
public sealed record CheckOverrideView(
    Guid DefinitionId, string Name, CheckType Type, string? TemplateName, int TemplateIntervalSeconds, double? TemplateWarningThreshold,
    double? TemplateCriticalThreshold, int TemplateFailuresBeforeAlert, bool Disabled, int? IntervalSeconds, bool OverrideThresholds,
    double? WarningThreshold, double? CriticalThreshold, int? FailuresBeforeAlert, IReadOnlyDictionary<string, string> Parameters);

/// <summary>A service seen on an endpoint, for picking the service of a service check.</summary>
public sealed record ServiceOption(string Name, string DisplayName, string StartType, string State);

/// <summary>Overrides of one template check. Null interval or failures, and OverrideThresholds false, inherit the template.</summary>
public sealed record CheckOverrideInput(int? IntervalSeconds, bool OverrideThresholds, double? WarningThreshold, double? CriticalThreshold,
    int? FailuresBeforeAlert);

/// <summary>Monitoring templates of an endpoint: through its site (read-only here), linked to the endpoint, and linkable ones.</summary>
public sealed record EndpointTemplateLinks(IReadOnlyList<LinkOption> SiteTemplates, IReadOnlyList<LinkOption> EndpointTemplates,
    IReadOnlyList<LinkOption> Available);

/// <summary>
/// Checks of one endpoint: the Checks tab, adjustments per endpoint on top of the linked monitoring templates (disable,
/// overrides, endpoint-only checks, extra templates), and requests to run or reset a check. Managed endpoints only: every
/// change is refused server-side for an agent-only endpoint, and the signer and gateway enforce the tier again.
/// </summary>
public sealed class EndpointCheckService
{
    /// <summary>A run request the agent does not receive within this time is dropped.</summary>
    public static readonly TimeSpan RunRequestLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Same gap as the agent's per-check limit: a faster second request would be dropped by the agent.</summary>
    public static readonly TimeSpan MinimumRunGap = TimeSpan.FromSeconds(30);

    public const int MaxRunRequestsPerMinute = 6;

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<EndpointCheckService> _logger;

    public EndpointCheckService(IFleetifyDbContextFactory dbFactory, LicenseService licenses, INotificationBus bus, TimeProvider time,
        ILogger<EndpointCheckService> logger)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public async Task<EndpointChecksView?> GetChecksAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        if (TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed)
        {
            return new EndpointChecksView(false, false, []);
        }

        var checks = await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: true, cancellationToken);
        var states = (await db.CheckStates.AsNoTracking().Where(s => s.EndpointId == endpointId).ToListAsync(cancellationToken))
            .ToLookup(s => s.CheckDefinitionId);
        var alerts = (await AlertService.Project(db, db.Alerts.AsNoTracking()
                    .Where(a => a.EndpointId == endpointId && a.Kind == AlertKind.Check && a.State != AlertState.Resolved && a.CheckDefinitionId != null))
                .ToListAsync(cancellationToken))
            .ToDictionary(a => (a.CheckDefinitionId!.Value, a.Target));

        var rows = new List<EndpointCheckRow>();
        foreach (var check in checks.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id))
        {
            var parameters = CheckParameters.Parse(check.Definition.ParametersJson);
            var checkStates = check.DisabledOnEndpoint ? [] : states[check.Id].OrderBy(s => s.Target, StringComparer.OrdinalIgnoreCase).ToList();
            if (checkStates.Count == 0)
            {
                alerts.TryGetValue((check.Id, string.Empty), out var alert);
                rows.Add(new EndpointCheckRow(check.Id, check.Name, check.Type, string.Empty, null, false, null, string.Empty, string.Empty, null,
                    check.IntervalSeconds, check.Source, check.TemplateName, check.DisabledOnEndpoint, check.HasOverrides, alert, parameters));
                continue;
            }

            foreach (var state in checkStates)
            {
                alerts.TryGetValue((check.Id, state.Target), out var alert);
                rows.Add(new EndpointCheckRow(check.Id, check.Name, check.Type, state.Target, state.Status, state.RerunRequested,
                    state.RerunRequested ? null : state.Value, state.RerunRequested ? string.Empty : state.Detail,
                    state.RerunRequested ? string.Empty : state.Error, state.LastResultAt, check.IntervalSeconds, check.Source, check.TemplateName,
                    false, check.HasOverrides, alert, parameters));
            }
        }

        return new EndpointChecksView(true, endpoint.AppliedConfigVersion < endpoint.ConfigVersion, rows);
    }

    public async Task<CheckOverrideView?> GetOverrideAsync(Caller caller, Guid endpointId, Guid checkId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var checks = await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: true, cancellationToken);
        if (checks.FirstOrDefault(c => c.Id == checkId) is not { Source: not CheckSource.Endpoint } check)
        {
            return null;
        }

        var d = check.Definition;
        var o = check.Override;
        return new CheckOverrideView(d.Id, d.Name, d.Type, check.TemplateName, d.IntervalSeconds, d.WarningThreshold, d.CriticalThreshold,
            d.FailuresBeforeAlert, o?.Disabled == true, o?.IntervalSeconds, o?.OverrideThresholds == true, o?.WarningThreshold, o?.CriticalThreshold,
            o?.FailuresBeforeAlert, CheckParameters.Parse(d.ParametersJson));
    }

    /// <summary>Switches a template check off or on again for this endpoint.</summary>
    public async Task<ServiceResult> SetDisabledAsync(Caller caller, Guid endpointId, Guid checkId, bool disabled,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var (endpoint, problem) = await LoadManagedEndpointAsync(db, endpointId, cancellationToken);
        if (endpoint is null)
        {
            return problem!;
        }

        var checks = await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: true, cancellationToken);
        if (checks.FirstOrDefault(c => c.Id == checkId) is not { Source: not CheckSource.Endpoint } check)
        {
            return ServiceResult.NotFound("check");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var adjustment = await db.EndpointCheckOverrides.SingleOrDefaultAsync(o => o.EndpointId == endpointId && o.CheckDefinitionId == checkId,
            cancellationToken);
        if ((adjustment?.Disabled ?? false) == disabled)
        {
            return ServiceResult.Ok();
        }

        if (adjustment is null)
        {
            adjustment = new EndpointCheckOverride { EndpointId = endpointId, CheckDefinitionId = checkId, ClientId = endpoint.ClientId, CreatedAt = now };
            db.EndpointCheckOverrides.Add(adjustment);
        }

        adjustment.Disabled = disabled;
        adjustment.UpdatedAt = now;
        adjustment.UpdatedByUserId = caller.UserId;
        if (adjustment.IsEmpty)
        {
            db.EndpointCheckOverrides.Remove(adjustment);
        }

        Changed(db, caller, endpoint, now, new { Change = disabled ? "check disabled on endpoint" : "check enabled on endpoint", Check = check.Name });
        await db.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(endpointId, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Saves the overrides of a template check for this endpoint; an input without overrides removes them.</summary>
    public async Task<ServiceResult> SaveOverrideAsync(Caller caller, Guid endpointId, Guid checkId, CheckOverrideInput input,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var (endpoint, problem) = await LoadManagedEndpointAsync(db, endpointId, cancellationToken);
        if (endpoint is null)
        {
            return problem!;
        }

        var checks = await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: true, cancellationToken);
        if (checks.FirstOrDefault(c => c.Id == checkId) is not { Source: not CheckSource.Endpoint } check)
        {
            return ServiceResult.NotFound("check");
        }

        // Validate the check as it would run on this endpoint.
        var effective = check.Definition;
        var candidate = new EffectiveCheck(effective, check.Source, check.TemplateName, null,
            input.IntervalSeconds ?? effective.IntervalSeconds,
            input.OverrideThresholds ? input.WarningThreshold : effective.WarningThreshold,
            input.OverrideThresholds ? input.CriticalThreshold : effective.CriticalThreshold,
            input.FailuresBeforeAlert ?? effective.FailuresBeforeAlert).ToEffectiveDefinition();
        var problems = CheckParameters.Validate(candidate);
        if (problems.Count > 0)
        {
            return ServiceResult.Fail(string.Join(" ", problems));
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var adjustment = await db.EndpointCheckOverrides.SingleOrDefaultAsync(o => o.EndpointId == endpointId && o.CheckDefinitionId == checkId,
            cancellationToken);
        if (adjustment is null)
        {
            adjustment = new EndpointCheckOverride { EndpointId = endpointId, CheckDefinitionId = checkId, ClientId = endpoint.ClientId, CreatedAt = now };
            db.EndpointCheckOverrides.Add(adjustment);
        }

        adjustment.IntervalSeconds = input.IntervalSeconds;
        adjustment.OverrideThresholds = input.OverrideThresholds;
        adjustment.WarningThreshold = input.OverrideThresholds ? input.WarningThreshold : null;
        adjustment.CriticalThreshold = input.OverrideThresholds ? input.CriticalThreshold : null;
        adjustment.FailuresBeforeAlert = input.FailuresBeforeAlert;
        adjustment.UpdatedAt = now;
        adjustment.UpdatedByUserId = caller.UserId;
        if (adjustment.IsEmpty)
        {
            db.EndpointCheckOverrides.Remove(adjustment);
        }

        Changed(db, caller, endpoint, now, new
        {
            Change = adjustment.IsEmpty ? "check overrides removed" : "check overrides changed",
            Check = check.Name,
            input.IntervalSeconds,
            WarningThreshold = input.OverrideThresholds ? input.WarningThreshold : null,
            CriticalThreshold = input.OverrideThresholds ? input.CriticalThreshold : null,
            input.OverrideThresholds,
            input.FailuresBeforeAlert
        });
        await db.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(endpointId, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>A check that exists only on this endpoint, for the edit dialog.</summary>
    public async Task<CheckDefinitionView?> GetEndpointCheckAsync(Caller caller, Guid endpointId, Guid checkId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var c = await db.CheckDefinitions.AsNoTracking().SingleOrDefaultAsync(d => d.Id == checkId && d.EndpointId == endpointId, cancellationToken);
        return c is null
            ? null
            : new CheckDefinitionView(c.Id, c.Name, c.Type, c.IntervalSeconds, CheckParameters.Parse(c.ParametersJson), c.WarningThreshold,
                c.CriticalThreshold, c.FailuresBeforeAlert, c.AppliesTo, c.Enabled);
    }

    /// <summary>Adds (checkId null) or changes a check that exists only on this endpoint. It runs whatever the endpoint class.</summary>
    public async Task<ServiceResult<Guid>> SaveEndpointCheckAsync(Caller caller, Guid endpointId, Guid? checkId, CheckDefinitionInput input,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var (endpoint, problem) = await LoadManagedEndpointAsync(db, endpointId, cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult<Guid>.Fail(problem!.Problem!);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        CheckDefinition check;
        if (checkId is { } id)
        {
            var existing = await db.CheckDefinitions.SingleOrDefaultAsync(c => c.Id == id && c.EndpointId == endpointId, cancellationToken);
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
            check = new CheckDefinition { Id = Guid.NewGuid(), EndpointId = endpointId, ClientId = endpoint.ClientId, CreatedAt = now };
        }

        if (!Enum.IsDefined(input.Type))
        {
            return ServiceResult<Guid>.Fail("Choose a check type.");
        }

        var parameters = CheckCatalog.CleanParameters(input.Type, input.Parameters);
        if (input.Type == CheckType.Script && await ScriptChecks.BindAsync(db, endpoint.ClientId, parameters, cancellationToken) is { } scriptProblem)
        {
            return ServiceResult<Guid>.Fail(scriptProblem);
        }

        if (!CheckCatalog.IsSupported(input.Type, parameters, endpoint.OsPlatform))
        {
            return ServiceResult<Guid>.Fail(input.Type == CheckType.Script
                ? "The script's language does not run on this endpoint's operating system. Choose another script."
                : $"{CheckCatalog.Get(input.Type).Label} checks run on {CheckCatalog.PlatformLabel(CheckCatalog.Get(input.Type).Platforms)}, not on this endpoint. Choose another check type.");
        }

        check.Name = input.Name?.Trim() ?? string.Empty;
        check.Type = input.Type;
        check.IntervalSeconds = input.IntervalSeconds;
        check.ParametersJson = CheckParameters.Serialize(parameters);
        check.WarningThreshold = input.WarningThreshold;
        check.CriticalThreshold = input.CriticalThreshold;
        check.FailuresBeforeAlert = input.FailuresBeforeAlert;
        check.AppliesTo = CheckAppliesTo.All;
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

        Changed(db, caller, endpoint, now, new
        {
            Change = checkId is null ? "endpoint check added" : "endpoint check changed",
            Check = check.Name,
            Type = check.Type.ToString(),
            check.IntervalSeconds,
            check.WarningThreshold,
            check.CriticalThreshold,
            check.Enabled
        });
        await db.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(endpointId, cancellationToken);
        return ServiceResult<Guid>.Ok(check.Id);
    }

    public async Task<ServiceResult> DeleteEndpointCheckAsync(Caller caller, Guid endpointId, Guid checkId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult.NotFound("endpoint");
        }

        // Deleting is allowed on an agent-only endpoint too: it only removes configuration that no longer runs.
        var check = await db.CheckDefinitions.SingleOrDefaultAsync(c => c.Id == checkId && c.EndpointId == endpointId, cancellationToken);
        if (check is null)
        {
            return ServiceResult.NotFound("check");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.CheckDefinitions.Remove(check);
        Changed(db, caller, endpoint, now, new { Change = "endpoint check deleted", Check = check.Name });
        await db.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(endpointId, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>The services the agent reported in the latest inventory, sorted by display name. Empty when there is no inventory.</summary>
    public async Task<IReadOnlyList<ServiceOption>> GetServicesAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var json = await db.InventorySnapshots.AsNoTracking().Where(i => i.EndpointId == endpointId).Select(i => i.ServicesJson)
            .FirstOrDefaultAsync(cancellationToken);
        return ServiceOptions.Parse(json, _logger);
    }

    public async Task<EndpointTemplateLinks?> GetTemplateLinksAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var site = await db.SiteMonitoringTemplates.AsNoTracking()
            .Where(l => l.SiteId == endpoint.SiteId)
            .OrderBy(l => l.MonitoringTemplate!.Name)
            .Select(l => new LinkOption(l.MonitoringTemplateId, l.MonitoringTemplate!.Name, l.MonitoringTemplate.ClientId == null))
            .ToListAsync(cancellationToken);
        var linked = await db.EndpointMonitoringTemplates.AsNoTracking()
            .Where(l => l.EndpointId == endpointId)
            .OrderBy(l => l.MonitoringTemplate!.Name)
            .Select(l => new LinkOption(l.MonitoringTemplateId, l.MonitoringTemplate!.Name, l.MonitoringTemplate.ClientId == null))
            .ToListAsync(cancellationToken);
        var available = await db.MonitoringTemplates.AsNoTracking()
            .Where(t => t.ClientId == null || t.ClientId == endpoint.ClientId)
            .OrderBy(t => t.ClientId != null).ThenBy(t => t.Name)
            .Select(t => new LinkOption(t.Id, t.Name, t.ClientId == null))
            .ToListAsync(cancellationToken);
        return new EndpointTemplateLinks(site, linked, available);
    }

    /// <summary>Sets the monitoring templates linked to this endpoint only (on top of those of its site).</summary>
    public async Task<ServiceResult> SetEndpointTemplatesAsync(Caller caller, Guid endpointId, IReadOnlyCollection<Guid> monitoringTemplateIds,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var (endpoint, problem) = await LoadManagedEndpointAsync(db, endpointId, cancellationToken);
        if (endpoint is null)
        {
            return problem!;
        }

        var wanted = monitoringTemplateIds.ToHashSet();
        var allowed = await db.MonitoringTemplates.AsNoTracking()
            .Where(t => wanted.Contains(t.Id) && (t.ClientId == null || t.ClientId == endpoint.ClientId))
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(cancellationToken);
        if (allowed.Count != wanted.Count)
        {
            return ServiceResult.NotFound("monitoring template");
        }

        var current = await db.EndpointMonitoringTemplates.Where(l => l.EndpointId == endpointId).ToListAsync(cancellationToken);
        var removed = current.Where(l => !wanted.Contains(l.MonitoringTemplateId)).ToList();
        var added = wanted.Where(id => current.All(l => l.MonitoringTemplateId != id)).ToList();
        if (removed.Count == 0 && added.Count == 0)
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.EndpointMonitoringTemplates.RemoveRange(removed);
        foreach (var id in added)
        {
            db.EndpointMonitoringTemplates.Add(new EndpointMonitoringTemplate
            {
                EndpointId = endpointId,
                ClientId = endpoint.ClientId,
                MonitoringTemplateId = id,
                CreatedAt = now,
                CreatedByUserId = caller.UserId
            });
        }

        var names = await db.MonitoringTemplates.AsNoTracking()
            .Where(t => removed.Select(l => l.MonitoringTemplateId).Contains(t.Id))
            .Select(t => t.Name)
            .ToListAsync(cancellationToken);
        Changed(db, caller, endpoint, now, new
        {
            Change = "monitoring templates changed",
            Added = allowed.Where(t => added.Contains(t.Id)).Select(t => t.Name).ToList(),
            Removed = names
        });
        await db.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(endpointId, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Asks the agent to run a check now. With <paramref name="reset"/> the open alerts of the check are resolved and its
    /// state shows "re-run requested" until the new result arrives. Returns whether the endpoint is online right now.
    /// </summary>
    public async Task<ServiceResult<bool>> RequestRunAsync(Caller caller, Guid endpointId, Guid checkId, bool reset,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<bool>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var (endpoint, problem) = await LoadManagedEndpointAsync(db, endpointId, cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult<bool>.Fail(problem!.Problem!);
        }

        var checks = await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: false, cancellationToken);
        if (checks.FirstOrDefault(c => c.Id == checkId) is not { } check)
        {
            return ServiceResult<bool>.Fail("This check does not run on this endpoint. Enable it for the endpoint first.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var lastMinute = now.AddMinutes(-1);
        if (await db.CheckRunRequests.CountAsync(r => r.EndpointId == endpointId && r.RequestedByUserId == caller.UserId && r.RequestedAt > lastMinute,
                cancellationToken) >= MaxRunRequestsPerMinute)
        {
            return ServiceResult<bool>.Fail("You asked for many check runs on this endpoint in the last minute. Wait a moment and try again.");
        }

        var gap = now - MinimumRunGap;
        if (await db.CheckRunRequests.AnyAsync(r => r.EndpointId == endpointId && r.CheckDefinitionId == checkId && r.RequestedAt > gap, cancellationToken))
        {
            return ServiceResult<bool>.Fail("This check was asked to run less than 30 seconds ago. Wait for its result, then try again.");
        }

        var request = new CheckRunRequest
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpointId,
            CheckDefinitionId = checkId,
            Reset = reset,
            RequestedByUserId = caller.UserId,
            RequestedByName = caller.Name.Length <= 200 ? caller.Name : caller.Name[..200],
            RequestedAt = now,
            ExpiresAt = now + RunRequestLifetime
        };
        db.CheckRunRequests.Add(request);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(reset ? AuditActions.CheckReset : AuditActions.CheckRunRequested, "Endpoint",
            endpointId.ToString(), endpoint.ClientId, new { endpoint.Hostname, Check = check.Name, RequestId = request.Id }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(endpoint.IsOnline);
    }

    /// <summary>Loads the endpoint for a change; refuses when it does not exist or is not managed (license included).</summary>
    private async Task<(Endpoint? Endpoint, ServiceResult? Problem)> LoadManagedEndpointAsync(FleetifyDbContext db, Guid endpointId,
        CancellationToken cancellationToken)
    {
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return (null, ServiceResult.NotFound("endpoint"));
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        if (TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed)
        {
            var reason = endpoint.Tier == EndpointTier.Managed
                ? "Checks are paused on every endpoint because the license has expired. Load a new license in Settings."
                : new TierRequiredException(endpoint.Id, ManagedFeature.Checks).Message;
            return (null, ServiceResult.Fail(reason));
        }

        return (endpoint, null);
    }

    /// <summary>Adds the configuration change event (the signer re-signs; unchanged content is skipped) and the audit entry.</summary>
    private static void Changed(FleetifyDbContext db, Caller caller, Endpoint endpoint, DateTime now, object details)
    {
        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Endpoint, ScopeId = endpoint.Id, CreatedAt = now });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.EndpointChecksChanged, "Endpoint", endpoint.Id.ToString(), endpoint.ClientId,
            new { endpoint.Hostname, Details = details }), now));
    }

    private async Task PublishStatusAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        try
        {
            await _bus.PublishAsync(NotificationChannels.EndpointStatus, endpointId.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish a status notification for endpoint {EndpointId}", endpointId);
        }
    }
}
