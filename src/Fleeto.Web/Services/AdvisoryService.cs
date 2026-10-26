using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>One advisory as the dashboard shows it. <paramref name="IsNew"/>: published since the user last opened the panel.</summary>
public sealed record AdvisoryView(Guid Id, string Source, string SourceName, string Title, string? Summary, DateTime PublishedAt,
    IReadOnlyList<string> Cves, string? Link, bool IsNew, DateTime? HandledAt, string? HandledByName, string? HandleComment);

/// <summary>A source as the panel shows it: when its data was last fetched, and why the last attempt failed.</summary>
public sealed record AdvisorySourceView(string Key, string Name, string HomeUrl, DateTime? LastSuccessAt, string? Problem);

public sealed record AdvisoryPanel(IReadOnlyList<AdvisoryView> Advisories, IReadOnlyList<AdvisorySourceView> Sources);

/// <summary>
/// Security advisories on the dashboard (0.8.0 step 0, VULNERABILITIES.md §2): the warnings of the last 30 days, for every user with
/// the permission, whatever clients its groups give, because an advisory belongs to no client and is public information. Marking
/// one as handled takes a comment, kept with it; the audit log holds its length, never its text.
/// </summary>
public sealed class AdvisoryService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public AdvisoryService(IFleetoDbContextFactory dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    /// <summary>
    /// The panel, or null for a caller without the permission. Marks every advisory as seen by the caller, so one is new only on the
    /// first visit after it was published; the advisories of this call keep their mark until the page reloads them.
    /// </summary>
    public async Task<AdvisoryPanel?> GetPanelAsync(Caller caller, bool markSeen, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.AdvisoriesView))
        {
            return null;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var since = now - AdvisorySources.Shown;
        await using var db = _dbFactory.CreateSystem();
        var seenAt = await db.Users.AsNoTracking().Where(u => u.Id == caller.UserId).Select(u => u.AdvisoriesSeenAt).FirstOrDefaultAsync(cancellationToken);
        var rows = await (
                from a in db.Advisories.AsNoTracking()
                join s in db.AdvisorySources.AsNoTracking() on a.Source equals s.Key
                join h in db.AdvisoryHandlings.AsNoTracking() on a.Id equals h.AdvisoryId into handlings
                from h in handlings.DefaultIfEmpty()
                where s.Enabled && a.PublishedAt >= since
                orderby a.PublishedAt descending
                select new { Advisory = a, Handling = h })
            .Take(200)
            .ToListAsync(cancellationToken);
        var sources = await db.AdvisorySources.AsNoTracking().Where(s => s.Enabled).OrderBy(s => s.Key).ToListAsync(cancellationToken);

        if (markSeen)
        {
            await db.Users.Where(u => u.Id == caller.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.AdvisoriesSeenAt, now), cancellationToken);
        }

        var advisories = rows.Select(r => new AdvisoryView(r.Advisory.Id, r.Advisory.Source, AdvisorySources.Find(r.Advisory.Source)?.Name ?? r.Advisory.Source,
            r.Advisory.Title, r.Advisory.Summary, r.Advisory.PublishedAt, r.Advisory.Cves,
            // Checked again on the way out, so a link stored by an older rule can never point elsewhere.
            AdvisorySources.Find(r.Advisory.Source) is { } info ? AdvisoryRules.SafeLink(r.Advisory.Link, info.Host) : null,
            // By when Fleeto first stored it, not by its publication: a source can publish an hour before Fleeto fetches it.
            seenAt is null || r.Advisory.FetchedAt > seenAt,
            r.Handling?.HandledAt, r.Handling?.UserName, r.Handling?.Comment)).ToList();
        var sourceViews = sources.Select(s => AdvisorySources.Find(s.Key) is { } info
                ? new AdvisorySourceView(s.Key, info.Name, info.HomeUrl, s.LastSuccessAt, s.Problem)
                : null)
            .OfType<AdvisorySourceView>().ToList();
        return new AdvisoryPanel(advisories, sourceViews);
    }

    /// <summary>Marks an advisory as handled with a required comment. Instance-wide: one handling per advisory.</summary>
    public async Task<ServiceResult> HandleAsync(Caller caller, Guid id, string? comment, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.AdvisoriesHandle))
        {
            return ServiceResult.Forbidden();
        }

        var text = comment?.Trim() ?? string.Empty;
        if (text.Length is 0 or > AdvisoryRules.MaxCommentLength)
        {
            return ServiceResult.Fail($"Enter a comment of at most {AdvisoryRules.MaxCommentLength} characters: what you did, for example which clients you informed.");
        }

        await using var db = _dbFactory.CreateSystem();
        var advisory = await db.Advisories.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.Id, a.Source }).SingleOrDefaultAsync(cancellationToken);
        if (advisory is null)
        {
            return ServiceResult.NotFound("advisory");
        }

        if (await db.AdvisoryHandlings.AnyAsync(h => h.AdvisoryId == id, cancellationToken))
        {
            return ServiceResult.Fail("Somebody marked this advisory as handled already. Refresh the dashboard.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.AdvisoryHandlings.Add(new AdvisoryHandling { AdvisoryId = id, UserId = caller.UserId, UserName = caller.Name, Comment = text, HandledAt = now });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AdvisoryHandled, "Advisory", id.ToString(), null,
            new { advisory.Source, CommentLength = text.Length }), now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return ServiceResult.Fail("Somebody marked this advisory as handled already. Refresh the dashboard.");
        }

        return ServiceResult.Ok();
    }
}
