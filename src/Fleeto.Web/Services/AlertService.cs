using System.Globalization;
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
        caller.EnsureView();
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
            a.State != AlertState.Resolved ? a.HeldUntil : null, a.CheckDefinitionId, a.Target));

    public async Task<ServiceResult> AcknowledgeAsync(Caller caller, Guid alertId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
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
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AlertAcknowledged, "Alert", alert.Id.ToString(), alert.ClientId,
            new { alert.EndpointId, alert.Endpoint?.Hostname, alert.Title }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(alert.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Resolves an alert by hand. If the cause is still there, the workers open a new alert on the next result, so a manual
    /// resolve never hides a real problem for long.
    /// </summary>
    public async Task<ServiceResult> ResolveAsync(Caller caller, Guid alertId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
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

        alert.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AlertResolvedManually, "Alert", alert.Id.ToString(), alert.ClientId,
            new { alert.EndpointId, alert.Endpoint?.Hostname, alert.Title }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(alert.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Puts an unresolved alert on hold until <paramref name="untilUtc"/>: no escalation or resolve emails, and left out of the
    /// open alert lists and counts. The alert still resolves when its cause recovers, and it returns (with one email) when the
    /// hold ends while it is still unresolved.
    /// </summary>
    public async Task<ServiceResult> HoldAsync(Caller caller, Guid alertId, DateTime untilUtc, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
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
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AlertHeld, "Alert", alert.Id.ToString(), alert.ClientId,
            new { alert.EndpointId, alert.Endpoint?.Hostname, alert.Title, HeldUntil = untilUtc }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(alert.Id, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Ends a hold before its time. No email: the technician is looking at the alert.</summary>
    public async Task<ServiceResult> EndHoldAsync(Caller caller, Guid alertId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var alert = await db.Alerts.Include(a => a.Endpoint).SingleOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert is null)
        {
            return ServiceResult.NotFound("alert");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (alert.State == AlertState.Resolved || alert.HeldUntil is null || alert.HeldUntil <= now)
        {
            return ServiceResult.Fail("This alert is not on hold.");
        }

        alert.HeldUntil = null;
        alert.HeldAt = null;
        alert.HeldByUserId = null;
        alert.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.AlertHoldEnded, "Alert", alert.Id.ToString(), alert.ClientId,
            new { alert.EndpointId, alert.Endpoint?.Hostname, alert.Title, Early = true }), now));
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(alert.Id, cancellationToken);
        return ServiceResult.Ok();
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
