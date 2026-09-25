using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>What a client, site or endpoint can link: the global ones and those of its own client.</summary>
public sealed record LinkChoices(IReadOnlyList<LinkOption> Policies, IReadOnlyList<LinkOption> PatchPolicies, IReadOnlyList<LinkOption> MonitoringTemplates);

/// <summary>A link that applies from a wider level, with the level it sits on (<see cref="LinkLevel.None"/>: the default policy).</summary>
public sealed record InheritedLink(Guid Id, string Name, LinkLevel Level);

/// <summary>
/// The links of one client, site or endpoint (0.6.0) and what applies without them. The policy and patch policy of the
/// most specific level win; monitoring templates add up, so the inherited ones always apply as well.
/// </summary>
/// <param name="TemplatesAllowed">False for an endpoint that is not managed: it runs no checks, so it links no monitoring templates.</param>
/// <param name="OtherAutomations">Automations in the patch management product of this client that Fleeto does not manage.</param>
public sealed record LevelLinks(
    LinkLevel Level,
    Guid ClientId,
    Guid? PolicyId,
    bool PolicyFromClientTemplate,
    InheritedLink? InheritedPolicy,
    Guid? PatchPolicyId,
    bool PatchPolicyFromClientTemplate,
    InheritedLink? InheritedPatchPolicy,
    IReadOnlyList<LinkedTemplate> MonitoringTemplates,
    IReadOnlyList<InheritedLink> InheritedTemplates,
    bool TemplatesAllowed,
    LinkChoices Choices,
    IReadOnlyList<string> OtherAutomations);

/// <summary>The links to save on one level. Null means no link: the wider level applies.</summary>
public sealed record LinksInput(Guid? PolicyId, Guid? PatchPolicyId, IReadOnlyCollection<Guid> MonitoringTemplateIds);

/// <summary>
/// Policies, patch policies and monitoring templates on client, site and endpoint level (0.6.0), read and saved together so
/// the dialogs show one coherent picture. The rule that decides what applies lives in <see cref="EffectivePolicyRules"/>.
/// <list type="bullet">
/// <item>A link the client template made can be replaced by another one, which makes it a link of the technician's own;
/// it cannot be removed, because the client template would link it again.</item>
/// <item>A monitoring template the client template linked cannot be removed here.</item>
/// <item>An endpoint that is not managed runs no checks and gets no monitoring templates of its own.</item>
/// </list>
/// </summary>
public sealed class LinkService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<LinkService> _logger;

    public LinkService(IFleetoDbContextFactory dbFactory, LicenseService licenses, INotificationBus bus, TimeProvider time,
        ILogger<LinkService> logger)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public async Task<LevelLinks?> GetAsync(Caller caller, LinkLevel level, Guid id, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var target = await TargetAsync(db, level, id, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var chain = await ChainAsync(db, target, cancellationToken);
        var own = chain.Of(level);
        var wider = chain.Wider(level);

        var templates = own.Templates.OrderBy(t => t.Name).Select(t => new LinkedTemplate(t.Id, t.Name, t.IsGlobal, t.Source)).ToList();
        var inheritedTemplates = wider.SelectMany(l => l.Templates.Select(t => new InheritedLink(t.Id, t.Name, l.Level)))
            .Where(t => templates.All(o => o.Id != t.Id))
            .DistinctBy(t => t.Id)
            .OrderBy(t => t.Name)
            .ToList();

        var inheritedPolicy = wider.Select(l => l.Policy is { } p ? new InheritedLink(p.Id, p.Name, l.Level) : null).FirstOrDefault(p => p is not null)
                              ?? (chain.DefaultPolicy is { } d ? new InheritedLink(d.Id, d.Name, LinkLevel.None) : null);
        var inheritedPatch = wider.Select(l => l.PatchPolicy is { } p ? new InheritedLink(p.Id, p.Name, l.Level) : null)
            .FirstOrDefault(p => p is not null);

        var managed = level != LinkLevel.Endpoint ||
                      TierRules.EffectiveTier(target.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) == EndpointTier.Managed;
        return new LevelLinks(level, target.ClientId,
            own.Policy?.Id, own.Policy?.Source == LinkSource.ClientTemplate, inheritedPolicy,
            own.PatchPolicy?.Id, own.PatchPolicy?.Source == LinkSource.ClientTemplate, inheritedPatch,
            templates, inheritedTemplates, managed || templates.Count > 0,
            await ChoicesAsync(db, target.ClientId, cancellationToken),
            await OtherAutomationsAsync(db, target.ClientId, cancellationToken));
    }

    /// <summary>What a client can link: global policies, patch policies and monitoring templates, and those of the client.</summary>
    public async Task<LinkChoices> GetChoicesAsync(Caller caller, Guid? clientId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await ChoicesAsync(db, clientId, cancellationToken);
    }

    public async Task<ServiceResult> SetAsync(Caller caller, LinkLevel level, Guid id, LinksInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var target = await TargetAsync(db, level, id, cancellationToken);
        if (target is null)
        {
            return ServiceResult.NotFound(level.ToString().ToLowerInvariant());
        }

        var chain = await ChainAsync(db, target, cancellationToken);
        var own = chain.Of(level);
        var clientId = target.ClientId;

        Policy? policy = null;
        if (input.PolicyId is { } policyId)
        {
            policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == policyId && (p.ClientId == null || p.ClientId == clientId),
                cancellationToken);
            if (policy is null)
            {
                return ServiceResult.NotFound("policy");
            }
        }

        PatchPolicy? patchPolicy = null;
        if (input.PatchPolicyId is { } patchPolicyId)
        {
            patchPolicy = await db.PatchPolicies.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == patchPolicyId && (p.ClientId == null || p.ClientId == clientId), cancellationToken);
            if (patchPolicy is null)
            {
                return ServiceResult.NotFound("patch policy");
            }
        }

        var wanted = input.MonitoringTemplateIds.ToHashSet();
        var allowed = await db.MonitoringTemplates.AsNoTracking()
            .Where(t => wanted.Contains(t.Id) && (t.ClientId == null || t.ClientId == clientId))
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(cancellationToken);
        if (allowed.Count != wanted.Count)
        {
            return ServiceResult.NotFound("monitoring template");
        }

        if (own.Policy is { Source: LinkSource.ClientTemplate } && policy is null)
        {
            return ServiceResult.Fail("The client template links this policy and would link it again. Change the client template, or choose another policy here.");
        }

        if (own.PatchPolicy is { Source: LinkSource.ClientTemplate } && patchPolicy is null)
        {
            return ServiceResult.Fail("The client template links this patch policy and would link it again. Change the client template, or choose another patch policy here.");
        }

        if (own.Templates.Any(t => t.Source == LinkSource.ClientTemplate && !wanted.Contains(t.Id)))
        {
            return ServiceResult.Fail("A monitoring template from the client template cannot be removed here. Change the client template, or detach the client first.");
        }

        var removedTemplates = own.Templates.Where(t => !wanted.Contains(t.Id)).ToList();
        var addedTemplates = wanted.Where(t => own.Templates.All(o => o.Id != t)).ToList();
        var templatesChanged = removedTemplates.Count > 0 || addedTemplates.Count > 0;
        if (level == LinkLevel.Endpoint && addedTemplates.Count > 0 &&
            TierRules.EffectiveTier(target.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) != EndpointTier.Managed)
        {
            return ServiceResult.Fail(new TierRequiredException(id, ManagedFeature.Checks).Message);
        }

        var policyChanged = own.Policy?.Id != policy?.Id;
        var patchChanged = own.PatchPolicy?.Id != patchPolicy?.Id;
        if (!policyChanged && !patchChanged && !templatesChanged)
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (policyChanged)
        {
            await SetPolicyAsync(db, level, target, policy?.Id, caller.UserId, now, cancellationToken);
        }

        if (patchChanged)
        {
            await SetPatchPolicyAsync(db, level, target, patchPolicy?.Id, caller.UserId, now, cancellationToken);
        }

        if (templatesChanged)
        {
            await SetTemplatesAsync(db, level, target, removedTemplates.Select(t => t.Id).ToList(), addedTemplates, caller.UserId, now,
                cancellationToken);
        }

        if (policyChanged || templatesChanged)
        {
            // The agents of the level get a new configuration; the signer skips the ones that did not change.
            var scope = level switch
            {
                LinkLevel.Client => ConfigChangeScope.Client,
                LinkLevel.Site => ConfigChangeScope.Site,
                _ => ConfigChangeScope.Endpoint
            };
            db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = scope, ScopeId = id, CreatedAt = now });
        }

        var action = level switch
        {
            LinkLevel.Client => AuditActions.ClientLinksChanged,
            LinkLevel.Site => AuditActions.SiteLinksChanged,
            _ => AuditActions.EndpointLinksChanged
        };
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(action, level.ToString(), id.ToString(), clientId, new
        {
            target.Name,
            Policy = policyChanged ? policy?.Name ?? "Inherited" : null,
            PatchPolicy = patchChanged ? patchPolicy?.Name ?? "Inherited" : null,
            AddedMonitoringTemplates = allowed.Where(t => addedTemplates.Contains(t.Id)).Select(t => t.Name).ToList(),
            RemovedMonitoringTemplates = removedTemplates.Select(t => t.Name).ToList()
        }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (patchChanged)
        {
            // The workers change the automations in the patch management product.
            await IntegrationFollow.NotifyAsync(_bus, _logger, cancellationToken);
        }

        if (level == LinkLevel.Endpoint)
        {
            await PublishStatusAsync(id, cancellationToken);
        }

        return ServiceResult.Ok();
    }

    private static async Task SetPolicyAsync(FleetoDbContext db, LinkLevel level, Target target, Guid? policyId, Guid userId, DateTime now,
        CancellationToken cancellationToken)
    {
        switch (level)
        {
            case LinkLevel.Client:
                await db.ClientPolicies.Where(l => l.ClientId == target.ClientId).ExecuteDeleteAsync(cancellationToken);
                if (policyId is { } clientPolicy)
                {
                    db.ClientPolicies.Add(new ClientPolicy { ClientId = target.ClientId, PolicyId = clientPolicy, Source = LinkSource.Manual, CreatedAt = now });
                }

                break;
            case LinkLevel.Site:
                await db.SitePolicies.Where(l => l.SiteId == target.SiteId).ExecuteDeleteAsync(cancellationToken);
                if (policyId is { } sitePolicy)
                {
                    db.SitePolicies.Add(new SitePolicy
                    {
                        SiteId = target.SiteId!.Value, ClientId = target.ClientId, PolicyId = sitePolicy, Source = LinkSource.Manual, CreatedAt = now
                    });
                }

                break;
            default:
                await db.EndpointPolicies.Where(l => l.EndpointId == target.EndpointId).ExecuteDeleteAsync(cancellationToken);
                if (policyId is { } endpointPolicy)
                {
                    db.EndpointPolicies.Add(new EndpointPolicy
                    {
                        EndpointId = target.EndpointId!.Value, ClientId = target.ClientId, PolicyId = endpointPolicy, CreatedAt = now, CreatedByUserId = userId
                    });
                }

                break;
        }
    }

    private static async Task SetPatchPolicyAsync(FleetoDbContext db, LinkLevel level, Target target, Guid? patchPolicyId, Guid userId, DateTime now,
        CancellationToken cancellationToken)
    {
        switch (level)
        {
            case LinkLevel.Client:
                await db.ClientPatchPolicies.Where(l => l.ClientId == target.ClientId).ExecuteDeleteAsync(cancellationToken);
                if (patchPolicyId is { } clientPatch)
                {
                    db.ClientPatchPolicies.Add(new ClientPatchPolicy
                    {
                        ClientId = target.ClientId, PatchPolicyId = clientPatch, Source = LinkSource.Manual, CreatedAt = now
                    });
                }

                break;
            case LinkLevel.Site:
                await db.SitePatchPolicies.Where(l => l.SiteId == target.SiteId).ExecuteDeleteAsync(cancellationToken);
                if (patchPolicyId is { } sitePatch)
                {
                    db.SitePatchPolicies.Add(new SitePatchPolicy
                    {
                        SiteId = target.SiteId!.Value, ClientId = target.ClientId, PatchPolicyId = sitePatch, Source = LinkSource.Manual, CreatedAt = now
                    });
                }

                break;
            default:
                await db.EndpointPatchPolicies.Where(l => l.EndpointId == target.EndpointId).ExecuteDeleteAsync(cancellationToken);
                if (patchPolicyId is { } endpointPatch)
                {
                    db.EndpointPatchPolicies.Add(new EndpointPatchPolicy
                    {
                        EndpointId = target.EndpointId!.Value, ClientId = target.ClientId, PatchPolicyId = endpointPatch, CreatedAt = now,
                        CreatedByUserId = userId
                    });
                }

                break;
        }
    }

    private static async Task SetTemplatesAsync(FleetoDbContext db, LinkLevel level, Target target, List<Guid> removed, List<Guid> added,
        Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        switch (level)
        {
            case LinkLevel.Client:
                await db.ClientMonitoringTemplates.Where(l => l.ClientId == target.ClientId && removed.Contains(l.MonitoringTemplateId))
                    .ExecuteDeleteAsync(cancellationToken);
                db.ClientMonitoringTemplates.AddRange(added.Select(t => new ClientMonitoringTemplate
                {
                    ClientId = target.ClientId, MonitoringTemplateId = t, Source = LinkSource.Manual, CreatedAt = now
                }));
                break;
            case LinkLevel.Site:
                await db.SiteMonitoringTemplates.Where(l => l.SiteId == target.SiteId && removed.Contains(l.MonitoringTemplateId))
                    .ExecuteDeleteAsync(cancellationToken);
                db.SiteMonitoringTemplates.AddRange(added.Select(t => new SiteMonitoringTemplate
                {
                    SiteId = target.SiteId!.Value, ClientId = target.ClientId, MonitoringTemplateId = t, Source = LinkSource.Manual, CreatedAt = now
                }));
                break;
            default:
                await db.EndpointMonitoringTemplates.Where(l => l.EndpointId == target.EndpointId && removed.Contains(l.MonitoringTemplateId))
                    .ExecuteDeleteAsync(cancellationToken);
                db.EndpointMonitoringTemplates.AddRange(added.Select(t => new EndpointMonitoringTemplate
                {
                    EndpointId = target.EndpointId!.Value, ClientId = target.ClientId, MonitoringTemplateId = t, CreatedAt = now, CreatedByUserId = userId
                }));
                break;
        }
    }

    private static async Task<LinkChoices> ChoicesAsync(FleetoDbContext db, Guid? clientId, CancellationToken cancellationToken)
    {
        var policies = await db.Policies.AsNoTracking()
            .Where(p => p.ClientId == null || p.ClientId == clientId)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.ClientId != null).ThenBy(p => p.Name)
            .Select(p => new LinkOption(p.Id, p.Name, p.ClientId == null, p.IsDefault))
            .ToListAsync(cancellationToken);
        var patchPolicies = await db.PatchPolicies.AsNoTracking()
            .Where(p => p.ClientId == null || p.ClientId == clientId)
            .OrderBy(p => p.ClientId != null).ThenBy(p => p.Name)
            .Select(p => new LinkOption(p.Id, p.Name, p.ClientId == null, false))
            .ToListAsync(cancellationToken);
        var templates = await db.MonitoringTemplates.AsNoTracking()
            .Where(t => t.ClientId == null || t.ClientId == clientId)
            .OrderBy(t => t.ClientId != null).ThenBy(t => t.Name)
            .Select(t => new LinkOption(t.Id, t.Name, t.ClientId == null, false))
            .ToListAsync(cancellationToken);
        return new LinkChoices(policies, patchPolicies, templates);
    }

    /// <summary>The names of the automations of the client's organization that Fleeto does not manage, as the workers last read them.</summary>
    internal static async Task<IReadOnlyList<string>> OtherAutomationsAsync(FleetoDbContext db, Guid clientId, CancellationToken cancellationToken)
    {
        var json = await db.IntegrationMappings.AsNoTracking().Where(m => m.ClientId == clientId).Select(m => m.OtherAutomationsJson)
            .FirstOrDefaultAsync(cancellationToken);
        return OtherAutomation.Parse(json).Select(a => a.Name).ToList();
    }

    private static async Task<Target?> TargetAsync(FleetoDbContext db, LinkLevel level, Guid id, CancellationToken cancellationToken) =>
        level switch
        {
            LinkLevel.Client => await db.Clients.AsNoTracking().Where(c => c.Id == id)
                .Select(c => new Target(c.Id, null, null, c.Name, EndpointTier.AgentOnly)).SingleOrDefaultAsync(cancellationToken),
            LinkLevel.Site => await db.Sites.AsNoTracking().Where(s => s.Id == id)
                .Select(s => new Target(s.ClientId, s.Id, null, s.Name, EndpointTier.AgentOnly)).SingleOrDefaultAsync(cancellationToken),
            LinkLevel.Endpoint => await db.Endpoints.AsNoTracking().Where(e => e.Id == id)
                .Select(e => new Target(e.ClientId, e.SiteId, e.Id, e.Hostname, e.Tier)).SingleOrDefaultAsync(cancellationToken),
            _ => null
        };

    /// <summary>The links of the client, and of the site and endpoint when the target has them.</summary>
    private static async Task<Chain> ChainAsync(FleetoDbContext db, Target target, CancellationToken cancellationToken)
    {
        var levels = new List<LevelState>
        {
            new(LinkLevel.Client,
                await db.ClientPolicies.AsNoTracking().Where(l => l.ClientId == target.ClientId)
                    .Select(l => new Linked(l.PolicyId, l.Policy!.Name, l.Policy.ClientId == null, l.Source)).FirstOrDefaultAsync(cancellationToken),
                await db.ClientPatchPolicies.AsNoTracking().Where(l => l.ClientId == target.ClientId)
                    .Select(l => new Linked(l.PatchPolicyId, l.PatchPolicy!.Name, l.PatchPolicy.ClientId == null, l.Source)).FirstOrDefaultAsync(cancellationToken),
                await db.ClientMonitoringTemplates.AsNoTracking().Where(l => l.ClientId == target.ClientId)
                    .Select(l => new Linked(l.MonitoringTemplateId, l.MonitoringTemplate!.Name, l.MonitoringTemplate.ClientId == null, l.Source))
                    .ToListAsync(cancellationToken))
        };

        if (target.SiteId is { } siteId)
        {
            levels.Add(new LevelState(LinkLevel.Site,
                await db.SitePolicies.AsNoTracking().Where(l => l.SiteId == siteId)
                    .Select(l => new Linked(l.PolicyId, l.Policy!.Name, l.Policy.ClientId == null, l.Source)).FirstOrDefaultAsync(cancellationToken),
                await db.SitePatchPolicies.AsNoTracking().Where(l => l.SiteId == siteId)
                    .Select(l => new Linked(l.PatchPolicyId, l.PatchPolicy!.Name, l.PatchPolicy.ClientId == null, l.Source)).FirstOrDefaultAsync(cancellationToken),
                await db.SiteMonitoringTemplates.AsNoTracking().Where(l => l.SiteId == siteId)
                    .Select(l => new Linked(l.MonitoringTemplateId, l.MonitoringTemplate!.Name, l.MonitoringTemplate.ClientId == null, l.Source))
                    .ToListAsync(cancellationToken)));
        }

        if (target.EndpointId is { } endpointId)
        {
            levels.Add(new LevelState(LinkLevel.Endpoint,
                await db.EndpointPolicies.AsNoTracking().Where(l => l.EndpointId == endpointId)
                    .Select(l => new Linked(l.PolicyId, l.Policy!.Name, l.Policy.ClientId == null, LinkSource.Manual)).FirstOrDefaultAsync(cancellationToken),
                await db.EndpointPatchPolicies.AsNoTracking().Where(l => l.EndpointId == endpointId)
                    .Select(l => new Linked(l.PatchPolicyId, l.PatchPolicy!.Name, l.PatchPolicy.ClientId == null, LinkSource.Manual))
                    .FirstOrDefaultAsync(cancellationToken),
                await db.EndpointMonitoringTemplates.AsNoTracking().Where(l => l.EndpointId == endpointId)
                    .Select(l => new Linked(l.MonitoringTemplateId, l.MonitoringTemplate!.Name, l.MonitoringTemplate.ClientId == null, LinkSource.Manual))
                    .ToListAsync(cancellationToken)));
        }

        var defaultPolicy = await db.Policies.IgnoreQueryFilters().AsNoTracking().Where(p => p.IsDefault)
            .Select(p => new Linked(p.Id, p.Name, true, LinkSource.Manual)).FirstOrDefaultAsync(cancellationToken);
        return new Chain(levels, defaultPolicy);
    }

    private async Task PublishStatusAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        try
        {
            await _bus.PublishAsync(NotificationChannels.EndpointStatus, endpointId.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not publish a status notification for endpoint {EndpointId}", endpointId);
        }
    }

    private sealed record Target(Guid ClientId, Guid? SiteId, Guid? EndpointId, string Name, EndpointTier Tier);

    private sealed record Linked(Guid Id, string Name, bool IsGlobal, LinkSource Source);

    private sealed record LevelState(LinkLevel Level, Linked? Policy, Linked? PatchPolicy, List<Linked> Templates);

    private sealed record Chain(List<LevelState> Levels, Linked? DefaultPolicy)
    {
        public LevelState Of(LinkLevel level) => Levels.Single(l => l.Level == level);

        /// <summary>The wider levels, the nearest first: for an endpoint its site, then its client.</summary>
        public IEnumerable<LevelState> Wider(LinkLevel level) => Levels.Where(l => l.Level < level).OrderByDescending(l => l.Level);
    }
}
