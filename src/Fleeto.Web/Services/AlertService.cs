using System.Globalization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>State filter of the alert list. Open and Acknowledged leave out alerts on hold; OnHold shows only those.</summary>
public enum AlertStateFilter
{
    All,
    Open,
    Acknowledged,
    OnHold,
    Resolved
}

public sealed record AlertQuery(AlertStateFilter State, AlertSeverity? Severity, Guid? ClientId, string? Cursor = null, int Limit = 50);

public sealed record AlertPage(IReadOnlyList<AlertView> Items, string? NextCursor);

/// <summary>What a user did with an alert (0.6.0), with the comment.</summary>
public sealed record AlertActivityView(long Id, AlertActivityKind Kind, string Comment, string UserName, DateTime Time, DateTime? HeldUntil);

/// <summary>Alert list with keyset pagination, and manual acknowledge, hold and resolve.</summary>
public sealed class AlertService
{
    /// <summary>Longest hold. Longer silence belongs to maintenance mode, which is visible on the endpoint.</summary>
    public static readonly TimeSpan MaximumHold = TimeSpan.FromDays(7);

    public static readonly TimeSpan MinimumHold = TimeSpan.FromMinutes(1);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertService> _logger;

    public AlertService(IFleetoDbContextFactory dbFactory, INotificationBus bus, TimeProvider time, ILogger<AlertService> logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    /// <summary>Newest first, keyset on (OpenedAt, Id): no OFFSET, so later pages stay fast on a large table.</summary>
    public async Task<AlertPage> ListAsync(Caller caller, AlertQuery query, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.AlertsView);
        var limit = Math.Clamp(query.Limit, 1, 200);
        await using var db = _dbFactory.Create(caller.Scope);
        var alerts = db.Alerts.AsNoTracking();
        var now = _time.GetUtcNow().UtcDateTime;
        // An alert is on hold while HeldUntil is in the future; the workers clear it shortly after it passes.
        alerts = query.State switch
        {
            AlertStateFilter.Open => alerts.Where(a => a.State == AlertState.Open && (a.HeldUntil == null || a.HeldUntil <= now)),
            AlertStateFilter.Acknowledged => alerts.Where(a => a.State == AlertState.Acknowledged && (a.HeldUntil == null || a.HeldUntil <= now)),
            AlertStateFilter.OnHold => alerts.Where(a => a.State != AlertState.Resolved && a.HeldUntil != null && a.HeldUntil > now),
            AlertStateFilter.Resolved => alerts.Where(a => a.State == AlertState.Resolved),
            _ => alerts
        };

        if (query.Severity is { } severity)
        {
            alerts = alerts.Where(a => a.Severity == severity);
        }

        if (query.ClientId is { } clientId)
        {
            alerts = alerts.Where(a => a.ClientId == clientId);
        }

        if (TryParseCursor(query.Cursor, out var openedAt, out var id))
        {
            // Row value comparison in SQL, so the order matches PostgreSQL's uuid ordering.
            alerts = alerts.Where(a => EF.Functions.LessThan(ValueTuple.Create(a.OpenedAt, a.Id), ValueTuple.Create(openedAt, id)));
        }

        var items = await Project(db, alerts.OrderByDescending(a => a.OpenedAt).ThenByDescending(a => a.Id).Take(limit + 1))
            .ToListAsync(cancellationToken);
        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(limit);
            var last = items[^1];
            next = FormatCursor(last.OpenedAt, last.Id);
        }

        return new AlertPage(items, next);
    }

    internal static IQueryable<AlertView> Project(FleetoDbContext db, IQueryable<Alert> alerts) =>
        alerts.Select(a => new AlertView(a.Id, a.EndpointId, a.Endpoint!.Hostname, a.ClientId,
            db.Clients.Where(c => c.Id == a.ClientId).Select(c => c.Code).FirstOrDefault() ?? string.Empty,
            a.Kind, a.Severity, a.State, a.Title, a.Detail, a.OpenedAt, a.UpdatedAt, a.AcknowledgedAt, a.ResolvedAt, a.ResolvedReason,
            a.HeldUntil, a.CheckDefinitionId, a.Target));

    /// <summary>
    /// Acknowledges an open alert. Like holding and resolving it needs a comment (0.6.0): why, kept with the alert, so whoever looks
    /// at the alert later knows what was done.
    /// </summary>
    public async Task<ServiceResult> AcknowledgeAsync(Caller caller, Guid alertId, string? comment, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.AlertsAcknowledge))
        {
            return ServiceResult.Forbidden();
        }

        if (CommentProblem(comment, required: true) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var alert = await db.Alerts.Include(a => a.Endpoint).SingleOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert is null)
        {
            return ServiceResult.NotFound("alert");
        }

        if (alert.State != AlertState.Open)
        {
            return ServiceResult.Fail(alert.State == AlertState.Resolved
                ? "This alert is already resolved."
                : "This alert is already acknowledged.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        alert.State = AlertState.Acknowledged;
        alert.AcknowledgedAt = now;
        alert.AcknowledgedByUserId = caller.UserId;
        alert.UpdatedAt = now;
        var activity = AddActivity(db, caller, alert, AlertActivityKind.Acknowledged, comment, now);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AlertAcknowledged, "Alert", alert.Id.ToString(), alert.ClientId,
            new { alert.EndpointId, alert.Endpoint?.Hostname, alert.Title, CommentLength = activity.Comment.Length }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(alert.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Resolves an alert by hand, with a comment. If the cause is still there, the workers open a new alert on the next result, so a
    /// manual resolve never hides a real problem for long; it also ends a hold, which would otherwise keep that new alert from opening.
    /// </summary>
    public async Task<ServiceResult> ResolveAsync(Caller caller, Guid alertId, string? comment, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.AlertsResolve))
        {
            return ServiceResult.Forbidden();
        }

        if (CommentProblem(comment, required: true) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var alert = await db.Alerts.Include(a => a.Endpoint).SingleOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert is null)
        {
            return ServiceResult.NotFound("alert");
        }

        if (alert.State == AlertState.Resolved)
        {
            return ServiceResult.Fail("This alert is already resolved.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        alert.State = AlertState.Resolved;
        alert.ResolvedAt = now;
        alert.ResolvedReason = $"Resolved manually by {caller.Name}";
        if (alert.ResolvedReason.Length > 500)
        {
            alert.ResolvedReason = alert.ResolvedReason[..500];
        }

        alert.HeldUntil = null;
        alert.HeldAt = null;
        alert.HeldByUserId = null;
        alert.UpdatedAt = now;
        var activity = AddActivity(db, caller, alert, AlertActivityKind.Resolved, comment, now);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AlertResolvedManually, "Alert", alert.Id.ToString(), alert.ClientId,
            new { alert.EndpointId, alert.Endpoint?.Hostname, alert.Title, CommentLength = activity.Comment.Length }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(alert.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Puts an unresolved alert on hold until <paramref name="untilUtc"/>, with a comment: maintenance for its source
    /// (<see cref="AlertHoldRules"/>), so it does not escalate and no new alert of the same check or problem opens, also after it
    /// resolved; no emails, and left out of the open alert lists and counts. The alert still resolves when its cause recovers, and it
    /// returns (with one email) when the hold ends while it is still unresolved.
    /// </summary>
    public async Task<ServiceResult> HoldAsync(Caller caller, Guid alertId, DateTime untilUtc, string? comment,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.AlertsHold))
        {
            return ServiceResult.Forbidden();
        }

        if (CommentProblem(comment, required: true) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        untilUtc = DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc);
        if (untilUtc < now + MinimumHold)
        {
            return ServiceResult.Fail("Choose a time in the future for the hold to end.");
        }

        if (untilUtc > now + MaximumHold)
        {
            return ServiceResult.Fail("A hold can last at most 7 days. Choose an earlier time.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var alert = await db.Alerts.Include(a => a.Endpoint).SingleOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert is null)
        {
            return ServiceResult.NotFound("alert");
        }

        if (alert.State == AlertState.Resolved)
        {
            return ServiceResult.Fail("This alert is already resolved.");
        }

        alert.HeldUntil = untilUtc;
        alert.HeldAt = now;
        alert.HeldByUserId = caller.UserId;
        alert.UpdatedAt = now;
        var activity = AddActivity(db, caller, alert, AlertActivityKind.Held, comment, now, untilUtc);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AlertHeld, "Alert", alert.Id.ToString(), alert.ClientId,
            new { alert.EndpointId, alert.Endpoint?.Hostname, alert.Title, HeldUntil = untilUtc, CommentLength = activity.Comment.Length }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(alert.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Ends a hold early; the alert is counted and listed as open again, or, when it resolved on hold, its source may open alerts again.
    /// A comment is optional here.
    /// </summary>
    public async Task<ServiceResult> EndHoldAsync(Caller caller, Guid alertId, string? comment = null, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.AlertsHold))
        {
            return ServiceResult.Forbidden();
        }

        if (CommentProblem(comment, required: false) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var alert = await db.Alerts.Include(a => a.Endpoint).SingleOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert is null)
        {
            return ServiceResult.NotFound("alert");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (alert.HeldUntil is null || alert.HeldUntil <= now)
        {
            return ServiceResult.Fail("This alert is not on hold.");
        }

        alert.HeldUntil = null;
        alert.HeldAt = null;
        alert.HeldByUserId = null;
        alert.UpdatedAt = now;
        var activity = AddActivity(db, caller, alert, AlertActivityKind.HoldEnded, comment, now);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AlertHoldEnded, "Alert", alert.Id.ToString(), alert.ClientId,
            new { alert.EndpointId, alert.Endpoint?.Hostname, alert.Title, Early = true, CommentLength = activity.Comment.Length }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(alert.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// What users did with these alerts, oldest first per alert, for the alerts the caller can see. Read apart from the alerts, so
    /// the lists that only count or show alerts do not load them.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AlertActivityView>>> GetActivitiesAsync(Caller caller,
        IReadOnlyCollection<Guid> alertIds, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.AlertsView);
        if (alertIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<AlertActivityView>>();
        }

        var ids = alertIds.Distinct().ToArray();
        await using var db = _dbFactory.Create(caller.Scope);
        var rows = await db.AlertActivities.AsNoTracking()
            .Where(a => ids.Contains(a.AlertId))
            .OrderBy(a => a.AlertId).ThenBy(a => a.Id)
            .Select(a => new { a.AlertId, View = new AlertActivityView(a.Id, a.Kind, a.Comment, a.UserName, a.Time, a.HeldUntil) })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.AlertId).ToDictionary(g => g.Key, g => (IReadOnlyList<AlertActivityView>)g.Select(r => r.View).ToList());
    }

    /// <summary>The problem with a comment, or null when it is fine.</summary>
    public static string? CommentProblem(string? comment, bool required)
    {
        var clean = ServiceSupport.Clean(comment);
        if (clean is null)
        {
            return required ? "Add a comment: say why, so others can follow what was done with this alert." : null;
        }

        return clean.Length > AlertActivity.MaxCommentLength
            ? $"The comment can be at most {AlertActivity.MaxCommentLength} characters."
            : null;
    }

    private static AlertActivity AddActivity(FleetoDbContext db, Caller caller, Alert alert, AlertActivityKind kind, string? comment, DateTime now,
        DateTime? heldUntil = null)
    {
        var activity = new AlertActivity
        {
            ClientId = alert.ClientId, EndpointId = alert.EndpointId, AlertId = alert.Id, Kind = kind,
            Comment = ServiceSupport.Clean(comment) ?? string.Empty, UserId = caller.UserId,
            UserName = caller.Name.Length <= 200 ? caller.Name : caller.Name[..200], Time = now, HeldUntil = heldUntil
        };
        db.AlertActivities.Add(activity);
        return activity;
    }

    private async Task PublishAsync(Guid alertId, CancellationToken cancellationToken)
    {
        try
        {
            await _bus.PublishAsync(NotificationChannels.Alerts, alertId.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish the change of alert {AlertId}", alertId);
        }
    }

    internal static string FormatCursor(DateTime time, Guid id) =>
        time.Ticks.ToString(CultureInfo.InvariantCulture) + "_" + id.ToString("N");

    internal static bool TryParseCursor(string? cursor, out DateTime time, out Guid id)
    {
        time = default;
        id = Guid.Empty;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 60)
        {
            return false;
        }

        var parts = cursor.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "N", out id))
        {
            return false;
        }

        time = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }
}
