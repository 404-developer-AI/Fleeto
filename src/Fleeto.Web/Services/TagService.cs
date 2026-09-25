using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record TagView(Guid Id, string Name, TagColor Color);

public sealed record TagListItem(Guid Id, string Name, TagColor Color, int ClientCount);

/// <summary>
/// Tags on clients (0.6.0), in the way of Proxmox: typed on a client by an admin or technician, created on first use, and
/// colored from a fixed palette. The names and colors are instance-wide, so renaming, recoloring and deleting a tag in
/// Settings is for admins who see every client. A caller limited to clients only sees the tags of those clients.
/// </summary>
public sealed class TagService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public TagService(IFleetoDbContextFactory dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    /// <summary>Every tag with the number of clients that carry it, for Settings.</summary>
    public async Task<IReadOnlyList<TagListItem>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        if (!caller.Scope.AllClients)
        {
            throw new AccessDeniedException();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        return await db.Tags.AsNoTracking()
            .OrderBy(t => t.NormalizedName)
            .Select(t => new TagListItem(t.Id, t.Name, t.Color, db.ClientTags.Count(l => l.TagId == t.Id)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The tags the caller may filter on and pick from: every tag for a caller who sees every client, otherwise only the tags
    /// of the clients in the caller's scope, so the name of a tag used elsewhere does not leak.
    /// </summary>
    public async Task<IReadOnlyList<TagView>> ListVisibleAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var tags = caller.Scope.AllClients
            ? db.Tags.AsNoTracking()
            : db.Tags.AsNoTracking().Where(t => db.ClientTags.Any(l => l.TagId == t.Id));
        return await tags.OrderBy(t => t.NormalizedName).Select(t => new TagView(t.Id, t.Name, t.Color)).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the tags of a client. A name that does not exist yet becomes a tag with the color of its name
    /// (<see cref="TagRules.DefaultColor"/>); a name that differs only in case from an existing tag is that tag.
    /// </summary>
    public async Task<ServiceResult> SetClientTagsAsync(Caller caller, Guid clientId, IEnumerable<string?> names,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        var (wanted, problem) = CleanNames(names);
        if (problem is not null)
        {
            return ServiceResult.Fail(problem);
        }

        // Two technicians can create the same new tag at the same moment: the second one finds it on the next attempt.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SetClientTagsOnceAsync(caller, clientId, wanted, cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation() && attempt < 3)
            {
            }
        }
    }

    private async Task<ServiceResult> SetClientTagsOnceAsync(Caller caller, Guid clientId, List<string> wanted, CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.Create(caller.Scope);
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken);
        if (client is null)
        {
            return ServiceResult.NotFound("client");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var (before, after) = await ApplyAsync(db, clientId, wanted, now, cancellationToken);
        if (before.SequenceEqual(after, StringComparer.Ordinal))
        {
            return ServiceResult.Ok();
        }

        client.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ClientTagsChanged, "Client", client.Id.ToString(), client.Id,
            new { client.Code, From = before, To = after }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Cleans and validates the tag names typed for a client: one per name regardless of case, at most
    /// <see cref="TagRules.MaxPerClient"/>. Returns the names, or the problem to show.
    /// </summary>
    internal static (List<string> Names, string? Problem) CleanNames(IEnumerable<string?> names)
    {
        var wanted = new List<string>();
        foreach (var raw in names)
        {
            var name = TagRules.Clean(raw);
            if (TagRules.Validate(name) is { } problem)
            {
                return ([], problem);
            }

            if (!wanted.Any(w => TagRules.Normalize(w) == TagRules.Normalize(name)))
            {
                wanted.Add(name);
            }
        }

        return wanted.Count > TagRules.MaxPerClient
            ? ([], $"A client can have at most {TagRules.MaxPerClient} tags. Remove one first.")
            : (wanted, null);
    }

    /// <summary>
    /// Gives a client exactly the tags <paramref name="wanted"/> names (cleaned by <see cref="CleanNames"/>), creating
    /// the ones that do not exist yet, without saving. Also used when a client is created, in the same transaction.
    /// Returns the tag names before and after, ordered, for the audit log.
    /// </summary>
    internal static async Task<(List<string> Before, List<string> After)> ApplyAsync(FleetoDbContext db, Guid clientId, List<string> wanted,
        DateTime now, CancellationToken cancellationToken)
    {
        var current = await db.ClientTags.Include(l => l.Tag).Where(l => l.ClientId == clientId).ToListAsync(cancellationToken);
        var normalized = wanted.Select(TagRules.Normalize).ToList();
        var existing = await db.Tags.Where(t => normalized.Contains(t.NormalizedName)).ToListAsync(cancellationToken);

        var tags = new List<Tag>();
        foreach (var name in wanted)
        {
            var tag = existing.FirstOrDefault(t => t.NormalizedName == TagRules.Normalize(name));
            if (tag is null)
            {
                tag = new Tag
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    NormalizedName = TagRules.Normalize(name),
                    Color = TagRules.DefaultColor(name),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.Tags.Add(tag);
            }

            tags.Add(tag);
        }

        var before = current.Select(l => l.Tag!.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var after = tags.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (before.SequenceEqual(after, StringComparer.Ordinal))
        {
            return (before, after);
        }

        db.ClientTags.RemoveRange(current.Where(l => tags.All(t => t.Id != l.TagId)));
        foreach (var tag in tags.Where(t => current.All(l => l.TagId != t.Id)))
        {
            db.ClientTags.Add(new ClientTag { ClientId = clientId, TagId = tag.Id, CreatedAt = now });
        }

        return (before, after);
    }

    /// <summary>Renames or recolors a tag everywhere it is used.</summary>
    public async Task<ServiceResult> UpdateAsync(Caller caller, Guid tagId, string? name, TagColor color, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin || !caller.Scope.AllClients)
        {
            return ServiceResult.Forbidden();
        }

        var cleanName = TagRules.Clean(name);
        if (TagRules.Validate(cleanName) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        if (!Enum.IsDefined(color))
        {
            return ServiceResult.Fail("Choose a color from the palette.");
        }

        try
        {
            await using var db = _dbFactory.Create(caller.Scope);
            var tag = await db.Tags.SingleOrDefaultAsync(t => t.Id == tagId, cancellationToken);
            if (tag is null)
            {
                return ServiceResult.NotFound("tag");
            }

            var normalized = TagRules.Normalize(cleanName);
            if (normalized != tag.NormalizedName && await db.Tags.AnyAsync(t => t.NormalizedName == normalized, cancellationToken))
            {
                return ServiceResult.Fail($"A tag named {cleanName} already exists. Choose another name.");
            }

            if (tag.Name == cleanName && tag.Color == color)
            {
                return ServiceResult.Ok();
            }

            var now = _time.GetUtcNow().UtcDateTime;
            var previous = new { tag.Name, Color = tag.Color.ToString() };
            tag.Name = cleanName;
            tag.NormalizedName = normalized;
            tag.Color = color;
            tag.UpdatedAt = now;
            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.TagUpdated, "Tag", tag.Id.ToString(), null,
                new { From = previous, To = new { tag.Name, Color = color.ToString() } }), now));
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult.Ok();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return ServiceResult.Fail($"A tag named {cleanName} already exists. Choose another name.");
        }
    }

    /// <summary>Deletes a tag and removes it from every client that carries it.</summary>
    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid tagId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin || !caller.Scope.AllClients)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var tag = await db.Tags.SingleOrDefaultAsync(t => t.Id == tagId, cancellationToken);
        if (tag is null)
        {
            return ServiceResult.NotFound("tag");
        }

        var clients = await db.ClientTags.CountAsync(l => l.TagId == tagId, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        db.Tags.Remove(tag);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.TagDeleted, "Tag", tag.Id.ToString(), null,
            new { tag.Name, Clients = clients }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>The tags of the given clients, ordered by name; read through the caller's context, so its scope applies.</summary>
    internal static async Task<Dictionary<Guid, IReadOnlyList<TagView>>> ForClientsAsync(FleetoDbContext db, IReadOnlyCollection<Guid> clientIds,
        CancellationToken cancellationToken)
    {
        if (clientIds.Count == 0)
        {
            return [];
        }

        var rows = await db.ClientTags.AsNoTracking()
            .Where(l => clientIds.Contains(l.ClientId))
            .OrderBy(l => l.Tag!.NormalizedName)
            .Select(l => new { l.ClientId, View = new TagView(l.TagId, l.Tag!.Name, l.Tag.Color) })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.ClientId).ToDictionary(g => g.Key, g => (IReadOnlyList<TagView>)g.Select(r => r.View).ToList());
    }
}
