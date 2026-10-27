using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public enum AccessTargetKind
{
    Client,
    Site,
    Endpoint
}

/// <summary>A client, site or endpoint a group rule names.</summary>
public sealed record AccessTargetRef(AccessTargetKind Kind, Guid Id);

/// <summary>A rule target with what to show: <c>ACME</c>, <c>ACME / Main site</c> or <c>ACME / SRV-01</c>.</summary>
public sealed record AccessTargetView(AccessTargetKind Kind, Guid Id, string Label);

public sealed record UserGroupListItem(Guid Id, string Name, string? Description, bool AllClients, bool IsDefault, int MemberCount,
    int IncludeCount, int ExcludeCount);

public sealed record UserGroupMemberView(Guid UserId, string Name, string Email);

public sealed record UserGroupDetail(Guid Id, string Name, string? Description, bool AllClients, bool IsDefault,
    IReadOnlyList<AccessTargetView> Includes, IReadOnlyList<AccessTargetView> Excludes, IReadOnlyList<UserGroupMemberView> Members);

public sealed record UserGroupInput(string? Name, string? Description, bool AllClients, IReadOnlyList<AccessTargetRef> Includes,
    IReadOnlyList<AccessTargetRef> Excludes, IReadOnlyCollection<Guid> MemberIds);

/// <summary>A group a user can be put in, for the user dialog.</summary>
public sealed record UserGroupOption(Guid Id, string Name, bool AllClients, bool IsDefault);

/// <summary>What a user sees (0.6.0): every client for an admin or a group for all clients without exceptions, otherwise per client.</summary>
public sealed record UserAccessView(bool IsAdmin, bool Everything, bool IncludesNewClients, IReadOnlyList<string> Groups,
    IReadOnlyList<ClientAccessView> Clients);

/// <summary>A client the user sees; for one it sees part of, the sites of it it sees.</summary>
public sealed record ClientAccessView(Guid Id, string Code, string Name, AccessLevel Level, IReadOnlyList<SiteAccessView> Sites);

/// <param name="Endpoints">How many endpoints of the site the user sees.</param>
public sealed record SiteAccessView(Guid Id, string Name, AccessLevel Level, int Endpoints, int TotalEndpoints);

/// <summary>A user that can reach a client, site or endpoint, and through what: the admin role or the names of its groups.</summary>
public sealed record AccessHolderView(Guid UserId, string Name, string Email, IReadOnlyList<string> Roles, AccessLevel Level,
    IReadOnlyList<string> Through);

/// <summary>
/// Groups of users (0.6.0), for admins: what each group gives, who is in it, what a user sees and who can reach a client, site
/// or endpoint. Every change is audited and takes effect at once: the open pages of a user that lost access reload, and its
/// remote session connections to an endpoint it can no longer reach are ended.
/// </summary>
public sealed class UserGroupService
{
    public const int MaxRules = 1000;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<UserGroupService> _logger;

    public UserGroupService(IFleetoDbContextFactory dbFactory, INotificationBus bus, TimeProvider time, ILogger<UserGroupService> logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UserGroupListItem>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.GroupsManage);
        await using var db = _dbFactory.CreateSystem();
        return await db.UserGroups.AsNoTracking()
            .OrderByDescending(g => g.IsDefault).ThenBy(g => g.NormalizedName)
            .Select(g => new UserGroupListItem(g.Id, g.Name, g.Description, g.AllClients, g.IsDefault, g.Members.Count,
                g.Rules.Count(r => r.Kind == UserGroupRuleKind.Include), g.Rules.Count(r => r.Kind == UserGroupRuleKind.Exclude)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserGroupOption>> ListOptionsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAny(Permissions.GroupsManage, Permissions.UsersManage);
        await using var db = _dbFactory.CreateSystem();
        return await db.UserGroups.AsNoTracking().OrderByDescending(g => g.IsDefault).ThenBy(g => g.NormalizedName)
            .Select(g => new UserGroupOption(g.Id, g.Name, g.AllClients, g.IsDefault)).ToListAsync(cancellationToken);
    }

    public async Task<UserGroupDetail?> GetAsync(Caller caller, Guid groupId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.GroupsManage);
        await using var db = _dbFactory.CreateSystem();
        var group = await db.UserGroups.AsNoTracking().Include(g => g.Rules).Include(g => g.Members)
            .SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return null;
        }

        var labels = await LabelsAsync(db, group.Rules.Select(Ref).ToList(), cancellationToken);
        var memberIds = group.Members.Select(m => m.UserId).ToList();
        var members = await db.Users.AsNoTracking().Where(u => memberIds.Contains(u.Id)).OrderBy(u => u.Email)
            .Select(u => new UserGroupMemberView(u.Id, u.DisplayName, u.Email ?? string.Empty)).ToListAsync(cancellationToken);
        return new UserGroupDetail(group.Id, group.Name, group.Description, group.AllClients, group.IsDefault,
            Views(group.Rules.Where(r => r.Kind == UserGroupRuleKind.Include), labels),
            Views(group.Rules.Where(r => r.Kind == UserGroupRuleKind.Exclude), labels), members);
    }

    /// <summary>Clients, sites and endpoints whose code, name or hostname matches, for the rule picker.</summary>
    public async Task<IReadOnlyList<AccessTargetView>> SearchTargetsAsync(Caller caller, string? search, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.GroupsManage);
        if (ServiceSupport.Clean(search) is not { } term)
        {
            return [];
        }

        await using var db = _dbFactory.CreateSystem();
        var pattern = ServiceSupport.LikePattern(term);
        var clients = await db.Clients.AsNoTracking()
            .Where(c => EF.Functions.ILike(c.Code, pattern, ServiceSupport.LikeEscape) ||
                        EF.Functions.ILike(c.Name, pattern, ServiceSupport.LikeEscape))
            .OrderBy(c => c.Code).Take(20)
            .Select(c => new AccessTargetView(AccessTargetKind.Client, c.Id, c.Code + " · " + c.Name)).ToListAsync(cancellationToken);
        var sites = await db.Sites.AsNoTracking()
            .Where(s => EF.Functions.ILike(s.Name, pattern, ServiceSupport.LikeEscape) ||
                        EF.Functions.ILike(s.Client!.Code, pattern, ServiceSupport.LikeEscape))
            .OrderBy(s => s.Client!.Code).ThenBy(s => s.Name).Take(20)
            .Select(s => new AccessTargetView(AccessTargetKind.Site, s.Id, s.Client!.Code + " / " + s.Name)).ToListAsync(cancellationToken);
        var endpoints = await db.Endpoints.AsNoTracking()
            .Where(e => EF.Functions.ILike(e.Hostname, pattern, ServiceSupport.LikeEscape))
            .OrderBy(e => e.Hostname).Take(20)
            .Select(e => new AccessTargetView(AccessTargetKind.Endpoint, e.Id, e.Site!.Client!.Code + " / " + e.Hostname)).ToListAsync(cancellationToken);
        return [.. clients, .. sites, .. endpoints];
    }

    /// <summary>Creates a group (<paramref name="groupId"/> null) or changes one: its name, what it gives and who is in it.</summary>
    public async Task<ServiceResult<Guid>> SaveAsync(Caller caller, Guid? groupId, UserGroupInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.GroupsManage))
        {
            return ServiceResult<Guid>.Forbidden();
        }

        var name = ServiceSupport.Clean(input.Name);
        if (name is null || name.Length > UserAccessRules.MaxNameLength)
        {
            return ServiceResult<Guid>.Fail($"Enter a group name of at most {UserAccessRules.MaxNameLength} characters.");
        }

        var description = ServiceSupport.Clean(input.Description);
        if (description?.Length > UserAccessRules.MaxDescriptionLength)
        {
            return ServiceResult<Guid>.Fail($"The description can be at most {UserAccessRules.MaxDescriptionLength} characters.");
        }

        var includes = input.AllClients ? [] : input.Includes.Distinct().ToList();
        var excludes = input.Excludes.Distinct().ToList();
        if (input.AllClients && input.Includes.Count > 0)
        {
            return ServiceResult<Guid>.Fail("A group for all clients includes every client already. Remove what it includes, or switch off All clients.");
        }

        if (includes.Count + excludes.Count > MaxRules)
        {
            return ServiceResult<Guid>.Fail($"A group can name at most {MaxRules} clients, sites and endpoints together.");
        }

        if (includes.Intersect(excludes).Any())
        {
            return ServiceResult<Guid>.Fail("A client, site or endpoint is both included and excluded. Keep it in one of the two lists.");
        }

        await using var db = _dbFactory.CreateSystem();
        var labels = await LabelsAsync(db, [.. includes, .. excludes], cancellationToken);
        if (labels.Count != includes.Count + excludes.Count)
        {
            return ServiceResult<Guid>.Fail("A client, site or endpoint the group names no longer exists. Refresh the page and try again.");
        }

        var memberIds = input.MemberIds.Distinct().ToList();
        var memberProblem = await MembersProblemAsync(db, memberIds, cancellationToken);
        if (memberProblem is not null)
        {
            return ServiceResult<Guid>.Fail(memberProblem);
        }

        var normalized = name.ToUpperInvariant();
        if (await db.UserGroups.AnyAsync(g => g.NormalizedName == normalized && g.Id != groupId, cancellationToken))
        {
            return ServiceResult<Guid>.Fail($"A group named {name} already exists. Choose another name.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        UserGroup group;
        List<Guid> before;
        if (groupId is { } id)
        {
            var existing = await db.UserGroups.Include(g => g.Rules).Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
            if (existing is null)
            {
                return ServiceResult<Guid>.NotFound("group");
            }

            group = existing;
            before = group.Members.Select(m => m.UserId).ToList();
            db.UserGroupRules.RemoveRange(group.Rules);
            db.UserGroupMembers.RemoveRange(group.Members.Where(m => !memberIds.Contains(m.UserId)));
        }
        else
        {
            group = new UserGroup { Id = Guid.NewGuid(), CreatedAt = now };
            db.UserGroups.Add(group);
            before = [];
        }

        group.Name = name;
        group.NormalizedName = normalized;
        group.Description = description;
        group.AllClients = input.AllClients;
        group.UpdatedAt = now;
        foreach (var target in includes)
        {
            db.UserGroupRules.Add(Rule(group.Id, UserGroupRuleKind.Include, target));
        }

        foreach (var target in excludes)
        {
            db.UserGroupRules.Add(Rule(group.Id, UserGroupRuleKind.Exclude, target));
        }

        var added = memberIds.Except(before).ToList();
        foreach (var userId in added)
        {
            db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId, AddedAt = now });
        }

        // A user put in another group leaves the default group "All clients" (decided 2026-10-14).
        if (!group.IsDefault && added.Count > 0)
        {
            await db.UserGroupMembers.Where(m => added.Contains(m.UserId) && db.UserGroups.Any(g => g.Id == m.GroupId && g.IsDefault))
                .ExecuteDeleteAsync(cancellationToken);
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(groupId is null ? AuditActions.UserGroupCreated : AuditActions.UserGroupUpdated,
            "UserGroup", group.Id.ToString(), null, new
            {
                group.Name,
                group.AllClients,
                Includes = includes.Select(t => labels[t]).ToList(),
                Excludes = excludes.Select(t => labels[t]).ToList(),
                Added = added.Count,
                Removed = before.Except(memberIds).Count()
            }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Every member before and after: a changed rule changes what all of them see.
        await AccessChanges.ApplyAsync(_dbFactory, _bus, _logger, before.Union(memberIds), now, cancellationToken);
        return ServiceResult<Guid>.Ok(group.Id);
    }

    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid groupId, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.GroupsManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var group = await db.UserGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return ServiceResult.NotFound("group");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var members = group.Members.Select(m => m.UserId).ToList();
        db.UserGroups.Remove(group);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.UserGroupDeleted, "UserGroup", group.Id.ToString(), null,
            new { group.Name, Members = members.Count }), now));
        await db.SaveChangesAsync(cancellationToken);
        await AccessChanges.ApplyAsync(_dbFactory, _bus, _logger, members, now, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>The groups of one user, from the user dialog.</summary>
    public async Task<IReadOnlyList<Guid>> GroupsOfAsync(Caller caller, Guid userId, CancellationToken cancellationToken = default)
    {
        caller.EnsureAny(Permissions.GroupsManage, Permissions.UsersManage);
        await using var db = _dbFactory.CreateSystem();
        return await db.UserGroupMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.GroupId).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Puts a user in exactly these groups. A user put in a group other than the default one leaves the default group, also
    /// when it is chosen together with it.
    /// </summary>
    public async Task<ServiceResult> SetGroupsOfAsync(Caller caller, Guid userId, IReadOnlyCollection<Guid> groupIds,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.UsersManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var wanted = groupIds.Distinct().ToList();
        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return ServiceResult.NotFound("user");
        }

        if (await RoleAuthority.ManageProblemAsync(db, caller, userId, cancellationToken) is { } manageProblem)
        {
            return ServiceResult.Fail(manageProblem);
        }

        // Taking an admin out of every group is fine; putting one in is not.
        if (wanted.Count > 0 && await MembersProblemAsync(db, [userId], cancellationToken) is { } memberProblem)
        {
            return ServiceResult.Fail(memberProblem);
        }

        var groups = await db.UserGroups.AsNoTracking().Where(g => wanted.Contains(g.Id)).Select(g => new { g.Id, g.Name, g.IsDefault })
            .ToListAsync(cancellationToken);
        if (groups.Count != wanted.Count)
        {
            return ServiceResult.NotFound("group");
        }

        var newDefault = groups.Where(g => g.IsDefault && wanted.Count > 1).Select(g => g.Id).ToList();
        var current = await db.UserGroupMembers.Where(m => m.UserId == userId).ToListAsync(cancellationToken);
        var currentIds = current.Select(m => m.GroupId).ToList();
        var addedOther = groups.Any(g => !g.IsDefault && !currentIds.Contains(g.Id));
        var final = wanted.Except(addedOther ? newDefault : []).ToList();
        if (final.OrderBy(g => g).SequenceEqual(currentIds.OrderBy(g => g)))
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.UserGroupMembers.RemoveRange(current.Where(m => !final.Contains(m.GroupId)));
        foreach (var groupId in final.Except(currentIds))
        {
            db.UserGroupMembers.Add(new UserGroupMember { GroupId = groupId, UserId = userId, AddedAt = now });
        }

        var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).SingleAsync(cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.UserGroupsChanged, "User", userId.ToString(), null,
            new { Email = email, Groups = groups.Where(g => final.Contains(g.Id)).Select(g => g.Name).OrderBy(n => n).ToList() }), now));
        await db.SaveChangesAsync(cancellationToken);
        await AccessChanges.ApplyAsync(_dbFactory, _bus, _logger, [userId], now, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>What a user sees, client by client, for Settings, Users.</summary>
    public async Task<UserAccessView?> GetUserAccessAsync(Caller caller, Guid userId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.UsersManage);
        await using var db = _dbFactory.CreateSystem();
        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return null;
        }

        var groupNames = await db.UserGroups.AsNoTracking().Where(g => g.Members.Any(m => m.UserId == userId)).OrderBy(g => g.NormalizedName)
            .Select(g => g.Name).ToListAsync(cancellationToken);
        var scope = await UserAccessResolver.ScopeOfAsync(db, userId, cancellationToken);
        var isAdmin = scope.AllClients && await db.UserRoles.Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.NormalizedName).AnyAsync(n => n == ScriptCheckResolver.AdminRole, cancellationToken);
        if (scope.AllClients)
        {
            return new UserAccessView(isAdmin, true, true, groupNames, []);
        }

        var clientIds = scope.ClientIds.ToArray();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).OrderBy(c => c.Code)
            .Select(c => new { c.Id, c.Code, c.Name }).ToListAsync(cancellationToken);
        var partial = clients.Where(c => !scope.HasWholeClient(c.Id)).Select(c => c.Id).ToArray();
        var sites = await db.Sites.AsNoTracking().Where(s => partial.Contains(s.ClientId)).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.ClientId, s.Name, Total = db.Endpoints.Count(e => e.SiteId == s.Id) }).ToListAsync(cancellationToken);
        var seen = scope.EndpointIds.ToArray();
        var seenPerSite = await db.Endpoints.AsNoTracking().Where(e => seen.Contains(e.Id)).GroupBy(e => e.SiteId)
            .Select(g => new { SiteId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.SiteId, g => g.Count, cancellationToken);
        return new UserAccessView(false, false, scope.IncludesNewClients, groupNames, clients.Select(c =>
        {
            var whole = scope.HasWholeClient(c.Id);
            return new ClientAccessView(c.Id, c.Code, c.Name, whole ? AccessLevel.Full : AccessLevel.Partial, whole
                ? []
                : sites.Where(s => s.ClientId == c.Id && scope.SeesSite(c.Id, s.Id))
                    .Select(s => new SiteAccessView(s.Id, s.Name, scope.HasWholeSite(c.Id, s.Id) ? AccessLevel.Full : AccessLevel.Partial,
                        seenPerSite.GetValueOrDefault(s.Id), s.Total))
                    .ToList());
        }).ToList());
    }

    /// <summary>Who can reach a client, site or endpoint: every admin, and every other user one of whose groups gives it.</summary>
    public async Task<IReadOnlyList<AccessHolderView>?> WhoCanReachAsync(Caller caller, AccessTargetKind kind, Guid id,
        CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.AccessView);
        await using var db = _dbFactory.CreateSystem();
        AccessTarget? place = kind switch
        {
            AccessTargetKind.Client => await db.Clients.AsNoTracking().Where(c => c.Id == id).Select(c => new AccessTarget(c.Id, null, null))
                .SingleOrDefaultAsync(cancellationToken),
            AccessTargetKind.Site => await db.Sites.AsNoTracking().Where(s => s.Id == id).Select(s => new AccessTarget(s.ClientId, s.Id, null))
                .SingleOrDefaultAsync(cancellationToken),
            _ => await db.Endpoints.AsNoTracking().Where(e => e.Id == id).Select(e => new AccessTarget(e.ClientId, e.SiteId, e.Id))
                .SingleOrDefaultAsync(cancellationToken)
        };
        if (place is null)
        {
            return null;
        }

        var users = await db.Users.AsNoTracking().OrderBy(u => u.Email).Select(u => new { u.Id, u.DisplayName, u.Email }).ToListAsync(cancellationToken);
        var roles = await db.UserRoles.AsNoTracking().Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync(cancellationToken);
        var groups = (await UserAccessResolver.LoadAsync(db, db.UserGroups, cancellationToken)).ToDictionary(g => g.Id);
        var memberships = await db.UserGroupMembers.AsNoTracking().ToListAsync(cancellationToken);

        AccessLevel LevelOf(GroupAccess group) => place switch
        {
            { EndpointId: { } endpointId } => group.Reaches(place.ClientId, place.SiteId!.Value, endpointId) ? AccessLevel.Full : AccessLevel.None,
            { SiteId: { } siteId } => group.ReachesSite(place.ClientId, siteId),
            _ => group.ReachesClient(place.ClientId)
        };

        var holders = new List<AccessHolderView>();
        foreach (var user in users)
        {
            var userRoles = roles.Where(r => r.UserId == user.Id).Select(r => r.Name!).OrderBy(r => r).ToList();
            if (userRoles.Count == 0)
            {
                continue;
            }

            if (userRoles.Contains(FleetoRoles.Admin))
            {
                holders.Add(new AccessHolderView(user.Id, user.DisplayName, user.Email ?? string.Empty, userRoles, AccessLevel.Full, ["Admin role"]));
                continue;
            }

            var own = memberships.Where(m => m.UserId == user.Id && groups.ContainsKey(m.GroupId)).Select(m => groups[m.GroupId]).ToList();
            var through = own.Where(g => LevelOf(g) != AccessLevel.None).Select(g => g.Name).OrderBy(n => n).ToList();
            if (through.Count == 0)
            {
                continue;
            }

            // The level from the resolved scope, the same one every query of the user is filtered on.
            var scope = await UserAccessResolver.ResolveAsync(db, own, cancellationToken);
            var level = place switch
            {
                { EndpointId: { } endpointId } => scope.SeesEndpoint(place.ClientId, endpointId) ? AccessLevel.Full : AccessLevel.None,
                { SiteId: { } siteId } => scope.HasWholeSite(place.ClientId, siteId) ? AccessLevel.Full
                    : scope.SeesSite(place.ClientId, siteId) ? AccessLevel.Partial : AccessLevel.None,
                _ => scope.ClientLevel(place.ClientId)
            };
            if (level != AccessLevel.None)
            {
                holders.Add(new AccessHolderView(user.Id, user.DisplayName, user.Email ?? string.Empty, userRoles, level, through));
            }
        }

        return holders;
    }

    private static async Task<string?> MembersProblemAsync(FleetoDbContext db, List<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return null;
        }

        var found = await db.Users.AsNoTracking().CountAsync(u => userIds.Contains(u.Id), cancellationToken);
        if (found != userIds.Count)
        {
            return "A user of the group no longer exists. Refresh the page and try again.";
        }

        var admins = await db.UserRoles.AsNoTracking().Where(ur => userIds.Contains(ur.UserId))
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.NormalizedName).AnyAsync(n => n == ScriptCheckResolver.AdminRole, cancellationToken);
        return admins ? "Admins see every client and are not put in groups. Remove the admin from the list." : null;
    }

    private static AccessTargetRef Ref(UserGroupRule rule) => rule switch
    {
        { EndpointId: { } endpointId } => new AccessTargetRef(AccessTargetKind.Endpoint, endpointId),
        { SiteId: { } siteId } => new AccessTargetRef(AccessTargetKind.Site, siteId),
        _ => new AccessTargetRef(AccessTargetKind.Client, rule.ClientId!.Value)
    };

    private static UserGroupRule Rule(Guid groupId, UserGroupRuleKind kind, AccessTargetRef target) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = groupId,
        Kind = kind,
        ClientId = target.Kind == AccessTargetKind.Client ? target.Id : null,
        SiteId = target.Kind == AccessTargetKind.Site ? target.Id : null,
        EndpointId = target.Kind == AccessTargetKind.Endpoint ? target.Id : null
    };

    private static List<AccessTargetView> Views(IEnumerable<UserGroupRule> rules, IReadOnlyDictionary<AccessTargetRef, string> labels) =>
        rules.Select(Ref).Where(labels.ContainsKey).Select(t => new AccessTargetView(t.Kind, t.Id, labels[t])).OrderBy(v => v.Label).ToList();

    /// <summary>The label of every target that still exists.</summary>
    private static async Task<Dictionary<AccessTargetRef, string>> LabelsAsync(FleetoDbContext db, IReadOnlyCollection<AccessTargetRef> targets,
        CancellationToken cancellationToken)
    {
        var clientIds = targets.Where(t => t.Kind == AccessTargetKind.Client).Select(t => t.Id).ToArray();
        var siteIds = targets.Where(t => t.Kind == AccessTargetKind.Site).Select(t => t.Id).ToArray();
        var endpointIds = targets.Where(t => t.Kind == AccessTargetKind.Endpoint).Select(t => t.Id).ToArray();
        var labels = new Dictionary<AccessTargetRef, string>();
        foreach (var c in await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).Select(c => new { c.Id, c.Code, c.Name })
                     .ToListAsync(cancellationToken))
        {
            labels[new AccessTargetRef(AccessTargetKind.Client, c.Id)] = c.Code + " · " + c.Name;
        }

        foreach (var s in await db.Sites.AsNoTracking().Where(s => siteIds.Contains(s.Id)).Select(s => new { s.Id, s.Client!.Code, s.Name })
                     .ToListAsync(cancellationToken))
        {
            labels[new AccessTargetRef(AccessTargetKind.Site, s.Id)] = s.Code + " / " + s.Name;
        }

        foreach (var e in await db.Endpoints.AsNoTracking().Where(e => endpointIds.Contains(e.Id)).Select(e => new { e.Id, e.Site!.Client!.Code, e.Hostname })
                     .ToListAsync(cancellationToken))
        {
            labels[new AccessTargetRef(AccessTargetKind.Endpoint, e.Id)] = e.Code + " / " + e.Hostname;
        }

        return labels;
    }
}

/// <summary>
/// Makes a change to what users may reach or do take effect at once (0.6.0): their remote session connections to an endpoint they
/// can no longer reach, or of a kind their roles no longer allow, are marked revoked, which the gateway ends and the signer refuses,
/// and their open pages are told to reload.
/// </summary>
internal static class AccessChanges
{
    public static async Task ApplyAsync(IFleetoDbContextFactory dbFactory, INotificationBus bus, ILogger logger, IEnumerable<Guid> userIds, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var db = dbFactory.CreateSystem();
        foreach (var userId in userIds.Distinct())
        {
            var scope = await UserAccessResolver.ScopeOfAsync(db, userId, cancellationToken);
            var (roles, granted) = await UserPermissions.LoadAsync(db, userId, cancellationToken);
            var caller = new Caller(userId, string.Empty, string.Empty, roles, scope, Granted: granted);
            var open = await db.RemoteSessionParticipants.AsNoTracking()
                .Where(p => p.UserId == userId && p.EndedAt == null && p.RevokedAt == null)
                .Join(db.RemoteSessions, p => p.SessionId, s => s.Id, (p, s) => new { p.Id, p.ClientId, p.EndpointId, s.Kind })
                .ToListAsync(cancellationToken);
            var lost = open.Where(p => !scope.SeesEndpoint(p.ClientId, p.EndpointId) || !caller.Has(RemotePermission(p.Kind)))
                .Select(p => p.Id).ToArray();
            if (lost.Length > 0)
            {
                await db.RemoteSessionParticipants.Where(p => lost.Contains(p.Id) && p.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.RevokedAt, now), cancellationToken);
            }

            try
            {
                await bus.PublishAsync(NotificationChannels.UserAccess, userId.ToString(), cancellationToken);
            }
            catch (Exception ex)
            {
                // The change is committed: pages pick it up within seconds and the gateway ends revoked connections within a minute.
                logger.LogWarning(ex, "Could not publish the access change of user {UserId}", userId);
            }
        }
    }

    /// <summary>The permission a kind of remote session needs.</summary>
    public static string RemotePermission(RemoteSessionKind kind) => RemoteSessionRules.Permission(kind);
}
