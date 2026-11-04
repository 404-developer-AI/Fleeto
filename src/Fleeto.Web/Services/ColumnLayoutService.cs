using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>The columns of the endpoint list a user sees, by the layout that applies to it (<see cref="Id"/> null for the default).</summary>
public sealed record UserColumnLayout(Guid? Id, string Name, IReadOnlyList<string> Columns);

public sealed record ColumnLayoutRoleView(Guid Id, string Name);

public sealed record ColumnLayoutListItem(Guid Id, string Name, string? Description, int Priority, IReadOnlyList<string> Columns,
    IReadOnlyList<ColumnLayoutRoleView> Roles, DateTime UpdatedAt, string? UpdatedBy);

/// <summary>Which column layout the users of a role see now, by the order of the layouts.</summary>
public sealed record RoleColumnLayoutView(Guid RoleId, string RoleName, Guid? LayoutId, string LayoutName);

public sealed record ColumnLayoutOverview(IReadOnlyList<ColumnLayoutListItem> Layouts, IReadOnlyList<RoleColumnLayoutView> Roles,
    IReadOnlyList<ColumnLayoutRoleView> AllRoles);

/// <summary>What Settings sends to create or change a column layout.</summary>
public sealed record ColumnLayoutInput(string? Name, string? Description, IReadOnlyList<string> Columns, IReadOnlyCollection<Guid> RoleIds);

/// <summary>
/// Column layouts of the endpoint list (0.8.0, decided 2026-11-04): made by a user with "Column layouts" (admins), linked to roles, and
/// applied to a user by <see cref="ColumnLayoutRules.Pick"/>, like dashboards. A user never changes its own columns. Every change is
/// audited.
/// </summary>
public sealed class ColumnLayoutService
{
    public const string DefaultName = "Default";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public ColumnLayoutService(IFleetoDbContextFactory dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public static UserColumnLayout Default { get; } = new(null, DefaultName, EndpointColumns.Default);

    /// <summary>The columns of the caller: the first layout in order linked to one of its roles, otherwise the default.</summary>
    public async Task<UserColumnLayout> GetForUserAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        var roleIds = await db.Roles.AsNoTracking().Where(r => caller.Roles.Contains(r.Name!)).Select(r => r.Id).ToListAsync(cancellationToken);
        var layouts = await db.ColumnLayouts.AsNoTracking()
            .Select(l => new { l.Id, l.Name, l.Priority, l.Columns, Roles = l.Roles.Select(r => r.RoleId).ToList() })
            .ToListAsync(cancellationToken);
        var id = ColumnLayoutRules.Pick(layouts.Select(l => new ColumnLayoutChoice(l.Id, l.Priority, l.Roles)), roleIds);
        return layouts.FirstOrDefault(l => l.Id == id) is { } layout ? new UserColumnLayout(layout.Id, layout.Name, layout.Columns) : Default;
    }

    /// <summary>Settings, Column layouts: every layout in order, and which layout the users of each role see.</summary>
    public async Task<ColumnLayoutOverview> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.ColumnLayoutsManage);
        await using var db = _dbFactory.CreateSystem();
        var roles = (await db.Roles.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(r => r.IsBuiltIn ? FleetoRoles.All.ToList().IndexOf(r.Name!) : int.MaxValue)
            .ThenBy(r => BuiltInRoles.DisplayName(r.Name!), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var roleNames = roles.ToDictionary(r => r.Id, r => BuiltInRoles.DisplayName(r.Name!));
        var layouts = await db.ColumnLayouts.AsNoTracking().Include(l => l.Roles).OrderBy(l => l.Priority).ToListAsync(cancellationToken);
        var items = layouts.Select(l => new ColumnLayoutListItem(l.Id, l.Name, l.Description, l.Priority, l.Columns,
                l.Roles.Where(r => roleNames.ContainsKey(r.RoleId)).Select(r => new ColumnLayoutRoleView(r.RoleId, roleNames[r.RoleId]))
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                l.UpdatedAt, l.UpdatedBy))
            .ToList();
        var choices = layouts.Select(l => new ColumnLayoutChoice(l.Id, l.Priority, l.Roles.Select(r => r.RoleId).ToList())).ToList();
        var names = layouts.ToDictionary(l => l.Id, l => l.Name);
        var perRole = roles
            .Select(r => ColumnLayoutRules.Pick(choices, [r.Id]) is { } id
                ? new RoleColumnLayoutView(r.Id, roleNames[r.Id], id, names[id])
                : new RoleColumnLayoutView(r.Id, roleNames[r.Id], null, DefaultName))
            .ToList();
        return new ColumnLayoutOverview(items, perRole, roles.Select(r => new ColumnLayoutRoleView(r.Id, roleNames[r.Id])).ToList());
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, ColumnLayoutInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ColumnLayoutsManage))
        {
            return ServiceResult<Guid>.Forbidden();
        }

        var (columns, description, problem) = Check(input);
        if (problem is not null)
        {
            return ServiceResult<Guid>.Fail(problem);
        }

        await using var db = _dbFactory.CreateSystem();
        if (await db.ColumnLayouts.CountAsync(cancellationToken) >= ColumnLayoutRules.MaxLayouts)
        {
            return ServiceResult<Guid>.Fail($"An instance holds at most {ColumnLayoutRules.MaxLayouts} column layouts. Delete one first.");
        }

        var roleIds = input.RoleIds.Distinct().ToList();
        if (await RolesProblemAsync(db, roleIds, cancellationToken) is { } rolesProblem)
        {
            return ServiceResult<Guid>.Fail(rolesProblem);
        }

        var now = Now;
        var layout = new ColumnLayout
        {
            Id = Guid.NewGuid(),
            Name = input.Name!.Trim(),
            Description = description,
            Priority = (await db.ColumnLayouts.MaxAsync(l => (int?)l.Priority, cancellationToken) ?? 0) + 1,
            Columns = columns!.ToList(),
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = caller.Name,
            Roles = roleIds.Select(r => new ColumnLayoutRole { RoleId = r }).ToList()
        };
        db.ColumnLayouts.Add(layout);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ColumnLayoutCreated, "ColumnLayout", layout.Id.ToString(), null,
            new { layout.Name, layout.Columns, Roles = roleIds.Order().ToList() }), now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return ServiceResult<Guid>.Fail("Another column layout has this name, or was created at the same moment. Choose another name or try again.");
        }

        return ServiceResult<Guid>.Ok(layout.Id);
    }

    public async Task<ServiceResult> UpdateAsync(Caller caller, Guid id, ColumnLayoutInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ColumnLayoutsManage))
        {
            return ServiceResult.Forbidden();
        }

        var (columns, description, problem) = Check(input);
        if (problem is not null)
        {
            return ServiceResult.Fail(problem);
        }

        await using var db = _dbFactory.CreateSystem();
        var layout = await db.ColumnLayouts.Include(l => l.Roles).SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (layout is null)
        {
            return ServiceResult.NotFound("column layout");
        }

        var wanted = input.RoleIds.Distinct().ToList();
        if (await RolesProblemAsync(db, wanted, cancellationToken) is { } rolesProblem)
        {
            return ServiceResult.Fail(rolesProblem);
        }

        var before = new { layout.Name, layout.Description, Columns = layout.Columns.ToList(), Roles = layout.Roles.Select(r => r.RoleId).Order().ToList() };
        layout.Name = input.Name!.Trim();
        layout.Description = description;
        layout.Columns = columns!.ToList();
        foreach (var link in layout.Roles.Where(r => !wanted.Contains(r.RoleId)).ToList())
        {
            layout.Roles.Remove(link);
        }

        foreach (var roleId in wanted.Where(r => layout.Roles.All(l => l.RoleId != r)))
        {
            layout.Roles.Add(new ColumnLayoutRole { LayoutId = id, RoleId = roleId });
        }

        layout.UpdatedAt = Now;
        layout.UpdatedBy = caller.Name;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ColumnLayoutUpdated, "ColumnLayout", id.ToString(), null,
            new { From = before, To = new { layout.Name, layout.Description, layout.Columns, Roles = wanted.Order().ToList() } }), Now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return ServiceResult.Fail("Another column layout has this name. Choose another name.");
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ColumnLayoutsManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var layout = await db.ColumnLayouts.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (layout is null)
        {
            return ServiceResult.NotFound("column layout");
        }

        db.ColumnLayouts.Remove(layout);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ColumnLayoutDeleted, "ColumnLayout", id.ToString(), null,
            new { layout.Name }), Now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Moves a layout one place up (earlier, so it wins) or down in the order.</summary>
    public async Task<ServiceResult> MoveAsync(Caller caller, Guid id, bool up, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ColumnLayoutsManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var layouts = await db.ColumnLayouts.OrderBy(l => l.Priority).ToListAsync(cancellationToken);
        var index = layouts.FindIndex(l => l.Id == id);
        if (index < 0)
        {
            return ServiceResult.NotFound("column layout");
        }

        var other = up ? index - 1 : index + 1;
        if (other < 0 || other >= layouts.Count)
        {
            return ServiceResult.Ok();
        }

        (layouts[index], layouts[other]) = (layouts[other], layouts[index]);
        // Priorities are unique: park every layout on a negative number first, then give each its place.
        for (var i = 0; i < layouts.Count; i++)
        {
            layouts[i].Priority = -1 - i;
        }

        await db.SaveChangesAsync(cancellationToken);
        for (var i = 0; i < layouts.Count; i++)
        {
            layouts[i].Priority = i + 1;
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ColumnLayoutOrderChanged, "ColumnLayout", id.ToString(), null,
            new { Order = layouts.Select(l => l.Name).ToList() }), Now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private static (IReadOnlyList<string>? Columns, string? Description, string? Problem) Check(ColumnLayoutInput input)
    {
        if (ColumnLayoutRules.NameProblem(input.Name) is { } nameProblem)
        {
            return (null, null, nameProblem);
        }

        var description = ServiceSupport.Clean(input.Description);
        if (description is { Length: > ColumnLayoutRules.MaxDescriptionLength })
        {
            return (null, null, $"A description has at most {ColumnLayoutRules.MaxDescriptionLength} characters.");
        }

        var (columns, problem) = ColumnLayoutRules.Validate(input.Columns);
        return (columns, description, problem);
    }

    private static async Task<string?> RolesProblemAsync(FleetoDbContext db, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        var known = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).CountAsync(cancellationToken);
        return known != roleIds.Count ? "A chosen role no longer exists. Refresh the page and try again." : null;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;
}
