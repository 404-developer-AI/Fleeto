using System.Globalization;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Web.Services;

public sealed record AlertQuery(AlertState? State, AlertSeverity? Severity, Guid? ClientId, string? Cursor = null, int Limit = 50);

public sealed record AlertPage(IReadOnlyList<AlertView> Items, string? NextCursor);

/// <summary>Alert list with keyset pagination, and manual acknowledge and resolve.</summary>
public sealed class AlertService
{
    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertService> _logger;

    public AlertService(IFleetifyDbContextFactory dbFactory, INotificationBus bus, TimeProvider time, ILogger<AlertService> logger)
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
        if (query.State is { } state)
        {
            alerts = alerts.Where(a => a.State == state);
        }

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

    internal static IQueryable<AlertView> Project(FleetifyDbContext db, IQueryable<Alert> alerts) =>
        alerts.Select(a => new AlertView(a.Id, a.EndpointId, a.Endpoint!.Hostname, a.ClientId,
            db.Clients.Where(c => c.Id == a.ClientId).Select(c => c.Code).FirstOrDefault() ?? string.Empty,
            a.Kind, a.Severity, a.State, a.Title, a.Detail, a.OpenedAt, a.UpdatedAt, a.AcknowledgedAt, a.ResolvedAt, a.ResolvedReason));

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
