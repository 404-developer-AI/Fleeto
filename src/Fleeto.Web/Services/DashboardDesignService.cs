using System.Text.Json;
using Fleeto.Core.Dashboards;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>The dashboard a user sees: one of the instance, or the built-in default (<see cref="Id"/> null).</summary>
public sealed record UserDashboard(Guid? Id, string Name, DashboardLayout Layout);

public sealed record DashboardRoleView(Guid Id, string Name);

public sealed record DashboardListItem(Guid Id, string Name, string? Description, int Priority, int PublishedVersion, DateTime? PublishedAt,
    string? PublishedBy, bool HasDraft, IReadOnlyList<DashboardRoleView> Roles, int TileCount);

/// <summary>Which dashboard the users of a role see now, by the order of the dashboards.</summary>
public sealed record RoleDashboardView(Guid RoleId, string RoleName, Guid? DashboardId, string DashboardName);

public sealed record DashboardOverview(IReadOnlyList<DashboardListItem> Dashboards, IReadOnlyList<RoleDashboardView> Roles);

public sealed record DashboardDetail(Guid Id, string Name, string? Description, IReadOnlyList<Guid> RoleIds, DashboardLayout Layout, bool HasDraft,
    int PublishedVersion, DateTime? PublishedAt, string? PublishedBy, DateTime UpdatedAt, string? UpdatedBy);

public sealed record DashboardVersionView(int Version, DateTime PublishedAt, string PublishedBy, int TileCount);

/// <summary>A role to preview a dashboard with, and its permissions.</summary>
public sealed record DashboardPreviewRole(Guid Id, string Name, IReadOnlySet<string> Permissions);

/// <summary>The names a tile scope can choose from in the designer.</summary>
public sealed record DashboardScopeChoices(IReadOnlyList<ClientOption> Clients, IReadOnlyList<(Guid Id, Guid ClientId, string Name)> Sites,
    IReadOnlyList<TagView> Tags);

/// <summary>
/// Dashboards of the instance (0.8.0, DASHBOARDS.md): designed by a user with "Edit dashboards", linked to roles, and shown to a
/// user by <see cref="DashboardRules.Pick"/>. Every layout is checked by <see cref="DashboardRules.Validate"/> before it is stored,
/// and scopes keep only clients, sites and tags that exist. Changes to the draft are not audited; creating, changing, publishing,
/// importing, deleting and the order are.
/// </summary>
public sealed class DashboardDesignService
{
    public const string ExportFormat = "fleeto-dashboard";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<DashboardDesignService> _logger;

    public DashboardDesignService(IFleetoDbContextFactory dbFactory, TimeProvider time, ILogger<DashboardDesignService> logger)
    {
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The dashboard of the caller: the first published one in order linked to one of its roles, otherwise the default. A stored
    /// layout this release cannot read falls back to the default rather than showing nothing.
    /// </summary>
    public async Task<UserDashboard> GetForUserAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        var roleIds = await db.Roles.AsNoTracking().Where(r => caller.Roles.Contains(r.Name!)).Select(r => r.Id).ToListAsync(cancellationToken);
        var choices = await ChoicesAsync(db, cancellationToken);
        if (DashboardRules.Pick(choices, roleIds) is not { } id)
        {
            return Default;
        }

        var dashboard = await db.Dashboards.AsNoTracking().Where(d => d.Id == id).Select(d => new { d.Name, d.PublishedLayout })
            .SingleOrDefaultAsync(cancellationToken);
        if (DashboardLayout.FromJson(dashboard?.PublishedLayout) is not { } layout)
        {
            _logger.LogWarning("The published layout of dashboard {DashboardId} could not be read; the default dashboard is shown", id);
            return Default;
        }

        return new UserDashboard(id, dashboard!.Name, layout);
    }

    public static UserDashboard Default { get; } = new(null, DefaultDashboard.Name, DefaultDashboard.Layout);

    /// <summary>Settings, Dashboards: every dashboard in order, and which dashboard the users of each role see.</summary>
    public async Task<DashboardOverview> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.DashboardsManage);
        await using var db = _dbFactory.CreateSystem();
        var roles = await db.Roles.AsNoTracking().ToListAsync(cancellationToken);
        var roleNames = roles.ToDictionary(r => r.Id, r => BuiltInRoles.DisplayName(r.Name!));
        var dashboards = await db.Dashboards.AsNoTracking().Include(d => d.Roles).OrderBy(d => d.Priority).ToListAsync(cancellationToken);
        var items = dashboards.Select(d => new DashboardListItem(d.Id, d.Name, d.Description, d.Priority, d.PublishedVersion, d.PublishedAt,
                d.PublishedBy, d.DraftLayout is not null,
                d.Roles.Where(r => roleNames.ContainsKey(r.RoleId)).Select(r => new DashboardRoleView(r.RoleId, roleNames[r.RoleId]))
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                DashboardLayout.FromJson(d.PublishedLayout ?? d.DraftLayout)?.Tiles.Count ?? 0))
            .ToList();
        var choices = dashboards.Select(Choice).ToList();
        var names = dashboards.ToDictionary(d => d.Id, d => d.Name);
        var perRole = roles
            .OrderBy(r => r.IsBuiltIn ? FleetoRoles.All.ToList().IndexOf(r.Name!) : int.MaxValue)
            .ThenBy(r => roleNames[r.Id], StringComparer.OrdinalIgnoreCase)
            .Select(r => DashboardRules.Pick(choices, [r.Id]) is { } id
                ? new RoleDashboardView(r.Id, roleNames[r.Id], id, names[id])
                : new RoleDashboardView(r.Id, roleNames[r.Id], null, DefaultDashboard.Name))
            .ToList();
        return new DashboardOverview(items, perRole);
    }

    public async Task<IReadOnlyList<DashboardRoleView>> ListRolesAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.DashboardsManage);
        await using var db = _dbFactory.CreateSystem();
        return (await db.Roles.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(r => r.IsBuiltIn ? FleetoRoles.All.ToList().IndexOf(r.Name!) : int.MaxValue)
            .ThenBy(r => BuiltInRoles.DisplayName(r.Name!), StringComparer.OrdinalIgnoreCase)
            .Select(r => new DashboardRoleView(r.Id, BuiltInRoles.DisplayName(r.Name!)))
            .ToList();
    }

    /// <summary>The roles with their permissions, to preview a dashboard as a user of that role would see it.</summary>
    public async Task<IReadOnlyList<DashboardPreviewRole>> ListPreviewRolesAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.DashboardsManage);
        await using var db = _dbFactory.CreateSystem();
        var roles = await db.Roles.AsNoTracking().ToListAsync(cancellationToken);
        var stored = await RoleAuthority.PermissionsOfAsync(db, roles, cancellationToken);
        return roles
            .OrderBy(r => r.IsBuiltIn ? FleetoRoles.All.ToList().IndexOf(r.Name!) : int.MaxValue)
            .ThenBy(r => BuiltInRoles.DisplayName(r.Name!), StringComparer.OrdinalIgnoreCase)
            .Select(r => new DashboardPreviewRole(r.Id, BuiltInRoles.DisplayName(r.Name!),
                r.Name == FleetoRoles.Admin ? BuiltInRoles.AdminPermissions.ToHashSet(StringComparer.Ordinal) : stored[r.Id].ToHashSet(StringComparer.Ordinal)))
            .ToList();
    }

    /// <summary>A dashboard to design: its draft, or what is published when there is no draft.</summary>
    public async Task<DashboardDetail?> GetAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.DashboardsManage);
        await using var db = _dbFactory.CreateSystem();
        var dashboard = await db.Dashboards.AsNoTracking().Include(d => d.Roles).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dashboard is null)
        {
            return null;
        }

        var layout = DashboardLayout.FromJson(dashboard.DraftLayout ?? dashboard.PublishedLayout) ?? DashboardLayout.Empty;
        return new DashboardDetail(dashboard.Id, dashboard.Name, dashboard.Description, dashboard.Roles.Select(r => r.RoleId).ToList(), layout,
            dashboard.DraftLayout is not null, dashboard.PublishedVersion, dashboard.PublishedAt, dashboard.PublishedBy, dashboard.UpdatedAt,
            dashboard.UpdatedBy);
    }

    /// <summary>The clients, sites and tags a scope can name. The editor sees every client: the permission is instance-wide.</summary>
    public async Task<DashboardScopeChoices> GetScopeChoicesAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.DashboardsManage);
        await using var db = _dbFactory.Create(caller.Scope);
        var clients = await db.Clients.AsNoTracking().OrderBy(c => c.Code).Select(c => new ClientOption(c.Id, c.Code, c.Name))
            .ToListAsync(cancellationToken);
        var sites = (await db.Sites.AsNoTracking().OrderBy(s => s.Name).Select(s => new { s.Id, s.ClientId, s.Name }).ToListAsync(cancellationToken))
            .Select(s => (s.Id, s.ClientId, s.Name)).ToList();
        var tags = await db.Tags.AsNoTracking().OrderBy(t => t.NormalizedName).Select(t => new TagView(t.Id, t.Name, t.Color))
            .ToListAsync(cancellationToken);
        return new DashboardScopeChoices(clients, sites, tags);
    }

    /// <summary>
    /// A new dashboard, empty or as a copy of another one or of the default (<paramref name="copyOf"/> <see cref="Guid.Empty"/>). It
    /// is last in order, linked to no role, and has only a draft, so nobody sees it until it is linked and published.
    /// </summary>
    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, string? name, string? description, Guid? copyOf,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.DashboardsManage))
        {
            return ServiceResult<Guid>.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        DashboardLayout layout = DashboardLayout.Empty;
        if (copyOf == Guid.Empty)
        {
            layout = DefaultDashboard.Layout;
        }
        else if (copyOf is { } sourceId)
        {
            var source = await db.Dashboards.AsNoTracking().Where(d => d.Id == sourceId).Select(d => d.DraftLayout ?? d.PublishedLayout)
                .SingleOrDefaultAsync(cancellationToken);
            if (source is null)
            {
                return ServiceResult<Guid>.NotFound("dashboard");
            }

            layout = DashboardLayout.FromJson(source) ?? DashboardLayout.Empty;
        }

        return await AddAsync(db, caller, name, description, layout, AuditActions.DashboardCreated, new { CopyOf = copyOf }, cancellationToken);
    }

    /// <summary>Name, description and the roles that see the dashboard.</summary>
    public async Task<ServiceResult> UpdateAsync(Caller caller, Guid id, string? name, string? description, IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.DashboardsManage))
        {
            return ServiceResult.Forbidden();
        }

        if (DashboardRules.NameProblem(name) is { } nameProblem)
        {
            return ServiceResult.Fail(nameProblem);
        }

        var cleanDescription = ServiceSupport.Clean(description);
        if (cleanDescription is { Length: > DashboardRules.MaxDescriptionLength })
        {
            return ServiceResult.Fail($"A description has at most {DashboardRules.MaxDescriptionLength} characters.");
        }

        await using var db = _dbFactory.CreateSystem();
        var dashboard = await db.Dashboards.Include(d => d.Roles).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dashboard is null)
        {
            return ServiceResult.NotFound("dashboard");
        }

        var wanted = roleIds.Distinct().ToList();
        var known = await db.Roles.AsNoTracking().Where(r => wanted.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken);
        if (known.Count != wanted.Count)
        {
            return ServiceResult.Fail("A chosen role no longer exists. Refresh the page and try again.");
        }

        var before = new { dashboard.Name, dashboard.Description, Roles = dashboard.Roles.Select(r => r.RoleId).Order().ToList() };
        dashboard.Name = name!.Trim();
        dashboard.Description = cleanDescription;
        foreach (var link in dashboard.Roles.Where(r => !wanted.Contains(r.RoleId)).ToList())
        {
            dashboard.Roles.Remove(link);
        }

        foreach (var roleId in wanted.Where(r => dashboard.Roles.All(l => l.RoleId != r)))
        {
            dashboard.Roles.Add(new DashboardRole { DashboardId = id, RoleId = roleId });
        }

        Touch(dashboard, caller);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.DashboardUpdated, "Dashboard", id.ToString(), null,
            new { From = before, To = new { dashboard.Name, dashboard.Description, Roles = wanted.Order().ToList() } }), Now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return ServiceResult.Fail("Another dashboard has this name. Choose another name.");
        }

        return ServiceResult.Ok();
    }

    /// <summary>Stores the draft after checking it; the cleaned layout is returned so the designer shows what was stored.</summary>
    public async Task<ServiceResult<DashboardLayout>> SaveDraftAsync(Caller caller, Guid id, DashboardLayout layout,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.DashboardsManage))
        {
            return ServiceResult<DashboardLayout>.Forbidden();
        }

        var (valid, problem) = DashboardRules.Validate(layout, allowUnknown: true);
        if (problem is not null)
        {
            return ServiceResult<DashboardLayout>.Fail(problem);
        }

        await using var db = _dbFactory.CreateSystem();
        var dashboard = await db.Dashboards.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dashboard is null)
        {
            return ServiceResult<DashboardLayout>.NotFound("dashboard");
        }

        var (cleaned, _) = await KeepExistingAsync(db, valid!, cancellationToken);
        dashboard.DraftLayout = cleaned.ToJson();
        Touch(dashboard, caller);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<DashboardLayout>.Ok(cleaned);
    }

    /// <summary>Goes back to the published layout.</summary>
    public async Task<ServiceResult> DiscardDraftAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.DashboardsManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var dashboard = await db.Dashboards.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dashboard is null)
        {
            return ServiceResult.NotFound("dashboard");
        }

        if (dashboard.PublishedLayout is null)
        {
            return ServiceResult.Fail("This dashboard was never published, so there is nothing to go back to. Delete it instead.");
        }

        dashboard.DraftLayout = null;
        Touch(dashboard, caller);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Publishes the draft: users of its roles see it from now on. The last <see cref="DashboardRules.MaxVersions"/> versions are kept.</summary>
    public async Task<ServiceResult> PublishAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.DashboardsManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var dashboard = await db.Dashboards.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dashboard is null)
        {
            return ServiceResult.NotFound("dashboard");
        }

        if (dashboard.DraftLayout is null)
        {
            return ServiceResult.Fail("There are no changes to publish.");
        }

        var (valid, problem) = DashboardRules.Validate(DashboardLayout.FromJson(dashboard.DraftLayout), allowUnknown: true);
        if (problem is not null)
        {
            return ServiceResult.Fail(problem);
        }

        var (cleaned, _) = await KeepExistingAsync(db, valid!, cancellationToken);
        var json = cleaned.ToJson();
        var now = Now;
        dashboard.PublishedLayout = json;
        dashboard.DraftLayout = null;
        dashboard.PublishedVersion++;
        dashboard.PublishedAt = now;
        dashboard.PublishedBy = caller.Name;
        Touch(dashboard, caller);
        db.DashboardVersions.Add(new DashboardVersion
        {
            DashboardId = id, Version = dashboard.PublishedVersion, Layout = json, PublishedAt = now, PublishedBy = caller.Name
        });
        var old = await db.DashboardVersions.Where(v => v.DashboardId == id && v.Version <= dashboard.PublishedVersion - DashboardRules.MaxVersions)
            .ToListAsync(cancellationToken);
        db.DashboardVersions.RemoveRange(old);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.DashboardPublished, "Dashboard", id.ToString(), null,
            new { dashboard.Name, Version = dashboard.PublishedVersion, Tiles = cleaned.Tiles.Count }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    public async Task<IReadOnlyList<DashboardVersionView>> ListVersionsAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.DashboardsManage);
        await using var db = _dbFactory.CreateSystem();
        var versions = await db.DashboardVersions.AsNoTracking().Where(v => v.DashboardId == id).OrderByDescending(v => v.Version)
            .ToListAsync(cancellationToken);
        return versions.Select(v => new DashboardVersionView(v.Version, v.PublishedAt, v.PublishedBy,
            DashboardLayout.FromJson(v.Layout)?.Tiles.Count ?? 0)).ToList();
    }

    /// <summary>Makes an earlier published version the draft, to look at and publish again.</summary>
    public async Task<ServiceResult> RestoreVersionAsync(Caller caller, Guid id, int version, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.DashboardsManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var dashboard = await db.Dashboards.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        var stored = await db.DashboardVersions.AsNoTracking().SingleOrDefaultAsync(v => v.DashboardId == id && v.Version == version, cancellationToken);
        if (dashboard is null || stored is null)
        {
            return ServiceResult.NotFound(dashboard is null ? "dashboard" : "version");
        }

        dashboard.DraftLayout = stored.Layout;
        Touch(dashboard, caller);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.DashboardsManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var dashboard = await db.Dashboards.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dashboard is null)
        {
            return ServiceResult.NotFound("dashboard");
        }

        db.Dashboards.Remove(dashboard);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.DashboardDeleted, "Dashboard", id.ToString(), null,
            new { dashboard.Name }), Now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Moves a dashboard one place up (earlier, so it wins) or down in the order.</summary>
    public async Task<ServiceResult> MoveAsync(Caller caller, Guid id, bool up, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.DashboardsManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var dashboards = await db.Dashboards.OrderBy(d => d.Priority).ToListAsync(cancellationToken);
        var index = dashboards.FindIndex(d => d.Id == id);
        if (index < 0)
        {
            return ServiceResult.NotFound("dashboard");
        }

        var other = up ? index - 1 : index + 1;
        if (other < 0 || other >= dashboards.Count)
        {
            return ServiceResult.Ok();
        }

        (dashboards[index], dashboards[other]) = (dashboards[other], dashboards[index]);
        // Priorities are unique: park every dashboard on a negative number first, then give each its place.
        for (var i = 0; i < dashboards.Count; i++)
        {
            dashboards[i].Priority = -1 - i;
        }

        await db.SaveChangesAsync(cancellationToken);
        for (var i = 0; i < dashboards.Count; i++)
        {
            dashboards[i].Priority = i + 1;
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.DashboardOrderChanged, "Dashboard", id.ToString(), null,
            new { Order = dashboards.Select(d => d.Name).ToList() }), Now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>The dashboard as a file for another instance: name, description and layout, never roles.</summary>
    public async Task<ServiceResult<string>> ExportAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.DashboardsManage))
        {
            return ServiceResult<string>.Forbidden();
        }

        var detail = await GetAsync(caller, id, cancellationToken);
        if (detail is null)
        {
            return ServiceResult<string>.NotFound("dashboard");
        }

        var file = new DashboardFile(ExportFormat, DashboardLayout.CurrentSchema, detail.Name, detail.Description, detail.Layout);
        return ServiceResult<string>.Ok(JsonSerializer.Serialize(file, new JsonSerializerOptions(DashboardJson.Options) { WriteIndented = true }));
    }

    /// <summary>
    /// A dashboard from an exported file, as a draft linked to no role. Clients, sites and tags this instance does not have are left
    /// out of the scopes, which the result says; an unknown tile type refuses the file.
    /// </summary>
    public async Task<ServiceResult<(Guid Id, string? Note)>> ImportAsync(Caller caller, string json, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.DashboardsManage))
        {
            return ServiceResult<(Guid, string?)>.Forbidden();
        }

        DashboardFile? file;
        try
        {
            file = json.Length > DashboardRules.MaxImportBytes ? null : JsonSerializer.Deserialize<DashboardFile>(json, DashboardJson.Options);
        }
        catch (JsonException)
        {
            file = null;
        }

        if (file is null || file.Format != ExportFormat || file.Layout is null)
        {
            return ServiceResult<(Guid, string?)>.Fail("This is not a dashboard exported from Fleeto. Choose the file that Export saved.");
        }

        var (valid, problem) = DashboardRules.Validate(file.Layout, allowUnknown: false);
        if (problem is not null)
        {
            return ServiceResult<(Guid, string?)>.Fail(problem);
        }

        await using var db = _dbFactory.CreateSystem();
        var (cleaned, dropped) = await KeepExistingAsync(db, valid!, cancellationToken);
        var name = file.Name?.Trim() ?? string.Empty;
        if (await db.Dashboards.AnyAsync(d => d.Name == name, cancellationToken))
        {
            name = (name.Length > DashboardRules.MaxNameLength - 11 ? name[..(DashboardRules.MaxNameLength - 11)] : name) + " (imported)";
        }

        var added = await AddAsync(db, caller, name, file.Description, cleaned, AuditActions.DashboardImported,
            new { Dropped = dropped }, cancellationToken);
        if (!added.Success)
        {
            return ServiceResult<(Guid, string?)>.Fail(added.Problem!);
        }

        var note = dropped switch
        {
            0 => null,
            1 => "One client, site or tag of the file does not exist in this instance and was left out of its scope.",
            _ => $"{dropped} clients, sites or tags of the file do not exist in this instance and were left out of their scopes."
        };
        return ServiceResult<(Guid, string?)>.Ok((added.Value, note));
    }

    private async Task<ServiceResult<Guid>> AddAsync(FleetoDbContext db, Caller caller, string? name, string? description, DashboardLayout layout,
        string action, object details, CancellationToken cancellationToken)
    {
        if (DashboardRules.NameProblem(name) is { } nameProblem)
        {
            return ServiceResult<Guid>.Fail(nameProblem);
        }

        var cleanDescription = ServiceSupport.Clean(description);
        if (cleanDescription is { Length: > DashboardRules.MaxDescriptionLength })
        {
            return ServiceResult<Guid>.Fail($"A description has at most {DashboardRules.MaxDescriptionLength} characters.");
        }

        if (await db.Dashboards.CountAsync(cancellationToken) >= DashboardRules.MaxDashboards)
        {
            return ServiceResult<Guid>.Fail($"An instance holds at most {DashboardRules.MaxDashboards} dashboards. Delete one first.");
        }

        var now = Now;
        var dashboard = new Dashboard
        {
            Id = Guid.NewGuid(),
            Name = name!.Trim(),
            Description = cleanDescription,
            Priority = (await db.Dashboards.MaxAsync(d => (int?)d.Priority, cancellationToken) ?? 0) + 1,
            DraftLayout = layout.ToJson(),
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = caller.Name
        };
        db.Dashboards.Add(dashboard);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(action, "Dashboard", dashboard.Id.ToString(), null,
            new { dashboard.Name, Tiles = layout.Tiles.Count, Details = details }), now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return ServiceResult<Guid>.Fail("Another dashboard has this name, or was created at the same moment. Choose another name or try again.");
        }

        return ServiceResult<Guid>.Ok(dashboard.Id);
    }

    /// <summary>
    /// Keeps in every scope only the clients, sites and tags that exist. A tile scope left without any follows the dashboard; a
    /// dashboard scope left without any becomes every client. Returns how many were left out.
    /// </summary>
    internal static async Task<(DashboardLayout Layout, int Dropped)> KeepExistingAsync(FleetoDbContext db, DashboardLayout layout,
        CancellationToken cancellationToken)
    {
        var scopes = layout.Tiles.Select(t => t.Settings.Scope).OfType<TileScope>().Append(layout.Scope).ToList();
        var clients = scopes.SelectMany(s => s.ClientIds).Distinct().ToList();
        var sites = scopes.SelectMany(s => s.SiteIds).Distinct().ToList();
        var tags = scopes.SelectMany(s => s.TagIds).Distinct().ToList();
        if (clients.Count + sites.Count + tags.Count == 0)
        {
            return (layout, 0);
        }

        var existing = new HashSet<Guid>(await db.Clients.AsNoTracking().Where(c => clients.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken));
        existing.UnionWith(await db.Sites.AsNoTracking().Where(s => sites.Contains(s.Id)).Select(s => s.Id).ToListAsync(cancellationToken));
        existing.UnionWith(await db.Tags.AsNoTracking().Where(t => tags.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken));
        var dropped = 0;

        TileScope? Keep(TileScope scope, bool onTile)
        {
            var kept = scope with
            {
                ClientIds = scope.ClientIds.Where(existing.Contains).ToList(),
                SiteIds = scope.SiteIds.Where(existing.Contains).ToList(),
                TagIds = scope.TagIds.Where(existing.Contains).ToList()
            };
            dropped += scope.Ids.Count - kept.Ids.Count;
            if (kept.Mode is TileScopeMode.Clients or TileScopeMode.Sites or TileScopeMode.Tags && kept.Ids.Count == 0)
            {
                return onTile
                    ? kept.Class is null ? null : TileScope.FollowDashboard with { Class = kept.Class }
                    : TileScope.Everything with { Class = kept.Class };
            }

            return kept;
        }

        var tiles = layout.Tiles.Select(t => t.Settings.Scope is { } scope ? t with { Settings = t.Settings with { Scope = Keep(scope, true) } } : t)
            .ToList();
        return (layout with { Scope = Keep(layout.Scope, false)!, Tiles = tiles }, dropped);
    }

    private static async Task<List<DashboardChoice>> ChoicesAsync(FleetoDbContext db, CancellationToken cancellationToken) =>
        (await db.Dashboards.AsNoTracking()
            .Select(d => new { d.Id, d.Priority, Published = d.PublishedLayout != null, Roles = d.Roles.Select(r => r.RoleId).ToList() })
            .ToListAsync(cancellationToken))
        .Select(d => new DashboardChoice(d.Id, d.Priority, d.Published, d.Roles)).ToList();

    private static DashboardChoice Choice(Dashboard d) =>
        new(d.Id, d.Priority, d.PublishedLayout is not null, d.Roles.Select(r => r.RoleId).ToList());

    private void Touch(Dashboard dashboard, Caller caller)
    {
        dashboard.UpdatedAt = Now;
        dashboard.UpdatedBy = caller.Name;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>The file Export saves and Import reads.</summary>
    public sealed record DashboardFile(string Format, int Schema, string? Name, string? Description, DashboardLayout? Layout);
}
