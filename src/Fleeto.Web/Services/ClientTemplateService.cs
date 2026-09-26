using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record ClientTemplateListItem(Guid Id, string Name, string? Description, int SiteCount, int ClientCount);

/// <param name="Policy">The policy of the site, for every endpoint or per class (0.6.0); none follows the client.</param>
/// <param name="PatchPolicy">The patch policy of the site, for every endpoint or per class (0.6.0); none follows the client.</param>
public sealed record ClientTemplateSiteInput(Guid? Id, string? Name, string? Description, ClassChoice Policy, IReadOnlyList<Guid> MonitoringTemplateIds,
    ClassChoice PatchPolicy = default);

/// <summary>The links of the client itself (0.6.0), which its sites and endpoints follow unless they have their own.</summary>
public sealed record ClientTemplateClientLinks(ClassChoice Policy, ClassChoice PatchPolicy, IReadOnlyList<Guid> MonitoringTemplateIds)
{
    public static readonly ClientTemplateClientLinks None = new(ClassChoice.None, ClassChoice.None, []);
}

public sealed record ClientTemplateInput(string? Name, string? Description, IReadOnlyList<ClientTemplateSiteInput> Sites,
    ClientTemplateClientLinks? Client = null);

public sealed record ClientTemplateDetail(Guid Id, string Name, string? Description, int ClientCount, IReadOnlyList<ClientTemplateSiteInput> Sites,
    ClientTemplateClientLinks Client);

/// <summary>
/// Client templates: blueprints of a client and its sites, each with a policy, a patch policy and monitoring templates
/// (global ones only). Linked, not copied:
/// saving a template applies it to every client that follows it, in batches.
/// </summary>
public sealed class ClientTemplateService
{
    /// <summary>Clients synchronised per transaction when a template changes.</summary>
    internal const int SyncBatchSize = 25;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<ClientTemplateService> _logger;

    public ClientTemplateService(IFleetoDbContextFactory dbFactory, TimeProvider time, ILogger<ClientTemplateService> logger)
    {
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ClientTemplateListItem>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await db.ClientTemplates.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new ClientTemplateListItem(t.Id, t.Name, t.Description, t.Sites.Count, db.Clients.Count(c => c.ClientTemplateId == t.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<ClientTemplateDetail?> GetAsync(Caller caller, Guid templateId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var template = await db.ClientTemplates.AsNoTracking()
            .Include(t => t.Sites).ThenInclude(s => s.MonitoringTemplates)
            .Include(t => t.MonitoringTemplates)
            .SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null)
        {
            return null;
        }

        var clientCount = await db.Clients.CountAsync(c => c.ClientTemplateId == templateId, cancellationToken);
        return new ClientTemplateDetail(template.Id, template.Name, template.Description, clientCount,
            template.Sites.OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
                .Select(s => new ClientTemplateSiteInput(s.Id, s.Name, s.Description, new ClassChoice(s.PolicyId, s.ServerPolicyId, s.WorkstationPolicyId),
                    s.MonitoringTemplates.Select(m => m.MonitoringTemplateId).ToList(),
                    new ClassChoice(s.PatchPolicyId, s.ServerPatchPolicyId, s.WorkstationPatchPolicyId)))
                .ToList(),
            new ClientTemplateClientLinks(new ClassChoice(template.PolicyId, template.ServerPolicyId, template.WorkstationPolicyId),
                new ClassChoice(template.PatchPolicyId, template.ServerPatchPolicyId, template.WorkstationPatchPolicyId),
                template.MonitoringTemplates.Select(m => m.MonitoringTemplateId).ToList()));
    }

    public async Task<ServiceResult<Guid>> SaveAsync(Caller caller, Guid? templateId, ClientTemplateInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        if (await ValidateAsync(db, templateId, input, cancellationToken) is { } problem)
        {
            return ServiceResult<Guid>.Fail(problem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        ClientTemplate template;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (templateId is { } id)
            {
                var existing = await db.ClientTemplates.Include(t => t.Sites).ThenInclude(s => s.MonitoringTemplates)
                    .Include(t => t.MonitoringTemplates)
                    .SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
                if (existing is null)
                {
                    return ServiceResult<Guid>.NotFound("client template");
                }

                template = existing;
            }
            else
            {
                template = new ClientTemplate { Id = Guid.NewGuid(), CreatedAt = now };
                db.ClientTemplates.Add(template);
            }

            template.Name = input.Name!.Trim();
            template.Description = ServiceSupport.Clean(input.Description);
            template.UpdatedAt = now;

            var clientLinks = input.Client ?? ClientTemplateClientLinks.None;
            (template.PolicyId, template.ServerPolicyId, template.WorkstationPolicyId) =
                (clientLinks.Policy.All, clientLinks.Policy.Server, clientLinks.Policy.Workstation);
            (template.PatchPolicyId, template.ServerPatchPolicyId, template.WorkstationPatchPolicyId) =
                (clientLinks.PatchPolicy.All, clientLinks.PatchPolicy.Server, clientLinks.PatchPolicy.Workstation);
            var wantedForClient = clientLinks.MonitoringTemplateIds.ToHashSet();
            foreach (var link in template.MonitoringTemplates.Where(l => !wantedForClient.Contains(l.MonitoringTemplateId)).ToList())
            {
                template.MonitoringTemplates.Remove(link);
                db.ClientTemplateMonitoringTemplates.Remove(link);
            }

            foreach (var monitoringTemplateId in wantedForClient.Where(w => template.MonitoringTemplates.All(l => l.MonitoringTemplateId != w)))
            {
                var link = new ClientTemplateMonitoringTemplate { ClientTemplateId = template.Id, MonitoringTemplateId = monitoringTemplateId };
                template.MonitoringTemplates.Add(link);
                db.ClientTemplateMonitoringTemplates.Add(link);
            }

            // Template sites that are removed: detach the client sites created from them first (their links become manual),
            // because the foreign key would clear the site's reference without touching its links.
            var keptIds = input.Sites.Where(s => s.Id is not null).Select(s => s.Id!.Value).ToHashSet();
            var removed = template.Sites.Where(s => !keptIds.Contains(s.Id)).ToList();
            if (removed.Count > 0)
            {
                var removedIds = removed.Select(s => s.Id).ToList();
                var affectedSites = db.Sites.Where(s => s.ClientTemplateSiteId != null && removedIds.Contains(s.ClientTemplateSiteId.Value));
                var affectedSiteIds = await affectedSites.Select(s => s.Id).ToListAsync(cancellationToken);
                await db.SitePolicies.Where(l => l.Source == LinkSource.ClientTemplate && affectedSiteIds.Contains(l.SiteId))
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Source, LinkSource.Manual), cancellationToken);
                await db.SitePatchPolicies.Where(l => l.Source == LinkSource.ClientTemplate && affectedSiteIds.Contains(l.SiteId))
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Source, LinkSource.Manual), cancellationToken);
                await db.SiteMonitoringTemplates.Where(l => l.Source == LinkSource.ClientTemplate && affectedSiteIds.Contains(l.SiteId))
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Source, LinkSource.Manual), cancellationToken);
                await db.Sites.Where(s => affectedSiteIds.Contains(s.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClientTemplateSiteId, (Guid?)null).SetProperty(x => x.UpdatedAt, now), cancellationToken);
                foreach (var siteId in affectedSiteIds)
                {
                    db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Site, ScopeId = siteId, CreatedAt = now });
                }

                foreach (var site in removed)
                {
                    template.Sites.Remove(site);
                    db.ClientTemplateSites.Remove(site);
                }
            }

            var order = 0;
            foreach (var siteInput in input.Sites)
            {
                var site = siteInput.Id is { } siteId ? template.Sites.SingleOrDefault(s => s.Id == siteId) : null;
                if (site is null)
                {
                    site = new ClientTemplateSite { Id = Guid.NewGuid(), ClientTemplateId = template.Id };
                    template.Sites.Add(site);
                    db.ClientTemplateSites.Add(site);
                }

                site.Name = siteInput.Name!.Trim();
                site.Description = ServiceSupport.Clean(siteInput.Description);
                (site.PolicyId, site.ServerPolicyId, site.WorkstationPolicyId) =
                    (siteInput.Policy.All, siteInput.Policy.Server, siteInput.Policy.Workstation);
                (site.PatchPolicyId, site.ServerPatchPolicyId, site.WorkstationPatchPolicyId) =
                    (siteInput.PatchPolicy.All, siteInput.PatchPolicy.Server, siteInput.PatchPolicy.Workstation);
                site.SortOrder = order++;

                var wanted = siteInput.MonitoringTemplateIds.ToHashSet();
                foreach (var link in site.MonitoringTemplates.Where(l => !wanted.Contains(l.MonitoringTemplateId)).ToList())
                {
                    site.MonitoringTemplates.Remove(link);
                    db.ClientTemplateSiteMonitoringTemplates.Remove(link);
                }

                foreach (var monitoringTemplateId in wanted.Where(w => site.MonitoringTemplates.All(l => l.MonitoringTemplateId != w)))
                {
                    var link = new ClientTemplateSiteMonitoringTemplate { ClientTemplateSiteId = site.Id, MonitoringTemplateId = monitoringTemplateId };
                    site.MonitoringTemplates.Add(link);
                    db.ClientTemplateSiteMonitoringTemplates.Add(link);
                }
            }

            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(templateId is null ? AuditActions.ClientTemplateCreated : AuditActions.ClientTemplateUpdated,
                "ClientTemplate", template.Id.ToString(), null,
                new { template.Name, Sites = input.Sites.Select(s => s.Name).ToList(), RemovedSites = removed.Select(s => s.Name).ToList() }), now));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                return ServiceResult<Guid>.Fail("A client template with this name already exists, or two sites share a name. Choose other names.");
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (templateId is not null)
        {
            var failed = await SyncFollowersAsync(caller, template.Id, cancellationToken);
            if (failed > 0)
            {
                return ServiceResult<Guid>.Fail(
                    $"The client template was saved, but {failed} client{(failed == 1 ? " was" : "s were")} not updated. Save the template again to retry, and check the web log if it keeps happening.");
            }
        }

        return ServiceResult<Guid>.Ok(template.Id);
    }

    /// <summary>
    /// Applies the template to every client that follows it, <see cref="SyncBatchSize"/> clients per transaction. A failing
    /// batch is logged and counted; the sync is idempotent, so saving again retries it.
    /// </summary>
    internal async Task<int> SyncFollowersAsync(Caller caller, Guid templateId, CancellationToken cancellationToken)
    {
        List<Guid> clientIds;
        await using (var db = _dbFactory.Create(caller.Scope))
        {
            clientIds = await db.Clients.AsNoTracking().Where(c => c.ClientTemplateId == templateId).OrderBy(c => c.Id).Select(c => c.Id)
                .ToListAsync(cancellationToken);
        }

        var failed = 0;
        foreach (var batch in clientIds.Chunk(SyncBatchSize))
        {
            try
            {
                await using var db = _dbFactory.Create(caller.Scope);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var now = _time.GetUtcNow().UtcDateTime;
                var clients = await db.Clients.Where(c => batch.Contains(c.Id) && c.ClientTemplateId == templateId).ToListAsync(cancellationToken);
                foreach (var client in clients)
                {
                    await ClientTemplateSync.ApplyAsync(db, client, now, cancellationToken);
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed += batch.Length;
                _logger.LogError(ex, "Applying client template {ClientTemplateId} to a batch of {Count} clients failed", templateId, batch.Length);
            }
        }

        return failed;
    }

    public async Task<ServiceResult<Guid>> CopyAsync(Caller caller, Guid templateId, string? name, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        var detail = await GetAsync(caller, templateId, cancellationToken);
        if (detail is null)
        {
            return ServiceResult<Guid>.NotFound("client template");
        }

        var input = new ClientTemplateInput(name, detail.Description,
            detail.Sites.Select(s => s with { Id = null }).ToList(), detail.Client);
        var result = await SaveAsync(caller, null, input, cancellationToken);
        if (result.Success)
        {
            await using var db = _dbFactory.Create(caller.Scope);
            await db.ClientTemplates.Where(t => t.Id == result.Value).ExecuteUpdateAsync(s => s.SetProperty(t => t.CopiedFromId, templateId), cancellationToken);
        }

        return result;
    }

    /// <summary>Detaches every client that follows the template (sites and links stay, as manual), then deletes it.</summary>
    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid templateId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        List<Guid> clientIds;
        string name;
        await using (var db = _dbFactory.Create(caller.Scope))
        {
            var template = await db.ClientTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
            if (template is null)
            {
                return ServiceResult.NotFound("client template");
            }

            name = template.Name;
            clientIds = await db.Clients.Where(c => c.ClientTemplateId == templateId).Select(c => c.Id).ToListAsync(cancellationToken);
        }

        foreach (var batch in clientIds.Chunk(SyncBatchSize))
        {
            await using var db = _dbFactory.Create(caller.Scope);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var now = _time.GetUtcNow().UtcDateTime;
            var clients = await db.Clients.Where(c => batch.Contains(c.Id) && c.ClientTemplateId == templateId).ToListAsync(cancellationToken);
            foreach (var client in clients)
            {
                client.ClientTemplateId = null;
                client.UpdatedAt = now;
                await ClientTemplateSync.ApplyAsync(db, client, now, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using (var db = _dbFactory.Create(caller.Scope))
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var template = await db.ClientTemplates.SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
            if (template is not null)
            {
                db.ClientTemplates.Remove(template);
            }

            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ClientTemplateDeleted, "ClientTemplate", templateId.ToString(), null,
                new { Name = name, DetachedClients = clientIds.Count }), now));
            await db.SaveChangesAsync(cancellationToken);
        }

        return ServiceResult.Ok();
    }

    private static bool Fits(ClassChoice choice, IReadOnlyDictionary<Guid, CheckAppliesTo> appliesTo) =>
        ClassChoice.Slots.All(slot => choice[slot] is not { } id || appliesTo[id] == CheckAppliesTo.All || appliesTo[id] == slot);

    private static async Task<string?> ValidateAsync(FleetoDbContext db, Guid? templateId, ClientTemplateInput input, CancellationToken cancellationToken)
    {
        var name = ServiceSupport.Clean(input.Name);
        if (name is null || name.Length > 100)
        {
            return "Enter a client template name of at most 100 characters.";
        }

        if (input.Description is { Length: > 1000 })
        {
            return "The description can be at most 1000 characters.";
        }

        if (await db.ClientTemplates.AnyAsync(t => t.Name.ToLower() == name.ToLower() && t.Id != templateId, cancellationToken))
        {
            return $"A client template named {name} already exists. Choose another name.";
        }

        if (input.Sites.Count == 0)
        {
            return "Add at least one site: a client needs at least one site.";
        }

        if (input.Sites.Count > 50)
        {
            return "A client template can have at most 50 sites.";
        }

        var siteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in input.Sites)
        {
            var siteName = ServiceSupport.Clean(site.Name);
            if (siteName is null || siteName.Length > 100)
            {
                return "Enter a name of at most 100 characters for every site.";
            }

            if (!siteNames.Add(siteName))
            {
                return $"Two sites are named {siteName}. Give every site its own name.";
            }

            if (site.Description is { Length: > 1000 })
            {
                return "A site description can be at most 1000 characters.";
            }
        }

        var clientLinks = input.Client ?? ClientTemplateClientLinks.None;
        var policyChoices = input.Sites.Select(s => s.Policy).Append(clientLinks.Policy).ToList();
        var patchChoices = input.Sites.Select(s => s.PatchPolicy).Append(clientLinks.PatchPolicy).ToList();
        if (policyChoices.Concat(patchChoices).Any(c => c.All is not null && (c.Server is not null || c.Workstation is not null)))
        {
            return "Choose one policy for every endpoint, or one for servers and one for workstations, not both.";
        }

        var policyIds = policyChoices.SelectMany(c => c.Ids).Distinct().ToList();
        var policies = await db.Policies.Where(p => policyIds.Contains(p.Id) && p.ClientId == null)
            .ToDictionaryAsync(p => p.Id, p => p.AppliesTo, cancellationToken);
        if (policies.Count != policyIds.Count)
        {
            return "A client template can only use global policies. Pick a global policy for the client and every site.";
        }

        var patchPolicyIds = patchChoices.SelectMany(c => c.Ids).Distinct().ToList();
        var patchPolicies = await db.PatchPolicies.Where(p => patchPolicyIds.Contains(p.Id) && p.ClientId == null)
            .ToDictionaryAsync(p => p.Id, p => p.AppliesTo, cancellationToken);
        if (patchPolicies.Count != patchPolicyIds.Count)
        {
            return "A client template can only use global patch policies. Pick a global patch policy for the client and every site.";
        }

        // A slot takes only what is for its class: a policy for servers never in the slot for every endpoint or workstations.
        if (policyChoices.Any(c => !Fits(c, policies)) || patchChoices.Any(c => !Fits(c, patchPolicies)))
        {
            return "A policy for servers or workstations only can only be chosen for that class. Use \"Different for servers and workstations\".";
        }

        var templateIds = input.Sites.SelectMany(s => s.MonitoringTemplateIds).Concat(clientLinks.MonitoringTemplateIds).Distinct().ToList();
        if (await db.MonitoringTemplates.CountAsync(t => templateIds.Contains(t.Id) && t.ClientId == null, cancellationToken) != templateIds.Count)
        {
            return "A client template can only use global monitoring templates.";
        }

        return null;
    }
}
