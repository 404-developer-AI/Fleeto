using System.Globalization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Web.Services;

public sealed record NoteView(Guid Id, Guid AuthorUserId, string AuthorName, string Body, DateTime CreatedAt, DateTime? EditedAt);

/// <param name="Managed">False for an agent-only endpoint: notes are stored but not shown, and nothing can be added or changed.</param>
public sealed record NotePage(bool Managed, IReadOnlyList<NoteView> Items, string? NextCursor);

/// <summary>
/// Notes on endpoints, newest first. Managed endpoints only (CLAUDE.md, Licensing): for an agent-only endpoint the list is empty
/// and adding, editing and deleting are refused server-side; notes written before stay stored (nothing is deleted) and return
/// when the endpoint is managed again. The author edits their own note,
/// an admin deletes. Audit entries record the note id and length, never the body: the audit log is append-only, and a
/// deleted endpoint must take its notes (which can hold personal data) with it.
/// </summary>
public sealed class NoteService
{
    public const int PageSize = 50;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly TimeProvider _time;

    public NoteService(IFleetoDbContextFactory dbFactory, LicenseService licenses, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _time = time;
    }

    /// <summary>Newest first, keyset on (CreatedAt, Id). Returns null when the endpoint does not exist.</summary>
    public async Task<NotePage?> ListAsync(Caller caller, Guid endpointId, string? cursor = null, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId).Select(e => new { e.Tier }).SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var managed = TierRules.EffectiveTier(endpoint.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) == EndpointTier.Managed;
        if (!managed)
        {
            return new NotePage(false, [], null);
        }

        var notes = db.Notes.AsNoTracking().Where(n => n.EndpointId == endpointId);
        if (TryParseCursor(cursor, out var createdAt, out var id))
        {
            notes = notes.Where(n => EF.Functions.LessThan(ValueTuple.Create(n.CreatedAt, n.Id), ValueTuple.Create(createdAt, id)));
        }

        var items = await notes.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(PageSize + 1)
            .Select(n => new NoteView(n.Id, n.AuthorUserId, n.AuthorName, n.Body, n.CreatedAt, n.EditedAt))
            .ToListAsync(cancellationToken);
        string? next = null;
        if (items.Count > PageSize)
        {
            items.RemoveAt(PageSize);
            next = FormatCursor(items[^1].CreatedAt, items[^1].Id);
        }

        return new NotePage(managed, items, next);
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, Guid endpointId, string? body, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        if (ValidateBody(body) is { } problem)
        {
            return ServiceResult<Guid>.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult<Guid>.NotFound("endpoint");
        }

        if (await TierProblemAsync(db, endpoint, cancellationToken) is { } tierProblem)
        {
            return ServiceResult<Guid>.Fail(tierProblem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var note = new Note
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            AuthorUserId = caller.UserId,
            AuthorName = caller.Name.Length <= 200 ? caller.Name : caller.Name[..200],
            Body = body!.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Notes.Add(note);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.NoteCreated, "Endpoint", endpoint.Id.ToString(), endpoint.ClientId,
            new { endpoint.Hostname, NoteId = note.Id, note.Body.Length }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(note.Id);
    }

    /// <summary>Changes the body of a note. Only its author may, and only while the endpoint is managed.</summary>
    public async Task<ServiceResult> UpdateAsync(Caller caller, Guid noteId, string? body, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        if (ValidateBody(body) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId, cancellationToken);
        if (note is null)
        {
            return ServiceResult.NotFound("note");
        }

        if (note.AuthorUserId != caller.UserId)
        {
            return ServiceResult.Fail("Only the author can edit a note. Add a new note instead.");
        }

        var endpoint = await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == note.EndpointId, cancellationToken);
        if (await TierProblemAsync(db, endpoint, cancellationToken) is { } tierProblem)
        {
            return ServiceResult.Fail(tierProblem);
        }

        var clean = body!.Trim();
        if (clean == note.Body)
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        note.Body = clean;
        note.EditedAt = now;
        note.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.NoteUpdated, "Endpoint", note.EndpointId.ToString(), note.ClientId,
            new { endpoint.Hostname, NoteId = note.Id, note.Body.Length }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Deletes a note. Admins only, and only while the endpoint is managed.</summary>
    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid noteId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId, cancellationToken);
        if (note is null)
        {
            return ServiceResult.NotFound("note");
        }

        var endpoint = await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == note.EndpointId, cancellationToken);
        if (await TierProblemAsync(db, endpoint, cancellationToken) is { } tierProblem)
        {
            return ServiceResult.Fail(tierProblem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.Notes.Remove(note);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.NoteDeleted, "Endpoint", note.EndpointId.ToString(), note.ClientId,
            new { endpoint.Hostname, NoteId = note.Id, note.AuthorName, note.CreatedAt }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private async Task<string?> TierProblemAsync(FleetoDbContext db, Endpoint endpoint, CancellationToken cancellationToken)
    {
        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        if (TierRules.EffectiveTier(endpoint.Tier, license) == EndpointTier.Managed)
        {
            return null;
        }

        return endpoint.Tier == EndpointTier.Managed
            ? "Notes cannot be changed while the license has expired. Load a new license in Settings."
            : new TierRequiredException(endpoint.Id, ManagedFeature.Notes).Message;
    }

    private static string? ValidateBody(string? body)
    {
        var clean = body?.Trim();
        if (string.IsNullOrEmpty(clean))
        {
            return "Write the note first.";
        }

        return clean.Length > Note.MaxBodyLength ? $"A note can be at most {Note.MaxBodyLength} characters." : null;
    }

    internal static string FormatCursor(DateTime time, Guid id) =>
        time.Ticks.ToString(CultureInfo.InvariantCulture) + "_" + id.ToString("N");

    internal static bool TryParseCursor(string? cursor, out DateTime time, out Guid id) => AlertService.TryParseCursor(cursor, out time, out id);
}
