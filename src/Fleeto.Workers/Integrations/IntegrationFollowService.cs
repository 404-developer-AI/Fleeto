using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Workers.Common;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Keeps Action1 in step with the clients and sites of Fleeto while the integration follows them (0.6.0).
/// <list type="bullet">
/// <item>A new client gets an organization, or the unmapped organization that already has its name, and is mapped to it.</item>
/// <item>Every site of a mapped client gets an endpoint group whose members are the endpoints of the site Action1 reports.</item>
/// <item>A renamed client or site renames its organization or group; a deleted one removes it.</item>
/// </list>
/// What cannot be read from the current state (a new client, a deletion) is an <see cref="IntegrationOperation"/> that web
/// writes in the same transaction as the change in Fleeto; the rest is compared with what Fleeto last sent. Every call
/// counts against the request budget of the whole instance, so one pass does at most <see cref="MaxWorkPerPass"/> pieces
/// of work and the rest waits for the next one.
/// </summary>
public sealed class IntegrationFollowService : WorkerLoop
{
    /// <summary>Pieces of work (an operation, a rename, a group, the members of a group) in one pass.</summary>
    public const int MaxWorkPerPass = 10;

    /// <summary>The members of a group are compared with Action1 at least this often, even when nothing changed in Fleeto.</summary>
    public static readonly TimeSpan MembersRecheck = TimeSpan.FromHours(24);

    /// <summary>When Action1 refuses an operation, it is tried again after this long; an admin can dismiss it meanwhile.</summary>
    public static readonly TimeSpan RefusedRetry = TimeSpan.FromHours(6);

    /// <summary>After a refusal while comparing state, that part waits this long, so a missing permission does not eat the budget.</summary>
    public static readonly TimeSpan StatePause = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How often the members of the groups are compared with the endpoints of their sites without a notification: it
    /// reads the patch state of every followed endpoint.
    /// </summary>
    public static readonly TimeSpan MembersInterval = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly Action1ClientFactory _clients;
    private readonly CircuitBreaker _breaker;
    private IDisposable? _subscription;
    private DateTime _statePausedUntil = DateTime.MinValue;
    private DateTime _membersCheckedAt = DateTime.MinValue;
    private volatile bool _woken;

    public IntegrationFollowService(IFleetoDbContextFactory dbFactory, INotificationBus bus, Action1ClientFactory clients,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<IntegrationFollowService> logger)
        : base("integration-follow", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _clients = clients;
        _breaker = new CircuitBreaker(failureThreshold: 3, pause: TimeSpan.FromMinutes(15), time);
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override TimeSpan MaxRunDuration => TimeSpan.FromMinutes(15);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.Integrations, (_, _) =>
        {
            _woken = true;
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await FollowAsync(cancellationToken);
        return false;
    }

    /// <summary>One pass: operations that are due, then renames, groups and members, within <see cref="MaxWorkPerPass"/>.</summary>
    public async Task FollowAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var integration = await db.Integrations.SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        if (integration is null || !integration.Enabled || !integration.FollowClients || _breaker.IsOpen)
        {
            return;
        }

        using var client = _clients.TryCreate(integration);
        if (client is null)
        {
            return;
        }

        var pass = new Pass(db, integration, client, Time.GetUtcNow().UtcDateTime);
        try
        {
            await RunOperationsAsync(pass, cancellationToken);
            if (pass.Now >= _statePausedUntil)
            {
                await RenameTenantsAsync(pass, cancellationToken);
                await FollowSitesAsync(pass, cancellationToken);
                if (_woken || pass.Now - _membersCheckedAt >= MembersInterval)
                {
                    _woken = false;
                    await FollowMembersAsync(pass, cancellationToken);
                    _membersCheckedAt = pass.Now;
                }
            }
        }
        catch (TransientException)
        {
            // Already recorded on the breaker; what is done so far is saved below.
        }

        if (pass.Problem is null && !pass.Transient)
        {
            integration.FollowMessage = null;
        }
        else if (pass.Problem is not null)
        {
            integration.FollowMessage = Cut(pass.Problem, 1000);
        }

        if (pass.TenantsChanged)
        {
            integration.TenantsJson = JsonSerializer.Serialize(pass.Tenants, Json);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (!pass.Transient)
        {
            _breaker.RecordSuccess();
        }
    }

    private async Task RunOperationsAsync(Pass pass, CancellationToken cancellationToken)
    {
        var due = await pass.Db.IntegrationOperations
            .Where(o => o.IntegrationId == pass.Integration.Id && o.NextAttemptAt <= pass.Now)
            .OrderBy(o => o.CreatedAt)
            .Take(MaxWorkPerPass)
            .ToListAsync(cancellationToken);

        foreach (var operation in due)
        {
            if (!pass.Spend())
            {
                return;
            }

            var result = operation.Kind switch
            {
                IntegrationOperationKind.CreateTenant => await CreateTenantAsync(pass, operation, cancellationToken),
                IntegrationOperationKind.DeleteTenant => await DeleteTenantAsync(pass, operation, cancellationToken),
                IntegrationOperationKind.MoveEndpoint => await MoveEndpointAsync(pass, operation, cancellationToken),
                _ => await DeleteGroupAsync(pass, operation, cancellationToken)
            };

            if (result is null)
            {
                continue; // done and removed
            }

            operation.Attempts++;
            operation.LastError = Cut(result.Message, 1000);
            if (result.Permanent)
            {
                operation.NextAttemptAt = pass.Now + RefusedRetry;
                Logger.LogWarning("Action1 refused {Kind} for {Name}: {Message}", operation.Kind, operation.Name, result.Message);
            }
            else
            {
                operation.NextAttemptAt = pass.Now + TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(operation.Attempts, 6))));
                Transient(pass);
                return;
            }
        }
    }

    /// <summary>
    /// Maps the new client to the unmapped organization with its name, or creates one. Returns null when the operation is
    /// done (and removes it), else the failure.
    /// </summary>
    private async Task<IntegrationResult?> CreateTenantAsync(Pass pass, IntegrationOperation operation, CancellationToken cancellationToken)
    {
        var db = pass.Db;
        var target = operation.TargetClientId is { } clientId
            ? await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken)
            : null;
        var mappings = await db.IntegrationMappings.Where(m => m.IntegrationId == pass.Integration.Id).ToListAsync(cancellationToken);
        if (target is null || mappings.Any(m => m.ClientId == target.Id))
        {
            // The client is gone, or an admin mapped it meanwhile: nothing left to do.
            db.IntegrationOperations.Remove(operation);
            return null;
        }

        var tenants = await pass.ReadTenantsAsync(cancellationToken);
        if (!tenants.Ok)
        {
            return tenants;
        }

        var mapped = mappings.Select(m => m.ExternalTenantId).ToHashSet(StringComparer.Ordinal);
        var existing = pass.Tenants.FirstOrDefault(t => !mapped.Contains(t.Id) && SameName(t.Name, target.Name));
        string tenantId;
        string tenantName;
        bool created;
        if (existing is not null)
        {
            (tenantId, tenantName, created) = (existing.Id, existing.Name, false);
        }
        else
        {
            // A name that another client's organization already carries gets the client code, so both stay recognisable.
            tenantName = pass.Tenants.Any(t => SameName(t.Name, target.Name)) ? Cut($"{target.Name} ({target.Code})", 200) : target.Name;
            var result = await pass.Client.CreateOrganizationAsync(tenantName, $"Created by Fleeto for client {target.Code}.", cancellationToken);
            if (!result.Ok)
            {
                return result;
            }

            (tenantId, created) = (result.Value!, true);
            pass.Tenants.Add(new ExternalTenant(tenantId, tenantName));
            pass.TenantsChanged = true;
        }

        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(),
            IntegrationId = pass.Integration.Id,
            ClientId = target.Id,
            ExternalTenantId = tenantId,
            ExternalTenantName = tenantName,
            SyncedName = target.Name,
            CreatedAt = pass.Now
        });
        db.IntegrationOperations.Remove(operation);
        Audit(db, created ? AuditActions.IntegrationTenantCreated : AuditActions.IntegrationMappingChanged, pass, target.Id,
            new { Type = IntegrationType.Action1.ToString(), target.Code, TenantId = tenantId, TenantName = tenantName, Change = created ? "created" : "mapped by name" });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // The client was deleted or mapped in the same moment. An organization made for nobody is removed again.
            Logger.LogWarning(ex, "The Action1 organization for client {Code} could not be mapped", target.Code);
            db.ChangeTracker.Clear();
            if (created)
            {
                db.IntegrationOperations.Add(NewOperation(pass, IntegrationOperationKind.DeleteTenant, null, tenantId, string.Empty, tenantName));
                await db.SaveChangesAsync(cancellationToken);
            }

            return null;
        }

        Logger.LogInformation("Client {Code} is mapped to Action1 organization {Tenant} ({Change})", target.Code, tenantId,
            created ? "created" : "existing");
        return null;
    }

    private async Task<IntegrationResult?> DeleteTenantAsync(Pass pass, IntegrationOperation operation, CancellationToken cancellationToken)
    {
        var db = pass.Db;
        if (await db.IntegrationMappings.AnyAsync(m => m.IntegrationId == pass.Integration.Id && m.ExternalTenantId == operation.ExternalTenantId,
                cancellationToken))
        {
            // Mapped to another client since: that client uses it, so it stays.
            db.IntegrationOperations.Remove(operation);
            return null;
        }

        var result = await pass.Client.DeleteOrganizationAsync(operation.ExternalTenantId, cancellationToken);
        if (!result.Ok)
        {
            return result.Permanent
                ? IntegrationResult.Fail(
                    $"{result.Message} Action1 removes an organization only when it holds no endpoints and is not the last organization. " +
                    "Remove its endpoints in the Action1 console, or dismiss this here; Fleeto tries again every 6 hours.", permanent: true)
                : result;
        }

        db.IntegrationOperations.Remove(operation);
        pass.RemoveTenant(operation.ExternalTenantId);

        Audit(db, AuditActions.IntegrationTenantDeleted, pass, null,
            new { Type = IntegrationType.Action1.ToString(), TenantId = operation.ExternalTenantId, TenantName = operation.Name });
        Logger.LogInformation("Action1 organization {Tenant} of a deleted client was removed", operation.ExternalTenantId);
        return null;
    }

    private async Task<IntegrationResult?> DeleteGroupAsync(Pass pass, IntegrationOperation operation, CancellationToken cancellationToken)
    {
        var db = pass.Db;
        if (await db.IntegrationSiteGroups.AnyAsync(g => g.ExternalGroupId == operation.ExternalGroupId, cancellationToken))
        {
            db.IntegrationOperations.Remove(operation);
            return null;
        }

        var result = await pass.Client.DeleteGroupAsync(operation.ExternalTenantId, operation.ExternalGroupId, cancellationToken);
        if (!result.Ok)
        {
            return result;
        }

        db.IntegrationOperations.Remove(operation);
        return null;
    }

    /// <summary>
    /// Moves an endpoint into the organization of the client it belongs to in Fleeto. The target is read again here, so a
    /// mapping changed since the patch sync saw it decides; afterwards the patch state is read again at once, so the
    /// endpoint shows its state under its new client instead of "not covered" for up to four hours.
    /// </summary>
    private async Task<IntegrationResult?> MoveEndpointAsync(Pass pass, IntegrationOperation operation, CancellationToken cancellationToken)
    {
        var db = pass.Db;
        var target = operation.TargetClientId is { } clientId
            ? await db.IntegrationMappings.AsNoTracking()
                .Where(m => m.IntegrationId == pass.Integration.Id && m.ClientId == clientId)
                .Select(m => new { m.ExternalTenantId, m.ExternalTenantName, m.ClientId })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (target is null || target.ExternalTenantId == operation.ExternalTenantId)
        {
            db.IntegrationOperations.Remove(operation);
            return null;
        }

        var result = await pass.Client.MoveEndpointAsync(operation.ExternalTenantId, operation.ExternalEndpointId, target.ExternalTenantId,
            cancellationToken);
        if (!result.Ok)
        {
            return result;
        }

        db.IntegrationOperations.Remove(operation);
        pass.Integration.PatchSyncedAt = null;
        Audit(db, AuditActions.IntegrationEndpointMoved, pass, target.ClientId, new
        {
            Type = IntegrationType.Action1.ToString(),
            Hostname = operation.Name,
            EndpointId = operation.ExternalEndpointId,
            FromTenantId = operation.ExternalTenantId,
            ToTenantId = target.ExternalTenantId,
            ToTenantName = target.ExternalTenantName
        });
        Logger.LogInformation("Action1 endpoint {Endpoint} moved from organization {From} to {To}", operation.ExternalEndpointId,
            operation.ExternalTenantId, target.ExternalTenantId);
        return null;
    }

    /// <summary>Renames the organization of every mapped client whose name changed since Fleeto last sent it.</summary>
    private async Task RenameTenantsAsync(Pass pass, CancellationToken cancellationToken)
    {
        var db = pass.Db;
        var renamed = await (from m in db.IntegrationMappings
                             join c in db.Clients on m.ClientId equals c.Id
                             where m.IntegrationId == pass.Integration.Id && m.SyncedName != c.Name
                             select new { Mapping = m, c.Name })
            .Take(MaxWorkPerPass)
            .ToListAsync(cancellationToken);

        foreach (var item in renamed)
        {
            if (!pass.Spend())
            {
                return;
            }

            var result = await pass.Client.RenameOrganizationAsync(item.Mapping.ExternalTenantId, item.Name, cancellationToken);
            if (!result.Ok)
            {
                Refused(pass, result, $"Fleeto could not rename Action1 organization {item.Mapping.ExternalTenantName}.");
                return;
            }

            item.Mapping.SyncedName = item.Name;
            item.Mapping.ExternalTenantName = item.Name;
            pass.RenameTenant(item.Mapping.ExternalTenantId, item.Name);
        }
    }

    /// <summary>
    /// Gives every site of a mapped client its endpoint group and keeps the names equal. A group of a client that is no
    /// longer mapped, or mapped to another organization, is let go: Fleeto stops following it and leaves it in Action1.
    /// </summary>
    private async Task FollowSitesAsync(Pass pass, CancellationToken cancellationToken)
    {
        var db = pass.Db;
        var mappings = await db.IntegrationMappings.AsNoTracking().Where(m => m.IntegrationId == pass.Integration.Id)
            .ToDictionaryAsync(m => m.ClientId, cancellationToken);
        var groups = await db.IntegrationSiteGroups.Where(g => g.IntegrationId == pass.Integration.Id).ToListAsync(cancellationToken);
        foreach (var group in groups.Where(g => !mappings.TryGetValue(g.ClientId, out var m) || m.ExternalTenantId != g.ExternalTenantId).ToList())
        {
            db.IntegrationSiteGroups.Remove(group);
            groups.Remove(group);
        }

        var clientIds = mappings.Keys.ToList();
        var sites = await db.Sites.AsNoTracking().Where(s => clientIds.Contains(s.ClientId))
            .OrderBy(s => s.CreatedAt)
            .Select(s => new { s.Id, s.ClientId, s.Name, ClientCode = s.Client!.Code })
            .ToListAsync(cancellationToken);
        var bySite = groups.ToDictionary(g => g.SiteId);
        var existingGroups = new Dictionary<string, List<ExternalTenant>>(StringComparer.Ordinal);

        foreach (var site in sites)
        {
            var tenantId = mappings[site.ClientId].ExternalTenantId;
            if (bySite.TryGetValue(site.Id, out var group))
            {
                if (group.SyncedName == site.Name)
                {
                    continue;
                }

                if (!pass.Spend())
                {
                    return;
                }

                var renamed = await pass.Client.RenameGroupAsync(tenantId, group.ExternalGroupId, site.Name, cancellationToken);
                if (!renamed.Ok)
                {
                    Refused(pass, renamed, $"Fleeto could not rename the Action1 endpoint group of site {site.Name}.");
                    return;
                }

                group.SyncedName = site.Name;
                continue;
            }

            if (!pass.Spend())
            {
                return;
            }

            // A group with the site's name that no site follows is taken over, so a pass that failed halfway never
            // leaves a second group behind.
            if (!existingGroups.TryGetValue(tenantId, out var known))
            {
                var listed = await pass.Client.ListGroupsAsync(tenantId, cancellationToken);
                if (!listed.Ok)
                {
                    Refused(pass, listed, $"Fleeto could not read the endpoint groups of Action1 organization {mappings[site.ClientId].ExternalTenantName}.");
                    return;
                }

                known = [.. listed.Value!];
                existingGroups[tenantId] = known;
            }

            var followed = groups.Select(g => g.ExternalGroupId).ToHashSet(StringComparer.Ordinal);
            var groupId = known.FirstOrDefault(g => !followed.Contains(g.Id) && SameName(g.Name, site.Name))?.Id;
            if (groupId is null)
            {
                var created = await pass.Client.CreateGroupAsync(tenantId, site.Name,
                    $"Site {site.Name} of client {site.ClientCode} in Fleeto. Fleeto keeps its members equal to the endpoints of the site.",
                    cancellationToken);
                if (!created.Ok)
                {
                    Refused(pass, created, $"Fleeto could not create the Action1 endpoint group of site {site.Name}.");
                    return;
                }

                groupId = created.Value!;
                known.Add(new ExternalTenant(groupId, site.Name));
            }

            var row = new IntegrationSiteGroup
            {
                Id = Guid.NewGuid(),
                IntegrationId = pass.Integration.Id,
                ClientId = site.ClientId,
                SiteId = site.Id,
                ExternalTenantId = tenantId,
                ExternalGroupId = groupId,
                SyncedName = site.Name,
                CreatedAt = pass.Now
            };
            db.IntegrationSiteGroups.Add(row);
            groups.Add(row);
        }
    }

    /// <summary>
    /// Makes the members of every group the endpoints of its site that Action1 reports in that organization. Only what
    /// changed in Fleeto is compared, and every group at least once a day, so a member removed in Action1 comes back.
    /// </summary>
    private async Task FollowMembersAsync(Pass pass, CancellationToken cancellationToken)
    {
        var db = pass.Db;
        await db.SaveChangesAsync(cancellationToken); // groups created above take part in this pass
        var groups = await db.IntegrationSiteGroups.Where(g => g.IntegrationId == pass.Integration.Id).ToListAsync(cancellationToken);
        if (groups.Count == 0)
        {
            return;
        }

        var siteIds = groups.Select(g => g.SiteId).ToList();
        var members = await (from p in db.EndpointPatchStates
                             join e in db.Endpoints on p.EndpointId equals e.Id
                             where siteIds.Contains(e.SiteId) && p.ExternalEndpointId != ""
                             select new { e.SiteId, p.ExternalTenantId, p.ExternalEndpointId })
            .ToListAsync(cancellationToken);

        foreach (var group in groups.OrderBy(g => g.MembersSyncedAt ?? DateTime.MinValue))
        {
            var wanted = members.Where(m => m.SiteId == group.SiteId && m.ExternalTenantId == group.ExternalTenantId)
                .Select(m => m.ExternalEndpointId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var hash = Hash(wanted);
            if (hash == group.MembersHash && group.MembersSyncedAt is { } synced && pass.Now - synced < MembersRecheck)
            {
                continue;
            }

            if (!pass.Spend())
            {
                return;
            }

            var current = await pass.Client.ListGroupMembersAsync(group.ExternalTenantId, group.ExternalGroupId, cancellationToken);
            if (!current.Ok)
            {
                Refused(pass, current, "Fleeto could not read the members of an Action1 endpoint group.");
                return;
            }

            if (current.Value is null)
            {
                // Removed in Action1: the next pass creates it again.
                db.IntegrationSiteGroups.Remove(group);
                continue;
            }

            var present = current.Value.Select(m => m.EndpointId).ToHashSet(StringComparer.Ordinal);
            var add = wanted.Where(id => !present.Contains(id)).ToList();
            var wantedSet = wanted.ToHashSet(StringComparer.Ordinal);
            var remove = current.Value.Where(m => m.Manual && !wantedSet.Contains(m.EndpointId)).Select(m => m.EndpointId).ToList();
            if (add.Count > 0 || remove.Count > 0)
            {
                var changed = await pass.Client.ChangeGroupMembersAsync(group.ExternalTenantId, group.ExternalGroupId, add, remove, cancellationToken);
                if (!changed.Ok)
                {
                    Refused(pass, changed, "Fleeto could not change the members of an Action1 endpoint group.");
                    return;
                }
            }

            group.MembersHash = hash;
            group.MembersSyncedAt = pass.Now;
        }
    }

    /// <summary>
    /// Records a failure while comparing state. A permanent one is shown in Settings and pauses this part; a passing one
    /// counts on the breaker and ends the pass.
    /// </summary>
    private void Refused(Pass pass, IntegrationResult result, string what)
    {
        if (result.Permanent)
        {
            pass.Problem = $"{what} {result.Message} Check that the role of the API credentials may manage organizations and endpoints.";
            _statePausedUntil = pass.Now + StatePause;
            Logger.LogWarning("{What} {Message}", what, result.Message);
            return;
        }

        Transient(pass);
        throw new TransientException();
    }

    private void Transient(Pass pass)
    {
        pass.Transient = true;
        if (_breaker.RecordFailure())
        {
            Logger.LogWarning("Following clients in Action1 failed repeatedly; pausing until {OpenUntil}", _breaker.OpenUntil);
        }
    }

    private IntegrationOperation NewOperation(Pass pass, IntegrationOperationKind kind, Guid? clientId, string tenantId, string groupId,
        string name) => new()
    {
        Id = Guid.NewGuid(),
        IntegrationId = pass.Integration.Id,
        Kind = kind,
        TargetClientId = clientId,
        ExternalTenantId = tenantId,
        ExternalGroupId = groupId,
        Name = Cut(name, 200),
        NextAttemptAt = pass.Now,
        CreatedAt = pass.Now
    };

    private static void Audit(FleetoDbContext db, string action, Pass pass, Guid? clientId, object details) =>
        db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(action, "Integration", pass.Integration.Id.ToString(), clientId,
            AuditActorType.System, "fleeto-workers", "fleeto-workers", details), pass.Now));

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static string Hash(IEnumerable<string> ids) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ids))));

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed class TransientException : Exception;

    /// <summary>What one pass knows and has spent.</summary>
    private sealed class Pass(FleetoDbContext db, Integration integration, Action1Client client, DateTime now)
    {
        private int _work;

        public FleetoDbContext Db { get; } = db;
        public Integration Integration { get; } = integration;
        public Action1Client Client { get; } = client;
        public DateTime Now { get; } = now;
        public string? Problem { get; set; }
        public bool Transient { get; set; }
        public bool TenantsChanged { get; set; }
        public bool TenantsLoaded { get; private set; }
        public List<ExternalTenant> Tenants { get; private set; } = ReadStored(integration);

        public bool Spend() => ++_work <= MaxWorkPerPass;

        /// <summary>Reads the organizations once per pass, so a new client is matched against what Action1 has now.</summary>
        public async Task<IntegrationResult> ReadTenantsAsync(CancellationToken cancellationToken)
        {
            if (TenantsLoaded)
            {
                return IntegrationResult.Success();
            }

            var result = await Client.ListTenantsAsync(cancellationToken);
            if (!result.Ok)
            {
                return result;
            }

            Tenants = [.. result.Value!];
            TenantsLoaded = true;
            TenantsChanged = true;
            Integration.TenantsUpdatedAt = Now;
            return IntegrationResult.Success();
        }

        public void RemoveTenant(string id)
        {
            Tenants.RemoveAll(t => t.Id == id);
            TenantsChanged = true;
        }

        public void RenameTenant(string id, string name)
        {
            var index = Tenants.FindIndex(t => t.Id == id);
            if (index >= 0)
            {
                Tenants[index] = Tenants[index] with { Name = name };
                TenantsChanged = true;
            }
        }

        private static List<ExternalTenant> ReadStored(Integration integration)
        {
            try
            {
                return JsonSerializer.Deserialize<List<ExternalTenant>>(integration.TenantsJson, Json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}
