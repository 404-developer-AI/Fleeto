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
/// The policy or patch policy of one level (0.6.0): what is chosen per class slot, which slots the client template set, and
/// what servers and workstations get without a choice here.
/// </summary>
public sealed record SlotLinks(ClassChoice Own, IReadOnlyCollection<CheckAppliesTo> FromClientTemplate, InheritedLink? InheritedServer,
    InheritedLink? InheritedWorkstation);

/// <summary>
/// The links of one client, site or endpoint (0.6.0) and what applies without them. The policy and patch policy of the
/// most specific level win, per class; monitoring templates add up, so the inherited ones always apply as well.
/// </summary>
/// <param name="EndpointClass">The class of the endpoint on endpoint level: it has one choice, for its own class.</param>
/// <param name="TemplatesAllowed">False for an endpoint that is not managed: it runs no checks, so it links no monitoring templates.</param>
/// <param name="OtherAutomations">Automations in the patch management product of this client that Fleeto does not manage.</param>
public sealed record LevelLinks(
    LinkLevel Level,
    Guid ClientId,
    EndpointClass? EndpointClass,
    SlotLinks Policy,
    SlotLinks PatchPolicy,
    IReadOnlyList<LinkedTemplate> MonitoringTemplates,
    IReadOnlyList<InheritedLink> InheritedTemplates,
    bool TemplatesAllowed,
    LinkChoices Choices,
    IReadOnlyList<string> OtherAutomations);

/// <summary>
/// The links to save on one level. A <see cref="ClassChoice"/> holds one choice for every endpoint or one per class; null
/// means no link, so the wider level applies. An endpoint uses <see cref="ClassChoice.All"/> only.
/// </summary>
public sealed record LinksInput(ClassChoice Policy, ClassChoice PatchPolicy, IReadOnlyCollection<Guid> MonitoringTemplateIds);

/// <summary>
/// Policies, patch policies and monitoring templates on client, site and endpoint level (0.6.0), read and saved together so
/// the dialogs show one coherent picture. The rule that decides what applies lives in <see cref="EffectivePolicyRules"/>.
/// <list type="bullet">
/// <item>A client or site has one policy for every endpoint, or one for servers and one for workstations; a slot takes only
/// a policy that is for that class (or for every endpoint). An endpoint has one, for its own class.</item>
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
        var wider = chain.Wider(level).ToList();

        var templates = own.Templates.OrderBy(t => t.Name).Select(t => new LinkedTemplate(t.Id, t.Name, t.IsGlobal, t.Source)).ToList();
        var inheritedTemplates = wider.SelectMany(l => l.Templates
                .Where(t => target.Class is not { } cls || EffectivePolicyRules.IsFor(t.AppliesTo, cls))
                .Select(t => new InheritedLink(t.Id, t.Name, l.Level)))
            .Where(t => templates.All(o => o.Id != t.Id))
            .DistinctBy(t => t.Id)
            .OrderBy(t => t.Name)
            .ToList();

        var managed = level != LinkLevel.Endpoint ||
                      TierRules.EffectiveTier(target.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) == EndpointTier.Managed;
        return new LevelLinks(level, target.ClientId, target.Class,
            Slots(own.Policies, wider.Select(l => (l.Level, l.Policies)), chain.DefaultPolicy),
            Slots(own.PatchPolicies, wider.Select(l => (l.Level, l.PatchPolicies)), null),
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

        if ((Shape(level, input.Policy, "policy") ?? Shape(level, input.PatchPolicy, "patch policy")) is { } shapeProblem)
        {
            return ServiceResult.Fail(shapeProblem);
        }

        var policyIds = input.Policy.Ids.Distinct().ToList();
        var policies = await db.Policies.AsNoTracking()
            .Where(p => policyIds.Contains(p.Id) && (p.ClientId == null || p.ClientId == clientId))
            .ToDictionaryAsync(p => p.Id, p => (p.Name, p.AppliesTo), cancellationToken);
        if (policies.Count != policyIds.Count)
        {
            return ServiceResult.NotFound("policy");
        }

        var patchIds = input.PatchPolicy.Ids.Distinct().ToList();
        var patchPolicies = await db.PatchPolicies.AsNoTracking()
            .Where(p => patchIds.Contains(p.Id) && (p.ClientId == null || p.ClientId == clientId))
            .ToDictionaryAsync(p => p.Id, p => (p.Name, p.AppliesTo), cancellationToken);
        if (patchPolicies.Count != patchIds.Count)
        {
            return ServiceResult.NotFound("patch policy");
        }

        if ((Fits(level, target.Class, input.Policy, policies, "policy") ??
             Fits(level, target.Class, input.PatchPolicy, patchPolicies, "patch policy")) is { } fitProblem)
        {
            return ServiceResult.Fail(fitProblem);
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

        if (own.Policies.Any(l => l.Source == LinkSource.ClientTemplate && input.Policy[l.Slot] is null))
        {
            return ServiceResult.Fail("The client template links this policy and would link it again. Change the client template, or choose another policy here.");
        }

        if (own.PatchPolicies.Any(l => l.Source == LinkSource.ClientTemplate && input.PatchPolicy[l.Slot] is null))
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

        var changedPolicySlots = ClassChoice.Slots.Where(s => Current(own.Policies, s) != input.Policy[s]).ToList();
        var changedPatchSlots = ClassChoice.Slots.Where(s => Current(own.PatchPolicies, s) != input.PatchPolicy[s]).ToList();
        if (changedPolicySlots.Count == 0 && changedPatchSlots.Count == 0 && !templatesChanged)
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var slot in changedPolicySlots)
        {
            await SetPolicyAsync(db, level, target, slot, input.Policy[slot], caller.UserId, now, cancellationToken);
        }

        foreach (var slot in changedPatchSlots)
        {
            await SetPatchPolicyAsync(db, level, target, slot, input.PatchPolicy[slot], caller.UserId, now, cancellationToken);
        }

        if (templatesChanged)
        {
            await SetTemplatesAsync(db, level, target, removedTemplates.Select(t => t.Id).ToList(), addedTemplates, caller.UserId, now,
                cancellationToken);
        }

        if (changedPolicySlots.Count > 0 || templatesChanged)
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
            Policy = changedPolicySlots.Count > 0 ? Describe(input.Policy, policies) : null,
            PatchPolicy = changedPatchSlots.Count > 0 ? Describe(input.PatchPolicy, patchPolicies) : null,
            AddedMonitoringTemplates = allowed.Where(t => addedTemplates.Contains(t.Id)).Select(t => t.Name).ToList(),
            RemovedMonitoringTemplates = removedTemplates.Select(t => t.Name).ToList()
        }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (changedPatchSlots.Count > 0)
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

    /// <summary>
    /// The policy each site of a client passes on, in words: "Servers weekly", or "Servers monthly (servers), Office
    /// (workstations)" when servers and workstations get a different one.
    /// </summary>
    internal static async Task<Dictionary<Guid, string>> SitePolicyNamesAsync(FleetoDbContext db, Guid clientId, CancellationToken cancellationToken)
    {
        var siteLinks = await db.SitePolicies.AsNoTracking().Where(l => l.ClientId == clientId)
            .Select(l => new { l.SiteId, l.AppliesTo, l.PolicyId, l.Policy!.Name, PolicyAppliesTo = l.Policy.AppliesTo })
            .ToListAsync(cancellationToken);
        var clientLinks = await db.ClientPolicies.AsNoTracking().Where(l => l.ClientId == clientId)
            .Select(l => new { l.AppliesTo, l.PolicyId, l.Policy!.Name, PolicyAppliesTo = l.Policy.AppliesTo })
            .ToListAsync(cancellationToken);
        var fallback = await db.Policies.IgnoreQueryFilters().AsNoTracking().Where(p => p.IsDefault).Select(p => p.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "Default policy";
        var names = siteLinks.Select(l => (l.PolicyId, l.Name)).Concat(clientLinks.Select(l => (l.PolicyId, l.Name)))
            .DistinctBy(l => l.PolicyId).ToDictionary(l => l.PolicyId, l => l.Name);
        var siteIds = await db.Sites.AsNoTracking().Where(s => s.ClientId == clientId).Select(s => s.Id).ToListAsync(cancellationToken);

        return siteIds.ToDictionary(siteId => siteId, siteId =>
        {
            var links = clientLinks.Select(l => new LevelLink(LinkLevel.Client, l.AppliesTo, l.PolicyId, l.PolicyAppliesTo))
                .Concat(siteLinks.Where(l => l.SiteId == siteId).Select(l => new LevelLink(LinkLevel.Site, l.AppliesTo, l.PolicyId, l.PolicyAppliesTo)))
                .ToList();
            var server = EffectivePolicyRules.Resolve(EndpointClass.Server, links) is { } s ? names[s.PolicyId] : fallback;
            var workstation = EffectivePolicyRules.Resolve(EndpointClass.Workstation, links) is { } w ? names[w.PolicyId] : fallback;
            return server == workstation ? server : $"{server} (servers), {workstation} (workstations)";
        });
    }

    /// <summary>A client or site holds one choice for every endpoint or one per class, never both; an endpoint one only.</summary>
    private static string? Shape(LinkLevel level, ClassChoice choice, string what)
    {
        if (level == LinkLevel.Endpoint && (choice.Server is not null || choice.Workstation is not null))
        {
            return $"An endpoint has one {what}, for its own class.";
        }

        return choice.All is not null && (choice.Server is not null || choice.Workstation is not null)
            ? $"Choose one {what} for every endpoint, or one for servers and one for workstations, not both."
            : null;
    }

    /// <summary>A slot takes only what is for its class: a server slot a policy for servers or every endpoint, and so on.</summary>
    private static string? Fits(LinkLevel level, EndpointClass? endpointClass, ClassChoice choice,
        IReadOnlyDictionary<Guid, (string Name, CheckAppliesTo AppliesTo)> chosen, string what)
    {
        foreach (var slot in ClassChoice.Slots)
        {
            if (choice[slot] is not { } id)
            {
                continue;
            }

            var (name, appliesTo) = chosen[id];
            var fits = level == LinkLevel.Endpoint
                ? endpointClass is { } cls && EffectivePolicyRules.IsFor(appliesTo, cls)
                : appliesTo == CheckAppliesTo.All || appliesTo == slot;
            if (!fits)
            {
                return level == LinkLevel.Endpoint
                    ? $"The {what} {name} is for {Plural(appliesTo)}, and this endpoint is not one."
                    : slot == CheckAppliesTo.All
                        ? $"The {what} {name} is for {Plural(appliesTo)} only. Choose it under \"Different for servers and workstations\"."
                        : $"The {what} {name} is for {Plural(appliesTo)}, not for {Plural(slot)}.";
            }
        }

        return null;
    }

    private static string Plural(CheckAppliesTo appliesTo) => appliesTo switch
    {
        CheckAppliesTo.Server => "servers",
        CheckAppliesTo.Workstation => "workstations",
        _ => "every endpoint"
    };

    private static object Describe(ClassChoice choice, IReadOnlyDictionary<Guid, (string Name, CheckAppliesTo AppliesTo)> names) =>
        choice.Split
            ? new { Servers = choice.Server is { } s ? names[s].Name : "Inherited", Workstations = choice.Workstation is { } w ? names[w].Name : "Inherited" }
            : choice.All is { } a ? names[a].Name : "Inherited";

    private static Guid? Current(IEnumerable<Linked> links, CheckAppliesTo slot) => links.FirstOrDefault(l => l.Slot == slot)?.Id;

    /// <summary>The choice of one level, and what servers and workstations get from the wider levels.</summary>
    private static SlotLinks Slots(IReadOnlyList<Linked> own, IEnumerable<(LinkLevel Level, IReadOnlyList<Linked> Links)> wider, Linked? fallback)
    {
        var widerLinks = wider.SelectMany(w => w.Links.Select(l => (w.Level, Link: l))).ToList();
        var candidates = widerLinks.Select(w => new LevelLink(w.Level, w.Link.Slot, w.Link.Id, w.Link.AppliesTo)).ToList();

        InheritedLink? For(EndpointClass endpointClass)
        {
            if (EffectivePolicyRules.Resolve(endpointClass, candidates) is { } link)
            {
                var found = widerLinks.First(w => w.Level == link.Level && w.Link.Slot == link.Slot);
                return new InheritedLink(found.Link.Id, found.Link.Name, found.Level);
            }

            return fallback is null ? null : new InheritedLink(fallback.Id, fallback.Name, LinkLevel.None);
        }

        var choice = new ClassChoice(Current(own, CheckAppliesTo.All), Current(own, CheckAppliesTo.Server), Current(own, CheckAppliesTo.Workstation));
        return new SlotLinks(choice, own.Where(l => l.Source == LinkSource.ClientTemplate).Select(l => l.Slot).ToList(),
            For(EndpointClass.Server), For(EndpointClass.Workstation));
    }

    private static async Task SetPolicyAsync(FleetoDbContext db, LinkLevel level, Target target, CheckAppliesTo slot, Guid? policyId, Guid userId,
        DateTime now, CancellationToken cancellationToken)
    {
        switch (level)
        {
            case LinkLevel.Client:
                await db.ClientPolicies.Where(l => l.ClientId == target.ClientId && l.AppliesTo == slot).ExecuteDeleteAsync(cancellationToken);
                if (policyId is { } clientPolicy)
                {
                    db.ClientPolicies.Add(new ClientPolicy
                    {
                        ClientId = target.ClientId, AppliesTo = slot, PolicyId = clientPolicy, Source = LinkSource.Manual, CreatedAt = now
                    });
                }

                break;
            case LinkLevel.Site:
                await db.SitePolicies.Where(l => l.SiteId == target.SiteId && l.AppliesTo == slot).ExecuteDeleteAsync(cancellationToken);
                if (policyId is { } sitePolicy)
                {
                    db.SitePolicies.Add(new SitePolicy
                    {
                        SiteId = target.SiteId!.Value, ClientId = target.ClientId, AppliesTo = slot, PolicyId = sitePolicy, Source = LinkSource.Manual,
                        CreatedAt = now
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

    private static async Task SetPatchPolicyAsync(FleetoDbContext db, LinkLevel level, Target target, CheckAppliesTo slot, Guid? patchPolicyId,
        Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        switch (level)
        {
            case LinkLevel.Client:
                await db.ClientPatchPolicies.Where(l => l.ClientId == target.ClientId && l.AppliesTo == slot).ExecuteDeleteAsync(cancellationToken);
                if (patchPolicyId is { } clientPatch)
                {
                    db.ClientPatchPolicies.Add(new ClientPatchPolicy
                    {
                        ClientId = target.ClientId, AppliesTo = slot, PatchPolicyId = clientPatch, Source = LinkSource.Manual, CreatedAt = now
                    });
                }

                break;
            case LinkLevel.Site:
                await db.SitePatchPolicies.Where(l => l.SiteId == target.SiteId && l.AppliesTo == slot).ExecuteDeleteAsync(cancellationToken);
                if (patchPolicyId is { } sitePatch)
                {
                    db.SitePatchPolicies.Add(new SitePatchPolicy
                    {
                        SiteId = target.SiteId!.Value, ClientId = target.ClientId, AppliesTo = slot, PatchPolicyId = sitePatch, Source = LinkSource.Manual,
                        CreatedAt = now
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
            .Select(p => new LinkOption(p.Id, p.Name, p.ClientId == null, p.IsDefault, p.AppliesTo))
            .ToListAsync(cancellationToken);
        var patchPolicies = await db.PatchPolicies.AsNoTracking()
            .Where(p => p.ClientId == null || p.ClientId == clientId)
            .OrderBy(p => p.ClientId != null).ThenBy(p => p.Name)
            .Select(p => new LinkOption(p.Id, p.Name, p.ClientId == null, false, p.AppliesTo))
            .ToListAsync(cancellationToken);
        var templates = await db.MonitoringTemplates.AsNoTracking()
            .Where(t => t.ClientId == null || t.ClientId == clientId)
            .OrderBy(t => t.ClientId != null).ThenBy(t => t.Name)
            .Select(t => new LinkOption(t.Id, t.Name, t.ClientId == null, false, t.AppliesTo))
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
                .Select(c => new Target(c.Id, null, null, c.Name, EndpointTier.AgentOnly, null)).SingleOrDefaultAsync(cancellationToken),
            LinkLevel.Site => await db.Sites.AsNoTracking().Where(s => s.Id == id)
                .Select(s => new Target(s.ClientId, s.Id, null, s.Name, EndpointTier.AgentOnly, null)).SingleOrDefaultAsync(cancellationToken),
            LinkLevel.Endpoint => await db.Endpoints.AsNoTracking().Where(e => e.Id == id)
                .Select(e => new Target(e.ClientId, e.SiteId, e.Id, e.Hostname, e.Tier, e.ClassOverride ?? e.DetectedClass))
                .SingleOrDefaultAsync(cancellationToken),
            _ => null
        };

    /// <summary>The links of the client, and of the site and endpoint when the target has them.</summary>
    private static async Task<Chain> ChainAsync(FleetoDbContext db, Target target, CancellationToken cancellationToken)
    {
        var levels = new List<LevelState>
        {
            new(LinkLevel.Client,
                await db.ClientPolicies.AsNoTracking().Where(l => l.ClientId == target.ClientId)
                    .Select(l => new Linked(l.PolicyId, l.Policy!.Name, l.Policy.ClientId == null, l.Source, l.AppliesTo, l.Policy.AppliesTo))
                    .ToListAsync(cancellationToken),
                await db.ClientPatchPolicies.AsNoTracking().Where(l => l.ClientId == target.ClientId)
                    .Select(l => new Linked(l.PatchPolicyId, l.PatchPolicy!.Name, l.PatchPolicy.ClientId == null, l.Source, l.AppliesTo, l.PatchPolicy.AppliesTo))
                    .ToListAsync(cancellationToken),
                await db.ClientMonitoringTemplates.AsNoTracking().Where(l => l.ClientId == target.ClientId)
                    .Select(l => new Linked(l.MonitoringTemplateId, l.MonitoringTemplate!.Name, l.MonitoringTemplate.ClientId == null, l.Source,
                        CheckAppliesTo.All, l.MonitoringTemplate.AppliesTo))
                    .ToListAsync(cancellationToken))
        };

        if (target.SiteId is { } siteId)
        {
            levels.Add(new LevelState(LinkLevel.Site,
                await db.SitePolicies.AsNoTracking().Where(l => l.SiteId == siteId)
                    .Select(l => new Linked(l.PolicyId, l.Policy!.Name, l.Policy.ClientId == null, l.Source, l.AppliesTo, l.Policy.AppliesTo))
                    .ToListAsync(cancellationToken),
                await db.SitePatchPolicies.AsNoTracking().Where(l => l.SiteId == siteId)
                    .Select(l => new Linked(l.PatchPolicyId, l.PatchPolicy!.Name, l.PatchPolicy.ClientId == null, l.Source, l.AppliesTo, l.PatchPolicy.AppliesTo))
                    .ToListAsync(cancellationToken),
                await db.SiteMonitoringTemplates.AsNoTracking().Where(l => l.SiteId == siteId)
                    .Select(l => new Linked(l.MonitoringTemplateId, l.MonitoringTemplate!.Name, l.MonitoringTemplate.ClientId == null, l.Source,
                        CheckAppliesTo.All, l.MonitoringTemplate.AppliesTo))
                    .ToListAsync(cancellationToken)));
        }

        if (target.EndpointId is { } endpointId)
        {
            levels.Add(new LevelState(LinkLevel.Endpoint,
                await db.EndpointPolicies.AsNoTracking().Where(l => l.EndpointId == endpointId)
                    .Select(l => new Linked(l.PolicyId, l.Policy!.Name, l.Policy.ClientId == null, LinkSource.Manual, CheckAppliesTo.All, l.Policy.AppliesTo))
                    .ToListAsync(cancellationToken),
                await db.EndpointPatchPolicies.AsNoTracking().Where(l => l.EndpointId == endpointId)
                    .Select(l => new Linked(l.PatchPolicyId, l.PatchPolicy!.Name, l.PatchPolicy.ClientId == null, LinkSource.Manual, CheckAppliesTo.All,
                        l.PatchPolicy.AppliesTo))
                    .ToListAsync(cancellationToken),
                await db.EndpointMonitoringTemplates.AsNoTracking().Where(l => l.EndpointId == endpointId)
                    .Select(l => new Linked(l.MonitoringTemplateId, l.MonitoringTemplate!.Name, l.MonitoringTemplate.ClientId == null, LinkSource.Manual,
                        CheckAppliesTo.All, l.MonitoringTemplate.AppliesTo))
                    .ToListAsync(cancellationToken)));
        }

        var defaultPolicy = await db.Policies.IgnoreQueryFilters().AsNoTracking().Where(p => p.IsDefault)
            .Select(p => new Linked(p.Id, p.Name, true, LinkSource.Manual, CheckAppliesTo.All, CheckAppliesTo.All)).FirstOrDefaultAsync(cancellationToken);
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

    private sealed record Target(Guid ClientId, Guid? SiteId, Guid? EndpointId, string Name, EndpointTier Tier, EndpointClass? Class);

    /// <param name="Slot">The class slot of the link (always All for monitoring templates and endpoint links).</param>
    /// <param name="AppliesTo">The endpoints the linked policy or template itself is for.</param>
    private sealed record Linked(Guid Id, string Name, bool IsGlobal, LinkSource Source, CheckAppliesTo Slot, CheckAppliesTo AppliesTo);

    private sealed record LevelState(LinkLevel Level, IReadOnlyList<Linked> Policies, IReadOnlyList<Linked> PatchPolicies, List<Linked> Templates);

    private sealed record Chain(List<LevelState> Levels, Linked? DefaultPolicy)
    {
        public LevelState Of(LinkLevel level) => Levels.Single(l => l.Level == level);

        /// <summary>The wider levels, the nearest first: for an endpoint its site, then its client.</summary>
        public IEnumerable<LevelState> Wider(LinkLevel level) => Levels.Where(l => l.Level < level).OrderByDescending(l => l.Level);
    }
}
